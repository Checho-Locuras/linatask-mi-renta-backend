using MiRenta.Application.Assistant;
using MiRenta.Application.Common;
using MiRenta.Domain.Assistant;
using MiRenta.Domain.Cases;
using MiRenta.Domain.Documents;
using MiRenta.Domain.Operations;
using MiRenta.Infrastructure.Data;
using MiRenta.Infrastructure.Extraction;
using MiRenta.Infrastructure.Jobs;
using MiRenta.Infrastructure.Security;
using MiRenta.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MiRenta.Application.Documents;

public sealed record DocumentDto(Guid Id, string FileName, string Format, long SizeBytes, string Kind, string Status, string? Note,
    DateTime CreatedAt, DateTime? ProcessedAt, IReadOnlyList<EvidenceDto> Evidence, int TransactionCount);
public sealed record EvidenceDto(Guid Id, string Field, decimal? ExtractedAmount, decimal? ConfirmedAmount, string Locator, decimal Confidence, string Status);
public sealed record UploadResult(DocumentDto Document, bool Duplicate);
public sealed record DownloadLink(string Url, DateTime ExpiresAt);
public sealed record FileContent(byte[] Content, string ContentType, string FileName);

public sealed class DocumentService(MiRentaDbContext db, CaseAccess access, CaseWorkflow workflow, Cases.FactService facts, IPrivateObjectStorage storage,
    IOptions<StorageOptions> storageOptions, DownloadTokenSigner signer, IOptions<AppOptions> app, Audit audit, TimeProvider clock)
{
    public const int MaxDocumentsPerCase = 60;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<UploadResult> UploadAsync(Guid caseId, CurrentUser user, string fileName, byte[] content, Guid? taskId, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        if (!CaseStateMachine.AcceptsChanges(c.Status)) throw AppException.Conflict("case_locked", "El expediente no admite documentos en su estado actual.");
        if (content.Length > storageOptions.Value.MaxFileBytes)
            throw new AppException(413, "file_too_large", $"El archivo supera el máximo de {storageOptions.Value.MaxFileBytes / (1024 * 1024)} MB.");
        var sniff = FileSniffer.Sniff(content, fileName);
        if (!sniff.Ok) { audit.Record("document.rejected", user.Id, c.Id, new { reason = sniff.Error }); await db.SaveChangesAsync(ct); throw AppException.BadRequest("invalid_file", sniff.Error!); }

        var sha = TokenUtil.Sha256Hex(content);
        var existing = await db.Documents.FirstOrDefaultAsync(d => d.CaseId == c.Id && d.Sha256 == sha && d.DeletedAt == null, ct);
        if (existing is not null) return new(await ToDtoAsync(existing, ct), true);
        if (await db.Documents.CountAsync(d => d.CaseId == c.Id && d.DeletedAt == null, ct) >= MaxDocumentsPerCase)
            throw AppException.Conflict("too_many_documents", "Alcanzaste el máximo de documentos para este expediente.");
        if (taskId is not null && !await db.Tasks.AnyAsync(t => t.Id == taskId && t.CaseId == c.Id, ct)) taskId = null;

        var doc = new TaxDocument
        {
            CaseId = c.Id, UploadedByUserId = user.Id, OriginalFileName = FileSniffer.SafeDisplayName(fileName), ContentType = sniff.ContentType,
            Format = sniff.Format, SizeBytes = content.Length, Sha256 = sha, Status = DocumentStatus.Received, RequestedByTaskId = taskId, CreatedAt = Now,
        };
        doc.StorageKey = StorageKeys.ForDocument(c.Id, doc.Id);
        // 1) Se guarda en el almacenamiento privado ANTES de cualquier procesamiento asíncrono.
        await storage.PutAsync(doc.StorageKey, content, sniff.ContentType, ct);
        doc.Status = DocumentStatus.Queued;
        db.Documents.Add(doc);
        audit.Record("document.uploaded", user.Id, c.Id, new { documentId = doc.Id, format = doc.Format.ToString(), doc.SizeBytes });
        workflow.MoveTo(c, CaseStatus.ProcessingDocument);
        c.LastUserActivityAt = Now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Carrera: el mismo archivo se subió en paralelo. Se elimina la copia y se devuelve el existente.
            await storage.DeleteAsync(doc.StorageKey, ct);
            db.ChangeTracker.Clear();
            var winner = await db.Documents.FirstAsync(d => d.CaseId == caseId && d.Sha256 == sha && d.DeletedAt == null, ct);
            return new(await ToDtoAsync(winner, ct), true);
        }
        // 2) Se encola el procesamiento (idempotente por documento).
        await JobQueue.EnqueueAsync(db, JobKinds.ProcessDocument, new { documentId = doc.Id }, $"process-document:{doc.Id}", ct);
        return new(await ToDtoAsync(doc, ct), false);
    }

    public async Task<List<DocumentDto>> ListAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var docs = await db.Documents.Where(d => d.CaseId == c.Id && d.DeletedAt == null).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        var result = new List<DocumentDto>();
        foreach (var d in docs) result.Add(await ToDtoAsync(d, ct));
        return result;
    }

    public async Task<DocumentDto> GetAsync(Guid caseId, Guid documentId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == documentId && x.CaseId == c.Id && x.DeletedAt == null, ct) ?? throw AppException.NotFound();
        return await ToDtoAsync(d, ct);
    }

    public async Task<DownloadLink> CreateDownloadLinkAsync(Guid caseId, Guid documentId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == documentId && x.CaseId == c.Id && x.DeletedAt == null, ct) ?? throw AppException.NotFound();
        var exp = Now.AddMinutes(Math.Clamp(app.Value.DownloadUrlMinutes, 1, 10));
        audit.Record("document.download_link", user.Id, c.Id, new { documentId = d.Id });
        await db.SaveChangesAsync(ct);
        return new($"/api/v1/my-rent/files/{signer.Create(d.Id, user.Id, exp)}", exp);
    }

    /// <summary>Sirve un archivo por token firmado: verifica firma, expiración y que el usuario siga teniendo acceso.</summary>
    public async Task<FileContent> ReadByTokenAsync(string token, CancellationToken ct)
    {
        var parsed = signer.Validate(token, Now) ?? throw new AppException(410, "link_expired", "El enlace expiró o no es válido.");
        var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == parsed.DocumentId && x.DeletedAt == null, ct) ?? throw AppException.NotFound();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == parsed.UserId && !u.IsDisabled, ct) ?? throw AppException.NotFound();
        await access.GetAsync(d.CaseId, new CurrentUser(user.Id, user.Role), CaseAccessLevel.Read, ct);
        var bytes = await storage.GetAsync(d.StorageKey, ct) ?? throw AppException.NotFound();
        audit.Record("document.downloaded", user.Id, d.CaseId, new { documentId = d.Id });
        await db.SaveChangesAsync(ct);
        return new(bytes, d.ContentType, d.OriginalFileName);
    }

    public async Task DeleteAsync(Guid caseId, Guid documentId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        if (c.OwnerUserId != user.Id) throw AppException.Forbidden("Solo el contribuyente puede eliminar sus documentos.");
        if (!CaseStateMachine.AcceptsChanges(c.Status)) throw AppException.Conflict("case_locked", "El expediente no admite cambios en su estado actual.");
        var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == documentId && x.CaseId == c.Id && x.DeletedAt == null, ct) ?? throw AppException.NotFound();
        await storage.DeleteAsync(d.StorageKey, ct);
        d.DeletedAt = Now;
        d.Status = DocumentStatus.Deleted;
        // La evidencia del documento deja de contar; los movimientos se excluyen y las tareas de confirmación se cancelan.
        var evidence = await db.EvidenceItems.Where(e => e.DocumentId == d.Id).ToListAsync(ct);
        var affected = evidence.Where(e => e.Status is EvidenceStatus.Confirmed or EvidenceStatus.Corrected).Select(e => e.Field).Distinct().ToList();
        foreach (var e in evidence) e.Status = EvidenceStatus.Rejected;
        foreach (var field in affected)
        {
            var remaining = await db.EvidenceItems.Where(e => e.CaseId == c.Id && e.Field == field && e.DocumentId != d.Id
                && (e.Status == EvidenceStatus.Confirmed || e.Status == EvidenceStatus.Corrected)).ToListAsync(ct);
            var current = await db.Facts.FirstOrDefaultAsync(f => f.CaseId == c.Id && f.Code == field && f.Status == Domain.Facts.FactStatus.Accepted, ct);
            if (current?.Source != Domain.Facts.FactSource.Document) continue; // un valor manual prevalece
            if (remaining.Count > 0)
                await facts.SetAsync(c, field, remaining.Sum(e => e.ConfirmedAmount ?? 0), null, Domain.Facts.FactSource.Document, user.Id,
                    remaining.Select(e => e.Id).ToList(), "Recalculado al eliminar un documento.", ct);
            else { current.Status = Domain.Facts.FactStatus.Superseded; await facts.InvalidateDraftsAsync(c, ct); }
        }
        await db.FinancialTransactions.Where(t => t.SourceDocumentId == d.Id).ExecuteDeleteAsync(ct);
        var confirm = await db.Tasks.FirstOrDefaultAsync(t => t.CaseId == c.Id && t.Code == "c.doc." + d.Id.ToString("N"), ct);
        if (confirm is { State: TaskState.Open }) { confirm.State = TaskState.Cancelled; confirm.ClosedAt = Now; }
        audit.Record("document.deleted", user.Id, c.Id, new { documentId = d.Id });
        await workflow.SyncAsync(c, ct);
        await db.SaveChangesAsync(ct);
        await ReconciliationService.RefreshAsync(db, c.Id, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task<DocumentDto> ToDtoAsync(TaxDocument d, CancellationToken ct)
    {
        var ev = await db.EvidenceItems.Where(e => e.DocumentId == d.Id).OrderBy(e => e.Field).ToListAsync(ct);
        var txCount = await db.FinancialTransactions.CountAsync(t => t.SourceDocumentId == d.Id, ct);
        var note = d.Status == DocumentStatus.NeedsManualEntry ? "No pudimos leer valores automáticamente: responde las preguntas manualmente."
                 : d.Status == DocumentStatus.Failed ? "No fue posible procesar el documento." : null;
        return new(d.Id, d.OriginalFileName, d.Format.ToString(), d.SizeBytes, d.Kind.ToString(), d.Status.ToString(), note, d.CreatedAt, d.ProcessedAt,
            ev.Select(e => new EvidenceDto(e.Id, e.Field, e.ExtractedAmount, e.ConfirmedAmount, e.Locator, e.Confidence, e.Status.ToString())).ToList(), txCount);
    }
}
