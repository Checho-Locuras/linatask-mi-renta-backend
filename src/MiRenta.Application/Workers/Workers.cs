using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiRenta.Application.Common;
using MiRenta.Application.Documents;
using MiRenta.Domain.Assistant;
using MiRenta.Domain.Cases;
using MiRenta.Domain.Operations;
using MiRenta.Infrastructure.Data;
using MiRenta.Infrastructure.Email;
using MiRenta.Infrastructure.Jobs;
using MiRenta.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MiRenta.Application.Workers;

/// <summary>Ejecuta trabajos de la cola persistente.</summary>
public sealed class JobRunner(MiRentaDbContext db, DocumentProcessor processor, ILogger<JobRunner> log)
{
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var job = await JobQueue.ClaimAsync(db, TimeSpan.FromMinutes(5), ct);
        if (job is null) return false;
        Guid? documentId = null;
        try
        {
            switch (job.Kind)
            {
                case JobKinds.ProcessDocument:
                    documentId = JsonDocument.Parse(job.PayloadJson).RootElement.GetProperty("documentId").GetGuid();
                    await processor.ProcessAsync(documentId.Value, ct);
                    break;
                default:
                    throw new InvalidOperationException("Tipo de trabajo desconocido.");
            }
            await JobQueue.CompleteAsync(db, job.Id, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Nunca se registra contenido del documento: solo tipo de error.
            log.LogWarning("Trabajo {JobId} ({Kind}) falló en intento {Attempt}: {Error}", job.Id, job.Kind, job.Attempts, e.GetType().Name);
            db.ChangeTracker.Clear();
            await JobQueue.FailAsync(db, job, e.GetType().Name + ": " + e.Message, ct);
            if (documentId is { } id) await processor.MarkFailedAsync(id, "Error al procesar el documento.", job.Attempts >= job.MaxAttempts, ct);
        }
        return true;
    }
}

/// <summary>Envía el outbox de correos. Sin SMTP configurado los marca como Skipped (no se pierde el registro).</summary>
public sealed class EmailDispatcher(MiRentaDbContext db, IEmailSender sender, TimeProvider clock, ILogger<EmailDispatcher> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var batch = await db.Emails.Where(e => e.Status == EmailStatus.Pending && e.ScheduledFor <= now).OrderBy(e => e.ScheduledFor).Take(20).ToListAsync(ct);
        foreach (var e in batch)
        {
            if (!sender.IsConfigured) { e.Status = EmailStatus.Skipped; e.LastError = "SMTP no configurado"; e.Body = null; continue; }
            try
            {
                await sender.SendAsync(e.ToAddress, e.Subject, e.Body ?? "", ct);
                e.Status = EmailStatus.Sent;
                e.SentAt = now;
                e.Body = null; // no se conservan enlaces con token
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Correo {EmailId} no enviado: {Error}", e.Id, ex.GetType().Name);
                e.Status = EmailStatus.Failed;
                e.LastError = ex.GetType().Name;
                e.Body = null;
            }
        }
        await db.SaveChangesAsync(ct);
        return batch.Count;
    }
}

