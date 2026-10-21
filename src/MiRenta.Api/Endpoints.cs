using System.Security.Claims;
using MiRenta.Application.Assistant;
using MiRenta.Application.Auth;
using MiRenta.Application.Cases;
using MiRenta.Application.Common;
using MiRenta.Application.Dian;
using MiRenta.Application.Documents;
using MiRenta.Application.Returns;
using MiRenta.Application.Workers;
using MiRenta.Domain.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MiRenta.Api;

public sealed record EmailRequest(string Email);
public sealed record TokenRequest(string Token);
public sealed record ResetRequest(string Token, string NewPassword);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ReminderSettings(bool OptOut);
public sealed record CreateCaseRequest(int TaxYear = 2025);

public static class Endpoints
{
    public const string Prefix = "/api/v1/my-rent";

    private static CurrentUser Me(ClaimsPrincipal p) =>
        new(Guid.Parse(p.FindFirstValue("sub")!), Enum.TryParse<AccountRole>(p.FindFirstValue("role"), out var r) ? r : AccountRole.Taxpayer);

    public static void MapMiRentaEndpoints(this WebApplication app)
    {
        var root = app.MapGroup(Prefix);

        // ---------------- Autenticación (pública, con límites estrictos) ----------------
        var auth = root.MapGroup("/auth").RequireRateLimiting(RateLimits.Auth);
        auth.MapPost("/register", (RegisterRequest r, AuthService s, CancellationToken ct) => Accepted(s.RegisterAsync(r, ct)));
        auth.MapPost("/resend-verification", (EmailRequest r, AuthService s, CancellationToken ct) => Accepted(s.ResendVerificationAsync(r.Email, ct)));
        auth.MapPost("/verify-email", async (TokenRequest r, AuthService s, CancellationToken ct) =>
        { await s.VerifyEmailAsync(r.Token, ct); return Results.Ok(new GenericMessage("Correo verificado. Ya puedes iniciar sesión.")); });
        auth.MapPost("/login", async (LoginRequest r, AuthService s, CancellationToken ct) =>
        {
            try { return Results.Ok(await s.LoginAsync(r, ct)); }
            catch (AppException e) when (e.Status == 401) { MiRentaMetrics.LoginFailures.Add(1); throw; }
        });
        auth.MapPost("/refresh", async (RefreshRequest r, AuthService s, CancellationToken ct) => Results.Ok(await s.RefreshAsync(r.RefreshToken, ct)));
        auth.MapPost("/logout", async (RefreshRequest r, AuthService s, CancellationToken ct) => { await s.LogoutAsync(r.RefreshToken, ct); return Results.NoContent(); });
        auth.MapPost("/forgot-password", (EmailRequest r, AuthService s, CancellationToken ct) => Accepted(s.ForgotPasswordAsync(r.Email, ct)));
        auth.MapPost("/reset-password", async (ResetRequest r, AuthService s, CancellationToken ct) =>
        { await s.ResetPasswordAsync(r.Token, r.NewPassword, ct); return Results.Ok(new GenericMessage("Contraseña actualizada. Inicia sesión de nuevo.")); });

        root.MapGet("/me", async (ClaimsPrincipal p, AuthService s, CancellationToken ct) => Results.Ok(await s.MeAsync(Me(p).Id, ct))).RequireAuthorization();
        root.MapPut("/me/reminders", async (ReminderSettings r, ClaimsPrincipal p, ReminderPreferences s, CancellationToken ct) =>
        { await s.SetAsync(Me(p).Id, r.OptOut, ct); return Results.NoContent(); }).RequireAuthorization();
        root.MapPost("/reminders/unsubscribe", async (TokenRequest r, ReminderPreferences s, CancellationToken ct) =>
        { await s.UnsubscribeAsync(r.Token, ct); return Results.Ok(new GenericMessage("Ya no recibirás recordatorios.")); }).RequireRateLimiting(RateLimits.Auth);

        // ---------------- Pre-consulta DIAN (pública, desactivada por defecto) ----------------
        var dian = root.MapGroup("/dian").RequireRateLimiting(RateLimits.Dian);
        dian.MapGet("/status", (DianService s) => Results.Ok(s.Status()));
        dian.MapPost("/questions", async (DianQuestionsRequest r, DianService s, CancellationToken ct) => Results.Ok(await s.QuestionsAsync(r, ct)));
        dian.MapPost("/verify-answers", async (DianVerifyRequest r, DianService s, CancellationToken ct) => Results.Ok(await s.VerifyAsync(r, ct)));
        dian.MapPost("/declarant", async (DianDeclarantRequest r, DianService s, CancellationToken ct) => Results.Ok(await s.DeclarantAsync(r, ct)));

        // ---------------- Archivos por enlace firmado de corta duración ----------------
        root.MapGet("/files/{token}", async (string token, DocumentService s, HttpContext http, CancellationToken ct) =>
        {
            var f = await s.ReadByTokenAsync(token, ct);
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            return Results.File(f.Content, f.ContentType, f.FileName);
        }).RequireRateLimiting(RateLimits.Upload);

        // ---------------- Reglas tributarias ----------------
        root.MapGet("/rules", async (RuleService s, CancellationToken ct, int taxYear = 2025) => Results.Ok(await s.ListAsync(taxYear, ct))).RequireAuthorization();
        root.MapPost("/rules/{code}/approve", async (string code, ApproveRuleRequest r, ClaimsPrincipal p, RuleService s, CancellationToken ct) =>
            Results.Ok(await s.ApproveAsync(code, Me(p), r, ct))).RequireAuthorization();

        // ---------------- Expedientes ----------------
        var cases = root.MapGroup("/cases").RequireAuthorization();
        cases.MapGet("", async (ClaimsPrincipal p, CaseService s, CancellationToken ct) => Results.Ok(await s.ListAsync(Me(p), ct)));
        cases.MapPost("", async (CreateCaseRequest? r, ClaimsPrincipal p, CaseService s, CancellationToken ct) =>
            Results.Ok(await s.CreateAsync(Me(p), r?.TaxYear ?? 2025, ct)));
        cases.MapGet("/{caseId:guid}", async (Guid caseId, ClaimsPrincipal p, CaseService s, CancellationToken ct) => Results.Ok(await s.GetAsync(caseId, Me(p), ct)));
        cases.MapGet("/{caseId:guid}/progress", async (Guid caseId, ClaimsPrincipal p, CaseService s, CancellationToken ct) => Results.Ok(await s.ProgressAsync(caseId, Me(p), ct)));
        cases.MapPost("/{caseId:guid}/accountants", async (Guid caseId, AccountantGrantRequest r, ClaimsPrincipal p, CaseService s, CancellationToken ct) =>
        { await s.GrantAccountantAsync(caseId, Me(p), r, ct); return Results.Accepted(value: new GenericMessage("Si el correo corresponde a un contador registrado, tendrá acceso a este expediente.")); });
        cases.MapDelete("/{caseId:guid}/accountants", async (Guid caseId, ClaimsPrincipal p, CaseService s, CancellationToken ct) =>
        { await s.RevokeAccountantsAsync(caseId, Me(p), ct); return Results.NoContent(); });

        // Documentos
        cases.MapGet("/{caseId:guid}/documents", async (Guid caseId, ClaimsPrincipal p, DocumentService s, CancellationToken ct) => Results.Ok(await s.ListAsync(caseId, Me(p), ct)));
        cases.MapPost("/{caseId:guid}/documents", async (Guid caseId, HttpRequest req, ClaimsPrincipal p, DocumentService s, CancellationToken ct) =>
        {
            if (!req.HasFormContentType) throw AppException.BadRequest("multipart_required", "Envía el archivo como multipart/form-data.");
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw AppException.BadRequest("file_required", "Adjunta un archivo.");
            Guid? taskId = Guid.TryParse(form["taskId"], out var t) ? t : null;
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var result = await s.UploadAsync(caseId, Me(p), file.FileName, ms.ToArray(), taskId, ct);
            if (!result.Duplicate) MiRentaMetrics.DocumentsUploaded.Add(1);
            return result.Duplicate ? Results.Ok(result) : Results.Created($"{Prefix}/cases/{caseId}/documents/{result.Document.Id}", result);
        }).RequireRateLimiting(RateLimits.Upload).DisableAntiforgery();
        cases.MapGet("/{caseId:guid}/documents/{documentId:guid}", async (Guid caseId, Guid documentId, ClaimsPrincipal p, DocumentService s, CancellationToken ct) =>
            Results.Ok(await s.GetAsync(caseId, documentId, Me(p), ct)));
        cases.MapPost("/{caseId:guid}/documents/{documentId:guid}/download-url", async (Guid caseId, Guid documentId, ClaimsPrincipal p, DocumentService s, CancellationToken ct) =>
            Results.Ok(await s.CreateDownloadLinkAsync(caseId, documentId, Me(p), ct)));
        cases.MapDelete("/{caseId:guid}/documents/{documentId:guid}", async (Guid caseId, Guid documentId, ClaimsPrincipal p, DocumentService s, CancellationToken ct) =>
        { await s.DeleteAsync(caseId, documentId, Me(p), ct); return Results.NoContent(); });

        // Asistente
        cases.MapGet("/{caseId:guid}/assistant/messages", async (Guid caseId, ClaimsPrincipal p, AssistantService s, CancellationToken ct, int take = 100) =>
            Results.Ok(await s.MessagesAsync(caseId, Me(p), take, ct)));
        cases.MapPost("/{caseId:guid}/assistant/messages", async (Guid caseId, PostMessageRequest r, ClaimsPrincipal p, AssistantService s, CancellationToken ct) =>
            Results.Ok(await s.PostMessageAsync(caseId, Me(p), r, ct)));
        cases.MapGet("/{caseId:guid}/tasks", async (Guid caseId, ClaimsPrincipal p, AssistantService s, CancellationToken ct) => Results.Ok(await s.TasksAsync(caseId, Me(p), ct)));
        cases.MapPost("/{caseId:guid}/tasks/{taskId:guid}/answers", async (Guid caseId, Guid taskId, AnswerRequest r, ClaimsPrincipal p, AssistantService s, CancellationToken ct) =>
            Results.Ok(await s.AnswerAsync(caseId, taskId, Me(p), r, ct)));

        // Hechos, movimientos y conciliaciones
        cases.MapGet("/{caseId:guid}/facts", async (Guid caseId, ClaimsPrincipal p, FactCommandService s, CancellationToken ct, bool history = false) =>
            Results.Ok(await s.ListAsync(caseId, Me(p), history, ct)));
        cases.MapPut("/{caseId:guid}/facts/{code}", async (Guid caseId, string code, SetFactRequest r, ClaimsPrincipal p, FactCommandService s, CancellationToken ct) =>
            Results.Ok(await s.SetAsync(caseId, code, Me(p), r, ct)));
        cases.MapGet("/{caseId:guid}/transactions", async (Guid caseId, ClaimsPrincipal p, ReconciliationService s, CancellationToken ct) => Results.Ok(await s.TransactionsAsync(caseId, Me(p), ct)));
        cases.MapGet("/{caseId:guid}/deposits-summary", async (Guid caseId, ClaimsPrincipal p, ReconciliationService s, CancellationToken ct) => Results.Ok(await s.DepositSummaryAsync(caseId, Me(p), ct)));
        cases.MapGet("/{caseId:guid}/reconciliations", async (Guid caseId, ClaimsPrincipal p, ReconciliationService s, CancellationToken ct) => Results.Ok(await s.ListAsync(caseId, Me(p), ct)));
        cases.MapPost("/{caseId:guid}/reconciliations/{reconciliationId:guid}/resolve", async (Guid caseId, Guid reconciliationId, ResolveRequest r, ClaimsPrincipal p, ReconciliationService s, CancellationToken ct) =>
            Results.Ok(await s.ResolveAsync(caseId, reconciliationId, Me(p), r, ct)));

        // Cálculo, borrador, revisión y exportación
        cases.MapPost("/{caseId:guid}/calculate", async (Guid caseId, ClaimsPrincipal p, CalculationService s, CancellationToken ct) =>
        { var r = await s.CalculateAsync(caseId, Me(p), ct); MiRentaMetrics.Calculations.Add(1); return Results.Ok(r); }).RequireRateLimiting(RateLimits.Calculate);
        cases.MapGet("/{caseId:guid}/calculations/latest", async (Guid caseId, ClaimsPrincipal p, CalculationService s, CancellationToken ct) =>
            await s.LatestAsync(caseId, Me(p), ct) is { } r ? Results.Ok(r) : Results.NoContent());
        cases.MapGet("/{caseId:guid}/draft", async (Guid caseId, ClaimsPrincipal p, DraftService s, CancellationToken ct) =>
            await s.CurrentAsync(caseId, Me(p), ct) is { } d ? Results.Ok(d) : Results.NoContent());
        cases.MapPost("/{caseId:guid}/draft/review", async (Guid caseId, ReviewRequest r, ClaimsPrincipal p, DraftService s, CancellationToken ct) =>
            Results.Ok(await s.ReviewAsync(caseId, Me(p), r, ct)));
        cases.MapGet("/{caseId:guid}/export", async (Guid caseId, ClaimsPrincipal p, DraftService s, CancellationToken ct, [FromQuery] string format = "json") =>
        {
            var f = await s.ExportAsync(caseId, Me(p), format == "csv" ? "csv" : "json", ct);
            MiRentaMetrics.Exports.Add(1);
            return Results.File(f.Content, f.ContentType, f.FileName);
        });
        cases.MapPost("/{caseId:guid}/mark-filed", async (Guid caseId, ClaimsPrincipal p, DraftService s, CancellationToken ct) =>
        { await s.MarkFiledAsync(caseId, Me(p), ct); return Results.NoContent(); });
        cases.MapGet("/{caseId:guid}/issues", async (Guid caseId, ClaimsPrincipal p, CaseAccess access, Infrastructure.Data.MiRentaDbContext db, CancellationToken ct) =>
        {
            var c = await access.GetAsync(caseId, Me(p), CaseAccessLevel.Read, ct);
            return Results.Ok(db.ValidationIssues.Where(i => i.CaseId == c.Id && i.IsOpen).OrderByDescending(i => i.Severity)
                .Select(i => new { i.Id, i.Code, Severity = i.Severity.ToString(), i.Message, i.CreatedAt }).ToList());
        });
    }

    private static async Task<IResult> Accepted(Task<GenericMessage> t) => Results.Accepted(value: await t);
}
