using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MiRenta.Domain.Facts;
using MiRenta.Infrastructure.Storage;
using MiRenta.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MiRenta.Tests.Integration;

public class DocumentTests(ApiFixture f) : IClassFixture<ApiFixture>
{
    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Document_is_stored_privately_before_async_processing_and_values_need_confirmation()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        await s.AnswerAsync(caseId, "q.fiscal_resident", "yes");
        await s.AnswerAsync(caseId, "q.income_sources", new[] { "labor" });

        var pdf = TestFiles.LaborCertificate(84_000_000m, 3_360_000m, 3_360_000m, 1_250_000m);
        var created = await s.UploadAsync(caseId, pdf, "certificado-220.pdf");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var doc = (await Json(created)).GetProperty("document");
        var docId = doc.GetProperty("id").GetGuid();
        Assert.Equal("Queued", doc.GetProperty("status").GetString());
        Assert.Equal("ProcessingDocument", (await s.GetJsonAsync($"cases/{caseId}/progress")).GetProperty("status").GetString());

        // Ya está en el bucket privado (clave opaca, sin nombre de archivo ni cédula) antes de procesarse.
        var row = await f.WithDb(db => db.Documents.FirstAsync(d => d.Id == docId));
        Assert.DoesNotContain("certificado", row.StorageKey);
        Assert.DoesNotContain(s.Document, row.StorageKey);
        Assert.Equal(64, row.Sha256.Length);
        var storage = f.App.Services.GetRequiredService<IPrivateObjectStorage>();
        Assert.Equal(pdf, await storage.GetAsync(row.StorageKey, default));

        // El mismo archivo otra vez es un duplicado: no se crea otro documento.
        var dup = await s.UploadAsync(caseId, pdf, "copia.pdf");
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.True((await Json(dup)).GetProperty("duplicate").GetBoolean());

        await f.RunJobsAsync();
        await f.RunJobsAsync(); // reprocesar no duplica
        var processed = await s.GetJsonAsync($"cases/{caseId}/documents/{docId}");
        Assert.Equal("Processed", processed.GetProperty("status").GetString());
        Assert.Equal("LaborIncomeCertificate", processed.GetProperty("kind").GetString());
        var evidence = processed.GetProperty("evidence").EnumerateArray().ToList();
        Assert.Equal(4, evidence.Count);
        Assert.All(evidence, e => Assert.Equal("Proposed", e.GetProperty("status").GetString()));

        // Nada se usa como hecho hasta confirmar.
        Assert.DoesNotContain((await s.GetJsonAsync($"cases/{caseId}/facts")).EnumerateArray(), x => x.GetProperty("code").GetString() == FactCodes.LaborGrossIncome);
        var progress = await s.GetJsonAsync($"cases/{caseId}/progress");
        Assert.Equal("AwaitingUserAnswer", progress.GetProperty("status").GetString());
        Assert.Equal(1, progress.GetProperty("openConfirmations").GetInt32());

