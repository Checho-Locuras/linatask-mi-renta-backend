namespace MiRenta.Application.Common;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "mi-renta";
    public string Audience { get; set; } = "mi-renta-web";
    /// <summary>Clave PROPIA de Mi renta (no se comparte con LinaTask). Mínimo 32 bytes.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public sealed class AppOptions
{
    public const string Section = "App";
    /// <summary>URL base del frontend (para enlaces de verificación y restablecimiento).</summary>
    public string FrontendBaseUrl { get; set; } = "http://localhost:4200";
    public string[] AllowedOrigins { get; set; } = [];
    /// <summary>Correos que al verificarse obtienen rol de contador (arranque del MVP).</summary>
    public string[] AccountantEmails { get; set; } = [];
    public string[] RulesAdminEmails { get; set; } = [];
    public string PrivacyNoticeVersion { get; set; } = "2026-10-v1";
    /// <summary>Clave HMAC para URLs de descarga y de baja de recordatorios (distinta de la del JWT).</summary>
    public string UrlSigningKey { get; set; } = string.Empty;
    public int DownloadUrlMinutes { get; set; } = 5;
    public bool RunWorkers { get; set; } = true;
}