/// <summary>
/// Recordatorios por correo: sin cifras, documentos ni enlaces a archivos; deduplicados por semana y tipo; con
/// límite semanal por usuario y enlace de baja. No afirman nada sobre plazos de la DIAN.
/// </summary>
public sealed class ReminderScheduler(MiRentaDbContext db, IOptions<EmailOptions> email, IOptions<AppOptions> app, UnsubscribeSigner unsub, TimeProvider clock)
{
    public static readonly TimeSpan Inactivity = TimeSpan.FromDays(3);

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var week = System.Globalization.ISOWeek.GetWeekOfYear(now);
        var cutoff = now - Inactivity;
        var candidates = await db.Cases.Where(c => c.DeletedAt == null && c.Status != CaseStatus.Completed && c.Status != CaseStatus.ApprovedForExport
                && c.Status != CaseStatus.Blocked && (c.LastUserActivityAt ?? c.CreatedAt) < cutoff)
            .Join(db.Users.Where(u => !u.RemindersOptOut && !u.IsDisabled && u.EmailVerifiedAt != null), c => c.OwnerUserId, u => u.Id, (c, u) => new { c, u })
            .OrderBy(x => x.c.CreatedAt).Take(200).ToListAsync(ct);
        var queued = 0;
        foreach (var x in candidates)
        {
            var sentThisWeek = await db.Emails.CountAsync(e => e.UserId == x.u.Id && e.Kind.StartsWith("reminder-") && e.CreatedAt > now.AddDays(-7), ct);
            if (sentThisWeek >= email.Value.MaxRemindersPerWeek) continue;
            var open = await db.Tasks.Where(t => t.CaseId == x.c.Id && t.State == TaskState.Open).Select(t => t.Kind).ToListAsync(ct);
            var (kind, subject, line) = open.Contains(TaskKind.Confirmation) || open.Contains(TaskKind.Question)
                ? (EmailKinds.PendingQuestion, "Tienes preguntas pendientes en Mi renta", "Tienes preguntas pendientes para avanzar con tu borrador de renta.")
                : open.Contains(TaskKind.DocumentRequest)
                    ? (EmailKinds.PendingDocuments, "Puedes subir tus documentos en Mi renta", "Aún puedes subir documentos que te ayudan a preparar tu borrador.")
                    : (EmailKinds.IncompleteReturn, "Tu borrador en Mi renta sigue en proceso", "Tu borrador de declaración aún no está terminado.");
            var body = line + "\n\nIngresa a Mi renta para continuar: " + app.Value.FrontendBaseUrl + "\n\n" +
                       "Consulta los plazos oficiales en el sitio de la DIAN. Este correo no contiene información de tu declaración.\n\n" +
                       "Para dejar de recibir recordatorios: " + app.Value.FrontendBaseUrl + "/recordatorios/baja?token=" + unsub.Create(x.u.Id);
            if (await EmailOutbox.QueueAsync(db, x.u.Id, x.c.Id, x.u.Email, kind, subject, body, $"{kind}:{x.c.Id}:{now.Year}-W{week}", ct)) queued++;
            await db.SaveChangesAsync(ct);
        }
        return queued;
    }
}

public sealed class UnsubscribeSigner(byte[] key)
{
    public string Create(Guid userId)
    {
        var id = userId.ToString("N");
        return id + "." + TokenUtil.Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("unsub:" + id)))[..22];
    }

    public Guid? Validate(string token)
    {
        var parts = (token ?? "").Split('.');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id)) return null;
        var expected = Create(id);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token!)) ? id : null;
    }
}

/// <summary>Bucle de fondo: cola de trabajos, outbox de correos y recordatorios (cada uno con su propio scope).</summary>
public sealed class BackgroundWorkersHost(IServiceScopeFactory scopes, IOptions<AppOptions> app, ILogger<BackgroundWorkersHost> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!app.Value.RunWorkers) return;
        var lastReminder = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                using (var scope = scopes.CreateScope())
                    didWork = await scope.ServiceProvider.GetRequiredService<JobRunner>().RunOnceAsync(stoppingToken);
                using (var scope = scopes.CreateScope())
                    didWork |= await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().RunOnceAsync(stoppingToken) > 0;
                if (DateTime.UtcNow - lastReminder > TimeSpan.FromHours(1))
                {
                    lastReminder = DateTime.UtcNow;
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ReminderScheduler>().RunOnceAsync(stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError("Error en trabajadores de fondo: {Error}", e.GetType().Name);
            }
            if (!didWork) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ContinueWith(_ => { });
        }
    }
}

public sealed class ReminderPreferences(MiRentaDbContext db, UnsubscribeSigner signer, Audit audit)
{
    public async Task UnsubscribeAsync(string token, CancellationToken ct)
    {
        var id = signer.Validate(token) ?? throw AppException.BadRequest("invalid_token", "Enlace inválido.");
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (u is null) return;
        u.RemindersOptOut = true;
        audit.Record("reminders.unsubscribed", u.Id, null);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetAsync(Guid userId, bool optOut, CancellationToken ct)
    {
        var u = await db.Users.FirstAsync(x => x.Id == userId, ct);
        u.RemindersOptOut = optOut;
        audit.Record(optOut ? "reminders.disabled" : "reminders.enabled", u.Id, null);
        await db.SaveChangesAsync(ct);
    }
}
