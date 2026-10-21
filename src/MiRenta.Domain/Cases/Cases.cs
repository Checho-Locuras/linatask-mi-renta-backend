namespace MiRenta.Domain.Cases;

/// <summary>Contribuyente asociado a una cuenta (la propia u otra persona que la cuenta gestiona).</summary>
public sealed class Taxpayer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public Identity.IdentityDocumentType DocumentType { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Expediente tributario: un contribuyente, un año gravable y un tipo de declaración.</summary>
public sealed class TaxCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaxpayerId { get; set; }
    /// <summary>Dueño (usuario autenticado que lo creó). Toda autorización parte de aquí.</summary>
    public Guid OwnerUserId { get; set; }
    public int TaxYear { get; set; }
    public string ReturnType { get; set; } = "210";
    public CaseStatus Status { get; set; } = CaseStatus.NotStarted;
    public string? BlockedReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUserActivityAt { get; set; }
    /// <summary>Control de concurrencia optimista.</summary>
    public uint Version { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Taxpayer? Taxpayer { get; set; }
}

public enum CaseStatus
{
    NotStarted = 0,
    CollectingInformation = 1,
    AwaitingDocument = 2,
    ProcessingDocument = 3,
    AwaitingUserAnswer = 4,
    NeedsProfessionalReview = 5,
    ReadyForCalculation = 6,
    Calculating = 7,
    ReadyForReview = 8,
    ApprovedForExport = 9,
    Completed = 10,
    Blocked = 11,
}

/// <summary>Autorización explícita del contribuyente a un contador para un expediente.</summary>
public sealed class ClientAuthorization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Guid AccountantUserId { get; set; }
    public Guid GrantedByUserId { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
}

/// <summary>Auditoría de acciones sensibles. Nunca guarda documentos, contraseñas, tokens ni respuestas de seguridad.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CaseId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Incidencia de validación del expediente (dato faltante, contradicción, regla sin verificar…).</summary>
public sealed class ValidationIssue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public string Code { get; set; } = string.Empty;
    public IssueSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsOpen { get; set; } = true;
    public string? RelatedJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

public enum IssueSeverity
{
    Info = 0,
    Warning = 1,
    /// <summary>Impide calcular o aprobar.</summary>
    Blocking = 2,
}
