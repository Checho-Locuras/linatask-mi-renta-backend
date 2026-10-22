using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MiRenta.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MiRenta.Tests.Integration;

public class AuthTests(ApiFixture f) : IClassFixture<ApiFixture>
{
    private static async Task<string> Message(HttpResponseMessage r)
    {
        var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return j.TryGetProperty("message", out var m) ? m.GetString()! : j.GetProperty("title").GetString()!;
    }

    [Fact]
    public async Task Register_requires_only_email_document_password_and_verification_before_login()
    {
        var c = f.Client();
        var email = $"n{Guid.NewGuid():N}@example.test";
        var doc = ApiFixture.NewDocument();
        var r = await c.PostAsJsonAsync("auth/register", new { email, documentNumber = doc, password = ApiFixture.Password, acceptPrivacyNotice = true });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);

        var login = await c.PostAsJsonAsync("auth/login", new { identifier = email, password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode); // correo sin verificar

        var token = await f.LastTokenAsync(email, "verify-email");
        // El enlace apunta a la ruta del frontend independiente de Mi renta.
        var body = await f.WithDb(db => db.Emails.Where(e => e.ToAddress == email && e.Kind == "verify-email").Select(e => e.Body).FirstAsync());
        Assert.Contains("http://localhost:4200/verificar-correo?token=" + token, body);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("auth/verify-email", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("auth/verify-email", new { token })).StatusCode); // un solo uso

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("auth/login", new { identifier = email.ToUpperInvariant(), password = ApiFixture.Password })).StatusCode);
        var byDoc = await c.PostAsJsonAsync("auth/login", new { identifier = doc, password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.OK, byDoc.StatusCode);
        var me = (await byDoc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user");
        Assert.DoesNotContain(doc, me.GetProperty("maskedDocument").GetString());

        var consent = await f.WithDb(db => db.Consents.CountAsync(x => x.UserId == me.GetProperty("id").GetGuid()));
        Assert.Equal(1, consent);
        // La cédula nunca es la clave primaria.
        Assert.NotEqual(Guid.Empty, me.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Duplicates_get_the_same_generic_answer_and_never_associate_accounts()
    {
        var s = await f.RegisterVerifiedAsync();
        var c = f.Client();
        var fresh = await c.PostAsJsonAsync("auth/register", new { email = $"x{Guid.NewGuid():N}@example.test", documentNumber = ApiFixture.NewDocument(), password = ApiFixture.Password, acceptPrivacyNotice = true });
        var sameEmail = await c.PostAsJsonAsync("auth/register", new { email = s.Email, documentNumber = ApiFixture.NewDocument(), password = ApiFixture.Password, acceptPrivacyNotice = true });
        var sameDoc = await c.PostAsJsonAsync("auth/register", new { email = $"o{Guid.NewGuid():N}@example.test", documentNumber = s.Document, password = ApiFixture.Password, acceptPrivacyNotice = true });
        Assert.Equal(fresh.StatusCode, sameEmail.StatusCode);
        Assert.Equal(fresh.StatusCode, sameDoc.StatusCode);
        Assert.Equal(await Message(fresh), await Message(sameEmail));
        Assert.Equal(await Message(fresh), await Message(sameDoc));
        var count = await f.WithDb(db => db.Users.CountAsync(u => u.DocumentNumber == s.Document || u.Email == s.Email));
        Assert.Equal(1, count);
        // El titular existente recibe un aviso; el solicitante no obtiene nada.
        Assert.True(await f.WithDb(db => db.Emails.AnyAsync(e => e.ToAddress == s.Email && e.Kind == "account-exists-notice")));
    }

    [Fact]
    public async Task Login_errors_do_not_enumerate_and_lock_out_after_repeated_failures()
    {
        var s = await f.RegisterVerifiedAsync();
        var c = f.Client();
        var unknown = await c.PostAsJsonAsync("auth/login", new { identifier = "nadie@example.test", password = "Cualquiera123" });
        var wrong = await c.PostAsJsonAsync("auth/login", new { identifier = s.Email, password = "Equivocada123" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await Message(unknown), await Message(wrong));

        for (var i = 0; i < 4; i++) await c.PostAsJsonAsync("auth/login", new { identifier = s.Email, password = "Equivocada123" });
        var locked = await c.PostAsJsonAsync("auth/login", new { identifier = s.Email, password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode); // bloqueada aunque la contraseña sea correcta
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_all_sessions()
    {
        var s = await f.RegisterVerifiedAsync();
        var c = f.Client();
        var r1 = await c.PostAsJsonAsync("auth/refresh", new { refreshToken = s.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var next = (await r1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refreshToken").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("auth/refresh", new { refreshToken = s.RefreshToken })).StatusCode); // reutilización
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("auth/refresh", new { refreshToken = next })).StatusCode);     // familia revocada
    }

    [Fact]
    public async Task Password_reset_is_generic_single_use_and_expires()
    {
        var s = await f.RegisterVerifiedAsync();
        var c = f.Client();
        var known = await c.PostAsJsonAsync("auth/forgot-password", new { email = s.Email });
        var unknown = await c.PostAsJsonAsync("auth/forgot-password", new { email = "nadie@example.test" });
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await Message(known), await Message(unknown));

        var token = await f.LastTokenAsync(s.Email, "password-reset");
        Assert.Contains("http://localhost:4200/restablecer?token=" + token,
            await f.WithDb(db => db.Emails.Where(e => e.ToAddress == s.Email && e.Kind == "password-reset").Select(e => e.Body).FirstAsync()));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("auth/reset-password", new { token, newPassword = "corta" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("auth/reset-password", new { token, newPassword = "NuevaClave-2025z" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("auth/reset-password", new { token, newPassword = "OtraClave-2025z" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("auth/login", new { identifier = s.Email, password = "NuevaClave-2025z" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("auth/refresh", new { refreshToken = s.RefreshToken })).StatusCode);

        await c.PostAsJsonAsync("auth/forgot-password", new { email = s.Email });
        var old = await f.LastTokenAsync(s.Email, "password-reset");
        f.Time.Advance(TimeSpan.FromMinutes(31));
        try { Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("auth/reset-password", new { token = old, newPassword = "NuevaClave-2026z" })).StatusCode); }
        finally { f.Time.Advance(TimeSpan.FromMinutes(-31)); }
    }

    [Fact]
    public async Task Weak_passwords_and_invalid_documents_are_rejected()
    {
        var c = f.Client();
        var doc = ApiFixture.NewDocument();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("auth/register", new { email = "w@example.test", documentNumber = doc, password = "solo-letras-largas", acceptPrivacyNotice = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("auth/register", new { email = "w@example.test", documentNumber = doc, password = "Clave" + doc, acceptPrivacyNotice = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("auth/register", new { email = "w@example.test", documentNumber = "12ab", password = ApiFixture.Password, acceptPrivacyNotice = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("auth/register", new { email = "w@example.test", documentNumber = doc, password = ApiFixture.Password, acceptPrivacyNotice = false })).StatusCode);
    }

    [Fact]
    public async Task Protected_endpoints_require_a_mi_renta_token()
    {
        var c = f.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("cases")).StatusCode);
        var forged = f.Client("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIwMDAwMDAwMC0wMDAwLTAwMDAtMDAwMC0wMDAwMDAwMDAwMDAifQ.x");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("cases")).StatusCode);
    }
}
