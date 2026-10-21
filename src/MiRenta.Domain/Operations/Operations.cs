namespace MiRenta.Domain.Operations;

/// <summary>Trabajo asíncrono persistente (se reanuda tras un reinicio).</summary>
public sealed class BackgroundJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    /// <summary>Evita encolar dos veces el mismo trabajo (p. ej. procesar el mismo documento).</summary>
    public string? DedupKey { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTime NextRunAt { get; set; } = DateTime.UtcNow;
    public DateTime? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public enum JobStatus { Pending = 0, Running = 1, Succeeded = 2, Failed = 3 }

public static class JobKinds
{
    public const string ProcessDocument = "process-document";
    public const string SendEmail = "send-email";
}

/// <summary>Correo saliente. Nunca contiene contraseñas, documentos, cifras fiscales ni enlaces a archivos.</summary>
public sealed class OutboundEmail
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public Guid? CaseId { get; set; }
    public string ToAddress { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    /// <summary>Cuerpo para envío. Los correos con token se borran al enviarse (solo queda el metadato).</summary>
    public string? Body { get; set; }
    public string DedupKey { get; set; } = string.Empty;
    public EmailStatus Status { get; set; } = EmailStatus.Pending;
    public DateTime ScheduledFor { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum EmailStatus { Pending = 0, Sent = 1, Failed = 2, Skipped = 3 }

public static class EmailKinds
{
    public const string VerifyEmail = "verify-email";
    public const string PasswordReset = "password-reset";
    public const string IncompleteReturn = "reminder-incomplete";
    public const string PendingDocuments = "reminder-documents";
    public const string PendingQuestion = "reminder-question";
    public const string DocumentProcessed = "document-processed";
    public const string DraftReady = "draft-ready";

    public static bool IsReminder(string kind) => kind.StartsWith("reminder-", StringComparison.Ordinal)
        || kind is DocumentProcessed or DraftReady;
}
