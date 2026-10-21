using MiRenta.Domain.Documents;
using MiRenta.Domain.Facts;
using MiRenta.Infrastructure.Ai;
using MiRenta.Infrastructure.Dian;
using MiRenta.Infrastructure.Extraction;
using MiRenta.Infrastructure.Security;
using MiRenta.Tests.Support;
using Xunit;

namespace MiRenta.Tests.Unit;

public class FileSnifferTests
{
    [Fact]
    public void Accepts_real_types_by_content()
    {
        Assert.Equal(DocumentFileFormat.Pdf, FileSniffer.Sniff(TestFiles.Pdf(["hola"]), "a.pdf").Format);
        Assert.Equal(DocumentFileFormat.Png, FileSniffer.Sniff(TestFiles.Png, "a.png").Format);
        Assert.Equal(DocumentFileFormat.Xlsx, FileSniffer.Sniff(TestFiles.Xlsx([["Fecha", "Valor"]]), "a.xlsx").Format);
        Assert.Equal(DocumentFileFormat.Csv, FileSniffer.Sniff(TestFiles.Csv("fecha;valor\n2025-01-01;1000"), "a.csv").Format);
        Assert.True(FileSniffer.Sniff([0xFF, 0xD8, 0xFF, 0xE0, 1, 2], "foto.JPG").Ok);
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("html-as-csv")]
    [InlineData("pdf-js")]
    [InlineData("pdf-embedded")]
    [InlineData("pdf-renamed")]
    [InlineData("xlsm")]
    [InlineData("zip")]
    [InlineData("empty")]
    [InlineData("svg")]
    public void Rejects_malicious_or_mismatched_files(string kind)
    {
        var (data, name) = kind switch
        {
            "exe" => (TestFiles.WindowsExe, "certificado.pdf"),
            "html-as-csv" => (TestFiles.Csv("<html><script>alert(1)</script></html>"), "extracto.csv"),
            "pdf-js" => (TestFiles.Pdf(["x"], "/OpenAction << /S /JavaScript /JS (app.alert(1)) >>"), "a.pdf"),
            "pdf-embedded" => (TestFiles.Pdf(["x"], "/Names << /EmbeddedFiles << >> >> /EmbeddedFile"), "a.pdf"),
            "pdf-renamed" => (TestFiles.Pdf(["x"]), "a.png"),
            "xlsm" => (TestFiles.Xlsx([["a"]], withMacros: true), "a.xlsx"),
            "zip" => (TestFiles.Xlsx([["a"]]), "a.zip"),
            "empty" => (Array.Empty<byte>(), "a.pdf"),
            _ => (System.Text.Encoding.UTF8.GetBytes("<svg onload=alert(1)>"), "a.svg"),
        };
        Assert.False(FileSniffer.Sniff(data, name).Ok);
    }

    [Fact]
    public void Display_name_strips_paths_and_control_chars() =>
        Assert.Equal("evil.pdf", FileSniffer.SafeDisplayName("../../etc/\u0007evil.pdf"));
}

public class ExtractorTests
{
    private readonly DeterministicDocumentExtractor _x = new();

    [Theory]
    [InlineData("1.234.567", 1234567)]
    [InlineData("$ 1.234.567,89", 1234567.89)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("1234567", 1234567)]
    [InlineData("12,5", 12.5)]
    [InlineData("-450.000", -450000)]
    [InlineData("(450.000)", -450000)]
    public void Parses_colombian_amounts(string raw, double expected) =>
        Assert.Equal((decimal)expected, DeterministicDocumentExtractor.ParseAmount(raw));

    [Fact]
    public async Task Labor_certificate_fields_are_proposed_with_locator_and_injection_is_ignored()
    {
        var pdf = TestFiles.LaborCertificate(84_000_000m, 3_360_000m, 3_360_000m, 1_250_000m);
        var r = await _x.ExtractAsync(pdf, DocumentFileFormat.Pdf, default);
        Assert.Equal(DocumentKind.LaborIncomeCertificate, r.Kind);
        Assert.Equal(84_000_000m, r.Evidence.Single(e => e.Field == FactCodes.LaborGrossIncome).Amount);
        Assert.Equal(3_360_000m, r.Evidence.Single(e => e.Field == FactCodes.LaborMandatoryHealth).Amount);
        Assert.Equal(3_360_000m, r.Evidence.Single(e => e.Field == FactCodes.LaborMandatoryPension).Amount);
        Assert.Equal(1_250_000m, r.Evidence.Single(e => e.Field == FactCodes.LaborWithholding).Amount);
        Assert.All(r.Evidence, e => Assert.StartsWith("página 1", e.Locator));
        Assert.False(r.NeedsManualEntry);
    }

