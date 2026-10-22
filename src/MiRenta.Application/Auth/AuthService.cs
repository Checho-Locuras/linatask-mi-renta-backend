using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using MiRenta.Application.Common;
using MiRenta.Domain.Identity;
using MiRenta.Domain.Operations;
using MiRenta.Infrastructure.Data;
using MiRenta.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MiRenta.Application.Auth;

public sealed record RegisterRequest(string Email, string DocumentNumber, string Password, bool AcceptPrivacyNotice,
    IdentityDocumentType DocumentType = IdentityDocumentType.CedulaCiudadania);
public sealed record LoginRequest(string Identifier, string Password);
public sealed record TokenResponse(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt, MeResponse User);
public sealed record MeResponse(Guid Id, string Email, string MaskedDocument, string Role, bool RemindersOptOut);
public sealed record GenericMessage(string Message);

/// <summary>
/// Identidad propia de Mi renta. Respuestas genéricas en registro, recuperación y login para no permitir enumerar
/// cuentas, correos ni cédulas.
/// </summary>
public sealed class AuthService(MiRentaDbContext db, IOptions<JwtOptions> jwt, IOptions<AppOptions> app, Audit audit, TimeProvider clock)
{
    public const string RegisterMessage = "Si los datos son válidos, te enviamos un correo para verificar tu cuenta.";
    public const string ForgotMessage = "Si existe una cuenta con ese correo, te enviamos instrucciones para restablecer la contraseña.";
    public const string InvalidCredentials = "Credenciales inválidas o cuenta bloqueada temporalmente.";
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan VerifyTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan ResetTtl = TimeSpan.FromMinutes(30);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static string? NormalizeDocument(IdentityDocumentType type, string raw)
    {
        var s = Regex.Replace(raw ?? "", @"[\s.\-]", "").ToUpperInvariant();
        return type switch
        {
            IdentityDocumentType.CedulaCiudadania => Regex.IsMatch(s, @"^\d{5,10}$") ? s.TrimStart('0') : null,
            IdentityDocumentType.CedulaExtranjeria => Regex.IsMatch(s, @"^\d{3,10}$") ? s : null,
            IdentityDocumentType.Pasaporte => Regex.IsMatch(s, @"^[A-Z0-9]{5,15}$") ? s : null,
            _ => null,
        };
    }

    public static string MaskDocument(string doc) => doc.Length <= 4 ? new string('•', doc.Length) : new string('•', doc.Length - 3) + doc[^3..];

