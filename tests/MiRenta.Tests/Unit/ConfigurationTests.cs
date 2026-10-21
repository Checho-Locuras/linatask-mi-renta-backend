using MiRenta.Api;
using MiRenta.Application.Common;
using MiRenta.Infrastructure.Ai;
using MiRenta.Infrastructure.Email;
using MiRenta.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace MiRenta.Tests.Unit;

public class ConfigurationTests
{
    private static readonly string K1 = new('a', 40), K2 = new('b', 40);
    private static JwtOptions Jwt(string k) => new() { SigningKey = k };
    private static AppOptions App(string k, params string[] origins) => new() { UrlSigningKey = k, AllowedOrigins = origins, FrontendBaseUrl = "https://app.example.test" };
    private static readonly StorageOptions S3 = new() { ServiceUrl = "https://x.example.test", BucketName = "b", AccessKeyId = "id", SecretAccessKey = "s" };

    [Fact]
    public void Development_needs_only_connection_and_two_distinct_keys()
    {
        StartupValidation.Validate(false, Jwt(K1), App(K2), new StorageOptions(), "Host=localhost");
        Assert.Throws<InvalidOperationException>(() => StartupValidation.Validate(false, Jwt(K1), App(K2), new StorageOptions(), null));
        Assert.Throws<InvalidOperationException>(() => StartupValidation.Validate(false, Jwt("corta"), App(K2), new StorageOptions(), "Host=x"));
        Assert.Throws<InvalidOperationException>(() => StartupValidation.Validate(false, Jwt(K1), App(K1), new StorageOptions(), "Host=x"));
    }

    [Fact]
    public void Production_requires_storage_https_origins_and_smtp_and_rejects_pickup_directory()
    {
        var smtp = new EmailOptions { SmtpHost = "smtp.example.test" };
        StartupValidation.Validate(true, Jwt(K1), App(K2, "https://app.example.test"), S3, "Host=x", smtp);
        Assert.Throws<InvalidOperationException>(() => StartupValidation.Validate(true, Jwt(K1), App(K2, "https://app.example.test"), new StorageOptions(), "Host=x", smtp));
        Assert.Throws<InvalidOperationException>(() => StartupValidation.Validate(true, Jwt(K1), App(K2, "*"), S3, "Host=x", smtp));
        Assert.Throws<InvalidOperationException>(() => StartupValidation.Validate(true, Jwt(K1), App(K2, "https://app.example.test"), S3, "Host=x", new EmailOptions()));
        Assert.Throws<InvalidOperationException>(() => StartupValidation.Validate(true, Jwt(K1), App(K2, "https://app.example.test"), S3, "Host=x",
            new EmailOptions { SmtpHost = "smtp.example.test", PickupDirectory = "mail" }));
    }

    [Fact]
    public void Ai_provider_must_be_known_and_have_a_key_when_enabled()
    {
        StartupValidation.Validate(false, Jwt(K1), App(K2), new StorageOptions(), "Host=x", ai: new AiOptions());
        Assert.Throws<InvalidOperationException>(() => StartupValidation.Validate(false, Jwt(K1), App(K2), new StorageOptions(), "Host=x", ai: new AiOptions { Provider = "Anthropic" }));
        Assert.Throws<InvalidOperationException>(() => StartupValidation.Validate(false, Jwt(K1), App(K2), new StorageOptions(), "Host=x", ai: new AiOptions { Provider = "Otro", ApiKey = "x" }));
    }

    [Fact]
    public async Task Pickup_directory_writes_local_eml_files_without_smtp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mirenta-mail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sender = new SmtpEmailSender(Options.Create(new EmailOptions { PickupDirectory = dir }));
            Assert.True(sender.IsConfigured);
            await sender.SendAsync("persona@example.test", "Verifica tu correo", "enlace de prueba", default);
            var file = Assert.Single(Directory.GetFiles(dir, "*.eml"));
            Assert.Contains("persona@example.test", await File.ReadAllTextAsync(file));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
