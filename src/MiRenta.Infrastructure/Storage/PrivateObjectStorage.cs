using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace MiRenta.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>Endpoint S3 (R2: https://&lt;cuenta&gt;.r2.cloudflarestorage.com).</summary>
    public string ServiceUrl { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    /// <summary>Bucket PRIVADO exclusivo de Mi renta (sin dominio público ni r2.dev).</summary>
    public string BucketName { get; set; } = string.Empty;
    public long MaxFileBytes { get; set; } = 15 * 1024 * 1024;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServiceUrl) && !string.IsNullOrWhiteSpace(BucketName)
        && !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey);
}

public interface IPrivateObjectStorage
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);
    Task<byte[]?> GetAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task<bool> PingAsync(CancellationToken ct);
}

public static class StorageKeys
{
    /// <summary>Clave opaca: no contiene nombre de archivo, cédula ni correo.</summary>
    public static string ForDocument(Guid caseId, Guid documentId) => $"mi-renta/cases/{caseId:N}/documents/{documentId:N}";
}

public sealed class S3PrivateObjectStorage : IPrivateObjectStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly bool _https;

    public S3PrivateObjectStorage(IOptions<StorageOptions> options)
    {
        var o = options.Value;
        if (!o.IsConfigured) throw new InvalidOperationException("Storage no está configurado.");
        _bucket = o.BucketName;
        _https = o.ServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        _client = new AmazonS3Client(new BasicAWSCredentials(o.AccessKeyId, o.SecretAccessKey), new AmazonS3Config
        {
            ServiceURL = o.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
        });
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket, Key = key, InputStream = ms, ContentType = contentType,
            DisablePayloadSigning = _https, // R2 no soporta STREAMING-AWS4-HMAC-SHA256-PAYLOAD (solo aplica sobre HTTPS)
            UseChunkEncoding = _https,
        }, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            using var res = await _client.GetObjectAsync(_bucket, key, ct);
            using var ms = new MemoryStream();
            await res.ResponseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public Task DeleteAsync(string key, CancellationToken ct) => _client.DeleteObjectAsync(_bucket, key, ct);

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        // Sonda barata: HEAD de una clave inexistente. 404 = bucket accesible con estas credenciales.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try { await _client.GetObjectMetadataAsync(_bucket, "mi-renta/health-probe", cts.Token); return true; }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return true; }
        catch (Exception) { return false; }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Almacenamiento en memoria SOLO para desarrollo sin S3 configurado (se rechaza en producción).</summary>
public sealed class InMemoryObjectStorage : IPrivateObjectStorage
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _items = new();
    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct) { _items[key] = content; return Task.CompletedTask; }
    public Task<byte[]?> GetAsync(string key, CancellationToken ct) => Task.FromResult(_items.TryGetValue(key, out var v) ? v : null);
    public Task DeleteAsync(string key, CancellationToken ct) { _items.TryRemove(key, out _); return Task.CompletedTask; }
    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
}
