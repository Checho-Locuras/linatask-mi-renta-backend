using System.Text.Json;
using System.Text.RegularExpressions;
using MiRenta.Application.Cases;
using MiRenta.Application.Common;
using MiRenta.Domain.Assistant;
using MiRenta.Domain.Cases;
using MiRenta.Domain.Documents;
using MiRenta.Domain.Facts;
using MiRenta.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MiRenta.Application.Assistant;

public sealed record MessageDto(Guid Id, string Role, string Content, DateTime CreatedAt);
public sealed record PostMessageRequest(string Content, string? IdempotencyKey);
public sealed record PostMessageResponse(MessageDto UserMessage, MessageDto Reply, ProgressDto Progress);
public sealed record AnswerRequest(JsonElement Value, string? IdempotencyKey);
public sealed record AnswerResponse(TaskDto Task, ProgressDto Progress);

/// <summary>
/// Asistente conversacional persistente. Las respuestas a tareas se convierten en hechos; los mensajes libres solo
/// reciben orientación determinista (no cambian datos). Todo es idempotente por clave del cliente.
/// </summary>
public sealed class AssistantService(MiRentaDbContext db, CaseAccess access, CaseWorkflow workflow, FactService facts,
    CaseService cases, Audit audit, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<List<MessageDto>> MessagesAsync(Guid caseId, CurrentUser user, int take, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        var conv = await db.Conversations.FirstAsync(x => x.CaseId == c.Id, ct);
        var list = await db.Messages.Where(m => m.ConversationId == conv.Id).OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Role)
            .Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return list.OrderBy(m => m.CreatedAt).ThenBy(m => m.Role).Select(ToDto).ToList();
    }

    public async Task<List<TaskDto>> TasksAsync(Guid caseId, CurrentUser user, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Read, ct);
        return (await db.Tasks.Where(t => t.CaseId == c.Id).OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt).ToListAsync(ct))
            .Select(CaseService.ToTaskDto).ToList();
    }

    public async Task<PostMessageResponse> PostMessageAsync(Guid caseId, CurrentUser user, PostMessageRequest req, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        var text = (req.Content ?? "").Trim();
        if (text.Length is 0 or > 2000) throw AppException.BadRequest("invalid_message", "El mensaje debe tener entre 1 y 2000 caracteres.");
        var key = NormalizeKey(req.IdempotencyKey);
        var conv = await db.Conversations.FirstAsync(x => x.CaseId == c.Id, ct);

        if (key is not null && await db.Messages.FirstOrDefaultAsync(m => m.ConversationId == conv.Id && m.IdempotencyKey == key, ct) is { } prev)
        {
            var prevReply = await db.Messages.FirstOrDefaultAsync(m => m.ConversationId == conv.Id && m.IdempotencyKey == key + ":reply", ct);
            return new(ToDto(prev), ToDto(prevReply ?? prev), await cases.ProgressAsync(caseId, user, ct));
        }

        var (stored, reply) = await ReplyAsync(c, text, ct);
        var userMsg = new ConversationMessage { ConversationId = conv.Id, Role = MessageRole.User, Content = stored, IdempotencyKey = key, AuthorUserId = user.Id, CreatedAt = Now };
        var botMsg = new ConversationMessage { ConversationId = conv.Id, Role = MessageRole.Assistant, Content = reply, IdempotencyKey = key is null ? null : key + ":reply", CreatedAt = Now.AddTicks(1) };
        db.Messages.AddRange(userMsg, botMsg);
        c.LastUserActivityAt = Now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (key is not null)
        {
            db.ChangeTracker.Clear();
            return await PostMessageAsync(caseId, user, req, ct); // otra petición con la misma clave ganó
        }
        return new(ToDto(userMsg), ToDto(botMsg), await cases.ProgressAsync(caseId, user, ct));
    }

    public async Task<AnswerResponse> AnswerAsync(Guid caseId, Guid taskId, CurrentUser user, AnswerRequest req, CancellationToken ct)
    {
        var c = await access.GetAsync(caseId, user, CaseAccessLevel.Write, ct);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.CaseId == c.Id, ct) ?? throw AppException.NotFound();
        var key = NormalizeKey(req.IdempotencyKey);
        if (key is not null && await db.UserResponses.AnyAsync(r => r.TaskId == task.Id && r.IdempotencyKey == key, ct))
            return new(CaseService.ToTaskDto(task), await cases.ProgressAsync(caseId, user, ct));
        if (!CaseStateMachine.AcceptsChanges(c.Status))
            throw AppException.Conflict("case_locked", "El expediente no admite cambios en su estado actual.");
        if (task.State == TaskState.Cancelled) throw AppException.Conflict("task_closed", "Esta tarea ya no aplica.");

        switch (task.Kind)
        {
            case TaskKind.Question:
                await ApplyQuestionAsync(c, task, req.Value, user, ct);
                break;
            case TaskKind.Confirmation:
                await ApplyConfirmationAsync(c, task, req.Value, user, ct);
                break;
            case TaskKind.DocumentRequest:
                if (req.Value.ValueKind == JsonValueKind.Object && req.Value.TryGetProperty("defer", out var d) && d.ValueKind == JsonValueKind.True)
                { task.State = TaskState.Deferred; task.ClosedAt = Now; }
                else throw AppException.BadRequest("upload_required", "Sube el documento o elige continuar sin él.");
                break;
            default:
                throw AppException.Forbidden("Esta tarea la resuelve un contador autorizado.");
        }

        db.UserResponses.Add(new UserResponse { TaskId = task.Id, UserId = user.Id, ValueJson = SafeValueJson(task, req.Value), IdempotencyKey = key, CreatedAt = Now });
        c.LastUserActivityAt = Now;
        await workflow.SyncAsync(c, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (key is not null)
        {
            db.ChangeTracker.Clear();
            var t = await db.Tasks.FirstAsync(x => x.Id == taskId, ct);
            return new(CaseService.ToTaskDto(t), await cases.ProgressAsync(caseId, user, ct));
        }
        return new(CaseService.ToTaskDto(task), await cases.ProgressAsync(caseId, user, ct));
    }

    private async Task ApplyQuestionAsync(TaxCase c, AssistantTask task, JsonElement value, CurrentUser user, CancellationToken ct)
    {
        var def = QuestionCatalog.Find(task.Code) ?? throw AppException.BadRequest("unknown_task", "Tarea desconocida.");
        switch (task.AnswerType)
        {
            case AnswerType.SingleChoice:
            {
                var v = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (v is null || def.Options!.All(o => o.Value != v)) throw AppException.BadRequest("invalid_option", "Opción inválida.");
                await facts.SetAsync(c, def.FactCode!, null, v, FactSource.UserAnswer, user.Id, null, null, ct);
                break;
            }
            case AnswerType.MultiChoice:
            {
                if (value.ValueKind != JsonValueKind.Array) throw AppException.BadRequest("invalid_option", "Selecciona al menos una opción.");
                var selected = value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : "").Distinct().ToList();
                if (selected.Count == 0 || selected.Any(s => def.Options!.All(o => o.Value != s))) throw AppException.BadRequest("invalid_option", "Opción inválida.");
                if (selected.Contains("none") && selected.Count > 1) throw AppException.BadRequest("invalid_option", "«No recibí ingresos» no se combina con otras opciones.");
                await facts.SetAsync(c, FactCodes.IncomeSources, null, string.Join(',', selected.OrderBy(x => x)), FactSource.UserAnswer, user.Id, null, null, ct);
                foreach (var (source, code) in QuestionCatalog.SourceFlags)
                    await facts.SetAsync(c, code, null, selected.Contains(source) ? "yes" : "no", FactSource.UserAnswer, user.Id, null, null, ct);
                if (!selected.Contains("labor"))
                    foreach (var code in new[] { FactCodes.LaborGrossIncome, FactCodes.LaborMandatoryPension, FactCodes.LaborMandatoryHealth, FactCodes.LaborWithholding })
                        await facts.SetAsync(c, code, 0, null, FactSource.Derived, user.Id, null, "Sin ingresos laborales según el contribuyente.", ct);
                if (!selected.Contains("fees"))
                    await facts.SetAsync(c, FactCodes.FeesGrossIncome, 0, null, FactSource.Derived, user.Id, null, "Sin honorarios según el contribuyente.", ct);
                break;
            }
            case AnswerType.Amount:
            case AnswerType.Integer:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var amount)) throw AppException.BadRequest("amount_required", "Escribe un valor numérico.");
                await facts.SetAsync(c, def.FactCode!, amount, null, FactSource.UserAnswer, user.Id, null, null, ct);
                break;
            }
            default:
                throw AppException.BadRequest("invalid_answer", "Respuesta inválida.");
        }
        task.State = TaskState.Answered;
        task.ClosedAt = Now;
    }

    /// <summary>Confirmación de valores propuestos desde un documento: {"items":[{"evidenceId":..,"action":"confirm|correct|reject","amount":..}]}.</summary>
    private async Task ApplyConfirmationAsync(TaxCase c, AssistantTask task, JsonElement value, CurrentUser user, CancellationToken ct)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw AppException.BadRequest("invalid_answer", "Respuesta inválida.");
        var related = Json.Deserialize<List<Guid>>(task.RelatedJson ?? "[]") ?? [];
        var evidence = await db.EvidenceItems.Where(e => e.CaseId == c.Id && related.Contains(e.Id)).ToListAsync(ct);
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("evidenceId", out var idEl) || !idEl.TryGetGuid(out var id)) throw AppException.BadRequest("invalid_answer", "Falta evidenceId.");
            var ev = evidence.FirstOrDefault(e => e.Id == id) ?? throw AppException.NotFound();
            var action = item.TryGetProperty("action", out var a) ? a.GetString() : null;
            switch (action)
            {
                case "confirm": ev.Status = EvidenceStatus.Confirmed; ev.ConfirmedAmount = ev.ExtractedAmount; break;
                case "correct":
                    if (!item.TryGetProperty("amount", out var am) || !am.TryGetDecimal(out var amount) || amount < 0)
                        throw AppException.BadRequest("amount_required", "Escribe el valor correcto.");
                    ev.Status = EvidenceStatus.Corrected; ev.ConfirmedAmount = amount; break;
                case "reject": ev.Status = EvidenceStatus.Rejected; ev.ConfirmedAmount = null; break;
                default: throw AppException.BadRequest("invalid_action", "Acción inválida.");
            }
            ev.ReviewedByUserId = user.Id;
            ev.ReviewedAt = Now;
        }
        if (evidence.Any(e => e.Status == EvidenceStatus.Proposed)) throw AppException.BadRequest("incomplete", "Revisa todos los valores propuestos.");

        // El hecho es la suma de la evidencia confirmada de ese campo en todo el expediente (p. ej. varios empleadores).
        foreach (var field in evidence.Select(e => e.Field).Distinct().Where(FactCodes.Monetary.Contains))
        {
            var all = await db.EvidenceItems.Where(e => e.CaseId == c.Id && e.Field == field).ToListAsync(ct);
            var confirmed = all.Where(e => e.Status is EvidenceStatus.Confirmed or EvidenceStatus.Corrected).ToList();
            if (confirmed.Count == 0) continue;
            await facts.SetAsync(c, field, confirmed.Sum(e => e.ConfirmedAmount ?? 0), null, FactSource.Document, user.Id,
                confirmed.Select(e => e.Id).ToList(), "Confirmado desde documentos.", ct);
        }
        task.State = TaskState.Answered;
        task.ClosedAt = Now;
        audit.Record("evidence.reviewed", user.Id, c.Id, new { taskId = task.Id, count = evidence.Count });
    }

    private static readonly Regex Secretish = new(@"(contrase[nñ]a|clave|password)\s*(de\s+(la\s+)?dian)?\s*(es|:)?\s*\S+", RegexOptions.IgnoreCase);
    private static readonly Regex LongNumber = new(@"(?<![\d.,])\d{6,12}(?![\d.,])");

    /// <summary>Respuesta determinista. Devuelve el texto a guardar (redactado) y la respuesta del asistente.</summary>
    private async Task<(string Stored, string Reply)> ReplyAsync(TaxCase c, string text, CancellationToken ct)
    {
        var lower = text.ToLowerInvariant();
        var stored = LongNumber.Replace(text, "[número oculto]");
        if (Secretish.IsMatch(text) && (lower.Contains("dian") || lower.Contains("muisca") || lower.Contains("clave") || lower.Contains("contrase")))
            return (Secretish.Replace(stored, "[dato sensible oculto]"),
                "Por seguridad no guardo contraseñas. Nunca compartas tu contraseña de la DIAN conmigo ni con nadie: la presentación la haces tú directamente en el portal oficial de la DIAN.");

        var next = await db.Tasks.Where(t => t.CaseId == c.Id && t.State == TaskState.Open)
            .OrderBy(t => t.Kind == TaskKind.Confirmation ? 0 : t.Kind == TaskKind.Question ? 1 : 2).ThenBy(t => t.SortOrder).FirstOrDefaultAsync(ct);
        string reply;
        if (lower.Contains("present") || lower.Contains("firma") || lower.Contains("portal"))
            reply = "Mi renta no presenta la declaración por ti. Cuando el contador apruebe el borrador podrás exportarlo y diligenciar el formulario 210 en el portal de la DIAN con tu propia cuenta.";
        else if (lower.Contains("debo declarar") || lower.Contains("obligad"))
            reply = "Para saber si estás obligado comparo tu patrimonio, ingresos, consumos con tarjeta, compras y consignaciones con los topes de 2025 en UVT. Responde las preguntas y te mostraré el resultado en el cálculo.";
        else if (lower.Contains("documento") || lower.Contains("subir") || lower.Contains("certificado"))
            reply = "Puedes subir PDF, XLSX, CSV, JPG o PNG (máx. 15 MB). El certificado de ingresos y retenciones me permite proponer los valores laborales; tú los confirmas antes de usarlos.";
        else reply = "Gracias. Tomo nota de tu mensaje, pero solo uso como datos las respuestas a las preguntas y los documentos que confirmes.";

        reply += next is null
            ? " Por ahora no tengo preguntas pendientes para ti."
            : next.Kind == TaskKind.ProfessionalReview ? " Siguiente paso: " + next.Prompt : " Siguiente paso: " + next.Prompt;
        return (stored, reply);
    }

    private static string SafeValueJson(AssistantTask task, JsonElement value)
    {
        var raw = value.GetRawText();
        return raw.Length > 4000 ? "\"[respuesta extensa omitida]\"" : raw;
    }

    private static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim();
        if (k.Length > 64 || !Regex.IsMatch(k, @"^[A-Za-z0-9_\-:.]+$")) throw AppException.BadRequest("invalid_idempotency_key", "Clave de idempotencia inválida.");
        return k;
    }

    public static MessageDto ToDto(ConversationMessage m) => new(m.Id, m.Role.ToString(), m.Content, m.CreatedAt);
}
