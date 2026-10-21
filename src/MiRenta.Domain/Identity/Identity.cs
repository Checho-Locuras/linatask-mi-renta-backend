namespace MiRenta.Domain.Identity;

/// <summary>
/// Cuenta de Mi renta, independiente de LinaTask. La clave es un Guid aleatorio; la cédula es un atributo protegido
/// y único, nunca un identificador público ni de autorización.
/// </summary>
public sealed class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Correo normalizado (minúsculas, sin espacios).</summary>
    public string Email { get; set; } = string.Empty;
    public DateTime? EmailVerifiedAt { get; set; }
    public IdentityDocumentType DocumentType { get; set; } = IdentityDocumentType.CedulaCiudadania;
    /// <summary>Número normalizado (solo dígitos o alfanumérico en mayúsculas según tipo).</summary>
    public string DocumentNumber { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public AccountRole Role { get; set; } = AccountRole.Taxpayer;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PasswordChangedAt { get; set; }
    /// <summary>Si es true, no se envían recordatorios (baja).</summary>
    public bool RemindersOptOut { get; set; }
    public bool IsDisabled { get; set; }

    public bool IsEmailVerified => EmailVerifiedAt is not null;
}

public enum AccountRole
{
    Taxpayer = 0,
    /// <summary>Contador: solo ve los expedientes que el contribuyente le autorizó.</summary>
    Accountant = 1,
    /// <summary>Administración de reglas tributarias (aprobación del catálogo).</summary>
    TaxRulesAdmin = 2,
}

public enum IdentityDocumentType
{
    CedulaCiudadania = 13,
    CedulaExtranjeria = 22,
    Pasaporte = 41,
    Nit = 31,
}

public enum EmailTokenPurpose
{
    VerifyEmail = 0,
    ResetPassword = 1,
}

/// <summary>Token de un solo uso enviado por correo. Solo se guarda su hash.</summary>
public sealed class EmailToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public EmailTokenPurpose Purpose { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Refresh token rotativo. Solo se guarda su hash.</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Aceptación de avisos de tratamiento de datos (sin más datos de los necesarios).</summary>
public sealed class ConsentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Null en consultas anónimas previas al registro.</summary>
    public Guid? UserId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string NoticeVersion { get; set; } = string.Empty;
    public DateTime AcceptedAt { get; set; } = DateTime.UtcNow;
}