    [Fact]
    public async Task Bank_statement_csv_and_xlsx_become_transactions()
    {
        var csv = TestFiles.Csv("Fecha;Descripción;Débito;Crédito\n2025-01-31;PAGO NOMINA EMPRESA;;5.000.000\n05/02/2025;COMPRA SUPERMERCADO;250.000;\n=cmd;x;;\n");
        var r = await _x.ExtractAsync(csv, DocumentFileFormat.Csv, default);
        Assert.Equal(DocumentKind.BankStatement, r.Kind);
        Assert.Equal(2, r.Transactions.Count);
        Assert.Equal(5_000_000m, r.Transactions[0].Amount);
        Assert.Equal(-250_000m, r.Transactions[1].Amount);

        var xlsx = TestFiles.Xlsx([["Extracto"], ["Fecha", "Concepto", "Valor"], ["2025-03-01", "CONSIGNACION", "1.500.000"]]);
        var x = await _x.ExtractAsync(xlsx, DocumentFileFormat.Xlsx, default);
        Assert.Single(x.Transactions);
        Assert.Equal(1_500_000m, x.Transactions[0].Amount);
        Assert.Equal("fila 3", x.Transactions[0].Locator);
    }

    [Fact]
    public async Task Images_and_textless_pdfs_need_manual_entry()
    {
        Assert.True((await _x.ExtractAsync(TestFiles.Png, DocumentFileFormat.Png, default)).NeedsManualEntry);
        Assert.True((await _x.ExtractAsync(TestFiles.Pdf([]), DocumentFileFormat.Pdf, default)).NeedsManualEntry);
    }
}

public class CryptoTests
{
    [Fact]
    public void Password_hash_roundtrip_and_rejects_wrong_password()
    {
        var h = PasswordHasher.Hash("Secreta123456");
        Assert.True(PasswordHasher.Verify("Secreta123456", h));
        Assert.False(PasswordHasher.Verify("secreta123456", h));
        Assert.NotEqual(h, PasswordHasher.Hash("Secreta123456"));
        Assert.False(PasswordHasher.Verify("x", "garbage"));
    }

    [Fact]
    public void Download_tokens_expire_and_detect_tampering()
    {
        var signer = new DownloadTokenSigner(new byte[32]);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var doc = Guid.NewGuid();
        var user = Guid.NewGuid();
        var t = signer.Create(doc, user, now.AddMinutes(5));
        Assert.Equal((doc, user), signer.Validate(t, now));
        Assert.Null(signer.Validate(t, now.AddMinutes(6)));
        Assert.Null(signer.Validate(t[..^2] + (t[^1] == 'A' ? "BB" : "AA"), now));
        Assert.Null(new DownloadTokenSigner(new byte[32].Select(_ => (byte)1).ToArray()).Validate(t, now));
    }
}

public class AiAndDianTests
{
    [Fact]
    public void Ai_output_is_schema_validated_and_amounts_must_appear_in_source()
    {
        var src = "Total de ingresos brutos 84.000.000\nRetención 1.250.000";
        var ok = AnthropicAiAssistant.Validate("""{"fields":[{"field":"labor.gross_income","amount":84000000,"locator":"p1"}]}""", src);
        Assert.Single(ok);
        Assert.Empty(AnthropicAiAssistant.Validate("""{"fields":[{"field":"labor.gross_income","amount":99000000}]}""", src)); // inventado
        Assert.Empty(AnthropicAiAssistant.Validate("""{"fields":[{"field":"case.status","amount":84000000}]}""", src));         // campo no permitido
        Assert.Empty(AnthropicAiAssistant.Validate("no json", src));
        Assert.DoesNotContain("1234567890", AnthropicAiAssistant.Minimize("cedula 1234567890 correo a@b.co", 100));
        Assert.DoesNotContain("a@b.co", AnthropicAiAssistant.Minimize("cedula 1234567890 correo a@b.co", 100));
    }

    [Fact]
    public void Dian_declarant_indicators_are_returned_raw_without_interpretation()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"numDocumento":"123","nit":"123","tipoDocumento":"13","anoGravable":"2025","razonSocial":"X","ident":"1","ident1":null,"ident3":"S","plazo":"2026-08-12"}""");
        var r = DianPrecheckClient.ParseDeclarant(doc.RootElement);
        Assert.Equal("1", r.RawIndicators["ident"]);
        Assert.Null(r.RawIndicators["ident1"]);
        Assert.Equal("S", r.RawIndicators["ident3"]);
        Assert.Equal(7, r.RawIndicators.Count);
    }
}
