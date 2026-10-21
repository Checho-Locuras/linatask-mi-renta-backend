using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MiRenta.Application.Common;
using MiRenta.Infrastructure.Dian;
using MiRenta.Infrastructure.Security;

namespace MiRenta.Application.Dian;

public sealed record DianQuestionsRequest(string Nit, string TipoDocumento);
public sealed record DianVerifyRequest(IReadOnlyList<DianAnswer> Answers);
public sealed record DianDeclarantRequest(string NumDocumento, string TipoDocumento, string ReCaptcha);

/// <summary>
/// Pre-consulta pública a endpoints observados de la DIAN (no documentados oficialmente). Desactivada por defecto.
/// Nada se persiste: ni el reCAPTCHA, ni las preguntas, ni las respuestas, ni el resultado. Un mismo reCAPTCHA no se
/// acepta dos veces (solo su hash vive en memoria unos minutos).
/// </summary>
public sealed class DianService(IDianPrecheckClient client, TimeProvider clock)
{
    private static readonly ConcurrentDictionary<string, DateTime> UsedCaptchas = new();
    private static readonly string[] DocTypes = ["13", "22", "31", "41"];

    public object Status() => new { enabled = client.Enabled, officialConsultUrl = client.OfficialConsultUrl,
        notice = "Consulta informativa. Los indicadores devueltos por la DIAN no se interpretan: verifica tu situación en la consulta oficial." };

    public Task<DianOutcome<IReadOnlyList<DianQuestion>>> QuestionsAsync(DianQuestionsRequest req, CancellationToken ct)
    {
        Validate(req.Nit, req.TipoDocumento);
        return client.GetQuestionsAsync(req.Nit.Trim(), req.TipoDocumento, ct);
    }

    public Task<DianOutcome<bool>> VerifyAsync(DianVerifyRequest req, CancellationToken ct)
    {
        if (req.Answers is null || req.Answers.Count is 0 or > 10
            || req.Answers.Any(a => !Regex.IsMatch(a.IdPregunta ?? "", @"^[\w-]{1,40}$") || !Regex.IsMatch(a.IdRespuesta ?? "", @"^[\w-]{1,40}$")))
            throw AppException.BadRequest("invalid_answers", "Respuestas inválidas.");
        return client.VerifyAnswersAsync(req.Answers, ct);
    }

    public async Task<DianOutcome<DianDeclarantResult>> DeclarantAsync(DianDeclarantRequest req, CancellationToken ct)
    {
        Validate(req.NumDocumento, req.TipoDocumento);
        if (!client.Enabled) return await client.CheckDeclarantAsync(req.NumDocumento, req.TipoDocumento, "", ct);
        var captcha = req.ReCaptcha?.Trim() ?? "";
        if (captcha.Length is < 20 or > 4000) throw AppException.BadRequest("captcha_required", "Resuelve el reCAPTCHA de la DIAN.");
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var (k, exp) in UsedCaptchas) if (exp < now) UsedCaptchas.TryRemove(k, out _);
        if (!UsedCaptchas.TryAdd(TokenUtil.Sha256Hex(captcha), now.AddMinutes(10)))
            throw AppException.BadRequest("captcha_reused", "Este reCAPTCHA ya se usó. Resuélvelo de nuevo.");
        return await client.CheckDeclarantAsync(req.NumDocumento.Trim(), req.TipoDocumento, captcha, ct);
    }

    private static void Validate(string number, string type)
    {
        if (!Regex.IsMatch(number?.Trim() ?? "", @"^\d{5,12}$")) throw AppException.BadRequest("invalid_document", "Número de documento inválido.");
        if (!DocTypes.Contains(type)) throw AppException.BadRequest("invalid_document_type", "Tipo de documento inválido.");
    }
}
