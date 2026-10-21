namespace MiRenta.Domain.Assistant;

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ConversationMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    /// <summary>Clave del cliente para no duplicar mensajes reenviados.</summary>
    public string? IdempotencyKey { get; set; }
    public Guid? AuthorUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum MessageRole { User = 0, Assistant = 1, System = 2 }

/// <summary>Pregunta, solicitud de documento, confirmación o revisión pendiente.</summary>
public sealed class AssistantTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public TaskKind Kind { get; set; }
    /// <summary>Código estable (p. ej. q.residence): evita crear dos veces la misma tarea.</summary>
    public string Code { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string? Help { get; set; }
    public AnswerType AnswerType { get; set; }
    public string? OptionsJson { get; set; }
    /// <summary>Si es true, el expediente no puede pasar a cálculo mientras esté abierta.</summary>
    public bool BlocksCalculation { get; set; } = true;
    public TaskState State { get; set; } = TaskState.Open;
    public string? RelatedJson { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
}

public enum TaskKind { Question = 0, DocumentRequest = 1, Confirmation = 2, ProfessionalReview = 3 }

public enum AnswerType { SingleChoice = 0, MultiChoice = 1, Amount = 2, Integer = 3, Text = 4, Document = 5, Confirmation = 6 }

public enum TaskState { Open = 0, Answered = 1, Deferred = 2, Cancelled = 3 }

public sealed class UserResponse
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public Guid UserId { get; set; }
    public string ValueJson { get; set; } = "null";
    public string? IdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
