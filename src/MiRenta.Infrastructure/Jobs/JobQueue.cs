using System.Text.Json;
using MiRenta.Domain.Operations;
using MiRenta.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MiRenta.Infrastructure.Jobs;

/// <summary>Cola persistente sobre PostgreSQL (FOR UPDATE SKIP LOCKED): sobrevive reinicios y admite varias réplicas.</summary>
public static class JobQueue
{
    public static async Task<bool> EnqueueAsync(MiRentaDbContext db, string kind, object payload, string? dedupKey, CancellationToken ct,
        DateTime? runAt = null)
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(payload);
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mi_renta.background_jobs (id, kind, payload_json, dedup_key, status, attempts, max_attempts, next_run_at, created_at)
            VALUES ({id}, {kind}, CAST({json} AS jsonb), {dedupKey}, 0, 0, 3, {runAt ?? DateTime.UtcNow}, {DateTime.UtcNow})
            ON CONFLICT (dedup_key) WHERE dedup_key IS NOT NULL DO NOTHING
            """, ct);
        return rows > 0;
    }

    /// <summary>Toma el siguiente trabajo vencido y lo bloquea durante <paramref name="lease"/>.</summary>
    public static async Task<BackgroundJob?> ClaimAsync(MiRentaDbContext db, TimeSpan lease, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(lease);
        var list = await db.Jobs.FromSqlInterpolated($"""
            UPDATE mi_renta.background_jobs SET status = 1, attempts = attempts + 1, locked_until = {until}
            WHERE id = (
              SELECT id FROM mi_renta.background_jobs
              WHERE (status = 0 AND next_run_at <= {now}) OR (status = 1 AND locked_until < {now})
              ORDER BY next_run_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);
        return list.FirstOrDefault();
    }

    public static async Task CompleteAsync(MiRentaDbContext db, Guid id, CancellationToken ct) =>
        await db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Succeeded).SetProperty(j => j.CompletedAt, DateTime.UtcNow)
            .SetProperty(j => j.LockedUntil, (DateTime?)null).SetProperty(j => j.LastError, (string?)null), ct);

    public static async Task FailAsync(MiRentaDbContext db, BackgroundJob job, string error, CancellationToken ct)
    {
        var final = job.Attempts >= job.MaxAttempts;
        var next = DateTime.UtcNow.AddSeconds(Math.Pow(4, job.Attempts) * 5);
        var msg = error.Length > 500 ? error[..500] : error;
        await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, final ? JobStatus.Failed : JobStatus.Pending)
            .SetProperty(j => j.NextRunAt, next).SetProperty(j => j.LockedUntil, (DateTime?)null)
            .SetProperty(j => j.LastError, msg), ct);
    }
}