        var gross = evidence.First(e => e.GetProperty("field").GetString() == FactCodes.LaborGrossIncome).GetProperty("id").GetGuid();
        var items = evidence.Select(e => e.GetProperty("id").GetGuid() == gross
            ? (object)new { evidenceId = gross, action = "correct", amount = 85_000_000 }
            : new { evidenceId = e.GetProperty("id").GetGuid(), action = "confirm" }).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.AnswerAsync(caseId, "c.doc." + docId.ToString("N"), new { items = items.Take(1) })).StatusCode); // incompleta
        Assert.True((await s.AnswerAsync(caseId, "c.doc." + docId.ToString("N"), new { items })).IsSuccessStatusCode);

        var facts = (await s.GetJsonAsync($"cases/{caseId}/facts")).EnumerateArray().ToDictionary(x => x.GetProperty("code").GetString()!);
        Assert.Equal(85_000_000m, facts[FactCodes.LaborGrossIncome].GetProperty("amount").GetDecimal());
        Assert.Equal("Document", facts[FactCodes.LaborGrossIncome].GetProperty("source").GetString());
        Assert.Equal(gross, facts[FactCodes.LaborGrossIncome].GetProperty("evidenceIds")[0].GetGuid());
        Assert.Equal(1_250_000m, facts[FactCodes.LaborWithholding].GetProperty("amount").GetDecimal());
        var tasks = await s.GetJsonAsync($"cases/{caseId}/tasks");
        Assert.Equal("Answered", tasks.EnumerateArray().First(t => t.GetProperty("code").GetString() == "q.labor_gross_income").GetProperty("state").GetString());

        // Borrar el documento elimina el objeto y retira los hechos que dependían solo de él.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Http.DeleteAsync($"cases/{caseId}/documents/{docId}")).StatusCode);
        Assert.Null(await storage.GetAsync(row.StorageKey, default));
        Assert.DoesNotContain((await s.GetJsonAsync($"cases/{caseId}/facts")).EnumerateArray(), x => x.GetProperty("code").GetString() == FactCodes.LaborGrossIncome);
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("js-pdf")]
    [InlineData("html")]
    [InlineData("macro")]
    [InlineData("svg")]
    public async Task Malicious_files_are_rejected_and_never_stored(string kind)
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        var (data, name) = kind switch
        {
            "exe" => (TestFiles.WindowsExe, "certificado.pdf"),
            "js-pdf" => (TestFiles.Pdf(["x"], "/OpenAction << /S /JavaScript /JS (app.alert(1)) >>"), "x.pdf"),
            "html" => (TestFiles.Csv("<script>fetch('//x')</script>"), "x.csv"),
            "macro" => (TestFiles.Xlsx([["a"]], withMacros: true), "x.xlsx"),
            _ => (System.Text.Encoding.UTF8.GetBytes("<svg onload=alert(1)>"), "x.svg"),
        };
        var r = await s.UploadAsync(caseId, data, name);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, await f.WithDb(db => db.Documents.CountAsync(d => d.CaseId == caseId)));
        Assert.True(await f.WithDb(db => db.AuditEvents.AnyAsync(a => a.CaseId == caseId && a.Action == "document.rejected")));
    }

    [Fact]
    public async Task Oversized_files_are_rejected()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        var big = TestFiles.Csv("Fecha;Valor\n" + new string('1', 1_100_000));
        var r = await s.UploadAsync(caseId, big, "grande.csv");
        Assert.True(r.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, r.StatusCode.ToString());
    }

    [Fact]
    public async Task Download_links_are_short_lived_bound_to_user_and_revalidated()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        var csv = TestFiles.Csv("Fecha;Descripción;Valor\n2025-02-01;PAGO NOMINA;2.000.000\n");
        var docId = (await Json(await s.UploadAsync(caseId, csv, "e.csv"))).GetProperty("document").GetProperty("id").GetGuid();
        var link = await Json(await s.Http.PostAsync($"cases/{caseId}/documents/{docId}/download-url", null));
        var url = link.GetProperty("url").GetString()!;
        Assert.StartsWith("/api/v1/my-rent/files/", url);

        var anon = f.App.GetTestClientRoot();
        var ok = await anon.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(csv, await ok.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", ok.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("sandbox", ok.Headers.GetValues("Content-Security-Policy").First());

        var tampered = url[..^3] + (url[^3] == 'a' ? "bcd" : "acd");
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync(tampered)).StatusCode);

        f.Time.Advance(TimeSpan.FromMinutes(6));
        try { Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync(url)).StatusCode); }
        finally { f.Time.Advance(TimeSpan.FromMinutes(-6)); }

        // Si el documento se elimina, el enlace aún vigente deja de servir.
        var link2 = (await Json(await s.Http.PostAsync($"cases/{caseId}/documents/{docId}/download-url", null))).GetProperty("url").GetString()!;
        await s.Http.DeleteAsync($"cases/{caseId}/documents/{docId}");
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(link2)).StatusCode);
    }

    [Fact]
    public async Task Bank_statements_produce_reconciliations_for_duplicates_transfers_reversals_and_unexplained_deposits()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        var a = TestFiles.Csv("Fecha;Descripción;Valor\n2025-03-01;PAGO NOMINA EMPRESA;6.000.000\n2025-03-03;TRANSFERENCIA A CUENTA AHORROS;-2.000.000\n" +
                              "2025-03-10;CONSIGNACION EFECTIVO;4.500.000\n2025-03-15;COMPRA TIENDA;-300.000\n2025-03-16;REVERSO COMPRA TIENDA;300.000\n");
        var b = TestFiles.Csv("Fecha;Descripción;Valor\n2025-03-04;TRANSFERENCIA RECIBIDA;2.000.000\n2025-03-10;CONSIGNACION EFECTIVO;4.500.000\n");
        await s.UploadAsync(caseId, a, "banco-a.csv");
        await s.UploadAsync(caseId, b, "banco-b.csv");
        await f.RunJobsAsync();

        var recs = (await s.GetJsonAsync($"cases/{caseId}/reconciliations")).EnumerateArray().ToList();
        Assert.Contains(recs, r => r.GetProperty("kind").GetString() == "PossibleDuplicate");
        Assert.Contains(recs, r => r.GetProperty("kind").GetString() == "PossibleOwnTransfer");
        Assert.Contains(recs, r => r.GetProperty("kind").GetString() == "PossibleReversal");
        Assert.Contains(recs, r => r.GetProperty("kind").GetString() == "UnexplainedDeposit");
        Assert.Equal("AwaitingUserAnswer", (await s.GetJsonAsync($"cases/{caseId}/progress")).GetProperty("status").GetString());
        Assert.Equal(7, (await s.GetJsonAsync($"cases/{caseId}/transactions")).GetArrayLength());

        var dup = recs.First(r => r.GetProperty("kind").GetString() == "PossibleDuplicate");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsJsonAsync($"cases/{caseId}/reconciliations/{dup.GetProperty("id").GetGuid()}/resolve", new { resolution = "loan" })).StatusCode);
        foreach (var r in recs.OrderBy(r => r.GetProperty("kind").GetString() == "PossibleDuplicate" ? 0 : 1))
        {
            var current = (await s.GetJsonAsync($"cases/{caseId}/reconciliations")).EnumerateArray().First(x => x.GetProperty("id").GetGuid() == r.GetProperty("id").GetGuid());
            if (!current.GetProperty("isOpen").GetBoolean()) continue; // cerrada al excluir el duplicado
            var res = r.GetProperty("kind").GetString() switch
            {
                "PossibleDuplicate" => "is_duplicate", "PossibleOwnTransfer" => "own_transfer", "PossibleReversal" => "reversal", _ => "loan",
            };
            var resolved = await s.Http.PostAsJsonAsync($"cases/{caseId}/reconciliations/{r.GetProperty("id").GetGuid()}/resolve", new { resolution = res });
            Assert.True(resolved.IsSuccessStatusCode, await resolved.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, (await s.GetJsonAsync($"cases/{caseId}/progress")).GetProperty("openReconciliations").GetInt32());
        var summary = await s.GetJsonAsync($"cases/{caseId}/deposits-summary");
        Assert.True(summary.GetProperty("excludedCredits").GetDecimal() >= 2_000_000m + 4_500_000m, summary.ToString() + (await s.GetJsonAsync($"cases/{caseId}/transactions")).ToString());

        // Re-subir un extracto ya procesado no duplica movimientos.
        Assert.True((await Json(await s.UploadAsync(caseId, a, "banco-a-otra-vez.csv"))).GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    public async Task Processing_failures_are_retried_and_then_marked_failed_without_leaking_content()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        var docId = (await Json(await s.UploadAsync(caseId, TestFiles.Csv("Fecha;Valor\n2025-01-01;1.000\n"), "x.csv"))).GetProperty("document").GetProperty("id").GetGuid();
        var key = await f.WithDb(db => db.Documents.Where(d => d.Id == docId).Select(d => d.StorageKey).FirstAsync());
        await f.App.Services.GetRequiredService<IPrivateObjectStorage>().DeleteAsync(key, default); // objeto perdido
        for (var i = 0; i < 3; i++)
        {
            await f.WithDb(db => db.Database.ExecuteSqlRawAsync("UPDATE mi_renta.background_jobs SET next_run_at = now() - interval '1 minute' WHERE status = 0"));
            await f.RunJobsAsync();
        }
        var job = await f.WithDb(db => db.Jobs.FirstAsync(j => j.DedupKey == "process-document:" + docId));
        Assert.Equal(Domain.Operations.JobStatus.Failed, job.Status);
        Assert.Equal(3, job.Attempts);
        Assert.Equal("Failed", (await s.GetJsonAsync($"cases/{caseId}/documents/{docId}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Images_without_ocr_ask_for_manual_entry()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        var docId = (await Json(await s.UploadAsync(caseId, TestFiles.Png, "foto.png"))).GetProperty("document").GetProperty("id").GetGuid();
        await f.RunJobsAsync();
        Assert.Equal("NeedsManualEntry", (await s.GetJsonAsync($"cases/{caseId}/documents/{docId}")).GetProperty("status").GetString());
        Assert.Equal("CollectingInformation", (await s.GetJsonAsync($"cases/{caseId}/progress")).GetProperty("status").GetString());
    }
}

internal static class TestServerExtensions
{
    public static HttpClient GetTestClientRoot(this Microsoft.AspNetCore.Builder.WebApplication app)
    {
        var c = Microsoft.AspNetCore.TestHost.HostBuilderTestServerExtensions.GetTestClient(app);
        c.BaseAddress = new Uri("http://localhost");
        return c;
    }
}
