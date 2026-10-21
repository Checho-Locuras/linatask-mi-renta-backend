using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace MiRenta.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string Section = "Email";
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "Mi renta <no-reply@example.invalid>";
    /// <summary>Máximo de recordatorios por usuario cada 7 días.</summary>
    public int MaxRemindersPerWeek { get; set; } = 2;
    /// <summary>
    /// SOLO desarrollo local: si se define, los correos se escriben como archivos .eml en esta carpeta en lugar de
    /// enviarse por SMTP (permite verificar la cuenta sin un proveedor). Producción rechaza esta opción al iniciar.
    /// </summary>
    public string? PickupDirectory { get; set; }
    public bool UsesPickupDirectory => !string.IsNullOrWhiteSpace(PickupDirectory);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SmtpHost) || UsesPickupDirectory;
}

public interface IEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(string to, string subject, string textBody, CancellationToken ct);
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task SendAsync(string to, string subject, string textBody, CancellationToken ct)
    {
        var o = options.Value;
        using var client = o.UsesPickupDirectory
            ? new SmtpClient { DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory, PickupDirectoryLocation = EnsureDirectory(o.PickupDirectory!) }
            : new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.UseSsl };
        if (!o.UsesPickupDirectory && !string.IsNullOrEmpty(o.Username)) client.Credentials = new NetworkCredential(o.Username, o.Password);
        using var msg = new MailMessage { From = new MailAddress(ParseAddress(o.From).Address, ParseAddress(o.From).DisplayName), Subject = subject, Body = textBody, IsBodyHtml = false };
        msg.To.Add(to);
        msg.BodyEncoding = System.Text.Encoding.UTF8;
        msg.SubjectEncoding = System.Text.Encoding.UTF8;
        // En la carpeta local el cuerpo queda legible (8 bits) para copiar el enlace de verificación.
        if (o.UsesPickupDirectory) msg.BodyTransferEncoding = System.Net.Mime.TransferEncoding.EightBit;
        await client.SendMailAsync(msg, ct);
    }

    private static MailAddress ParseAddress(string s) => new(s);

    private static string EnsureDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        return full;
    }
}

/// <summary>Captura en memoria (pruebas y desarrollo).</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public bool IsConfigured => true;
    public List<(string To, string Subject, string Body)> Sent { get; } = new();
    public Task SendAsync(string to, string subject, string textBody, CancellationToken ct)
    {
        lock (Sent) Sent.Add((to, subject, textBody));
        return Task.CompletedTask;
    }
}
