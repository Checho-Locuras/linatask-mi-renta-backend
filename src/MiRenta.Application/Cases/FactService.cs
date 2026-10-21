using MiRenta.Application.Common;
using MiRenta.Domain.Cases;
using MiRenta.Domain.Facts;
using MiRenta.Domain.Returns;
using MiRenta.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MiRenta.Application.Cases;

public sealed record FactDto(Guid Id, string Code, decimal? Amount, string? TextValue, string Source, string Status, int Version,
    IReadOnlyList<Guid> EvidenceIds, DateTime CreatedAt, string? Note);

/// <summary>Hechos versionados: un cambio crea una versión nueva y marca la anterior como reemplazada.</summary>
public sealed class FactService(MiRentaDbContext db, Audit audit, TimeProvider clock)
{
    public async Task<List<TaxFact>> CurrentAsync(Guid caseId, CancellationToken ct) =>
        await db.Facts.Where(f => f.CaseId == caseId && f.Status == FactStatus.Accepted).OrderBy(f => f.Code).ToListAsync(ct);

    /// <summary>Fija el valor vigente de un hecho. Devuelve true si cambió algo.</summary>
    public async Task<bool> SetAsync(TaxCase c, string code, decimal? amount, string? text, FactSource source, Guid userId,
        IReadOnlyList<Guid>? evidenceIds, string? note, CancellationToken ct)
    {
        if (!FactCodes.All.Contains(code)) throw AppException.BadRequest("unknown_fact", "Dato desconocido.");
        if (FactCodes.Monetary.Contains(code) || code == FactCodes.DependentsCount)
        {
            if (amount is null) throw AppException.BadRequest("amount_required", "Se requiere un valor numérico.");
            if (amount < 0 || amount > 1_000_000_000_000m) throw AppException.BadRequest("amount_range", "Valor fuera de rango.");
            amount = decimal.Round(amount.Value, 2);
            if (code == FactCodes.DependentsCount && (amount != decimal.Truncate(amount.Value) || amount > 20))
                throw AppException.BadRequest("integer_required", "El número de dependientes debe ser un entero.");
            text = null;
        }
        else if (FactCodes.YesNo.Contains(code))
        {
            if (text is not ("yes" or "no")) throw AppException.BadRequest("yes_no_required", "Responde sí o no.");
            amount = null;
        }
        else text = text?.Trim() is { Length: > 0 and <= 500 } t ? t : throw AppException.BadRequest("text_required", "Se requiere un texto.");

        var current = await db.Facts.Where(f => f.CaseId == c.Id && f.Code == code && f.Status == FactStatus.Accepted).FirstOrDefaultAsync(ct);
        var evidenceJson = Json.Serialize(evidenceIds ?? []);
        if (current is not null && current.Amount == amount && current.TextValue == text && current.Source == source && current.EvidenceIdsJson == evidenceJson)
            return false;
        var maxVersion = await db.Facts.Where(f => f.CaseId == c.Id && f.Code == code).MaxAsync(f => (int?)f.Version, ct) ?? 0;
        var fact = new TaxFact
        {
            CaseId = c.Id, Code = code, Amount = amount, TextValue = text, Source = source, EvidenceIdsJson = evidenceJson,
            Status = FactStatus.Accepted, Version = maxVersion + 1, CreatedByUserId = userId, Note = note, CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        if (current is not null) { current.Status = FactStatus.Superseded; current.SupersededById = fact.Id; }
        db.Facts.Add(fact);
        audit.Record("fact.set", userId, c.Id, new { code, source = source.ToString(), version = fact.Version });
        await InvalidateDraftsAsync(c, ct);
        return true;
    }

    /// <summary>Si cambian los hechos, cualquier borrador vigente deja de ser válido.</summary>
    public async Task InvalidateDraftsAsync(TaxCase c, CancellationToken ct)
    {
        var drafts = await db.Drafts.Where(d => d.CaseId == c.Id && d.Status != DraftStatus.Superseded).ToListAsync(ct);
        foreach (var d in drafts) d.Status = DraftStatus.Superseded;
    }

    public static FactDto ToDto(TaxFact f) => new(f.Id, f.Code, f.Amount, f.TextValue, f.Source.ToString(), f.Status.ToString(), f.Version,
        Json.Deserialize<List<Guid>>(f.EvidenceIdsJson) ?? [], f.CreatedAt, f.Note);
}
