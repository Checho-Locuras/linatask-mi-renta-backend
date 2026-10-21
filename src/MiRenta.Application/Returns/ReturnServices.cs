using System.Globalization;
using System.Text;
using MiRenta.Application.Assistant;
using MiRenta.Application.Cases;
using MiRenta.Application.Common;
using MiRenta.Domain.Assistant;
using MiRenta.Domain.Cases;
using MiRenta.Domain.Documents;
using MiRenta.Domain.Engine;
using MiRenta.Domain.Facts;
using MiRenta.Domain.Identity;
using MiRenta.Domain.Operations;
using MiRenta.Domain.Returns;
using MiRenta.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MiRenta.Application.Returns;

public sealed record CalculationDto(Guid Id, string Status, bool IsPreview, string EngineVersion, string RuleSetVersion, DateTime CreatedAt,
    IReadOnlyList<EngineIssue> Blocking, IReadOnlyList<EngineIssue> Warnings, IReadOnlyList<EngineLine> Lines,
    ObligationAssessment Obligation, IReadOnlyList<string> RulesUsed, Guid? DraftId);
public sealed record DraftLineDto(Guid Id, string Concept, int? BoxNumber, string Label, decimal Value, string RuleCode, string Formula,
    IReadOnlyList<Guid> SourceFactIds, IReadOnlyList<string> Warnings, int SortOrder);
public sealed record DraftDto(Guid Id, int Version, string Status, string FormVersion, Guid CalculationId, string CalculationStatus,
    DateTime CreatedAt, Guid? ReviewedByUserId, DateTime? ReviewedAt, string? ReviewComment, IReadOnlyList<DraftLineDto> Lines,
    IReadOnlyList<string> Disclaimers);
public sealed record ReviewRequest(Guid DraftId, int Version, string Decision, string? Comment, bool AcknowledgePendingRules);
public sealed record ExportFile(byte[] Content, string ContentType, string FileName);
public sealed record SetFactRequest(decimal? Amount, string? TextValue, string? Note);
public sealed record RuleDto(string Code, int TaxYear, string RuleSetVersion, string Description, string LegalReference, string? SourceUrl,
    string Status, Guid? ApprovedByUserId, DateTime? ApprovedAt, string? ApprovalNote, string ParametersJson);
public sealed record ApproveRuleRequest(string Note);

public sealed class CalculationService(MiRentaDbContext db, CaseAccess access, CaseWorkflow workflow, Audit audit, TimeProvider clock)
{
    public const string FormVersion = "Formulario 210 AG 2025 (Res. DIAN 000044 de 2024) — mapeo de casillas sin verificar";
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<CalculationDto> CalculateAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        var open = await db.Tasks.Where(t => t.CaseId == c.Id && t.State == TaskState.Open).ToListAsync(ct);
        var docs = await db.Documents.Where(d => d.CaseId == c.Id && d.DeletedAt == null).Select(d => d.Status).ToListAsync(ct);
        var openRecon = await db.Reconciliations.CountAsync(r => r.CaseId == c.Id && r.IsOpen, ct);
        if (!CaseService.CanCalculate(c, open, docs, openRecon))
            throw AppException.Conflict("not_ready", "El expediente aún no está listo para calcular: completa las preguntas, confirmaciones y conciliaciones pendientes.");

        var facts = await db.Facts.Where(f => f.CaseId == c.Id && f.Status == FactStatus.Accepted).OrderBy(f => f.Code).ThenBy(f => f.Version).ToListAsync(ct);
        var rules = await db.Rules.Where(r => r.TaxYear == c.TaxYear).ToListAsync(ct);
        var result = Form210Engine.Calculate(new EngineInput(c.TaxYear,
            facts.Select(f => new EngineFact(f.Id, f.Code, f.Amount, f.TextValue, f.Status, f.Source)).ToList(), rules));

