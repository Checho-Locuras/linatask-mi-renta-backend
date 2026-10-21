using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace MiRenta.Infrastructure.Dian;

public sealed class DianOptions
{
    public const string Section = "Dian";
    /// <summary>Desactivado por defecto: los endpoints fueron observados, no documentados oficialmente.</summary>
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://apiazure.dian.gov.co";
    /// <summary>Consulta oficial a la que se redirige al usuario cuando el adaptador está desactivado.</summary>
    public string OfficialConsultUrl { get; set; } = "https://www.dian.gov.co/";
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed record DianQuestion(string IdPregunta, string Texto, IReadOnlyList<DianOption> Opciones);
public sealed record DianOption(string IdRespuesta, string Texto);
public sealed record DianAnswer(string IdPregunta, string IdRespuesta);

/// <summary>
/// Respuesta de la consulta de declarante. Los campos ident* se devuelven SIN interpretar: no hay fuente oficial que
/// documente su significado.
/// </summary>
public sealed record DianDeclarantResult(
    string? NumDocumento, string? Nit, string? TipoDocumento, string? AnoGravable, string? RazonSocial,
    IReadOnlyDictionary<string, string?> RawIndicators, string? Plazo);

public sealed record DianOutcome<T>(bool Disabled, bool Ok, T? Value, string? Error, string OfficialConsultUrl);

public interface IDianPrecheckClient
{
    bool Enabled { get; }
    string OfficialConsultUrl { get; }
    Task<DianOutcome<IReadOnlyList<DianQuestion>>> GetQuestionsAsync(string nit, string tipoDocumento, CancellationToken ct);
    Task<DianOutcome<bool>> VerifyAnswersAsync(IReadOnlyList<DianAnswer> answers, CancellationToken ct);
    /// <summary>El reCAPTCHA lo resuelve el usuario real en su navegador; aquí solo se reenvía y nunca se guarda.</summary>
    Task<DianOutcome<DianDeclarantResult>> CheckDeclarantAsync(string numDocumento, string tipoDocumento, string reCaptcha, CancellationToken ct);
}

public sealed class DianPrecheckClient(HttpClient http, IOptions<DianOptions> options) : IDianPrecheckClient
{
    private DianOptions O => options.Value;
    public bool Enabled => O.Enabled;
    public string OfficialConsultUrl => O.OfficialConsultUrl;

    private DianOutcome<T> Off<T>() => new(true, false, default, "La consulta automática a la DIAN está desactivada. Usa la consulta oficial.", O.OfficialConsultUrl);
    private DianOutcome<T> Err<T>(string e) => new(false, false, default, e, O.OfficialConsultUrl);

    public async Task<DianOutcome<IReadOnlyList<DianQuestion>>> GetQuestionsAsync(string nit, string tipoDocumento, CancellationToken ct)
    {
        if (!Enabled) return Off<IReadOnlyList<DianQuestion>>();
        try
        {
            using var res = await http.GetAsync(
                $"adminidentity/v1/consultapreguntasbynit/{Uri.EscapeDataString(nit)}/tipodocumento/{Uri.EscapeDataString(tipoDocumento)}", ct);
            if (!res.IsSuccessStatusCode) return Err<IReadOnlyList<DianQuestion>>("La DIAN no respondió correctamente.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return new(false, true, ParseQuestions(doc.RootElement), null, O.OfficialConsultUrl);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Err<IReadOnlyList<DianQuestion>>("No fue posible consultar a la DIAN en este momento.");
        }
    }

    public async Task<DianOutcome<bool>> VerifyAnswersAsync(IReadOnlyList<DianAnswer> answers, CancellationToken ct)
    {
        if (!Enabled) return Off<bool>();
        try
        {
            using var res = await http.PostAsJsonAsync("adminidentity/v1/verificarrespuestas",
                answers.Select(a => new { idPregunta = a.IdPregunta, idRespuesta = a.IdRespuesta }), ct);
            return new(false, res.IsSuccessStatusCode, res.IsSuccessStatusCode, res.IsSuccessStatusCode ? null : "Las respuestas no fueron validadas.", O.OfficialConsultUrl);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Err<bool>("No fue posible consultar a la DIAN en este momento.");
        }
    }

    public async Task<DianOutcome<DianDeclarantResult>> CheckDeclarantAsync(string numDocumento, string tipoDocumento, string reCaptcha, CancellationToken ct)
    {
        if (!Enabled) return Off<DianDeclarantResult>();
        try
        {
            using var res = await http.PostAsJsonAsync("exogena/api/v1/terceros/declaracion",
                new { numDocumento, tipoDocumento, reCaptcha }, ct);
            if (!res.IsSuccessStatusCode) return Err<DianDeclarantResult>("La DIAN no respondió correctamente.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return new(false, true, ParseDeclarant(doc.RootElement), null, O.OfficialConsultUrl);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Err<DianDeclarantResult>("No fue posible consultar a la DIAN en este momento.");
        }
    }

    internal static IReadOnlyList<DianQuestion> ParseQuestions(JsonElement root)
    {
        var list = new List<DianQuestion>();
        var arr = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("preguntas", out var p) ? p : default;
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var q in arr.EnumerateArray().Take(10))
        {
            var opts = new List<DianOption>();
            if (q.TryGetProperty("respuestas", out var r) && r.ValueKind == JsonValueKind.Array)
                foreach (var o in r.EnumerateArray().Take(10))
                    opts.Add(new(Str(o, "idRespuesta") ?? "", Str(o, "respuesta") ?? Str(o, "texto") ?? ""));
            list.Add(new(Str(q, "idPregunta") ?? "", Str(q, "pregunta") ?? Str(q, "texto") ?? "", opts));
        }
        return list;
    }

    internal static DianDeclarantResult ParseDeclarant(JsonElement e)
    {
        var raw = new Dictionary<string, string?>();
        foreach (var k in new[] { "ident", "ident1", "ident2", "ident3", "ident4", "ident5", "ident6" }) raw[k] = Str(e, k);
        return new(Str(e, "numDocumento"), Str(e, "nit"), Str(e, "tipoDocumento"), Str(e, "anoGravable"), Str(e, "razonSocial"), raw, Str(e, "plazo"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Null => null, _ => v.GetRawText() }
            : null;
}
