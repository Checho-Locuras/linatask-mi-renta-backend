using MiRenta.Application.Common;
using MiRenta.Domain.Assistant;
using MiRenta.Domain.Cases;
using MiRenta.Domain.Documents;
using MiRenta.Domain.Facts;
using MiRenta.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MiRenta.Application.Assistant;

/// <summary>
/// Planificador determinista del expediente: decide qué tareas hacen falta según los hechos vigentes y deriva el
/// estado. La IA nunca decide transiciones; todas pasan por <see cref="CaseStateMachine"/>.
/// </summary>
public sealed class CaseWorkflow(MiRentaDbContext db, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task SyncAsync(TaxCase c, CancellationToken ct)
    {
        // Los hechos pendientes de guardar también cuentan (Local + BD).
        var facts = db.Facts.Local.Where(f => f.CaseId == c.Id && f.Status == FactStatus.Accepted)
            .Concat(await db.Facts.Where(f => f.CaseId == c.Id && f.Status == FactStatus.Accepted).ToListAsync(ct))
            .Distinct().Where(f => f.Status == FactStatus.Accepted)
            .GroupBy(f => f.Code).ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.Version).First());
        var tasks = await db.Tasks.Where(t => t.CaseId == c.Id).ToListAsync(ct);

        var sources = facts.TryGetValue(FactCodes.IncomeSources, out var src) ? (src.TextValue ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet() : null;
        var nonResident = facts.TryGetValue(FactCodes.FiscalResident, out var res) && res.TextValue == "no";
        var hasLabor = sources?.Contains("labor") == true;

        var needed = new List<QuestionDef>();
        foreach (var q in QuestionCatalog.Questions)
        {
            if (nonResident && q.Code != "q.fiscal_resident") continue;
            var include = q.Code switch
            {
                "q.labor_gross_income" or "q.labor_pension" or "q.labor_health" or "q.labor_withholding" or QuestionCatalog.LaborCertificateRequest => hasLabor,
                "q.fees_gross_income" => sources?.Contains("fees") == true,
                "q.fiscal_resident" or QuestionCatalog.IncomeSourcesCode => true,
                _ => sources is not null,
            };
            if (include) needed.Add(q);
        }

        foreach (var q in needed)
        {
            var satisfied = q.FactCode is not null
                ? facts.TryGetValue(q.FactCode, out var f) && !(hasLabor && f.Source == FactSource.Derived && q.FactCode.StartsWith("labor.", StringComparison.Ordinal))
                : q.Code == QuestionCatalog.IncomeSourcesCode ? sources is not null : false;
            var task = tasks.FirstOrDefault(t => t.Code == q.Code);
            if (task is null)
            {
                task = new AssistantTask
                {
                    CaseId = c.Id, Code = q.Code, Kind = q.Kind, Prompt = q.Prompt, Help = q.Help, AnswerType = q.AnswerType,
                    OptionsJson = q.Options is null ? null : Json.Serialize(q.Options), BlocksCalculation = q.Blocks,
                    SortOrder = q.Order, CreatedAt = Now, State = satisfied ? TaskState.Answered : TaskState.Open,
                    ClosedAt = satisfied ? Now : null,
                };
                db.Tasks.Add(task);
                tasks.Add(task);
            }
            else if (satisfied && task.State == TaskState.Open && q.Kind == TaskKind.Question) { task.State = TaskState.Answered; task.ClosedAt = Now; }
            else if (!satisfied && task.State is TaskState.Answered or TaskState.Cancelled && q.Kind == TaskKind.Question) { task.State = TaskState.Open; task.ClosedAt = null; }
            else if (task.State == TaskState.Cancelled) { task.State = TaskState.Open; task.ClosedAt = null; }
        }

        // Tareas de preguntas que ya no aplican (p. ej. cambió la respuesta de fuentes de ingreso).
        var neededCodes = needed.Select(q => q.Code).ToHashSet();
        foreach (var t in tasks.Where(t => t.State == TaskState.Open && QuestionCatalog.Find(t.Code) is not null && !neededCodes.Contains(t.Code)))
        {
            t.State = TaskState.Cancelled;
            t.ClosedAt = Now;
        }

        // Valores que solo puede ingresar un contador (reglas sin implementar).
        var questionsDone = needed.Where(q => q.Kind == TaskKind.Question).All(q => tasks.First(t => t.Code == q.Code).State == TaskState.Answered);
        var missingAccountant = MissingAccountantFacts(facts);
        var review = tasks.FirstOrDefault(t => t.Code == QuestionCatalog.AccountantInputs);
        if (questionsDone && !nonResident && missingAccountant.Count > 0)
        {
            var prompt = "Un contador autorizado debe ingresar: " + string.Join(", ", missingAccountant.Select(AccountantLabel)) + ".";
            if (review is null)
                db.Tasks.Add(new AssistantTask
                {
                    CaseId = c.Id, Code = QuestionCatalog.AccountantInputs, Kind = TaskKind.ProfessionalReview, Prompt = prompt,
                    Help = "Estos conceptos dependen de reglas que el asistente no calcula (anticipo, deducción adicional por dependientes). Autoriza a tu contador desde el expediente.",
                    AnswerType = AnswerType.Confirmation, BlocksCalculation = true, SortOrder = 90, CreatedAt = Now,
                    RelatedJson = Json.Serialize(missingAccountant),
                });
            else { review.Prompt = prompt; review.RelatedJson = Json.Serialize(missingAccountant); if (review.State != TaskState.Open) { review.State = TaskState.Open; review.ClosedAt = null; } }
        }
        else if (review is { State: TaskState.Open }) { review.State = missingAccountant.Count == 0 ? TaskState.Answered : TaskState.Cancelled; review.ClosedAt = Now; }

        await RecomputeStatusAsync(c, nonResident, facts, ct);
    }

    public static List<string> MissingAccountantFacts(IReadOnlyDictionary<string, TaxFact> facts)
    {
        var list = new List<string>();
        foreach (var code in new[] { FactCodes.AdvancePaymentNextYear, FactCodes.PreviousYearAdvance })
            if (!facts.TryGetValue(code, out var f) || f.Source != FactSource.Accountant) list.Add(code);
        if (facts.TryGetValue(FactCodes.DependentsCount, out var d) && d.Amount > 0
            && (!facts.TryGetValue(FactCodes.AccountantDependentsAdditional, out var a) || a.Source != FactSource.Accountant))
            list.Add(FactCodes.AccountantDependentsAdditional);
        return list;
    }

    private static string AccountantLabel(string code) => code switch
    {
        FactCodes.AdvancePaymentNextYear => "anticipo de renta del año siguiente",
        FactCodes.PreviousYearAdvance => "anticipo liquidado en la declaración anterior",
        FactCodes.AccountantDependentsAdditional => "deducción adicional por dependientes (art. 336)",
        _ => code,
    };

    private async Task RecomputeStatusAsync(TaxCase c, bool nonResident, IReadOnlyDictionary<string, TaxFact> facts, CancellationToken ct)
    {
        if (c.Status is CaseStatus.Completed) return;
        var openTasks = db.Tasks.Local.Where(t => t.CaseId == c.Id && t.State == TaskState.Open).ToList();
        var processing = await db.Documents.AnyAsync(d => d.CaseId == c.Id && d.DeletedAt == null
            && (d.Status == DocumentStatus.Stored || d.Status == DocumentStatus.Queued || d.Status == DocumentStatus.Processing), ct);
        var openRecon = await db.Reconciliations.AnyAsync(r => r.CaseId == c.Id && r.IsOpen, ct);
        // Se materializa para ver también los cambios aún no guardados (borradores invalidados en esta operación).
        var draftValid = (await db.Drafts.Where(d => d.CaseId == c.Id).ToListAsync(ct))
            .Any(d => d.Status is not (Domain.Returns.DraftStatus.Superseded or Domain.Returns.DraftStatus.RejectedByAccountant));

        CaseStatus target;
        string? blocked = null;
        if (nonResident) { target = CaseStatus.Blocked; blocked = "No residente fiscal: fuera del alcance de este asistente."; }
        else if (processing) target = CaseStatus.ProcessingDocument;
        else if (openTasks.Any(t => t.Kind == TaskKind.Confirmation) || openRecon) target = CaseStatus.AwaitingUserAnswer;
        else if (openTasks.Any(t => t.Kind == TaskKind.Question && t.BlocksCalculation))
            target = facts.Count == 0 ? CaseStatus.CollectingInformation : CaseStatus.AwaitingUserAnswer;
        else if (openTasks.Any(t => t.Kind == TaskKind.DocumentRequest && t.BlocksCalculation)) target = CaseStatus.AwaitingDocument;
        else if (openTasks.Any(t => t.Kind == TaskKind.ProfessionalReview)) target = CaseStatus.NeedsProfessionalReview;
        else if (c.Status is CaseStatus.ReadyForReview or CaseStatus.ApprovedForExport && draftValid) return;
        else target = CaseStatus.ReadyForCalculation;

        MoveTo(c, target);
        c.BlockedReason = blocked;
    }

    /// <summary>Aplica la transición si la máquina de estados la permite (pasando por CollectingInformation si hace falta).</summary>
    public void MoveTo(TaxCase c, CaseStatus target)
    {
        if (c.Status == target) return;
        if (!CaseStateMachine.CanTransition(c.Status, target) && CaseStateMachine.CanTransition(c.Status, CaseStatus.CollectingInformation)
            && CaseStateMachine.CanTransition(CaseStatus.CollectingInformation, target))
            c.Status = CaseStatus.CollectingInformation;
        CaseStateMachine.EnsureTransition(c, target);
        c.Status = target;
        c.UpdatedAt = Now;
    }
}
