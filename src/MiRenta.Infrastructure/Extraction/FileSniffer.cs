using System.IO.Compression;
using System.Text;
using MiRenta.Domain.Documents;

namespace MiRenta.Infrastructure.Extraction;

public sealed record SniffResult(bool Ok, DocumentFileFormat Format, string ContentType, string? Error);

/// <summary>
/// Determina el tipo REAL del archivo por su contenido (firmas mágicas y estructura), no por la extensión ni el
/// Content-Type del cliente. Rechaza ejecutables, HTML/SVG, PDF con JavaScript o archivos embebidos y XLSX con macros.
/// </summary>
public static class FileSniffer
{
    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];

    public static SniffResult Sniff(byte[] data, string fileName)
    {
        if (data.Length == 0) return Fail("El archivo está vacío.");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (Starts(data, Pdf))
        {
            if (ext != ".pdf") return Fail("La extensión no coincide con el contenido.");
            var text = Encoding.Latin1.GetString(data);
            if (text.Contains("/JavaScript") || text.Contains("/JS ") || text.Contains("/JS(") || text.Contains("/Launch")
                || text.Contains("/EmbeddedFile") || text.Contains("/OpenAction") && text.Contains("/JS"))
                return Fail("El PDF contiene contenido activo (JavaScript, acciones o archivos embebidos) y no se acepta.");
            return new(true, DocumentFileFormat.Pdf, "application/pdf", null);
        }
        if (Starts(data, Png)) return ext == ".png" ? new(true, DocumentFileFormat.Png, "image/png", null) : Fail("La extensión no coincide con el contenido.");
        if (Starts(data, Jpeg)) return ext is ".jpg" or ".jpeg" ? new(true, DocumentFileFormat.Jpeg, "image/jpeg", null) : Fail("La extensión no coincide con el contenido.");
        if (Starts(data, Zip))
        {
            if (ext != ".xlsx") return Fail("Solo se aceptan archivos comprimidos de tipo XLSX.");
            try
            {
                using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
                long total = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    total += entry.Length;
                    if (total > 100L * 1024 * 1024 || entry.Length > 50L * 1024 * 1024) return Fail("El XLSX descomprimido es demasiado grande.");
                    names.Add(entry.FullName);
                }
                if (!names.Contains("[Content_Types].xml") || !names.Contains("xl/workbook.xml")) return Fail("El archivo no es un XLSX válido.");
                if (names.Any(n => n.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))) return Fail("No se aceptan libros con macros.");
                return new(true, DocumentFileFormat.Xlsx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null);
            }
            catch (InvalidDataException) { return Fail("El archivo XLSX está dañado."); }
        }
        if (ext == ".csv")
        {
            if (data.Take(8192).Any(b => b == 0)) return Fail("El CSV contiene datos binarios.");
            string sample;
            try { sample = new UTF8Encoding(false, true).GetString(data, 0, Math.Min(data.Length, 8192)); }
            catch (DecoderFallbackException) { sample = Encoding.Latin1.GetString(data, 0, Math.Min(data.Length, 8192)); }
            var trimmed = sample.TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (trimmed.StartsWith('<')) return Fail("El contenido parece HTML/XML, no CSV.");
            return new(true, DocumentFileFormat.Csv, "text/csv", null);
        }
        return Fail("Formato no permitido. Usa PDF, XLSX, CSV, JPG o PNG.");
    }

    /// <summary>Nombre seguro para mostrar (sin rutas ni caracteres de control).</summary>
    public static string SafeDisplayName(string name)
    {
        var n = Path.GetFileName(name.Replace('\\', '/'));
        n = new string(n.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (n.Length > 120) n = n[..80] + "…" + n[^30..];
        return string.IsNullOrWhiteSpace(n) ? "documento" : n;
    }

    private static bool Starts(byte[] d, byte[] sig) => d.Length >= sig.Length && d.AsSpan(0, sig.Length).SequenceEqual(sig);
    private static SniffResult Fail(string e) => new(false, default, string.Empty, e);
}
