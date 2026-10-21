namespace MiRenta.Domain.Returns;

/// <summary>Regla del catálogo tributario, versionada por año gravable y con su referencia normativa.</summary>
public sealed class TaxRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public int TaxYear { get; set; }
    public string RuleSetVersion { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = "{}";
    public string LegalReference { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public RuleStatus Status { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalNote { get; set; }
}

public enum RuleStatus
{
    /// <summary>Sin fuente verificable: los casos que dependan de ella se bloquean.</summary>
    NotImplemented = 0,
    /// <summary>Implementada con fuente oficial citada, falta la aprobación de un profesional tributario.</summary>
    PendingProfessionalApproval = 1,
    /// <summary>Aprobada por un profesional (identidad y fecha registradas).</summary>
    Approved = 2,
}

/// <summary>Ejecución del motor: versión de reglas, versión del motor, hechos usados y resultado.</summary>
public sealed class TaxCalculation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public string EngineVersion { get; set; } = string.Empty;
    public string RuleSetVersion { get; set; } = string.Empty;
    public string FactsSnapshotJson { get; set; } = "[]";
    public string ResultJson { get; set; } = "{}";
    public CalculationStatus Status { get; set; }
    public string BlockingReasonsJson { get; set; } = "[]";
    public Guid RequestedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum CalculationStatus
{
    /// <summary>No se pudo calcular (faltan hechos materiales o reglas).</summary>
    Blocked = 0,
    /// <summary>Calculado con reglas pendientes de aprobación profesional o datos sin confirmar.</summary>
    Preliminary = 1,
    /// <summary>Todas las reglas aprobadas, hechos aceptados y sin incidencias bloqueantes.</summary>
    ReadyForApproval = 2,
}

public sealed class ReturnDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Guid CalculationId { get; set; }
    public int Version { get; set; }
    public DraftStatus Status { get; set; } = DraftStatus.Draft;
    public string FormVersion { get; set; } = string.Empty;
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum DraftStatus { Draft = 0, ApprovedByAccountant = 1, RejectedByAccountant = 2, ConfirmedByTaxpayer = 3, Superseded = 4 }

/// <summary>Una línea (concepto/casilla) del borrador con su trazabilidad completa.</summary>
public sealed class ReturnLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DraftId { get; set; }
    public string Concept { get; set; } = string.Empty;
    /// <summary>Número de casilla del formulario oficial cuando el mapeo está verificado; null si no.</summary>
    public int? BoxNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string SourcesJson { get; set; } = "[]";
    public string RuleCode { get; set; } = string.Empty;
    public string Formula { get; set; } = string.Empty;
    public string ReviewStatus { get; set; } = "pending";
    public string WarningsJson { get; set; } = "[]";
    public int SortOrder { get; set; }
}

public sealed class DraftReviewNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DraftId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