    public static string? ValidatePassword(string password, string email, string document)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 10) return "La contraseña debe tener al menos 10 caracteres.";
        if (password.Length > 128) return "La contraseña es demasiado larga.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) return "La contraseña debe combinar letras y números.";
        var lower = password.ToLowerInvariant();
        if (lower.Contains(document.ToLowerInvariant()) || lower.Contains(email.Split('@')[0])) return "La contraseña no puede contener tu cédula ni tu correo.";
        return null;
    }

    public async Task<GenericMessage> RegisterAsync(RegisterRequest req, CancellationToken ct)
    {
        if (!req.AcceptPrivacyNotice) throw AppException.BadRequest("consent_required", "Debes aceptar el aviso de privacidad.");
        var email = NormalizeEmail(req.Email ?? "");
        if (!IsValidEmail(email)) throw AppException.BadRequest("invalid_email", "Correo inválido.");
        var doc = NormalizeDocument(req.DocumentType, req.DocumentNumber) ?? throw AppException.BadRequest("invalid_document", "Número de documento inválido.");
        if (ValidatePassword(req.Password, email, doc) is { } pwError) throw AppException.BadRequest("weak_password", pwError);

        var byEmail = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var byDoc = await db.Users.FirstOrDefaultAsync(u => u.DocumentType == req.DocumentType && u.DocumentNumber == doc, ct);

        if (byEmail is null && byDoc is null)
        {
            var user = new UserAccount
            {
                Email = email, DocumentType = req.DocumentType, DocumentNumber = doc,
                PasswordHash = PasswordHasher.Hash(req.Password), CreatedAt = Now,
            };
            db.Users.Add(user);
            db.Consents.Add(new ConsentRecord { UserId = user.Id, Purpose = "privacy-notice", NoticeVersion = app.Value.PrivacyNoticeVersion, AcceptedAt = Now });
            await QueueVerificationAsync(user, ct);
            audit.Record("auth.registered", user.Id, null);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* carrera con otro registro idéntico: misma respuesta genérica */ }
            return new(RegisterMessage);
        }

        // Ya existe: no se crea ni se asocia nada. Se avisa al titular existente (no al solicitante).
        var owner = byEmail ?? byDoc!;
        _ = PasswordHasher.Verify(req.Password, PasswordHasher.Dummy); // iguala el costo de tiempo
        var bucket = Now.ToString("yyyyMMddHH");
        await EmailOutbox.QueueAsync(db, owner.Id, null, owner.Email, "account-exists-notice",
            "Intento de registro en Mi renta",
            "Alguien intentó crear una cuenta de Mi renta con tu correo o tu documento. Si fuiste tú, inicia sesión o " +
            "restablece tu contraseña. Si no fuiste tú, puedes ignorar este mensaje; no se creó ninguna cuenta nueva.",
            $"account-exists:{owner.Id}:{bucket}", ct);
        audit.Record("auth.register_conflict", owner.Id, null, new { sameEmail = byEmail is not null, sameDocument = byDoc is not null });
        await db.SaveChangesAsync(ct);
        return new(RegisterMessage);
    }

    public async Task<GenericMessage> ResendVerificationAsync(string email, CancellationToken ct)
    {
        var e = NormalizeEmail(email ?? "");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == e, ct);
        if (user is { EmailVerifiedAt: null, IsDisabled: false }) { await QueueVerificationAsync(user, ct); await db.SaveChangesAsync(ct); }
        return new(RegisterMessage);
    }

    public async Task VerifyEmailAsync(string token, CancellationToken ct)
    {
        var t = await ConsumeTokenAsync(token, EmailTokenPurpose.VerifyEmail, ct)
            ?? throw AppException.BadRequest("invalid_token", "El enlace no es válido o expiró.");
        var user = await db.Users.FirstAsync(u => u.Id == t.UserId, ct);
        user.EmailVerifiedAt ??= Now;
        if (app.Value.AccountantEmails.Any(x => NormalizeEmail(x) == user.Email)) user.Role = AccountRole.Accountant;
        if (app.Value.RulesAdminEmails.Any(x => NormalizeEmail(x) == user.Email)) user.Role = AccountRole.TaxRulesAdmin;
        audit.Record("auth.email_verified", user.Id, null);
        await db.SaveChangesAsync(ct);
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        var id = (req.Identifier ?? "").Trim();
        UserAccount? user;
        if (id.Contains('@')) { var e = NormalizeEmail(id); user = await db.Users.FirstOrDefaultAsync(u => u.Email == e, ct); }
        else
        {
            var doc = NormalizeDocument(IdentityDocumentType.CedulaCiudadania, id) ?? NormalizeDocument(IdentityDocumentType.Pasaporte, id);
            // Un mismo número con dos tipos de documento distintos es ambiguo: se rechaza igual que credenciales inválidas.
            var matches = doc is null ? [] : await db.Users.Where(u => u.DocumentNumber == doc).OrderBy(u => u.Id).Take(2).ToListAsync(ct);
            user = matches.Count == 1 ? matches[0] : null;
        }

        if (user is null) { _ = PasswordHasher.Verify(req.Password ?? "", PasswordHasher.Dummy); throw Invalid(); }
        if (user.IsDisabled || user.LockoutUntil > Now) { _ = PasswordHasher.Verify(req.Password ?? "", PasswordHasher.Dummy); throw Invalid(); }
        if (!PasswordHasher.Verify(req.Password ?? "", user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedLogins) { user.LockoutUntil = Now.Add(Lockout); user.FailedLoginCount = 0; audit.Record("auth.locked_out", user.Id, null); }
            await db.SaveChangesAsync(ct);
            throw Invalid();
        }
        if (!user.IsEmailVerified)
            throw new AppException(403, "email_not_verified", "Verifica tu correo antes de iniciar sesión. Puedes pedir un nuevo enlace.");

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        audit.Record("auth.login", user.Id, null);
        return await IssueTokensAsync(user, ct);
    }

    public async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var hash = TokenUtil.Sha256Hex(refreshToken ?? "");
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct) ?? throw Unauthorized();
        if (stored.RevokedAt is not null)
        {
            // Reutilización de un token ya rotado: posible robo. Se revocan todas las sesiones.
            await db.RefreshTokens.Where(t => t.UserId == stored.UserId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, Now), ct);
            audit.Record("auth.refresh_reuse_detected", stored.UserId, null);
            await db.SaveChangesAsync(ct);
            throw Unauthorized();
        }
        if (stored.ExpiresAt <= Now) throw Unauthorized();
        var user = await db.Users.FirstAsync(u => u.Id == stored.UserId, ct);
        if (user.IsDisabled) throw Unauthorized();
        stored.RevokedAt = Now;
        var tokens = await IssueTokensAsync(user, ct, stored);
        return tokens;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = TokenUtil.Sha256Hex(refreshToken ?? "");
        await db.RefreshTokens.Where(t => t.TokenHash == hash && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, Now), ct);
    }

    public async Task<GenericMessage> ForgotPasswordAsync(string email, CancellationToken ct)
    {
        var e = NormalizeEmail(email ?? "");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == e, ct);
        if (user is { IsDisabled: false })
        {
            // Máximo 3 solicitudes por hora por cuenta.
            var recent = await db.EmailTokens.CountAsync(t => t.UserId == user.Id && t.Purpose == EmailTokenPurpose.ResetPassword && t.CreatedAt > Now.AddHours(-1), ct);
            if (recent < 3)
            {
                await db.EmailTokens.Where(t => t.UserId == user.Id && t.Purpose == EmailTokenPurpose.ResetPassword && t.UsedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, Now), ct);
                var token = NewEmailToken(user.Id, EmailTokenPurpose.ResetPassword, ResetTtl);
                await EmailOutbox.QueueAsync(db, user.Id, null, user.Email, EmailKinds.PasswordReset, "Restablece tu contraseña de Mi renta",
                    $"Para crear una contraseña nueva abre este enlace (válido 30 minutos, un solo uso):\n{app.Value.FrontendBaseUrl}/restablecer?token={token}\n\n" +
                    "Si no lo solicitaste, ignora este correo. Nunca te pediremos tu contraseña de la DIAN.",
                    $"reset:{user.Id}:{Guid.NewGuid():N}", ct);
                audit.Record("auth.reset_requested", user.Id, null);
                await db.SaveChangesAsync(ct);
            }
        }
        else _ = PasswordHasher.Verify("x", PasswordHasher.Dummy);
        return new(ForgotMessage);
    }

    public async Task ResetPasswordAsync(string token, string newPassword, CancellationToken ct)
    {
        var hash = TokenUtil.Sha256Hex(token ?? "");
        var t = await db.EmailTokens.FirstOrDefaultAsync(x => x.TokenHash == hash && x.Purpose == EmailTokenPurpose.ResetPassword, ct);
        if (t is null || t.UsedAt is not null || t.ExpiresAt <= Now || t.FailedAttempts >= 5)
            throw AppException.BadRequest("invalid_token", "El enlace no es válido o expiró.");
        var user = await db.Users.FirstAsync(u => u.Id == t.UserId, ct);
        if (ValidatePassword(newPassword, user.Email, user.DocumentNumber) is { } err)
        {
            t.FailedAttempts++;
            await db.SaveChangesAsync(ct);
            throw AppException.BadRequest("weak_password", err);
        }
        t.UsedAt = Now;
        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.PasswordChangedAt = Now;
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.EmailVerifiedAt ??= Now; // demostró control del correo
        await db.RefreshTokens.Where(r => r.UserId == user.Id && r.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, Now), ct);
        audit.Record("auth.password_reset", user.Id, null);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MeResponse> MeAsync(Guid userId, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct) ?? throw Unauthorized();
        return ToMe(u);
    }

    public static MeResponse ToMe(UserAccount u) => new(u.Id, u.Email, MaskDocument(u.DocumentNumber), u.Role.ToString(), u.RemindersOptOut);

    // ----------------------------------------------------------------------------------------------------------

    private async Task QueueVerificationAsync(UserAccount user, CancellationToken ct)
    {
        var recent = await db.EmailTokens.CountAsync(t => t.UserId == user.Id && t.Purpose == EmailTokenPurpose.VerifyEmail && t.CreatedAt > Now.AddHours(-1), ct);
        if (recent >= 3) return;
        var token = NewEmailToken(user.Id, EmailTokenPurpose.VerifyEmail, VerifyTtl);
        await EmailOutbox.QueueAsync(db, user.Id, null, user.Email, EmailKinds.VerifyEmail, "Verifica tu correo en Mi renta",
            $"Para activar tu cuenta abre este enlace (válido 24 horas, un solo uso):\n{app.Value.FrontendBaseUrl}/verificar-correo?token={token}\n\n" +
            "Si no creaste esta cuenta, ignora este correo.", $"verify:{user.Id}:{Guid.NewGuid():N}", ct);
    }

    private string NewEmailToken(Guid userId, EmailTokenPurpose purpose, TimeSpan ttl)
    {
        var token = TokenUtil.NewToken();
        db.EmailTokens.Add(new EmailToken { UserId = userId, Purpose = purpose, TokenHash = TokenUtil.Sha256Hex(token), ExpiresAt = Now.Add(ttl), CreatedAt = Now });
        return token;
    }

    private async Task<EmailToken?> ConsumeTokenAsync(string token, EmailTokenPurpose purpose, CancellationToken ct)
    {
        var hash = TokenUtil.Sha256Hex(token ?? "");
        var t = await db.EmailTokens.FirstOrDefaultAsync(x => x.TokenHash == hash && x.Purpose == purpose, ct);
        if (t is null || t.UsedAt is not null || t.ExpiresAt <= Now) return null;
        t.UsedAt = Now;
        return t;
    }

    private async Task<TokenResponse> IssueTokensAsync(UserAccount user, CancellationToken ct, RefreshToken? replaced = null)
    {
        var o = jwt.Value;
        var accessExp = Now.AddMinutes(o.AccessTokenMinutes);
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey)), SecurityAlgorithms.HmacSha256);
        var jwtToken = new JwtSecurityToken(o.Issuer, o.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim("role", user.Role.ToString()),
             new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))],
            notBefore: Now, expires: accessExp, signingCredentials: creds);
        var access = new JwtSecurityTokenHandler().WriteToken(jwtToken);

        var refresh = TokenUtil.NewToken();
        var rt = new RefreshToken { UserId = user.Id, TokenHash = TokenUtil.Sha256Hex(refresh), ExpiresAt = Now.AddDays(o.RefreshTokenDays), CreatedAt = Now };
        db.RefreshTokens.Add(rt);
        if (replaced is not null) replaced.ReplacedById = rt.Id;
        await db.SaveChangesAsync(ct);
        return new TokenResponse(access, accessExp, refresh, rt.ExpiresAt, ToMe(user));
    }

    private static bool IsValidEmail(string e)
    {
        if (e.Length is < 3 or > 254) return false;
        try { return new MailAddress(e).Address == e && e.Contains('.', StringComparison.Ordinal); }
        catch (FormatException) { return false; }
    }

    private static AppException Invalid() => new(401, "invalid_credentials", InvalidCredentials);
    private static AppException Unauthorized() => new(401, "unauthorized", "Sesión inválida o expirada.");
}
