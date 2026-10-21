using MiRenta.Application.Assistant;
using MiRenta.Application.Common;
using MiRenta.Domain.Assistant;
using MiRenta.Domain.Cases;
using MiRenta.Domain.Documents;
using MiRenta.Domain.Engine;
using MiRenta.Domain.Identity;
using MiRenta.Domain.Returns;
using MiRenta.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MiRenta.Application.Cases;

public sealed record CaseDto(Guid Id, int TaxYear, string ReturnType, string Status, string? BlockedReason, DateTime CreatedAt,
    DateTime UpdatedAt, bool IsOwner, string MaskedTaxpayerDocument);
public sealed record ProgressDto(string Status, string? BlockedReason, int QuestionsAnswered, int QuestionsTotal, int DocumentsTotal,
    int DocumentsProcessing, int OpenReconciliations, int OpenConfirmations, bool NeedsAccountant, bool CanCalculate,
    string? LatestCalculationStatus, Guid? CurrentDraftId, string? CurrentDraftStatus, IReadOnlyList<TaskDto> NextTasks, int PercentComplete);
public sealed record TaskDto(Guid Id, string Code, string Kind, string Prompt, string? Help, string AnswerType,
    IReadOnlyList<ChoiceOption>? Options, string State, bool BlocksCalculation, object? Related);
public sealed record AccountantGrantRequest(string AccountantEmail);

