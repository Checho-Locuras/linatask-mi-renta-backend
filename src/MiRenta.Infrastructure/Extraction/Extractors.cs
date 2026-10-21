using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MiRenta.Domain.Documents;
using MiRenta.Domain.Facts;

namespace MiRenta.Infrastructure.Extraction;

public sealed record ExtractedEvidence(string Field, string RawText, decimal? Amount, string Locator, decimal Confidence);

public sealed record ExtractedTransaction(DateOnly Date, decimal Amount, string Description, string Locator);

public sealed record ExtractionOutput(
    string Method, string ExtractorVersion, DocumentKind Kind, decimal Confidence, int? PageCount, int? RowCount,
    IReadOnlyList<ExtractedEvidence> Evidence, IReadOnlyList<ExtractedTransaction> Transactions, bool NeedsManualEntry, string? Note);

public interface IDocumentExtractor
{
    Task<ExtractionOutput> ExtractAsync(byte[] content, DocumentFileFormat format, CancellationToken ct);
}

/// <summary>
/// Extracción determinista: el texto del documento se trata SIEMPRE como datos (nunca como instrucciones). Solo se
/// buscan rótulos conocidos y montos; cualquier otra cosa se ignora. Las imágenes quedan para ingreso manual.
/// </summary>
public sealed class DeterministicDocumentExtractor : IDocumentExtractor
{
    public const string Version = "det-0.1.0";
    private static readonly TimeSpan PdfTimeout = TimeSpan.FromSeconds(20);

    public async Task<ExtractionOutput> ExtractAsync(byte[] content, DocumentFileFormat format, CancellationToken ct)
    {
        switch (format)
        {
            case DocumentFileFormat.Pdf:
            {
                var (pages, ok) = await PdfToTextAsync(content, ct);
                if (!ok || pages.All(string.IsNullOrWhiteSpace))
                    return Manual("pdftotext", pages.Count, "El PDF no tiene texto extraíble (posiblemente escaneado).");
                return FromText(pages, "pdftotext");
            }
            case DocumentFileFormat.Xlsx:
                return FromRows(ReadXlsx(content), "openxml");
            case DocumentFileFormat.Csv:
                return FromRows(ReadCsv(content), "csv");
            default:
                return Manual("none", null, "Las imágenes requieren ingreso manual (no hay OCR configurado).");
        }
    }

    private static ExtractionOutput Manual(string method, int? pages, string note) =>
        new(method, Version, DocumentKind.Unknown, 0, pages, null, [], [], true, note);

    // ---------- Certificado de ingresos y retenciones (formulario 220) ----------

