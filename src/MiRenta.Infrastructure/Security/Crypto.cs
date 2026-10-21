using System.Security.Cryptography;
using System.Text;

namespace MiRenta.Infrastructure.Security;

/// <summary>PBKDF2-SHA256 con sal aleatoria; formato: pbkdf2-sha256$iteraciones$sal$hash.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var it)) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch (FormatException) { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, it, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Hash ficticio para igualar el tiempo de respuesta cuando la cuenta no existe (anti-enumeración).</summary>
    public static readonly string Dummy = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
}

public static class TokenUtil
{
    /// <summary>Token aleatorio de 256 bits en base64url (se entrega una sola vez; se guarda solo su hash).</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Sha256Hex(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromBase64Url(string s)
    {
        try
        {
            var p = s.Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            return Convert.FromBase64String(p);
        }
        catch (FormatException) { return null; }
    }
}

/// <summary>
/// URL de descarga firmada y de corta duración, servida por la API (el bucket nunca es público). El token liga
/// documento, usuario y expiración con HMAC-SHA256; la API vuelve a verificar el permiso al servirlo.
/// </summary>
public sealed class DownloadTokenSigner(byte[] key)
{
    public string Create(Guid documentId, Guid userId, DateTime expiresAtUtc)
    {
        var payload = $"{documentId:N}.{userId:N}.{new DateTimeOffset(expiresAtUtc).ToUnixTimeSeconds()}";
        var sig = TokenUtil.Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
        return TokenUtil.Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + sig;
    }

    public (Guid DocumentId, Guid UserId)? Validate(string token, DateTime nowUtc)
    {
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        var payloadBytes = TokenUtil.FromBase64Url(parts[0]);
        var sig = TokenUtil.FromBase64Url(parts[1]);
        if (payloadBytes is null || sig is null) return null;
        var expected = HMACSHA256.HashData(key, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, sig)) return null;
        var fields = Encoding.UTF8.GetString(payloadBytes).Split('.');
        if (fields.Length != 3 || !Guid.TryParseExact(fields[0], "N", out var doc) || !Guid.TryParseExact(fields[1], "N", out var user)
            || !long.TryParse(fields[2], out var exp)) return null;
        if (DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime <= nowUtc) return null;
        return (doc, user);
    }
}