public sealed class CaseService(MiRentaDbContext db, CaseAccess access, CaseWorkflow workflow, Audit audit, TimeProvider clock)
{
    public async Task<CaseDto> CreateAsync(CurrentUser user, int taxYear, CancellationToken ct)
    {
        if (taxYear != RuleCatalog2025.TaxYear) throw AppException.BadRequest("unsupported_year", "Solo está disponible el año gravable 2025.");
        var account = await db.Users.FirstAsync(u => u.Id == user.Id, ct);
        var taxpayer = await db.Taxpayers.FirstOrDefaultAsync(t => t.OwnerUserId == user.Id && t.DocumentType == account.DocumentType
                                                                  && t.DocumentNumber == account.DocumentNumber, ct);
        if (taxpayer is null)
        {
            taxpayer = new Taxpayer { OwnerUserId = user.Id, DocumentType = account.DocumentType, DocumentNumber = account.DocumentNumber };
            db.Taxpayers.Add(taxpayer);
        }
        var existing = await db.Cases.FirstOrDefaultAsync(c => c.TaxpayerId == taxpayer.Id && c.TaxYear == taxYear && c.ReturnType == "210" && c.DeletedAt == null, ct);
        if (existing is not null) return ToDto(existing, user, account.DocumentNumber);

        var now = clock.GetUtcNow().UtcDateTime;
        var c = new TaxCase { TaxpayerId = taxpayer.Id, OwnerUserId = user.Id, TaxYear = taxYear, CreatedAt = now, UpdatedAt = now };
        db.Cases.Add(c);
        db.Conversations.Add(new Conversation { CaseId = c.Id, CreatedAt = now });
        db.Messages.Add(new ConversationMessage
        {
            ConversationId = (db.Conversations.Local.First(x => x.CaseId == c.Id)).Id, Role = MessageRole.Assistant, CreatedAt = now,
            Content = "Hola. Te acompañaré a preparar el borrador de tu declaración de renta 2025 (formulario 210). Te haré preguntas " +
                      "cortas y podrás subir documentos. Al final, un contador debe revisarlo; la presentación la haces tú en el portal de la DIAN. " +
                      "Nunca me compartas tu contraseña de la DIAN.",
        });
        workflow.MoveTo(c, CaseStatus.CollectingInformation);
        await workflow.SyncAsync(c, ct);
        audit.Record("case.created", user.Id, c.Id, new { taxYear });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var again = await db.Cases.FirstAsync(x => x.OwnerUserId == user.Id && x.TaxYear == taxYear && x.DeletedAt == null, ct);
            return ToDto(again, user, account.DocumentNumber);
        }
        return ToDto(c, user, account.DocumentNumber);
    }

    public async Task<List<CaseDto>> ListAsync(CurrentUser user, CancellationToken ct)
    {
        var authorized = db.ClientAuthorizations.Where(a => a.AccountantUserId == user.Id && a.RevokedAt == null).Select(a => a.CaseId);
        var cases = await db.Cases.Include(c => c.Taxpayer)
            .Where(c => c.DeletedAt == null && (c.OwnerUserId == user.Id || (user.Role == AccountRole.Accountant && authorized.Contains(c.Id))))
            .OrderByDescending(c => c.UpdatedAt).ToListAsync(ct);
        return cases.Select(c => ToDto(c, user, c.Taxpayer!.DocumentNumber)).ToList();
    }

    public async Task<CaseDto> GetAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var tp = await db.Taxpayers.FirstAsync(t => t.Id == c.TaxpayerId, ct);
        return ToDto(c, user, tp.DocumentNumber);
    }

    public async Task<ProgressDto> ProgressAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var tasks = await db.Tasks.Where(t => t.CaseId == c.Id && t.State != TaskState.Cancelled).OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt).ToListAsync(ct);
        var questions = tasks.Where(t => t.Kind == TaskKind.Question).ToList();
        var docs = await db.Documents.Where(d => d.CaseId == c.Id && d.DeletedAt == null).Select(d => d.Status).ToListAsync(ct);
        var openRecon = await db.Reconciliations.CountAsync(r => r.CaseId == c.Id && r.IsOpen, ct);
        var latest = await db.Calculations.Where(x => x.CaseId == c.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var draft = await db.Drafts.Where(d => d.CaseId == c.Id && d.Status != DraftStatus.Superseded).OrderByDescending(d => d.Version).FirstOrDefaultAsync(ct);
        var open = tasks.Where(t => t.State == TaskState.Open).ToList();
        var next = open.Where(t => t.Kind is TaskKind.Confirmation).Concat(open.Where(t => t.Kind is TaskKind.Question).Take(1))
            .Concat(open.Where(t => t.Kind is TaskKind.DocumentRequest or TaskKind.ProfessionalReview)).Select(ToTaskDto).ToList();
        var answered = questions.Count(t => t.State == TaskState.Answered);
        var percent = questions.Count == 0 ? 0 : (int)Math.Round(100.0 * answered / questions.Count);
        if (c.Status is CaseStatus.ReadyForReview) percent = Math.Max(percent, 90);
        if (c.Status is CaseStatus.ApprovedForExport or CaseStatus.Completed) percent = 100;
        return new ProgressDto(c.Status.ToString(), c.BlockedReason, answered, questions.Count, docs.Count,
            docs.Count(s => s is DocumentStatus.Stored or DocumentStatus.Queued or DocumentStatus.Processing), openRecon,
            open.Count(t => t.Kind == TaskKind.Confirmation), open.Any(t => t.Kind == TaskKind.ProfessionalReview),
            CanCalculate(c, open, docs, openRecon), latest?.Status.ToString(), draft?.Id, draft?.Status.ToString(), next, percent);
    }

    /// <summary>Se puede calcular (o previsualizar) cuando no quedan preguntas, confirmaciones, conciliaciones ni documentos en proceso.</summary>
    public static bool CanCalculate(TaxCase c, IReadOnlyList<AssistantTask> open, IReadOnlyList<DocumentStatus> docs, int openRecon) =>
        c.Status is CaseStatus.ReadyForCalculation or CaseStatus.NeedsProfessionalReview or CaseStatus.ReadyForReview
        && !open.Any(t => t.BlocksCalculation && t.Kind != TaskKind.ProfessionalReview)
        && openRecon == 0 && !docs.Any(s => s is DocumentStatus.Stored or DocumentStatus.Queued or DocumentStatus.Processing);

    public async Task GrantAccountantAsync(Guid caseId, CurrentUser user, AccountantGrantRequest req, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        if (c.OwnerUserId != user.Id) throw AppException.Forbidden();
        var email = Auth.AuthService.NormalizeEmail(req.AccountantEmail ?? "");
        var accountant = await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.Role == AccountRole.Accountant && !u.IsDisabled, ct);
        // Respuesta idéntica exista o no (no se revela quién es contador).
        if (accountant is null || accountant.Id == user.Id) { audit.Record("case.accountant_grant_unknown", user.Id, c.Id); await db.SaveChangesAsync(ct); return; }
        if (!await access.IsAuthorizedAccountantAsync(c.Id, accountant.Id, ct))
        {
            db.ClientAuthorizations.Add(new ClientAuthorization { CaseId = c.Id, AccountantUserId = accountant.Id, GrantedByUserId = user.Id, GrantedAt = clock.GetUtcNow().UtcDateTime });
            audit.Record("case.accountant_granted", user.Id, c.Id, new { accountantUserId = accountant.Id });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAccountantsAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        if (c.OwnerUserId != user.Id) throw AppException.Forbidden();
        var now = clock.GetUtcNow().UtcDateTime;
        await db.ClientAuthorizations.Where(a => a.CaseId == c.Id && a.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.RevokedAt, now), ct);
        audit.Record("case.accountants_revoked", user.Id, c.Id);
        await db.SaveChangesAsync(ct);
    }

    public static TaskDto ToTaskDto(AssistantTask t) => new(t.Id, t.Code, t.Kind.ToString(), t.Prompt, t.Help, t.AnswerType.ToString(),
        t.OptionsJson is null ? null : Json.Deserialize<List<ChoiceOption>>(t.OptionsJson), t.State.ToString(), t.BlocksCalculation,
        t.RelatedJson is null ? null : Json.Deserialize<System.Text.Json.JsonElement>(t.RelatedJson));

    private static CaseDto ToDto(TaxCase c, CurrentUser user, string document) => new(c.Id, c.TaxYear, c.ReturnType, c.Status.ToString(),
        c.BlockedReason, c.CreatedAt, c.UpdatedAt, c.OwnerUserId == user.Id, Auth.AuthService.MaskDocument(document));
}
