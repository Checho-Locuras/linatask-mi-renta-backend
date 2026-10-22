using System.Diagnostics.Metrics;
using MiRenta.Application.Common;
using MiRenta.Infrastructure.Storage;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MiRenta.Api;

public static class RateLimits
{
    public const string Auth = "auth";
    public const string Dian = "dian";
    public const string Upload = "upload";
    public const string Calculate = "calculate";
}

public static class StartupValidation
{
    /// <summary>Falla al iniciar si en producción falta configuración de seguridad (nunca imprime valores).</summary>
    public static void Validate(bool isProd, JwtOptions jwt, AppOptions app, StorageOptions storage, string? connection,
        Infrastructure.Email.EmailOptions? email = null, Infrastructure.Ai.AiOptions? ai = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(connection)) errors.Add("ConnectionStrings:MiRenta es obligatorio.");
        if (System.Text.Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32) errors.Add("Jwt:SigningKey debe tener al menos 32 bytes.");
        if (System.Text.Encoding.UTF8.GetByteCount(app.UrlSigningKey) < 32) errors.Add("App:UrlSigningKey debe tener al menos 32 bytes.");
        if (jwt.SigningKey == app.UrlSigningKey && jwt.SigningKey.Length > 0) errors.Add("Jwt:SigningKey y App:UrlSigningKey deben ser distintas.");
        if (isProd)
        {
            if (!storage.IsConfigured) errors.Add("Storage:* es obligatorio en producción (bucket privado).");
            if (storage.UsesLocalDirectory) errors.Add("Storage:LocalDirectory solo se permite en desarrollo.");
            if (app.AllowedOrigins.Length == 0 || app.AllowedOrigins.Any(o => o == "*" || !o.StartsWith("https://", StringComparison.Ordinal)))
                errors.Add("App:AllowedOrigins debe listar orígenes https explícitos.");
            if (string.IsNullOrWhiteSpace(email?.SmtpHost)) errors.Add("Email:SmtpHost es obligatorio en producción (verificación de correo y recuperación).");
            if (email?.UsesPickupDirectory == true) errors.Add("Email:PickupDirectory solo se permite en desarrollo.");
            if (!app.FrontendBaseUrl.StartsWith("https://", StringComparison.Ordinal)) errors.Add("App:FrontendBaseUrl debe ser https en producción.");
        }
        if (ai is { Provider: "Anthropic" } && string.IsNullOrWhiteSpace(ai.ApiKey)) errors.Add("Ai:ApiKey es obligatoria si Ai:Provider=Anthropic.");
        if (ai is not null && ai.Provider is not ("Disabled" or "Anthropic")) errors.Add("Ai:Provider debe ser Disabled o Anthropic.");
        if (errors.Count > 0) throw new InvalidOperationException("Configuración inválida: " + string.Join(" ", errors));
    }
}

public static class ErrorHandling
{
    public static async Task HandleAsync(HttpContext ctx)
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var (status, code, message) = ex switch
        {
            AppException a => (a.Status, a.Code, a.Message),
            Domain.Cases.InvalidCaseTransitionException => (409, "invalid_transition", "La acción no es válida en el estado actual del expediente."),
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => (409, "concurrency", "El expediente cambió mientras lo editabas; vuelve a intentarlo."),
            BadHttpRequestException b => (b.StatusCode, "bad_request", "Solicitud inválida."),
            System.Text.Json.JsonException => (400, "bad_json", "JSON inválido."),
            _ => (500, "server_error", "Ocurrió un error inesperado."),
        };
        if (status >= 500)
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MiRenta").LogError("Error no controlado {Type} en {Path}", ex?.GetType().Name, ctx.Request.Path.Value);
        ctx.Response.StatusCode = status;
        // Solo fuera de producción se expone el tipo y mensaje interno (diagnóstico local y pruebas).
        var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var detail = status >= 500 && !env.IsProduction() && !env.IsStaging() ? $"{ex?.GetType().Name}: {ex?.Message}" : null;
        await Results.Problem(title: message, detail: detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(ctx);
    }
}

public sealed class StorageHealthCheck(IPrivateObjectStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await storage.PingAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("storage");
}

/// <summary>Métricas (System.Diagnostics.Metrics; exportables con dotnet-counters u OpenTelemetry).</summary>
public static class MiRentaMetrics
{
    public static readonly Meter Meter = new("MiRenta.Api", "1.0");
    public static readonly Counter<long> DocumentsUploaded = Meter.CreateCounter<long>("mirenta.documents.uploaded");
    public static readonly Counter<long> Calculations = Meter.CreateCounter<long>("mirenta.calculations");
    public static readonly Counter<long> LoginFailures = Meter.CreateCounter<long>("mirenta.auth.login_failures");
    public static readonly Counter<long> Exports = Meter.CreateCounter<long>("mirenta.exports");
}
