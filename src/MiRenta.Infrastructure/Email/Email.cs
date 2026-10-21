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
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SmtpHost);
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
        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.UseSsl };
        if (!string.IsNullOrEmpty(o.Username)) client.Credentials = new NetworkCredential(o.Username, o.Password);
        using var msg = new MailMessage { From = new MailAddress(ParseAddress(o.From).Address, ParseAddress(o.From).DisplayName), Subject = subject, Body = textBody, IsBodyHtml = false };
        msg.To.Add(to);
        await client.SendMailAsync(msg, ct);
    }

    private static MailAddress ParseAddress(string s) => new(s);
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
