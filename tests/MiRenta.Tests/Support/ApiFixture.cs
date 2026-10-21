using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MiRenta.Api;
using MiRenta.Application.Workers;
using MiRenta.Infrastructure.Data;
using MiRenta.Infrastructure.Email;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MiRenta.Tests.Support;

public sealed class MutableTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

/// <summary>
/// API real sobre TestServer con PostgreSQL y S3 locales (s3rver). Cada clase de pruebas usa una base de datos
/// propia y desechable. Variables: MIRENTA_TEST_POSTGRES (cadena del servidor) y MIRENTA_TEST_S3_ENDPOINT.
/// </summary>
public class ApiFixture : IAsyncLifetime
{
    public const string AccountantEmail = "contador@example.test";
    public const string RulesAdminEmail = "reglas@example.test";
    public static readonly string Password = "Clave-Prueba-2025x";

    private readonly string _db = "mirenta_t_" + Guid.NewGuid().ToString("N")[..12];
    private string _server = "";
    public WebApplication App { get; private set; } = null!;
    public CapturingEmailSender Emails { get; } = new();
    public MutableTime Time { get; } = new();
    protected virtual Dictionary<string, string?> ExtraConfig => new();
    protected virtual void Override(IServiceCollection services) { }

    public static string ServerConnection => Environment.GetEnvironmentVariable("MIRENTA_TEST_POSTGRES")
        ?? "Host=localhost;Port=5432;Username=postgres";
    public static string? S3Endpoint => Environment.GetEnvironmentVariable("MIRENTA_TEST_S3_ENDPOINT") ?? "http://localhost:4569";

    public async Task InitializeAsync()
    {
        _server = ServerConnection;
        await using (var conn = new NpgsqlConnection(_server + ";Database=postgres"))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_db}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var config = new Dictionary<string, string?>
        {
            ["ConnectionStrings:MiRenta"] = $"{_server};Database={_db};Include Error Detail=false",
            ["Jwt:SigningKey"] = "clave-de-pruebas-jwt-solo-local-0123456789abcdef",
            ["App:UrlSigningKey"] = "clave-de-pruebas-urls-solo-local-0123456789abcdef",
            ["App:RunWorkers"] = "false",
            ["App:FrontendBaseUrl"] = "http://localhost:4200",
            ["App:AllowedOrigins:0"] = "http://localhost:4200",
            ["App:AccountantEmails:0"] = AccountantEmail,
            ["App:RulesAdminEmails:0"] = RulesAdminEmail,
            ["RateLimiting:AuthPerMinute"] = "10000",
            ["RateLimiting:DianPerMinute"] = "10000",
            ["RateLimiting:UploadPer5Minutes"] = "10000",
            ["RateLimiting:CalculatePerMinute"] = "10000",
            ["RateLimiting:GlobalPerMinute"] = "100000",
            ["Storage:MaxFileBytes"] = "1048576",
        };
        if (S3Endpoint is { Length: > 0 } s3)
        {
            config["Storage:ServiceUrl"] = s3;
            config["Storage:AccessKeyId"] = "S3RVER";
            config["Storage:SecretAccessKey"] = "S3RVER";
            config["Storage:BucketName"] = "mi-renta-test";
        }
        foreach (var kv in ExtraConfig) config[kv.Key] = kv.Value;

