using System.Text.RegularExpressions;
using MiRenta.Application.Assistant;
using MiRenta.Application.Common;
using MiRenta.Domain.Finance;
using MiRenta.Infrastructure.Data;
using MiRenta.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace MiRenta.Application.Documents;

public sealed record ReconciliationDto(Guid Id, string Kind, string Explanation, bool IsOpen, string? Resolution,
    IReadOnlyList<TransactionDto> Transactions, IReadOnlyList<string> AllowedResolutions);
public sealed record TransactionDto(Guid Id, DateOnly Date, decimal Amount, string Description, string TaxTreatment, string ReviewStatus, Guid SourceDocumentId, string Locator);
public sealed record ResolveRequest(string Resolution, string? Note);
public sealed record DepositSummary(decimal TotalCredits, decimal ExcludedCredits, decimal SuggestedDeposits, int Transactions);

/// <summary>
/// Conciliación de movimientos: duplicados entre extractos, transferencias propias, reversiones y depósitos sin
/// explicar. Solo PROPONE; la clasificación definitiva la confirma el usuario.
/// </summary>
public sealed class ReconciliationService(MiRentaDbContext db, CaseAccess access, CaseWorkflow workflow, Audit audit, TimeProvider clock)
{
    /// <summary>Depósitos desde este valor sin descripción reconocible se preguntan al usuario.</summary>
    public const decimal UnexplainedThreshold = 1_000_000m;

    private static readonly Regex Payroll = new(@"n[oó]mina|salario|pago\s+de\s+n[oó]mina|prima|cesant", RegexOptions.IgnoreCase);
    private static readonly Regex OwnTransfer = new(@"transf.*(propia|entre\s+cuentas|mismo\s+titular)|traslado\s+entre", RegexOptions.IgnoreCase);
    private static readonly Regex ReversalWords = new(@"revers|devoluci|anulaci|reintegro", RegexOptions.IgnoreCase);

    public static readonly Dictionary<ReconciliationKind, string[]> Resolutions = new()
    {
        [ReconciliationKind.PossibleDuplicate] = ["is_duplicate", "not_duplicate"],
        [ReconciliationKind.PossibleOwnTransfer] = ["own_transfer", "not_own_transfer"],
        [ReconciliationKind.PossibleReversal] = ["reversal", "not_reversal"],
        [ReconciliationKind.UnexplainedDeposit] = ["labor_income", "own_transfer", "loan", "non_taxable", "other_income"],
    };

