using System.Text.Json;
using MiRenta.Domain.Cases;
using MiRenta.Domain.Identity;
using MiRenta.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MiRenta.Application.Common;

/// <summary>Error de negocio que la API traduce a ProblemDetails (sin detalles internos).</summary>
public sealed class AppException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static AppException NotFound() => new(404, "not_found", "No encontrado.");
    public static AppException Forbidden(string m = "No tienes permiso para esta acción.") => new(403, "forbidden", m);
    public static AppException BadRequest(string code, string m) => new(400, code, m);
    public static AppException Conflict(string code, string m) => new(409, code, m);
}

/// <summary>Usuario autenticado (extraído del JWT propio de Mi renta).</summary>
public sealed record CurrentUser(Guid Id, AccountRole Role);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

public enum CaseAccessLevel { Read, Write }

/// <summary>
/// Punto único de autorización por expediente. Acceso = dueño, o contador con autorización vigente. Cualquier otro
/// caso devuelve 404 (no 403) para no revelar la existencia de expedientes ajenos (IDOR).
/// </summary>
public sealed class CaseAccess(MiRentaDbContext db)
{
    public async Task<TaxCase> GetAsync(Guid caseId, CurrentUser user, CaseAccessLevel level, CancellationToken ct)
    {
        var c = await db.Cases.FirstOrDefaultAsync(x => x.Id == caseId && x.DeletedAt == null, ct) ?? throw AppException.NotFound();
        if (c.OwnerUserId == user.Id) return c;
        if (user.Role == AccountRole.Accountant && await IsAuthorizedAccountantAsync(caseId, user.Id, ct)) return c;
        throw AppException.NotFound();
    }

    public Task<bool> IsAuthorizedAccountantAsync(Guid caseId, Guid userId, CancellationToken ct) =>
        db.ClientAuthorizations.AnyAsync(a => a.CaseId == caseId && a.AccountantUserId == userId && a.RevokedAt == null, ct);

    public async Task<bool> IsAccountantForCaseAsync(TaxCase c, CurrentUser user, CancellationToken ct) =>
        user.Role == AccountRole.Accountant && c.OwnerUserId != user.Id && await IsAuthorizedAccountantAsync(c.Id, user.Id, ct);
}

public sealed class Audit(MiRentaDbContext db)
{
    /// <summary>Registra la acción. <paramref name="details"/> nunca debe contener documentos, tokens, contraseñas ni respuestas de seguridad.</summary>
    public void Record(string action, Guid? userId, Guid? caseId, object? details = null) =>
        db.AuditEvents.Add(new AuditEvent
        {
            Action = action, UserId = userId, CaseId = caseId,
            DetailsJson = details is null ? null : Json.Serialize(details),
        });
}
