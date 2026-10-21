namespace MiRenta.Domain.Documents;

/// <summary>Documento tributario guardado en el bucket privado de R2. Original inmutable.</summary>
public sealed class TaxDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Guid UploadedByUserId { get; set; }
    /// <summary>Nombre original saneado (solo se muestra; nunca forma parte de la clave de R2).</summary>
    public string OriginalFileName { get; set; } = string.Empty;
    /// <summary>Tipo real detectado por firma del archivo (no el declarado por el cliente).</summary>
    public string ContentType { get; set; } = string.Empty;
    public DocumentFileFormat Format { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>SHA-256 en hexadecimal; con CaseId evita duplicados.</summary>
    public string Sha256 { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public DocumentKind Kind { get; set; } = DocumentKind.Unknown;
    public DocumentStatus Status { get; set; } = DocumentStatus.Received;
    public int ProcessingAttempts { get; set; }
    public string? LastError { get; set; }
    /// <summary>Solicitud del asistente que este documento atiende (opcional).</summary>
    public Guid? RequestedByTaskId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public enum DocumentFileFormat { Pdf = 0, Xlsx = 1, Csv = 2, Jpeg = 3, Png = 4 }

public enum DocumentKind
{
    Unknown = 0,
    /// <summary>Certificado de ingresos y retenciones por rentas de trabajo (formulario 220).</summary>
    LaborIncomeCertificate = 1,
    BankStatement = 2,
    BankBalanceCertificate = 3,
    WithholdingCertificate = 4,
    DebtCertificate = 5,
    IdentityDocument = 6,
    PropertyDocument = 7,
    VehicleDocument = 8,
    Other = 99,
}

public enum DocumentStatus
{
    Received = 0,
    Stored = 1,
    Queued = 2,
    Processing = 3,
    /// <summary>Extracción lista; puede requerir confirmación del usuario.</summary>
    Processed = 4,
    /// <summary>No se pudo extraer automáticamente (p. ej. imagen sin OCR): requiere ingreso manual.</summary>
    NeedsManualEntry = 5,
    Failed = 6,
    Deleted = 7,
}

/// <summary>Resultado de una extracción (versionada: reintentar crea una versión nueva, no borra la anterior).</summary>
public sealed class DocumentExtraction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int Version { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? ExtractorVersion { get; set; }
    public int? PageCount { get; set; }
    public int? RowCount { get; set; }
    /// <summary>Clasificación propuesta (no confirmada).</summary>
    public DocumentKind ProposedKind { get; set; }
    public decimal Confidence { get; set; }
    public string? StructuredJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Dato extraído de un documento con su ubicación (página, fila, celda). Pasa por Proposed → Confirmed / Rejected; la
/// corrección del usuario crea un valor confirmado sin borrar el original.
/// </summary>
public sealed class EvidenceItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid ExtractionId { get; set; }
    public string Field { get; set; } = string.Empty;
    public string? RawText { get; set; }
    public decimal? ExtractedAmount { get; set; }
    public string? ExtractedValue { get; set; }
    public decimal? ConfirmedAmount { get; set; }
    public string Locator { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public EvidenceStatus Status { get; set; } = EvidenceStatus.Proposed;
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum EvidenceStatus { Proposed = 0, Confirmed = 1, Corrected = 2, Rejected = 3 }