    public static string DedupKey(Guid documentId, DateOnly date, decimal amount, string description, int occurrence) =>
        TokenUtil.Sha256Hex($"{documentId:N}|{date:yyyy-MM-dd}|{amount:0.00}|{Normalize(description)}|{occurrence}");

    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"\s+", " ").Trim();

    /// <summary>Detecta posibles incidencias (idempotente por clave). No reabre las ya resueltas.</summary>
    public static async Task RefreshAsync(MiRentaDbContext db, Guid caseId, CancellationToken ct)
    {
        var txs = await db.FinancialTransactions.Where(t => t.CaseId == caseId).OrderBy(t => t.Date).ToListAsync(ct);
        var existing = await db.Reconciliations.Where(r => r.CaseId == caseId).ToListAsync(ct);
        var ids = txs.Select(t => t.Id).ToHashSet();

        // Conciliaciones abiertas cuyos movimientos ya no existen (documento eliminado) se cierran.
        foreach (var r in existing.Where(r => r.IsOpen))
            if ((Json.Deserialize<List<Guid>>(r.TransactionIdsJson) ?? []).Any(id => !ids.Contains(id))) { r.IsOpen = false; r.Resolution = "source_deleted"; }

        var proposals = new List<(ReconciliationKind Kind, List<Guid> Ids, string Explanation)>();
        var active = txs.Where(t => t.TaxTreatment != TaxTreatment.Excluded).ToList();
        for (var i = 0; i < active.Count; i++)
        {
            var a = active[i];
            for (var j = i + 1; j < active.Count; j++)
            {
                var b = active[j];
                if (b.Date.DayNumber - a.Date.DayNumber > 5) break;
                if (a.Amount == b.Amount && a.SourceDocumentId != b.SourceDocumentId && a.Date == b.Date && Normalize(a.Description) == Normalize(b.Description))
                    proposals.Add((ReconciliationKind.PossibleDuplicate, [a.Id, b.Id], "El mismo movimiento aparece en dos documentos distintos."));
                else if (a.Amount == -b.Amount && (ReversalWords.IsMatch(a.Description) || ReversalWords.IsMatch(b.Description)))
                    proposals.Add((ReconciliationKind.PossibleReversal, [a.Id, b.Id], "Un movimiento y su reverso por el mismo valor."));
                else if (a.Amount == -b.Amount && a.SourceDocumentId != b.SourceDocumentId && b.Date.DayNumber - a.Date.DayNumber <= 3)
                    proposals.Add((ReconciliationKind.PossibleOwnTransfer, [a.Id, b.Id], "Salida y entrada por el mismo valor en cuentas distintas: posible transferencia entre tus cuentas."));
            }
            if (a.Amount > 0 && OwnTransfer.IsMatch(a.Description) && a.TaxTreatment == TaxTreatment.Unclassified)
                proposals.Add((ReconciliationKind.PossibleOwnTransfer, [a.Id], "La descripción indica una transferencia entre cuentas propias."));
            else if (a.Amount >= UnexplainedThreshold && a.TaxTreatment == TaxTreatment.Unclassified && !Payroll.IsMatch(a.Description))
                proposals.Add((ReconciliationKind.UnexplainedDeposit, [a.Id], "Depósito sin origen identificado: indícanos qué fue."));
            else if (a.Amount > 0 && Payroll.IsMatch(a.Description) && a.TaxTreatment == TaxTreatment.Unclassified)
            {
                a.TaxTreatment = TaxTreatment.LaborIncome; // propuesta, el usuario la puede cambiar
                a.ReviewStatus = ReviewStatus.Proposed;
            }
        }

        // Un depósito que ya forma parte de un par (transferencia propia, reverso o duplicado) no se pregunta aparte.
        var paired = proposals.Where(p => p.Ids.Count > 1).SelectMany(p => p.Ids).ToHashSet();
        proposals.RemoveAll(p => p.Kind == ReconciliationKind.UnexplainedDeposit && paired.Contains(p.Ids[0]));
        foreach (var (kind, txIds, explanation) in proposals)
        {
            var key = TokenUtil.Sha256Hex($"{kind}|{string.Join(',', txIds.OrderBy(x => x))}");
            if (existing.Any(r => r.DedupKey == key) || db.Reconciliations.Local.Any(r => r.DedupKey == key)) continue;
            // Un movimiento ya resuelto en otra conciliación del mismo tipo no se vuelve a preguntar.
            if (txIds.Count == 1 && existing.Any(r => !r.IsOpen && r.TransactionIdsJson.Contains(txIds[0].ToString()))) continue;
            db.Reconciliations.Add(new Reconciliation { CaseId = caseId, Kind = kind, TransactionIdsJson = Json.Serialize(txIds), Explanation = explanation, DedupKey = key });
        }
    }

    public async Task<List<ReconciliationDto>> ListAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var recs = await db.Reconciliations.Where(r => r.CaseId == c.Id).OrderBy(r => r.IsOpen ? 0 : 1).ThenBy(r => r.CreatedAt).ToListAsync(ct);
        var txs = await db.FinancialTransactions.Where(t => t.CaseId == c.Id).ToDictionaryAsync(t => t.Id, ct);
        return recs.Select(r => new ReconciliationDto(r.Id, r.Kind.ToString(), r.Explanation, r.IsOpen, r.Resolution,
            (Json.Deserialize<List<Guid>>(r.TransactionIdsJson) ?? []).Where(txs.ContainsKey).Select(id => ToDto(txs[id])).ToList(),
            Resolutions[r.Kind])).ToList();
    }

    public async Task<List<TransactionDto>> TransactionsAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        return (await db.FinancialTransactions.Where(t => t.CaseId == c.Id).OrderBy(t => t.Date).Take(5000).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<DepositSummary> DepositSummaryAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var credits = await db.FinancialTransactions.Where(t => t.CaseId == c.Id && t.Amount > 0).ToListAsync(ct);
        var excluded = credits.Where(t => t.TaxTreatment is TaxTreatment.Excluded or TaxTreatment.OwnTransfer or TaxTreatment.Reversal).Sum(t => t.Amount);
        var total = credits.Sum(t => t.Amount);
        return new(total, excluded, total - excluded, credits.Count);
    }

    public async Task<ReconciliationDto> ResolveAsync(Guid caseId, Guid reconciliationId, CurrentUser user, ResolveRequest req, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        if (!Domain.Cases.CaseStateMachine.AcceptsChanges(c.Status)) throw AppException.Conflict("case_locked", "El expediente no admite cambios en su estado actual.");
        var r = await db.Reconciliations.FirstOrDefaultAsync(x => x.Id == reconciliationId && x.CaseId == c.Id, ct) ?? throw AppException.NotFound();
        if (!Resolutions[r.Kind].Contains(req.Resolution)) throw AppException.BadRequest("invalid_resolution", "Resolución inválida.");
        var ids = Json.Deserialize<List<Guid>>(r.TransactionIdsJson) ?? [];
        var txs = await db.FinancialTransactions.Where(t => t.CaseId == c.Id && ids.Contains(t.Id)).OrderBy(t => t.CreatedAt).ToListAsync(ct);

        // Un movimiento ya excluido como duplicado no se reclasifica.
        if (req.Resolution != "is_duplicate") txs = txs.Where(t => t.TaxTreatment != TaxTreatment.Excluded).ToList();
        switch (req.Resolution)
        {
            case "is_duplicate":
                var excluded = txs.Skip(1).ToList();
                excluded.ForEach(t => t.TaxTreatment = TaxTreatment.Excluded);
                // Las preguntas abiertas sobre el movimiento excluido ya no aplican.
                var excludedIds = excluded.Select(t => t.Id.ToString()).ToList();
                foreach (var other in await db.Reconciliations.Where(x => x.CaseId == c.Id && x.IsOpen && x.Id != r.Id).ToListAsync(ct))
                    if (excludedIds.Any(id => other.TransactionIdsJson.Contains(id)))
                    { other.IsOpen = false; other.Resolution = "excluded_duplicate"; other.ResolvedByUserId = user.Id; other.ResolvedAt = clock.GetUtcNow().UtcDateTime; }
                break;
            case "own_transfer": txs.ForEach(t => t.TaxTreatment = TaxTreatment.OwnTransfer); break;
            case "reversal": txs.ForEach(t => t.TaxTreatment = TaxTreatment.Reversal); break;
            case "labor_income": txs.ForEach(t => t.TaxTreatment = TaxTreatment.LaborIncome); break;
            case "loan": txs.ForEach(t => t.TaxTreatment = TaxTreatment.Loan); break;
            case "non_taxable": txs.ForEach(t => t.TaxTreatment = TaxTreatment.NonTaxable); break;
            case "other_income":
                txs.ForEach(t => t.TaxTreatment = TaxTreatment.OtherIncome);
                db.ValidationIssues.Add(new Domain.Cases.ValidationIssue
                {
                    CaseId = c.Id, Code = "income.other_from_deposit", Severity = Domain.Cases.IssueSeverity.Warning,
                    Message = "Indicaste que un depósito fue otro tipo de ingreso: revisa con tu contador si debe declararse en otra cédula.",
                    RelatedJson = Json.Serialize(ids),
                });
                break;
        }
        if (req.Resolution is "own_transfer" or "reversal")
            foreach (var other in await db.Reconciliations.Where(x => x.CaseId == c.Id && x.IsOpen && x.Id != r.Id && x.Kind == ReconciliationKind.UnexplainedDeposit).ToListAsync(ct))
                if (txs.Any(t => other.TransactionIdsJson.Contains(t.Id.ToString())))
                { other.IsOpen = false; other.Resolution = "classified_" + req.Resolution; other.ResolvedByUserId = user.Id; other.ResolvedAt = clock.GetUtcNow().UtcDateTime; }
        foreach (var t in txs) t.ReviewStatus = ReviewStatus.Confirmed;
        r.IsOpen = false;
        r.Resolution = req.Resolution;
        r.ResolvedByUserId = user.Id;
        r.ResolvedAt = clock.GetUtcNow().UtcDateTime;
        audit.Record("reconciliation.resolved", user.Id, c.Id, new { reconciliationId = r.Id, kind = r.Kind.ToString(), req.Resolution });
        await db.SaveChangesAsync(ct);
        await workflow.SyncAsync(c, ct);
        await db.SaveChangesAsync(ct);
        var dtos = await ListAsync(caseId, user, ct);
        return dtos.First(x => x.Id == r.Id);
    }

    private static TransactionDto ToDto(FinancialTransaction t) => new(t.Id, t.Date, t.Amount, t.Description, t.TaxTreatment.ToString(),
        t.ReviewStatus.ToString(), t.SourceDocumentId, t.SourceLocator);
}
