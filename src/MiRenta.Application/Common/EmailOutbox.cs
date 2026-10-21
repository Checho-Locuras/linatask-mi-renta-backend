using MiRenta.Domain.Operations;
using MiRenta.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MiRenta.Application.Common;

public static class EmailOutbox
{
    /// <summary>Encola un correo de forma idempotente (dedupKey única). Devuelve false si ya existía.</summary>
    public static async Task<bool> QueueAsync(MiRentaDbContext db, Guid? userId, Guid? caseId, string to, string kind,
        string subject, string body, string dedupKey, CancellationToken ct, DateTime? scheduledFor = null)
    {
        if (await db.Emails.AnyAsync(e => e.DedupKey == dedupKey, ct)) return false;
        db.Emails.Add(new OutboundEmail
        {
            UserId = userId, CaseId = caseId, ToAddress = to, Kind = kind, Subject = subject, Body = body,
            DedupKey = dedupKey, ScheduledFor = scheduledFor ?? DateTime.UtcNow,
        });
        return true;
    }
}