        App = await MiRentaApp.BuildAsync([], b =>
        {
            b.Environment.EnvironmentName = "Testing";
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(config);
        }, services =>
        {
            services.AddSingleton<IEmailSender>(Emails);
            services.AddSingleton<TimeProvider>(Time);
            Override(services);
        });
        await App.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_server + ";Database=postgres");
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_db} WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public HttpClient Client(string? bearer = null)
    {
        var c = App.GetTestClient();
        c.BaseAddress = new Uri("http://localhost" + Endpoints.Prefix + "/");
        if (bearer is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }

    public async Task<T> WithDb<T>(Func<MiRentaDbContext, Task<T>> f)
    {
        using var scope = App.Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<MiRentaDbContext>());
    }

    public async Task RunJobsAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            using var scope = App.Services.CreateScope();
            if (!await scope.ServiceProvider.GetRequiredService<JobRunner>().RunOnceAsync(default)) return;
        }
    }

    public async Task DispatchEmailsAsync()
    {
        using var scope = App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().RunOnceAsync(default);
    }

    /// <summary>Último token enviado por correo a esa dirección para el tipo dado (lee el outbox).</summary>
    public Task<string> LastTokenAsync(string email, string kind) => WithDb(async db =>
    {
        var body = await db.Emails.Where(e => e.ToAddress == email && e.Kind == kind).OrderByDescending(e => e.CreatedAt).Select(e => e.Body).FirstAsync();
        return Regex.Match(body!, @"token=([A-Za-z0-9_\-]+)").Groups[1].Value;
    });

    private static int _seq = 1_000_000;
    public static string NewDocument() => Interlocked.Increment(ref _seq).ToString() + Random.Shared.Next(100, 999);

    public async Task<Session> RegisterVerifiedAsync(string? email = null, string? document = null)
    {
        email ??= $"u{Guid.NewGuid():N}@example.test";
        document ??= NewDocument();
        var c = Client();
        (await c.PostAsJsonAsync("auth/register", new { email, documentNumber = document, password = Password, acceptPrivacyNotice = true })).EnsureSuccessStatusCode();
        var token = await LastTokenAsync(email, "verify-email");
        (await c.PostAsJsonAsync("auth/verify-email", new { token })).EnsureSuccessStatusCode();
        var login = await c.PostAsJsonAsync("auth/login", new { identifier = email, password = Password });
        login.EnsureSuccessStatusCode();
        var json = await login.Content.ReadFromJsonAsync<JsonElement>();
        return new Session(this, email, document, json.GetProperty("accessToken").GetString()!, json.GetProperty("refreshToken").GetString()!,
            json.GetProperty("user").GetProperty("id").GetGuid());
    }
}

public sealed record Session(ApiFixture F, string Email, string Document, string AccessToken, string RefreshToken, Guid UserId)
{
    public HttpClient Http => F.Client(AccessToken);

    public async Task<Guid> CreateCaseAsync()
    {
        var r = await Http.PostAsJsonAsync("cases", new { taxYear = 2025 });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        var r = await Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<HttpResponseMessage> AnswerAsync(Guid caseId, string code, object value, string? key = null)
    {
        var tasks = await GetJsonAsync($"cases/{caseId}/tasks");
        var task = tasks.EnumerateArray().First(t => t.GetProperty("code").GetString() == code);
        return await Http.PostAsJsonAsync($"cases/{caseId}/tasks/{task.GetProperty("id").GetGuid()}/answers", new { value, idempotencyKey = key });
    }

    public async Task<HttpResponseMessage> UploadAsync(Guid caseId, byte[] content, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await Http.PostAsync($"cases/{caseId}/documents", form);
    }

    /// <summary>Responde todas las preguntas de un asalariado sencillo (datos sintéticos).</summary>
    public async Task AnswerSalariedProfileAsync(Guid caseId, bool includeLaborAmounts = true, int dependents = 0)
    {
        async Task Ok(string code, object v) { var r = await AnswerAsync(caseId, code, v); Assert.True(r.IsSuccessStatusCode, code + ": " + await r.Content.ReadAsStringAsync()); }
        await Ok("q.fiscal_resident", "yes");
        await Ok("q.income_sources", new[] { "labor" });
        await Ok("q.iva_responsible", "no");
        if (includeLaborAmounts)
        {
            await Ok("q.labor_gross_income", 120_000_000);
            await Ok("q.labor_pension", 4_800_000);
            await Ok("q.labor_health", 4_800_000);
            await Ok("q.labor_withholding", 5_000_000);
        }
        await Ok("q.dependents_count", dependents);
        await Ok("q.housing_interest", 0);
        await Ok("q.prepaid_health", 0);
        await Ok("q.gross_assets", 80_000_000);
        await Ok("q.debts", 10_000_000);
        await Ok("q.credit_card_spending", 12_000_000);
        await Ok("q.total_purchases", 40_000_000);
        await Ok("q.total_deposits", 110_000_000);
    }
}