        var calc = new TaxCalculation
        {
            CaseId = c.Id, EngineVersion = result.EngineVersion, RuleSetVersion = result.RuleSetVersion, Status = result.Status,
            RequestedByUserId = user.Id, CreatedAt = Now, BlockingReasonsJson = Json.Serialize(result.Blocking), ResultJson = Json.Serialize(result),
            FactsSnapshotJson = Json.Serialize(facts.Select(f => new { f.Id, f.Code, f.Amount, f.TextValue, Source = f.Source.ToString(), f.Version })),
        };
        db.Calculations.Add(calc);

        // Incidencias del motor: se reemplazan por las del último cálculo.
        foreach (var i in await db.ValidationIssues.Where(i => i.CaseId == c.Id && i.IsOpen && i.Code.StartsWith("engine.")).ToListAsync(ct))
        { i.IsOpen = false; i.ResolvedAt = Now; }
        foreach (var b in result.Blocking)
            db.ValidationIssues.Add(new ValidationIssue { CaseId = c.Id, Code = "engine." + b.Code, Severity = IssueSeverity.Blocking, Message = b.Message });

        Guid? draftId = null;
        if (result.Status != CalculationStatus.Blocked && c.Status is CaseStatus.ReadyForCalculation or CaseStatus.ReadyForReview)
        {
            if (c.Status == CaseStatus.ReadyForReview) workflow.MoveTo(c, CaseStatus.ReadyForCalculation);
            workflow.MoveTo(c, CaseStatus.Calculating);
            foreach (var d in await db.Drafts.Where(d => d.CaseId == c.Id && d.Status != DraftStatus.Superseded).ToListAsync(ct)) d.Status = DraftStatus.Superseded;
            var version = (await db.Drafts.Where(d => d.CaseId == c.Id).MaxAsync(d => (int?)d.Version, ct) ?? 0) + 1;
            var draft = new ReturnDraft { CaseId = c.Id, CalculationId = calc.Id, Version = version, FormVersion = FormVersion, CreatedAt = Now };
            db.Drafts.Add(draft);
            foreach (var l in result.Lines)
                db.ReturnLines.Add(new ReturnLine
                {
                    DraftId = draft.Id, Concept = l.Concept, BoxNumber = null, Label = l.Label, Value = l.Value, RuleCode = l.RuleCode,
                    Formula = l.Formula, SourcesJson = Json.Serialize(l.SourceFactIds), WarningsJson = Json.Serialize(l.Warnings), SortOrder = l.Order,
                });
            workflow.MoveTo(c, CaseStatus.ReadyForReview);
            draftId = draft.Id;
            await NotifyDraftReadyAsync(c, draft, ct);
        }
        else if (result.Status == CalculationStatus.Blocked && !result.IsPreview && c.Status == CaseStatus.ReadyForCalculation)
            workflow.MoveTo(c, CaseStatus.NeedsProfessionalReview);

        audit.Record("calculation.run", user.Id, c.Id, new { calculationId = calc.Id, status = result.Status.ToString(), preview = result.IsPreview });
        await db.SaveChangesAsync(ct);
        return ToDto(calc, result, draftId);
    }

    public async Task<CalculationDto?> LatestAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var calc = await db.Calculations.Where(x => x.CaseId == c.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (calc is null) return null;
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.CalculationId == calc.Id, ct);
        return ToDto(calc, Json.Deserialize<EngineResult>(calc.ResultJson)!, draft?.Id);
    }

    private async Task NotifyDraftReadyAsync(TaxCase c, ReturnDraft draft, CancellationToken ct)
    {
        var owner = await db.Users.FirstAsync(u => u.Id == c.OwnerUserId, ct);
        if (owner.RemindersOptOut) return;
        await EmailOutbox.QueueAsync(db, owner.Id, c.Id, owner.Email, EmailKinds.DraftReady, "Tu borrador en Mi renta está listo para revisión",
            "Generamos un borrador de tu declaración. Un contador autorizado debe revisarlo antes de que puedas exportarlo. " +
            "Ingresa a Mi renta para ver el detalle.\n\nEste correo no contiene cifras ni documentos.", $"draft-ready:{draft.Id}", ct);
    }

    private static CalculationDto ToDto(TaxCalculation calc, EngineResult r, Guid? draftId) => new(calc.Id, r.Status.ToString(), r.IsPreview,
        r.EngineVersion, r.RuleSetVersion, calc.CreatedAt, r.Blocking, r.Warnings, r.Lines, r.Obligation, r.RulesUsed, draftId);
}

