using MiRenta.Application.Assistant;
using MiRenta.Application.Common;
using MiRenta.Domain.Assistant;
using MiRenta.Domain.Documents;
using MiRenta.Domain.Facts;
using MiRenta.Domain.Finance;
using MiRenta.Domain.Operations;
using MiRenta.Infrastructure.Ai;
using MiRenta.Infrastructure.Data;
using MiRenta.Infrastructure.Extraction;
using MiRenta.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MiRenta.Application.Documents;

/// <summary>
/// Procesa un documento ya almacenado: extracción determinista (y, si está configurada, sugerencias de IA sobre un
/// extracto minimizado), evidencia propuesta, movimientos, conciliaciones y la tarea de confirmación. Idempotente:
/// reprocesar un documento ya procesado no duplica nada.
/// </summary>
public sealed class DocumentProcessor(MiRentaDbContext db, IPrivateObjectStorage storage, IDocumentExtractor extractor,
    IAiExtractionAssistant ai, CaseWorkflow workflow, TimeProvider clock, ILogger<DocumentProcessor> log)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task ProcessAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null || doc.DeletedAt is not null || doc.Status is DocumentStatus.Processed or DocumentStatus.NeedsManualEntry) return;
        var c = await db.Cases.FirstAsync(x => x.Id == doc.CaseId, ct);
        doc.Status = DocumentStatus.Processing;
        doc.ProcessingAttempts++;
        await db.SaveChangesAsync(ct);

        var bytes = await storage.GetAsync(doc.StorageKey, ct) ?? throw new InvalidOperationException("El objeto no existe en el almacenamiento.");
        // Defensa en profundidad: el contenido almacenado debe coincidir con el hash registrado.
        if (Infrastructure.Security.TokenUtil.Sha256Hex(bytes) != doc.Sha256) throw new InvalidOperationException("Hash del documento no coincide.");

        var output = await extractor.ExtractAsync(bytes, doc.Format, ct);
        var evidence = output.Evidence.ToList();
        var method = output.Method;
        if (ai.Enabled && doc.Format == DocumentFileFormat.Pdf && output.Kind == DocumentKind.LaborIncomeCertificate && evidence.Count < 4)
        {
            var excerpt = string.Join('\n', evidence.Select(e => e.RawText));
            foreach (var s in await ai.SuggestFieldsAsync(excerpt, ct))
                if (evidence.All(e => e.Field != s.Field)) evidence.Add(new ExtractedEvidence(s.Field, "sugerido por IA", s.Amount, s.Locator, 0.4m));
            method += "+ai";
        }

        var version = (await db.DocumentExtractions.Where(x => x.DocumentId == doc.Id).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1;
        var extraction = new DocumentExtraction
        {
            DocumentId = doc.Id, Version = version, Method = method, ExtractorVersion = output.ExtractorVersion, PageCount = output.PageCount,
            RowCount = output.RowCount, ProposedKind = output.Kind, Confidence = output.Confidence, CreatedAt = Now,
            StructuredJson = Json.Serialize(new { evidence = evidence.Count, transactions = output.Transactions.Count, note = output.Note }),
        };
        db.DocumentExtractions.Add(extraction);

        var evidenceIds = new List<Guid>();
        foreach (var e in evidence.Where(e => FactCodes.Monetary.Contains(e.Field)))
        {
            var item = new EvidenceItem
            {
                CaseId = doc.CaseId, DocumentId = doc.Id, ExtractionId = extraction.Id, Field = e.Field, RawText = e.RawText,
                ExtractedAmount = e.Amount, Locator = e.Locator, Confidence = e.Confidence, CreatedAt = Now,
            };
            db.EvidenceItems.Add(item);
            evidenceIds.Add(item.Id);
        }

        if (output.Transactions.Count > 0)
        {
            var account = new FinancialAccount { CaseId = doc.CaseId, Institution = "Extracto " + doc.OriginalFileName, SourceDocumentId = doc.Id, CreatedAt = Now };
            db.FinancialAccounts.Add(account);
            var existingKeys = (await db.FinancialTransactions.Where(t => t.CaseId == doc.CaseId).Select(t => t.DedupKey).ToListAsync(ct)).ToHashSet();
            var occurrences = new Dictionary<string, int>();
            foreach (var t in output.Transactions.Take(5000))
            {
                var baseKey = $"{t.Date}|{t.Amount}|{t.Description}";
                occurrences[baseKey] = occurrences.GetValueOrDefault(baseKey) + 1;
                var key = ReconciliationService.DedupKey(doc.Id, t.Date, t.Amount, t.Description, occurrences[baseKey]);
                if (!existingKeys.Add(key)) continue;
                db.FinancialTransactions.Add(new FinancialTransaction
                {
                    CaseId = doc.CaseId, AccountId = account.Id, SourceDocumentId = doc.Id, SourceLocator = t.Locator, Date = t.Date,
                    Amount = t.Amount, Description = t.Description, DedupKey = key, CreatedAt = Now,
                });
            }
        }

        doc.Kind = output.Kind;
        doc.Status = evidenceIds.Count == 0 && output.Transactions.Count == 0 ? DocumentStatus.NeedsManualEntry : DocumentStatus.Processed;
        doc.ProcessedAt = Now;
        doc.LastError = null;

        if (evidenceIds.Count > 0)
        {
            var code = "c.doc." + doc.Id.ToString("N");
            if (!await db.Tasks.AnyAsync(t => t.CaseId == doc.CaseId && t.Code == code, ct))
                db.Tasks.Add(new AssistantTask
                {
                    CaseId = doc.CaseId, Code = code, Kind = TaskKind.Confirmation, AnswerType = AnswerType.Confirmation,
                    Prompt = $"Revisa los valores que encontramos en «{doc.OriginalFileName}» y confírmalos o corrígelos.",
                    Help = "Solo usamos un valor después de que lo confirmes.", RelatedJson = Json.Serialize(evidenceIds),
                    BlocksCalculation = true, SortOrder = 5, CreatedAt = Now,
                });
        }
        await db.SaveChangesAsync(ct);

        await ReconciliationService.RefreshAsync(db, doc.CaseId, ct);
        if (doc.RequestedByTaskId is { } taskId && await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct) is { Kind: TaskKind.DocumentRequest, State: TaskState.Open } req)
        { req.State = TaskState.Answered; req.ClosedAt = Now; }
        var conv = await db.Conversations.FirstAsync(x => x.CaseId == doc.CaseId, ct);
        db.Messages.Add(new ConversationMessage
        {
            ConversationId = conv.Id, Role = MessageRole.Assistant, CreatedAt = Now,
            Content = doc.Status == DocumentStatus.Processed
                ? $"Procesé «{doc.OriginalFileName}». " + (evidenceIds.Count > 0 ? "Encontré valores para que los confirmes." : $"Importé {output.Transactions.Count} movimientos.")
                : $"No pude leer valores de «{doc.OriginalFileName}». Puedes escribirlos en las preguntas.",
        });
        await workflow.SyncAsync(c, ct);
        await db.SaveChangesAsync(ct);

        if (c.OwnerUserId != Guid.Empty && await db.Users.FirstOrDefaultAsync(u => u.Id == c.OwnerUserId, ct) is { RemindersOptOut: false } owner)
        {
            await EmailOutbox.QueueAsync(db, owner.Id, c.Id, owner.Email, EmailKinds.DocumentProcessed, "Tu documento en Mi renta fue procesado",
                "Terminamos de revisar un documento que subiste a Mi renta. Ingresa a la aplicación para continuar.\n\n" +
                "Este correo no contiene información de tu declaración.", $"doc-processed:{doc.Id}", ct);
            await db.SaveChangesAsync(ct);
        }
        log.LogInformation("Documento procesado {DocumentId} estado {Status}", doc.Id, doc.Status);
    }

    public async Task MarkFailedAsync(Guid documentId, string error, bool final, CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null) return;
        doc.LastError = error.Length > 300 ? error[..300] : error;
        doc.Status = final ? DocumentStatus.Failed : DocumentStatus.Queued;
        if (final)
        {
            var c = await db.Cases.FirstAsync(x => x.Id == doc.CaseId, ct);
            await workflow.SyncAsync(c, ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
