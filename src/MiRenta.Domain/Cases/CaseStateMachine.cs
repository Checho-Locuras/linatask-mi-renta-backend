namespace MiRenta.Domain.Cases;

/// <summary>
/// Transiciones permitidas del expediente. El asistente (o la IA) solo puede PROPONER un estado; el servicio valida
/// la transición aquí y las precondiciones de negocio (tareas abiertas, documentos en proceso, incidencias) antes
/// de aplicarla.
/// </summary>
public static class CaseStateMachine
{
    private static readonly Dictionary<CaseStatus, CaseStatus[]> Allowed = new()
    {
        [CaseStatus.NotStarted] = [CaseStatus.CollectingInformation, CaseStatus.Blocked],
        [CaseStatus.CollectingInformation] = [CaseStatus.AwaitingDocument, CaseStatus.AwaitingUserAnswer, CaseStatus.ProcessingDocument,
            CaseStatus.NeedsProfessionalReview, CaseStatus.ReadyForCalculation, CaseStatus.Blocked],
        [CaseStatus.AwaitingDocument] = [CaseStatus.ProcessingDocument, CaseStatus.CollectingInformation, CaseStatus.AwaitingUserAnswer,
            CaseStatus.ReadyForCalculation, CaseStatus.NeedsProfessionalReview, CaseStatus.Blocked],
        [CaseStatus.ProcessingDocument] = [CaseStatus.AwaitingUserAnswer, CaseStatus.AwaitingDocument, CaseStatus.CollectingInformation,
            CaseStatus.ReadyForCalculation, CaseStatus.NeedsProfessionalReview, CaseStatus.Blocked],
        [CaseStatus.AwaitingUserAnswer] = [CaseStatus.CollectingInformation, CaseStatus.AwaitingDocument, CaseStatus.ProcessingDocument,
            CaseStatus.ReadyForCalculation, CaseStatus.NeedsProfessionalReview, CaseStatus.Blocked],
        [CaseStatus.NeedsProfessionalReview] = [CaseStatus.CollectingInformation, CaseStatus.AwaitingUserAnswer, CaseStatus.AwaitingDocument,
            CaseStatus.ReadyForCalculation, CaseStatus.ReadyForReview, CaseStatus.Blocked],
        [CaseStatus.ReadyForCalculation] = [CaseStatus.Calculating, CaseStatus.CollectingInformation, CaseStatus.AwaitingUserAnswer,
            CaseStatus.AwaitingDocument, CaseStatus.ProcessingDocument, CaseStatus.Blocked],
        [CaseStatus.Calculating] = [CaseStatus.ReadyForReview, CaseStatus.NeedsProfessionalReview, CaseStatus.CollectingInformation,
            CaseStatus.AwaitingUserAnswer, CaseStatus.Blocked],
        [CaseStatus.ReadyForReview] = [CaseStatus.ApprovedForExport, CaseStatus.NeedsProfessionalReview, CaseStatus.CollectingInformation,
            CaseStatus.AwaitingUserAnswer, CaseStatus.AwaitingDocument, CaseStatus.ProcessingDocument, CaseStatus.ReadyForCalculation, CaseStatus.Blocked],
        [CaseStatus.ApprovedForExport] = [CaseStatus.Completed, CaseStatus.ReadyForReview, CaseStatus.CollectingInformation],
        [CaseStatus.Completed] = [],
        [CaseStatus.Blocked] = [CaseStatus.CollectingInformation, CaseStatus.NeedsProfessionalReview],
    };

    public static bool CanTransition(CaseStatus from, CaseStatus to) =>
        from == to || (Allowed.TryGetValue(from, out var next) && next.Contains(to));

    /// <summary>Estados en los que el expediente admite nuevos documentos, respuestas y hechos.</summary>
    public static bool AcceptsChanges(CaseStatus status) =>
        status is not (CaseStatus.Completed or CaseStatus.ApprovedForExport or CaseStatus.Calculating);

    public static void EnsureTransition(TaxCase taxCase, CaseStatus to)
    {
        if (!CanTransition(taxCase.Status, to))
            throw new InvalidCaseTransitionException(taxCase.Status, to);
    }
}

public sealed class InvalidCaseTransitionException(CaseStatus from, CaseStatus to)
    : InvalidOperationException($"Transición no permitida: {from} → {to}.")
{
    public CaseStatus From { get; } = from;
    public CaseStatus To { get; } = to;
}