public sealed class DraftService(MiRentaDbContext db, CaseAccess access, CaseWorkflow workflow, Audit audit, TimeProvider clock)
{
    public static readonly string[] Disclaimers =
    [
        "Borrador preparado por un asistente. No es la declaración oficial ni se presenta automáticamente ante la DIAN.",
        "Las reglas tributarias usadas citan su norma y pueden estar pendientes de aprobación profesional.",
        "Los números de casilla del formulario 210 no están verificados: diligencia el formulario oficial con la guía de la DIAN.",
        "La presentación la hace el contribuyente en el portal de la DIAN con su propia cuenta. Mi renta nunca pide ni guarda la contraseña de la DIAN.",
    ];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<DraftDto?> CurrentAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var d = await db.Drafts.Where(x => x.CaseId == c.Id && x.Status != DraftStatus.Superseded).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        return d is null ? null : await ToDtoAsync(d, ct);
    }

    public async Task<DraftDto> ReviewAsync(Guid caseId, CurrentUser user, ReviewRequest req, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        if (!await access.IsAccountantForCaseAsync(c, user, ct)) throw AppException.Forbidden("Solo un contador autorizado para este expediente puede revisar el borrador.");
        var d = await db.Drafts.FirstOrDefaultAsync(x => x.Id == req.DraftId && x.CaseId == c.Id, ct) ?? throw AppException.NotFound();
        if (d.Status != DraftStatus.Draft || c.Status != CaseStatus.ReadyForReview) throw AppException.Conflict("draft_not_reviewable", "Este borrador ya no se puede revisar.");
        if (d.Version != req.Version) throw AppException.Conflict("version_mismatch", "El borrador cambió; vuelve a cargarlo.");
        var calc = await db.Calculations.FirstAsync(x => x.Id == d.CalculationId, ct);
        var comment = req.Comment?.Trim();
        if (comment is { Length: > 2000 }) throw AppException.BadRequest("comment_too_long", "El comentario es demasiado largo.");

        if (req.Decision == "approve")
        {
            if (calc.Status == CalculationStatus.Preliminary && !req.AcknowledgePendingRules)
                throw AppException.BadRequest("ack_required", "El cálculo usa reglas pendientes de aprobación profesional: confirma que las revisaste.");
            d.Status = DraftStatus.ApprovedByAccountant;
            workflow.MoveTo(c, CaseStatus.ApprovedForExport);
        }
        else if (req.Decision == "reject")
        {
            if (string.IsNullOrWhiteSpace(comment)) throw AppException.BadRequest("comment_required", "Indica qué debe corregirse.");
            d.Status = DraftStatus.RejectedByAccountant;
            workflow.MoveTo(c, CaseStatus.NeedsProfessionalReview);
        }
        else throw AppException.BadRequest("invalid_decision", "Decisión inválida.");

        d.ReviewedByUserId = user.Id;
        d.ReviewedAt = Now;
        d.ReviewComment = comment;
        if (comment is { Length: > 0 }) db.DraftReviewNotes.Add(new DraftReviewNote { DraftId = d.Id, AuthorUserId = user.Id, Text = comment, CreatedAt = Now });
        audit.Record("draft.reviewed", user.Id, c.Id, new { draftId = d.Id, d.Version, decision = req.Decision, calculation = calc.Status.ToString(), req.AcknowledgePendingRules });
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(d, ct);
    }

    public async Task<ExportFile> ExportAsync(Guid caseId, CurrentUser user, string format, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var d = await db.Drafts.Where(x => x.CaseId == c.Id && (x.Status == DraftStatus.ApprovedByAccountant || x.Status == DraftStatus.ConfirmedByTaxpayer))
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        if (d is null || c.Status is not (CaseStatus.ApprovedForExport or CaseStatus.Completed))
            throw AppException.Conflict("not_approved", "Solo se puede exportar un borrador aprobado por un contador.");
        var dto = await ToDtoAsync(d, ct);
        var reviewer = await db.Users.Where(u => u.Id == d.ReviewedByUserId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        audit.Record("draft.exported", user.Id, c.Id, new { draftId = d.Id, d.Version, format });
        await db.SaveChangesAsync(ct);

        var stamp = $"{c.TaxYear}-v{d.Version}";
        if (format == "csv")
        {
            var sb = new StringBuilder();
            sb.AppendLine("concepto;casilla;descripcion;valor;regla;formula;advertencias");
            foreach (var l in dto.Lines)
                sb.AppendLine(string.Join(';', Csv(l.Concept), l.BoxNumber?.ToString() ?? "por verificar", Csv(l.Label),
                    l.Value.ToString("0", CultureInfo.InvariantCulture), Csv(l.RuleCode), Csv(l.Formula), Csv(string.Join(" | ", l.Warnings))));
            sb.AppendLine();
            sb.AppendLine(Csv($"Aprobado por {reviewer} el {d.ReviewedAt:yyyy-MM-dd HH:mm} UTC; versión {d.Version}; {d.FormVersion}"));
            foreach (var disc in Disclaimers) sb.AppendLine(Csv(disc));
            return new(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8", $"mi-renta-borrador-{stamp}.csv");
        }
        var json = Json.Serialize(new { draft = dto, approvedBy = reviewer, exportedAt = Now, disclaimers = Disclaimers });
        return new(Encoding.UTF8.GetBytes(json), "application/json", $"mi-renta-borrador-{stamp}.json");
    }

    /// <summary>El contribuyente indica que ya presentó la declaración por su cuenta en el portal de la DIAN.</summary>
    public async Task MarkFiledAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        if (c.OwnerUserId != user.Id) throw AppException.Forbidden();
        if (c.Status != CaseStatus.ApprovedForExport) throw AppException.Conflict("not_approved", "El borrador aún no está aprobado.");
        var d = await db.Drafts.Where(x => x.CaseId == c.Id && x.Status == DraftStatus.ApprovedByAccountant).OrderByDescending(x => x.Version).FirstAsync(ct);
        d.Status = DraftStatus.ConfirmedByTaxpayer;
        workflow.MoveTo(c, CaseStatus.Completed);
        audit.Record("case.marked_filed", user.Id, c.Id, new { draftId = d.Id });
        await db.SaveChangesAsync(ct);
    }

    private static string Csv(string s)
    {
        var v = s.Length > 0 && "=+-@".Contains(s[0]) ? "'" + s : s;
        return v.Contains(';') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    private async Task<DraftDto> ToDtoAsync(ReturnDraft d, CancellationToken ct)
    {
        var lines = await db.ReturnLines.Where(l => l.DraftId == d.Id).OrderBy(l => l.SortOrder).ToListAsync(ct);
        var calc = await db.Calculations.FirstAsync(x => x.Id == d.CalculationId, ct);
        return new(d.Id, d.Version, d.Status.ToString(), d.FormVersion, d.CalculationId, calc.Status.ToString(), d.CreatedAt, d.ReviewedByUserId,
            d.ReviewedAt, d.ReviewComment, lines.Select(l => new DraftLineDto(l.Id, l.Concept, l.BoxNumber, l.Label, l.Value, l.RuleCode, l.Formula,
                Json.Deserialize<List<Guid>>(l.SourcesJson) ?? [], Json.Deserialize<List<string>>(l.WarningsJson) ?? [], l.SortOrder)).ToList(), Disclaimers);
    }
}

public sealed class FactCommandService(MiRentaDbContext db, CaseAccess access, CaseWorkflow workflow, FactService facts)
{
    public async Task<List<FactDto>> ListAsync(Guid caseId, CurrentUser user, bool includeHistory, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var q = db.Facts.Where(f => f.CaseId == c.Id);
        if (!includeHistory) q = q.Where(f => f.Status == FactStatus.Accepted);
        return (await q.OrderBy(f => f.Code).ThenByDescending(f => f.Version).ToListAsync(ct)).Select(FactService.ToDto).ToList();
    }

    /// <summary>Ingreso directo de un hecho. Los conceptos de contador solo los puede fijar un contador autorizado.</summary>
    public async Task<FactDto> SetAsync(Guid caseId, string code, CurrentUser user, SetFactRequest req, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        if (!CaseStateMachine.AcceptsChanges(c.Status)) throw AppException.Conflict("case_locked", "El expediente no admite cambios en su estado actual.");
        var isAccountant = await access.IsAccountantForCaseAsync(c, user, ct);
        if (FactCodes.AccountantOnly.Contains(code) && !isAccountant) throw AppException.Forbidden("Este dato solo lo puede ingresar un contador autorizado.");
        if (code == FactCodes.IncomeSources || code.StartsWith("unsupported.", StringComparison.Ordinal))
            throw AppException.BadRequest("use_assistant", "Este dato se responde desde las preguntas del asistente.");
        await facts.SetAsync(c, code, req.Amount, req.TextValue, isAccountant ? FactSource.Accountant : FactSource.UserAnswer, user.Id, null,
            req.Note?.Length > 500 ? req.Note[..500] : req.Note, ct);
        await workflow.SyncAsync(c, ct);
        await db.SaveChangesAsync(ct);
        var f = await db.Facts.Where(x => x.CaseId == c.Id && x.Code == code && x.Status == FactStatus.Accepted).FirstAsync(ct);
        return FactService.ToDto(f);
    }
}

public sealed class RuleService(MiRentaDbContext db, Audit audit, TimeProvider clock)
{
    public async Task<List<RuleDto>> ListAsync(int taxYear, CancellationToken ct) =>
        (await db.Rules.Where(r => r.TaxYear == taxYear).OrderBy(r => r.Code).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<RuleDto> ApproveAsync(string code, CurrentUser user, ApproveRuleRequest req, CancellationToken ct)
    {
        if (user.Role != AccountRole.TaxRulesAdmin) throw AppException.Forbidden();
        var r = await db.Rules.FirstOrDefaultAsync(x => x.Code == code && x.TaxYear == RuleCatalog2025.TaxYear && x.RuleSetVersion == RuleCatalog2025.RuleSetVersion, ct)
                ?? throw AppException.NotFound();
        if (r.Status == RuleStatus.NotImplemented) throw AppException.Conflict("not_implemented", "Una regla sin implementar no se puede aprobar.");
        if (string.IsNullOrWhiteSpace(req.Note)) throw AppException.BadRequest("note_required", "Indica qué fuente verificaste.");
        r.Status = RuleStatus.Approved;
        r.ApprovedByUserId = user.Id;
        r.ApprovedAt = clock.GetUtcNow().UtcDateTime;
        r.ApprovalNote = req.Note.Trim().Length > 1000 ? req.Note.Trim()[..1000] : req.Note.Trim();
        audit.Record("rule.approved", user.Id, null, new { code, r.RuleSetVersion });
        await db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    private static RuleDto ToDto(TaxRule r) => new(r.Code, r.TaxYear, r.RuleSetVersion, r.Description, r.LegalReference, r.SourceUrl,
        r.Status.ToString(), r.ApprovedByUserId, r.ApprovedAt, r.ApprovalNote, r.ParametersJson);
}
