using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MiRenta.Application.Workers;
using MiRenta.Infrastructure.Dian;
using MiRenta.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MiRenta.Tests.Integration;

public class PlatformTests(ApiFixture f) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Health_cors_and_security_headers()
    {
        var root = f.App.GetTestClientRoot();
        Assert.Equal(HttpStatusCode.OK, (await root.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await root.GetAsync("/health/ready")).StatusCode);

        var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/my-rent/cases");
        allowed.Headers.Add("Origin", "http://localhost:4200");
        allowed.Headers.Add("Access-Control-Request-Method", "GET");
        var ok = await root.SendAsync(allowed);
        Assert.Equal("http://localhost:4200", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var evil = new HttpRequestMessage(HttpMethod.Options, "/api/v1/my-rent/cases");
        evil.Headers.Add("Origin", "https://linatask-develop.up.railway.app.evil.example");
        evil.Headers.Add("Access-Control-Request-Method", "GET");
        Assert.False((await root.SendAsync(evil)).Headers.Contains("Access-Control-Allow-Origin"));

        var r = await root.GetAsync("/api/v1/my-rent/dian/status");
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", r.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Dian_precheck_is_disabled_by_default_and_points_to_the_official_consultation()
    {
        var c = f.Client();
        var status = await (await c.GetAsync("dian/status")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(status.GetProperty("enabled").GetBoolean());
        var r = await (await c.PostAsJsonAsync("dian/declarant", new { numDocumento = "1234567", tipoDocumento = "13", reCaptcha = "" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(r.GetProperty("disabled").GetBoolean());
        Assert.StartsWith("https://www.dian.gov.co", r.GetProperty("officialConsultUrl").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("dian/questions", new { nit = "abc", tipoDocumento = "13" })).StatusCode);
    }

    [Fact]
    public async Task Reminders_have_no_figures_are_deduplicated_rate_limited_and_can_be_unsubscribed()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        await s.AnswerAsync(caseId, "q.fiscal_resident", "yes");
        f.Time.Advance(TimeSpan.FromDays(4));
        try
        {
            using (var scope = f.App.Services.CreateScope())
            {
                var sched = scope.ServiceProvider.GetRequiredService<ReminderScheduler>();
                Assert.True(await sched.RunOnceAsync(default) >= 1);
                await sched.RunOnceAsync(default); // misma semana: no se repite
            }
            var reminders = await f.WithDb(db => db.Emails.Where(e => e.UserId == s.UserId && e.Kind.StartsWith("reminder-")).ToListAsync());
            Assert.Single(reminders);
            var body = reminders[0].Body!;
            Assert.DoesNotMatch(@"\d{1,3}(\.\d{3}){2,}", body);       // sin cifras
            Assert.DoesNotContain("/files/", body);                    // sin enlaces a documentos
            Assert.DoesNotContain("vence", body.ToLowerInvariant());   // sin afirmar plazos
            Assert.Contains("recordatorios/baja?token=", body);

            await f.DispatchEmailsAsync();
            Assert.Contains(f.Emails.Sent, m => m.To == s.Email && m.Subject.Contains("pendientes"));
            Assert.Null((await f.WithDb(db => db.Emails.FirstAsync(e => e.Id == reminders[0].Id))).Body); // no se conserva el cuerpo

            var token = System.Text.RegularExpressions.Regex.Match(body, @"token=([A-Za-z0-9_\-.]+)").Groups[1].Value;
            Assert.Equal(HttpStatusCode.BadRequest, (await f.Client().PostAsJsonAsync("reminders/unsubscribe", new { token = token + "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await f.Client().PostAsJsonAsync("reminders/unsubscribe", new { token })).StatusCode);
            Assert.True(await f.WithDb(db => db.Users.Where(u => u.Id == s.UserId).Select(u => u.RemindersOptOut).FirstAsync()));

            f.Time.Advance(TimeSpan.FromDays(8));
            using (var scope = f.App.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ReminderScheduler>().RunOnceAsync(default);
            Assert.Equal(1, await f.WithDb(db => db.Emails.CountAsync(e => e.UserId == s.UserId && e.Kind.StartsWith("reminder-"))));
        }
        finally { f.Time.Now = DateTimeOffset.UtcNow; }
    }

    [Fact]
    public async Task Audit_trail_never_contains_secrets_or_document_content()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        await s.UploadAsync(caseId, TestFiles.LaborCertificate(50_000_000, 2_000_000, 2_000_000, 0), "c.pdf");
        await f.RunJobsAsync();
        var details = await f.WithDb(db => db.AuditEvents.Where(a => a.DetailsJson != null).Select(a => a.DetailsJson!).ToListAsync());
        Assert.DoesNotContain(details, d => d.Contains(ApiFixture.Password) || d.Contains("50.000.000") || d.Contains(s.Document) || d.Contains("token"));
    }
}

/// <summary>DIAN habilitada contra un servidor falso: el reCAPTCHA no se reutiliza y nada se persiste.</summary>
public sealed class DianEnabledFixture : ApiFixture
{
    public FakeDian Fake { get; } = new();
    protected override Dictionary<string, string?> ExtraConfig => new() { ["Dian:Enabled"] = "true", ["RateLimiting:DianPerMinute"] = "3" };
    protected override void Override(IServiceCollection services) =>
        services.AddHttpClient<IDianPrecheckClient, DianPrecheckClient>(c => c.BaseAddress = new Uri("https://dian.invalid/"))
            .ConfigurePrimaryHttpMessageHandler(() => Fake);
}

public sealed class FakeDian : HttpMessageHandler
{
    public List<(string Path, string Body)> Calls { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        var json = request.RequestUri.AbsolutePath.Contains("declaracion")
            ? """{"numDocumento":"1234567","nit":"1234567","tipoDocumento":"13","anoGravable":"2025","razonSocial":"PERSONA DE PRUEBA","ident":"1","ident1":"0","ident2":null,"ident3":"0","ident4":"0","ident5":"0","ident6":"0","plazo":null}"""
            : """[{"idPregunta":"p1","pregunta":"¿Pregunta de prueba?","respuestas":[{"idRespuesta":"r1","respuesta":"A"}]}]""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }
}

public class DianEnabledTests(DianEnabledFixture f) : IClassFixture<DianEnabledFixture>
{
    [Fact]
    public async Task Captcha_cannot_be_reused_indicators_are_raw_nothing_is_stored_and_rate_limit_applies()
    {
        var c = f.Client();
        var captcha = new string('c', 40);
        var first = await c.PostAsJsonAsync("dian/declarant", new { numDocumento = "1234567", tipoDocumento = "13", reCaptcha = captcha });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var json = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1", json.GetProperty("value").GetProperty("rawIndicators").GetProperty("ident").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("dian/declarant", new { numDocumento = "1234567", tipoDocumento = "13", reCaptcha = captcha })).StatusCode);
        Assert.Single(f.Fake.Calls, x => x.Path.Contains("declaracion"));

        var dbHasIt = await f.WithDb(async db =>
            (await db.AuditEvents.Select(a => a.DetailsJson).ToListAsync()).Any(d => d != null && (d.Contains(captcha) || d.Contains("1234567"))) ||
            await db.Messages.AnyAsync(m => m.Content.Contains("1234567")) || await db.Users.AnyAsync(u => u.DocumentNumber == "1234567"));
        Assert.False(dbHasIt);

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("dian/questions", new { nit = "1234567", tipoDocumento = "13" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("dian/questions", new { nit = "1234567", tipoDocumento = "13" })).StatusCode);
    }
}

public sealed class StrictLimitsFixture : ApiFixture
{
    protected override Dictionary<string, string?> ExtraConfig => new() { ["RateLimiting:AuthPerMinute"] = "5" };
}

public class RateLimitTests(StrictLimitsFixture f) : IClassFixture<StrictLimitsFixture>
{
    [Fact]
    public async Task Auth_endpoints_are_rate_limited()
    {
        var c = f.Client();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++) codes.Add((await c.PostAsJsonAsync("auth/login", new { identifier = "a@example.test", password = "x" })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }
}
