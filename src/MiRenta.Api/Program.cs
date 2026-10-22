using System.Text;
using System.Threading.RateLimiting;
using MiRenta.Api;
using MiRenta.Application.Assistant;
using MiRenta.Application.Auth;
using MiRenta.Application.Cases;
using MiRenta.Application.Common;
using MiRenta.Application.Dian;
using MiRenta.Application.Documents;
using MiRenta.Application.Returns;
using MiRenta.Application.Workers;
using MiRenta.Infrastructure.Ai;
using MiRenta.Infrastructure.Data;
using MiRenta.Infrastructure.Dian;
using MiRenta.Infrastructure.Email;
using MiRenta.Infrastructure.Extraction;
using MiRenta.Infrastructure.Security;
using MiRenta.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var app = await MiRentaApp.BuildAsync(args);
app.Run();

public partial class Program;

/// <summary>Composición de la API (reutilizada por las pruebas de integración con TestServer).</summary>
public static class MiRentaApp
{
  public static async Task<WebApplication> BuildAsync(string[] args, Action<WebApplicationBuilder>? configure = null,
      Action<IServiceCollection>? overrideServices = null)
  {
var builder = WebApplication.CreateBuilder(args);
configure?.Invoke(builder);
var config = builder.Configuration;
var isProd = builder.Environment.IsProduction();

builder.Services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
builder.Services.Configure<AppOptions>(config.GetSection(AppOptions.Section));
builder.Services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
builder.Services.Configure<EmailOptions>(config.GetSection(EmailOptions.Section));
builder.Services.Configure<DianOptions>(config.GetSection(DianOptions.Section));
builder.Services.Configure<AiOptions>(config.GetSection(AiOptions.Section));
// Carpetas locales de desarrollo: rutas relativas se resuelven contra la raíz del proyecto de API (igual en
// dotnet run, Visual Studio o VS Code), nunca contra el directorio desde donde se lanzó el proceso.
var contentRoot = builder.Environment.ContentRootPath;
builder.Services.PostConfigure<StorageOptions>(o => { if (o.UsesLocalDirectory && !Path.IsPathRooted(o.LocalDirectory)) o.LocalDirectory = Path.Combine(contentRoot, o.LocalDirectory!); });
builder.Services.PostConfigure<EmailOptions>(o => { if (o.UsesPickupDirectory && !Path.IsPathRooted(o.PickupDirectory)) o.PickupDirectory = Path.Combine(contentRoot, o.PickupDirectory!); });

var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
var app0 = config.GetSection(AppOptions.Section).Get<AppOptions>() ?? new AppOptions();
var storage = config.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
var connection = config.GetConnectionString("MiRenta");
StartupValidation.Validate(isProd, jwt, app0, storage, connection,
    config.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions(),
    config.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions());

builder.Services.AddDbContext<MiRentaDbContext>(o => o.UseNpgsql(connection, n => n.MigrationsHistoryTable("__ef_migrations_history", "mi_renta"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new DownloadTokenSigner(Encoding.UTF8.GetBytes(app0.UrlSigningKey + ":download")));
builder.Services.AddSingleton(new UnsubscribeSigner(Encoding.UTF8.GetBytes(app0.UrlSigningKey + ":unsubscribe")));
if (storage.IsConfigured) builder.Services.AddSingleton<IPrivateObjectStorage, S3PrivateObjectStorage>();
else if (storage.UsesLocalDirectory) builder.Services.AddSingleton<IPrivateObjectStorage, LocalDiskObjectStorage>();
else builder.Services.AddSingleton<IPrivateObjectStorage, InMemoryObjectStorage>();
builder.Services.AddSingleton<IDocumentExtractor, DeterministicDocumentExtractor>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
var ai = config.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
if (ai.Provider == "Anthropic") builder.Services.AddHttpClient<IAiExtractionAssistant, AnthropicAiAssistant>(c => { c.BaseAddress = new Uri(ai.BaseUrl); c.Timeout = TimeSpan.FromSeconds(30); });
else builder.Services.AddSingleton<IAiExtractionAssistant, DisabledAiAssistant>();
var dian = config.GetSection(DianOptions.Section).Get<DianOptions>() ?? new DianOptions();
builder.Services.AddHttpClient<IDianPrecheckClient, DianPrecheckClient>(c =>
{
    c.BaseAddress = new Uri(dian.BaseUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(dian.TimeoutSeconds);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });

foreach (var t in new[] { typeof(CaseAccess), typeof(Audit), typeof(AuthService), typeof(FactService), typeof(CaseWorkflow), typeof(CaseService),
             typeof(AssistantService), typeof(DocumentService), typeof(DocumentProcessor), typeof(ReconciliationService), typeof(CalculationService),
             typeof(DraftService), typeof(FactCommandService), typeof(RuleService), typeof(JobRunner), typeof(EmailDispatcher),
             typeof(ReminderScheduler), typeof(ReminderPreferences), typeof(DianService) })
    builder.Services.AddScoped(t);
builder.Services.AddHostedService<BackgroundWorkersHost>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ClockSkew = TimeSpan.FromSeconds(30), RoleClaimType = "role", NameClaimType = "sub",
    };
});
builder.Services.AddAuthorization();

builder.Services.AddCors(o => o.AddPolicy("frontend", p => p.WithOrigins(app0.AllowedOrigins).AllowAnyHeader()
    .WithMethods("GET", "POST", "PUT", "DELETE").WithExposedHeaders("Content-Disposition").SetPreflightMaxAge(TimeSpan.FromHours(1))));

var limits = config.GetSection("RateLimiting");
int Limit(string key, int fallback) => limits.GetValue(key, fallback);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    static string UserOrIp(HttpContext c) => c.User.FindFirst("sub")?.Value ?? Ip(c);
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(UserOrIp(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Limit("GlobalPerMinute", 300), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimits.Auth, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = Limit("AuthPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimits.Dian, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = Limit("DianPerMinute", 5), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimits.Upload, c => RateLimitPartition.GetFixedWindowLimiter(UserOrIp(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = Limit("UploadPer5Minutes", 20), Window = TimeSpan.FromMinutes(5) }));
    o.AddPolicy(RateLimits.Calculate, c => RateLimitPartition.GetFixedWindowLimiter(UserOrIp(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = Limit("CalculatePerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddNpgSql(connection ?? "Host=invalid", name: "postgres", tags: ["ready"])
    .AddCheck<StorageHealthCheck>("storage", tags: ["ready"]);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    o.ForwardLimit = 1;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = storage.MaxFileBytes + 64 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = storage.MaxFileBytes + 64 * 1024);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = false);

overrideServices?.Invoke(builder.Services);
var app = builder.Build();

if (config.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<MiRentaDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler(e => e.Run(ErrorHandling.HandleAsync));
app.UseStatusCodePages();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Cache-Control"] = "no-store";
    h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    await next();
});
if (!isProd) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseCors("frontend");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapMiRentaEndpoints();
return app;
  }
}