    private static readonly (string Field, Regex Label)[] LaborLabels =
    [
        (FactCodes.LaborGrossIncome, new(@"total\s+de\s+ingresos\s+brutos", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (FactCodes.LaborMandatoryHealth, new(@"aportes?\s+obligatorios?\s+(por|de|a)\s+salud", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (FactCodes.LaborMandatoryPension, new(@"aportes?\s+obligatorios?\s+(a|de)\s+(fondos\s+de\s+)?pensi[oó]n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (FactCodes.LaborWithholding, new(@"valor\s+de\s+la\s+retenci[oó]n\s+en\s+la\s+fuente", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    private static readonly Regex Amount = new(@"\$?\s*(\d{1,3}(?:[.,]\d{3})+|\d+)(?:[.,](\d{1,2}))?(?!\d)", RegexOptions.CultureInvariant);

    internal static ExtractionOutput FromText(IReadOnlyList<string> pages, string method)
    {
        var evidence = new List<ExtractedEvidence>();
        var all = string.Join('\n', pages);
        var isLabor = Regex.IsMatch(all, @"certificado\s+de\s+ingresos\s+y\s+retenciones", RegexOptions.IgnoreCase)
                      || Regex.IsMatch(all, @"formulario\s+220", RegexOptions.IgnoreCase);
        for (var p = 0; p < pages.Count; p++)
        {
            var lines = pages[p].Split('\n');
            for (var l = 0; l < lines.Length; l++)
            {
                var line = lines[l];
                foreach (var (field, label) in LaborLabels)
                {
                    var m = label.Match(line);
                    if (!m.Success || evidence.Any(e => e.Field == field)) continue;
                    var rest = line[(m.Index + m.Length)..];
                    var amount = LastAmount(rest);
                    if (amount is null && l + 1 < lines.Length) amount = LastAmount(lines[l + 1]);
                    if (amount is null) continue;
                    evidence.Add(new(field, Truncate(line.Trim(), 200), amount, $"página {p + 1}, línea {l + 1}", isLabor ? 0.8m : 0.5m));
                }
            }
        }
        var kind = isLabor ? DocumentKind.LaborIncomeCertificate : DocumentKind.Unknown;
        var conf = isLabor ? (evidence.Count >= 3 ? 0.8m : 0.5m) : 0.2m;
        return new(method, Version, kind, conf, pages.Count, null, evidence, [], evidence.Count == 0,
            evidence.Count == 0 ? "No se reconocieron campos; ingresa los valores manualmente." : null);
    }

    private static decimal? LastAmount(string s)
    {
        decimal? last = null;
        foreach (Match m in Amount.Matches(s))
        {
            var v = ParseAmount(m.Value);
            if (v is not null) last = v;
        }
        return last;
    }

    /// <summary>Interpreta montos colombianos: "1.234.567", "1.234.567,89", "$ 1,234,567", "1234567".</summary>
    internal static decimal? ParseAmount(string raw)
    {
        var s = raw.Replace("$", "").Replace(" ", "").Trim();
        if (s.Length == 0) return null;
        var neg = s.StartsWith('-') || (s.StartsWith('(') && s.EndsWith(')'));
        s = s.Trim('-', '(', ')');
        int lastDot = s.LastIndexOf('.'), lastComma = s.LastIndexOf(',');
        string normalized;
        if (lastDot >= 0 && lastComma >= 0)
            normalized = lastComma > lastDot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (lastComma >= 0)
            normalized = s.Length - lastComma - 1 is 1 or 2 ? s.Replace(',', '.') : s.Replace(",", "");
        else if (lastDot >= 0)
            normalized = s.Length - lastDot - 1 is 1 or 2 && s.Count(c => c == '.') == 1 ? s : s.Replace(".", "");
        else normalized = s;
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return null;
        return neg ? -v : v;
    }

    // ---------- Extractos bancarios (CSV / XLSX) ----------

    internal static ExtractionOutput FromRows(List<string[]> rows, string method)
    {
        if (rows.Count == 0) return Manual(method, null, "El archivo no tiene filas.");
        var headerIdx = rows.FindIndex(r => r.Any(c => Norm(c).Contains("fecha")) && r.Any(c => IsAmountHeader(Norm(c))));
        if (headerIdx < 0) return new(method, Version, DocumentKind.Unknown, 0.1m, null, rows.Count, [], [], true,
            "No se reconoció un encabezado con fecha y valor.");
        var h = rows[headerIdx].Select(Norm).ToArray();
        int date = Array.FindIndex(h, c => c.Contains("fecha"));
        int desc = Array.FindIndex(h, c => c.Contains("descrip") || c.Contains("concepto") || c.Contains("detalle"));
        int amount = Array.FindIndex(h, c => c is "valor" or "monto" or "importe" or "valor transaccion" or "valor movimiento");
        int debit = Array.FindIndex(h, c => c.Contains("debito") || c.Contains("retiro") || c.Contains("cargo"));
        int credit = Array.FindIndex(h, c => c.Contains("credito") || c.Contains("abono") || c.Contains("consignacion") || c.Contains("deposito"));
        var txs = new List<ExtractedTransaction>();
        for (var i = headerIdx + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string Cell(int idx) => idx >= 0 && idx < r.Length ? r[idx].Trim() : string.Empty;
            if (!TryDate(Cell(date), out var d)) continue;
            decimal? v = null;
            if (amount >= 0) v = ParseAmount(Cell(amount));
            else
            {
                var c = ParseAmount(Cell(credit)) ?? 0;
                var db = ParseAmount(Cell(debit)) ?? 0;
                if (c != 0 || db != 0) v = c - Math.Abs(db);
            }
            if (v is null || v == 0) continue;
            txs.Add(new(d, v.Value, Truncate(SanitizeText(Cell(desc)), 300), $"fila {i + 1}"));
        }
        return new(method, Version, txs.Count > 0 ? DocumentKind.BankStatement : DocumentKind.Unknown, txs.Count > 0 ? 0.7m : 0.1m,
            null, rows.Count, [], txs, txs.Count == 0, txs.Count == 0 ? "No se reconocieron movimientos." : null);
    }

    private static bool IsAmountHeader(string c) => c is "valor" or "monto" or "importe" || c.Contains("debito") || c.Contains("credito")
                                                  || c.Contains("valor transaccion") || c.Contains("valor movimiento");

    private static bool TryDate(string s, out DateOnly d)
    {
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "yyyy/MM/dd", "dd/MM/yy"];
        if (DateOnly.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return true;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 30000 and < 80000)
        {
            d = DateOnly.FromDateTime(DateTime.FromOADate(serial));
            return true;
        }
        return false;
    }

    private static string Norm(string s)
    {
        var formD = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(formD.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    /// <summary>Quita caracteres de control y marcadores típicos de inyección; el texto sigue siendo solo dato.</summary>
    internal static string SanitizeText(string s) => new(s.Where(c => !char.IsControl(c)).ToArray());

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    internal static List<string[]> ReadCsv(byte[] data)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(data); }
        text = text.TrimStart('﻿');
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Take(20_000).ToList();
        if (lines.Count == 0) return [];
        var sep = lines.Take(5).Sum(l => l.Count(c => c == ';')) > lines.Take(5).Sum(l => l.Count(c => c == ',')) ? ';' : ',';
        return lines.Select(l => SplitCsv(l, sep)).ToList();
    }

    private static string[] SplitCsv(string line, char sep)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else quoted = !quoted; }
            else if (c == sep && !quoted) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        // Neutraliza fórmulas si alguna vez se reexporta (CSV injection).
        return cells.Select(x => x.Length > 0 && "=+@".Contains(x[0]) ? "'" + x : x).ToArray();
    }

    internal static List<string[]> ReadXlsx(byte[] data)
    {
        var rows = new List<string[]>();
        using var doc = SpreadsheetDocument.Open(new MemoryStream(data), false);
        var wb = doc.WorkbookPart;
        var sheet = wb?.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault();
        if (wb is null || sheet?.Id?.Value is null) return rows;
        var ws = (WorksheetPart)wb.GetPartById(sheet.Id.Value);
        var shared = wb.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
        foreach (var row in ws.Worksheet.Descendants<Row>().Take(20_000))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<Cell>())
            {
                var col = ColumnIndex(cell.CellReference?.Value);
                while (cells.Count < col) cells.Add(string.Empty);
                var v = cell.CellValue?.Text ?? cell.InnerText;
                if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(v, out var si) && si < shared.Length) v = shared[si];
                cells.Add(v);
            }
            rows.Add(cells.ToArray());
        }
        return rows;
    }

    private static int ColumnIndex(string? reference)
    {
        if (reference is null) return 0;
        var n = 0;
        foreach (var c in reference.TakeWhile(char.IsLetter)) n = n * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        return Math.Max(0, n - 1);
    }

    private static async Task<(List<string> Pages, bool Ok)> PdfToTextAsync(byte[] pdf, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("mirenta-pdf-");
        try
        {
            var input = Path.Combine(dir.FullName, "in.pdf");
            var output = Path.Combine(dir.FullName, "out.txt");
            await File.WriteAllBytesAsync(input, pdf, ct);
            var psi = new ProcessStartInfo("pdftotext") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-layout", "-enc", "UTF-8", "-l", "30", input, output }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return ([], false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PdfTimeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { /* ya terminó */ } return ([], false); }
            if (proc.ExitCode != 0 || !File.Exists(output)) return ([], false);
            var text = await File.ReadAllTextAsync(output, ct);
            return (text.Split('\f').Select(p => p.Replace("\r", "")).Where((p, i) => i == 0 || p.Length > 0).ToList(), true);
        }
        catch (System.ComponentModel.Win32Exception) { return ([], false); }
        finally { try { dir.Delete(true); } catch { /* temporal */ } }
    }
}
