using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MiRenta.Domain.Facts;
using MiRenta.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MiRenta.Tests.Integration;

public class CaseFlowTests(ApiFixture f) : IClassFixture<ApiFixture>
{
    private static string Status(JsonElement progress) => progress.GetProperty("status").GetString()!;

    [Fact]
    public async Task Creating_a_case_is_idempotent_and_starts_the_assistant()
    {
        var s = await f.RegisterVerifiedAsync();
        var a = await s.CreateCaseAsync();
        var b = await s.CreateCaseAsync();
        Assert.Equal(a, b);
        var progress = await s.GetJsonAsync($"cases/{a}/progress");
        Assert.Equal("CollectingInformation", Status(progress));
        // Pregunta progresiva: solo la siguiente pregunta (más solicitudes de documento opcionales).
        Assert.Equal("q.fiscal_resident", progress.GetProperty("nextTasks").EnumerateArray().First(t => t.GetProperty("kind").GetString() == "Question").GetProperty("code").GetString());
        var messages = await s.GetJsonAsync($"cases/{a}/assistant/messages");
        Assert.Contains("contraseña de la DIAN", messages[0].GetProperty("content").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsJsonAsync("cases", new { taxYear = 2024 })).StatusCode);
    }

    [Fact]
    public async Task Questions_are_progressive_and_answers_are_idempotent()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        var tasks = await s.GetJsonAsync($"cases/{caseId}/tasks");
        Assert.DoesNotContain(tasks.EnumerateArray(), t => t.GetProperty("code").GetString() == "q.labor_gross_income");

        Assert.True((await s.AnswerAsync(caseId, "q.fiscal_resident", "yes", "k1")).IsSuccessStatusCode);
        Assert.True((await s.AnswerAsync(caseId, "q.fiscal_resident", "yes", "k1")).IsSuccessStatusCode); // reintento
        Assert.True((await s.AnswerAsync(caseId, "q.income_sources", new[] { "labor" }, "k2")).IsSuccessStatusCode);
        tasks = await s.GetJsonAsync($"cases/{caseId}/tasks");
        Assert.Contains(tasks.EnumerateArray(), t => t.GetProperty("code").GetString() == "q.labor_gross_income" && t.GetProperty("state").GetString() == "Open");
        Assert.Equal(1, await f.WithDb(db => db.Facts.CountAsync(x => x.CaseId == caseId && x.Code == FactCodes.FiscalResident)));
        Assert.Equal(1, await f.WithDb(db => db.UserResponses.CountAsync(x => x.IdempotencyKey == "k1")));

        Assert.Equal(HttpStatusCode.BadRequest, (await s.AnswerAsync(caseId, "q.iva_responsible", "tal vez")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.AnswerAsync(caseId, "q.labor_gross_income", -5)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.AnswerAsync(caseId, "q.income_sources", new[] { "none", "labor" })).StatusCode);

        // Cambiar la respuesta crea una nueva versión del hecho y oculta preguntas que ya no aplican.
        Assert.True((await s.AnswerAsync(caseId, "q.income_sources", new[] { "none" })).IsSuccessStatusCode);
        tasks = await s.GetJsonAsync($"cases/{caseId}/tasks");
        Assert.Equal("Cancelled", tasks.EnumerateArray().First(t => t.GetProperty("code").GetString() == "q.labor_gross_income").GetProperty("state").GetString());
        var history = await s.GetJsonAsync($"cases/{caseId}/facts?history=true");
        Assert.Equal(2, history.EnumerateArray().Count(x => x.GetProperty("code").GetString() == FactCodes.IncomeSources));
    }

    [Fact]
    public async Task Messages_are_persistent_idempotent_and_never_store_dian_passwords()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        var r1 = await s.Http.PostAsJsonAsync($"cases/{caseId}/assistant/messages", new { content = "¿Debo declarar?", idempotencyKey = "m-1" });
        var r2 = await s.Http.PostAsJsonAsync($"cases/{caseId}/assistant/messages", new { content = "¿Debo declarar?", idempotencyKey = "m-1" });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal((await r1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reply").GetProperty("id").GetGuid(),
            (await r2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reply").GetProperty("id").GetGuid());

        var secret = await s.Http.PostAsJsonAsync($"cases/{caseId}/assistant/messages", new { content = "mi contraseña de la DIAN es Sup3rS3creta! y mi cédula 1098765432" });
        var reply = (await secret.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reply").GetProperty("content").GetString();
        Assert.Contains("no guardo contraseñas", reply);
        var stored = await f.WithDb(db => db.Messages.Select(m => m.Content).ToListAsync());
        Assert.DoesNotContain(stored, m => m.Contains("Sup3rS3creta") || m.Contains("1098765432"));
        var all = await s.GetJsonAsync($"cases/{caseId}/assistant/messages");
        Assert.Equal(5, all.GetArrayLength()); // bienvenida + 2 pares
    }

    [Fact]
    public async Task Other_users_and_unauthorized_accountants_get_404_for_every_case_resource()
    {
        var owner = await f.RegisterVerifiedAsync();
        var caseId = await owner.CreateCaseAsync();
        var upload = await owner.UploadAsync(caseId, TestFiles.Csv("Fecha;Descripción;Valor\n2025-01-10;PAGO NOMINA;3.000.000\n"), "extracto.csv");
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());
        var docId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("id").GetGuid();
        var intruder = await f.RegisterVerifiedAsync();
        var accountant = await AccountantAsync();

        foreach (var who in new[] { intruder, accountant })
        {
            foreach (var url in new[] { $"cases/{caseId}", $"cases/{caseId}/progress", $"cases/{caseId}/tasks", $"cases/{caseId}/facts", $"cases/{caseId}/documents",
                         $"cases/{caseId}/documents/{docId}", $"cases/{caseId}/assistant/messages", $"cases/{caseId}/reconciliations", $"cases/{caseId}/draft",
                         $"cases/{caseId}/calculations/latest", $"cases/{caseId}/export", $"cases/{caseId}/transactions" })
                Assert.Equal(HttpStatusCode.NotFound, (await who.Http.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await who.Http.PostAsync($"cases/{caseId}/calculate", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await who.Http.PostAsync($"cases/{caseId}/documents/{docId}/download-url", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await who.Http.DeleteAsync($"cases/{caseId}/documents/{docId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await who.Http.PostAsJsonAsync($"cases/{caseId}/assistant/messages", new { content = "hola" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await who.Http.PutAsJsonAsync($"cases/{caseId}/facts/{FactCodes.GrossAssets}", new { amount = 1 })).StatusCode);
            Assert.Empty((await who.GetJsonAsync("cases")).EnumerateArray());
        }
    }

    [Fact]
    public async Task Full_flow_from_questions_to_accountant_approval_export_and_filing()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();

        // Antes de responder: calcular no está permitido.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Http.PostAsync($"cases/{caseId}/calculate", null)).StatusCode);
        await s.AnswerSalariedProfileAsync(caseId);
        var progress = await s.GetJsonAsync($"cases/{caseId}/progress");
        Assert.Equal("NeedsProfessionalReview", Status(progress));
        Assert.True(progress.GetProperty("needsAccountant").GetBoolean());
        Assert.True(progress.GetProperty("canCalculate").GetBoolean());

        // Vista previa (bloqueada por los valores del contador): no genera borrador.
        var preview = await (await s.Http.PostAsync($"cases/{caseId}/calculate", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Blocked", preview.GetProperty("status").GetString());
        Assert.True(preview.GetProperty("isPreview").GetBoolean());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("draftId").ValueKind);

        // El contribuyente no puede fijar los conceptos de contador.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Http.PutAsJsonAsync($"cases/{caseId}/facts/{FactCodes.AdvancePaymentNextYear}", new { amount = 0 })).StatusCode);

        var accountant = await f.RegisterVerifiedAsync();
        // Un usuario que no es contador no obtiene acceso aunque se le "autorice" (respuesta genérica).
        Assert.Equal(HttpStatusCode.Accepted, (await s.Http.PostAsJsonAsync($"cases/{caseId}/accountants", new { accountantEmail = accountant.Email })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await accountant.Http.GetAsync($"cases/{caseId}")).StatusCode);

        var real = await AccountantAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await s.Http.PostAsJsonAsync($"cases/{caseId}/accountants", new { accountantEmail = ApiFixture.AccountantEmail })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await real.Http.GetAsync($"cases/{caseId}")).StatusCode);
        Assert.Single((await real.GetJsonAsync("cases")).EnumerateArray(), c => c.GetProperty("id").GetGuid() == caseId);
        Assert.Equal(HttpStatusCode.OK, (await real.Http.PutAsJsonAsync($"cases/{caseId}/facts/{FactCodes.AdvancePaymentNextYear}", new { amount = 0, note = "Primer año declarando" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await real.Http.PutAsJsonAsync($"cases/{caseId}/facts/{FactCodes.PreviousYearAdvance}", new { amount = 0 })).StatusCode);
        Assert.Equal("ReadyForCalculation", Status(await s.GetJsonAsync($"cases/{caseId}/progress")));

        var calc = await (await s.Http.PostAsync($"cases/{caseId}/calculate", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Preliminary", calc.GetProperty("status").GetString());
        var tax = calc.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("concept").GetString() == "tax.income").GetProperty("value").GetDecimal();
        Assert.Equal(5_419_000m, tax);
        Assert.Equal("ReadyForReview", Status(await s.GetJsonAsync($"cases/{caseId}/progress")));

        var draft = await s.GetJsonAsync($"cases/{caseId}/draft");
        Assert.All(draft.GetProperty("lines").EnumerateArray(), l => Assert.Equal(JsonValueKind.Null, l.GetProperty("boxNumber").ValueKind));
        var draftId = draft.GetProperty("id").GetGuid();
        var version = draft.GetProperty("version").GetInt32();

        // Exportar antes de la aprobación está prohibido; el contribuyente no puede auto-aprobarse.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Http.GetAsync($"cases/{caseId}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Http.PostAsJsonAsync($"cases/{caseId}/draft/review", new { draftId, version, decision = "approve", acknowledgePendingRules = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await real.Http.PostAsJsonAsync($"cases/{caseId}/draft/review", new { draftId, version, decision = "approve" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await real.Http.PostAsJsonAsync($"cases/{caseId}/draft/review", new { draftId, version = version + 1, decision = "approve", acknowledgePendingRules = true })).StatusCode);
        var approved = await real.Http.PostAsJsonAsync($"cases/{caseId}/draft/review", new { draftId, version, decision = "approve", comment = "Revisado", acknowledgePendingRules = true });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var approvedJson = await approved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(real.UserId, approvedJson.GetProperty("reviewedByUserId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, approvedJson.GetProperty("reviewedAt").ValueKind);

        // Con el expediente aprobado ya no se aceptan cambios.
        Assert.Equal(HttpStatusCode.Conflict, (await s.AnswerAsync(caseId, "q.debts", 1)).StatusCode);

        var csv = await s.Http.GetAsync($"cases/{caseId}/export?format=csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var csvText = await csv.Content.ReadAsStringAsync();
        Assert.Contains("por verificar", csvText);
        Assert.Contains("No es la declaración oficial", csvText);
        var json = await s.Http.GetAsync($"cases/{caseId}/export");
        Assert.Equal("application/json", json.Content.Headers.ContentType!.MediaType);
        Assert.Equal(2, await f.WithDb(db => db.AuditEvents.CountAsync(a => a.CaseId == caseId && a.Action == "draft.exported")));

        Assert.Equal(HttpStatusCode.Forbidden, (await real.Http.PostAsync($"cases/{caseId}/mark-filed", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Http.PostAsync($"cases/{caseId}/mark-filed", null)).StatusCode);
        Assert.Equal("Completed", Status(await s.GetJsonAsync($"cases/{caseId}/progress")));

        // Ningún correo contiene cifras de la declaración.
        var bodies = await f.WithDb(db => db.Emails.Where(e => e.CaseId == caseId).Select(e => e.Body).ToListAsync());
        Assert.DoesNotContain(bodies, b => b != null && (b.Contains("5.419.000") || b.Contains("120.000.000")));
    }

    [Fact]
    public async Task Changing_facts_after_the_draft_supersedes_it_and_accountant_rejection_returns_to_review()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        await s.AnswerSalariedProfileAsync(caseId);
        var acc = await AccountantAsync();
        await s.Http.PostAsJsonAsync($"cases/{caseId}/accountants", new { accountantEmail = ApiFixture.AccountantEmail });
        await acc.Http.PutAsJsonAsync($"cases/{caseId}/facts/{FactCodes.AdvancePaymentNextYear}", new { amount = 0 });
        await acc.Http.PutAsJsonAsync($"cases/{caseId}/facts/{FactCodes.PreviousYearAdvance}", new { amount = 0 });
        await s.Http.PostAsync($"cases/{caseId}/calculate", null);
        var draft1 = await s.GetJsonAsync($"cases/{caseId}/draft");

        Assert.True((await s.AnswerAsync(caseId, "q.debts", 20_000_000)).IsSuccessStatusCode);
        Assert.Equal("ReadyForCalculation", Status(await s.GetJsonAsync($"cases/{caseId}/progress")));
        Assert.Equal(HttpStatusCode.NoContent, (await s.Http.GetAsync($"cases/{caseId}/draft")).StatusCode);

        await s.Http.PostAsync($"cases/{caseId}/calculate", null);
        var draft2 = await s.GetJsonAsync($"cases/{caseId}/draft");
        Assert.Equal(draft1.GetProperty("version").GetInt32() + 1, draft2.GetProperty("version").GetInt32());
        var reject = await acc.Http.PostAsJsonAsync($"cases/{caseId}/draft/review",
            new { draftId = draft2.GetProperty("id").GetGuid(), version = draft2.GetProperty("version").GetInt32(), decision = "reject", comment = "Falta certificado" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.Equal("NeedsProfessionalReview", Status(await s.GetJsonAsync($"cases/{caseId}/progress")));

        // Revocar al contador le quita el acceso de inmediato.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Http.DeleteAsync($"cases/{caseId}/accountants")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await acc.Http.GetAsync($"cases/{caseId}")).StatusCode);
    }

    [Fact]
    public async Task Unsupported_income_and_non_residents_are_routed_out_of_automatic_calculation()
    {
        var s = await f.RegisterVerifiedAsync();
        var caseId = await s.CreateCaseAsync();
        await s.AnswerAsync(caseId, "q.fiscal_resident", "no");
        var p = await s.GetJsonAsync($"cases/{caseId}/progress");
        Assert.Equal("Blocked", Status(p));
        Assert.False(p.GetProperty("canCalculate").GetBoolean());

        var s2 = await f.RegisterVerifiedAsync();
        var c2 = await s2.CreateCaseAsync();
        await s2.AnswerAsync(c2, "q.fiscal_resident", "yes");
        await s2.AnswerAsync(c2, "q.income_sources", new[] { "labor", "dividends" });
        Assert.Equal("yes", (await s2.GetJsonAsync($"cases/{c2}/facts")).EnumerateArray().First(x => x.GetProperty("code").GetString() == FactCodes.HasDividends).GetProperty("textValue").GetString());
    }

    [Fact]
    public async Task Rules_are_listed_and_only_rules_admins_can_approve_them()
    {
        var s = await f.RegisterVerifiedAsync();
        var rules = await s.GetJsonAsync("rules");
        Assert.All(rules.EnumerateArray(), r => Assert.NotEqual("Approved", r.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Http.PostAsJsonAsync("rules/uvt/approve", new { note = "x" })).StatusCode);

        var admin = await f.RegisterVerifiedAsync(ApiFixture.RulesAdminEmail);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Http.PostAsJsonAsync("rules/advance.807/approve", new { note = "x" })).StatusCode);
        var ok = await admin.Http.PostAsJsonAsync("rules/uvt/approve", new { note = "Verificada Res. 000193/2024 en el normograma DIAN" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var json = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Approved", json.GetProperty("status").GetString());
        Assert.Equal(admin.UserId, json.GetProperty("approvedByUserId").GetGuid());
        await f.WithDb(async db =>
        {
            var r = await db.Rules.FirstAsync(x => x.Code == "uvt");
            r.Status = Domain.Returns.RuleStatus.PendingProfessionalApproval; r.ApprovedByUserId = null; r.ApprovedAt = null;
            return await db.SaveChangesAsync();
        });
    }

    private Session? _accountant;
    private async Task<Session> AccountantAsync()
    {
        if (_accountant is not null) return _accountant;
        var exists = await f.WithDb(db => db.Users.AnyAsync(u => u.Email == ApiFixture.AccountantEmail));
        if (!exists) return _accountant = await f.RegisterVerifiedAsync(ApiFixture.AccountantEmail);
        var login = await f.Client().PostAsJsonAsync("auth/login", new { identifier = ApiFixture.AccountantEmail, password = ApiFixture.Password });
        var j = await login.Content.ReadFromJsonAsync<JsonElement>();
        return _accountant = new Session(f, ApiFixture.AccountantEmail, "", j.GetProperty("accessToken").GetString()!, j.GetProperty("refreshToken").GetString()!,
            j.GetProperty("user").GetProperty("id").GetGuid());
    }
}
