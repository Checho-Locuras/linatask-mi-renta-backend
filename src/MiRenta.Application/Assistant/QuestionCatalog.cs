using MiRenta.Domain.Assistant;
using MiRenta.Domain.Facts;

namespace MiRenta.Application.Assistant;

public sealed record ChoiceOption(string Value, string Label);

/// <summary>Pregunta del asistente ligada a un hecho (o a varios, en el caso de las fuentes de ingreso).</summary>
public sealed record QuestionDef(string Code, string? FactCode, string Prompt, string? Help, AnswerType AnswerType,
    IReadOnlyList<ChoiceOption>? Options, int Order, TaskKind Kind = TaskKind.Question, bool Blocks = true);

/// <summary>
/// Preguntas progresivas. Solo se muestran las necesarias según lo ya respondido (p. ej. las de certificado laboral
/// solo si declaró ingresos laborales). Los textos no afirman nada que la DIAN no haya publicado.
/// </summary>
public static class QuestionCatalog
{
    public const string IncomeSourcesCode = "q.income_sources";
    public const string LaborCertificateRequest = "d.labor_certificate";
    public const string BankStatementsRequest = "d.bank_statements";
    public const string AccountantInputs = "r.accountant_inputs";

    private static readonly ChoiceOption[] YesNo = [new("yes", "Sí"), new("no", "No")];

    public static readonly ChoiceOption[] IncomeSources =
    [
        new("labor", "Salario o pagos laborales (con certificado de ingresos y retenciones)"),
        new("fees", "Honorarios o servicios personales como independiente"),
        new("pension", "Pensión"),
        new("capital", "Arriendos, intereses o rendimientos financieros"),
        new("non_labor", "Ventas, comercio u otros ingresos no laborales"),
        new("dividends", "Dividendos o participaciones"),
        new("occasional", "Herencias, loterías, venta de bienes poseídos por más de dos años"),
        new("none", "No recibí ingresos"),
    ];

    public static readonly QuestionDef[] Questions =
    [
        new("q.fiscal_resident", FactCodes.FiscalResident, "¿Fuiste residente fiscal en Colombia durante 2025?",
            "En general eres residente si permaneciste en Colombia más de 183 días (continuos o no) en un periodo de 365 días (art. 10 ET). Si tienes dudas, responde y lo revisará un profesional.",
            AnswerType.SingleChoice, YesNo, 10),
        new(IncomeSourcesCode, null, "¿Qué tipos de ingresos recibiste en 2025? Marca todos los que apliquen.", null,
            AnswerType.MultiChoice, IncomeSources, 20),
        new("q.iva_responsible", FactCodes.IvaResponsible, "¿Fuiste responsable de IVA en 2025?",
            "Aparece en tu RUT. La mayoría de asalariados no lo son.", AnswerType.SingleChoice, YesNo, 30),
        new("q.labor_gross_income", FactCodes.LaborGrossIncome, "Total de ingresos brutos laborales de 2025",
            "Está en el certificado de ingresos y retenciones (formulario 220). Si tuviste varios empleadores, suma todos. Si subes el certificado, este valor se propone automáticamente.",
            AnswerType.Amount, null, 40),
        new("q.labor_pension", FactCodes.LaborMandatoryPension, "Aportes obligatorios a pensión descontados en 2025",
            "Renglón de aportes obligatorios a fondos de pensiones del certificado de ingresos y retenciones.", AnswerType.Amount, null, 41),
        new("q.labor_health", FactCodes.LaborMandatoryHealth, "Aportes obligatorios a salud descontados en 2025",
            "Renglón de aportes obligatorios por salud del certificado de ingresos y retenciones.", AnswerType.Amount, null, 42),
        new("q.labor_withholding", FactCodes.LaborWithholding, "Retención en la fuente que te practicaron en 2025",
            "Valor de la retención en la fuente por rentas de trabajo del certificado.", AnswerType.Amount, null, 43),
        new("q.fees_gross_income", FactCodes.FeesGrossIncome, "Total de honorarios o pagos por servicios personales recibidos en 2025",
            "El cálculo de honorarios aún no está implementado: si el valor es mayor que 0, el caso pasará a revisión profesional.",
            AnswerType.Amount, null, 44),
        new("q.dependents_count", FactCodes.DependentsCount, "¿Cuántas personas dependieron económicamente de ti en 2025?",
            "Hijos menores de 18, hijos de 18 a 23 que estudian, cónyuge o padres en condiciones del art. 387 ET. Escribe 0 si ninguna.",
            AnswerType.Integer, null, 50),
        new("q.housing_interest", FactCodes.HousingLoanInterest, "Intereses pagados en 2025 por crédito de vivienda (escribe 0 si no aplica)",
            "Según el certificado del banco.", AnswerType.Amount, null, 51),
        new("q.prepaid_health", FactCodes.PrepaidHealth, "Pagos de medicina prepagada o seguros de salud en 2025 (0 si no aplica)",
            null, AnswerType.Amount, null, 52),
        new("q.gross_assets", FactCodes.GrossAssets, "Valor total de tus bienes y derechos al 31 de diciembre de 2025 (patrimonio bruto)",
            "Saldos bancarios, inversiones, inmuebles y vehículos a su valor fiscal, cesantías, etc.", AnswerType.Amount, null, 60),
        new("q.debts", FactCodes.Debts, "Total de tus deudas al 31 de diciembre de 2025 (0 si no tenías)", null, AnswerType.Amount, null, 61),
        new("q.credit_card_spending", FactCodes.CreditCardSpending, "Total de consumos con tarjeta de crédito en 2025",
            "Sirve para saber si estás obligado a declarar.", AnswerType.Amount, null, 70),
        new("q.total_purchases", FactCodes.TotalPurchases, "Total de compras y consumos en 2025", null, AnswerType.Amount, null, 71),
        new("q.total_deposits", FactCodes.TotalDeposits, "Total de consignaciones, depósitos o inversiones financieras en 2025",
            "Si subes tus extractos, te mostramos los movimientos para que revises transferencias propias y duplicados.", AnswerType.Amount, null, 72),
        new(LaborCertificateRequest, null, "Sube tu certificado de ingresos y retenciones de 2025 (PDF)",
            "Opcional pero recomendado: propone automáticamente los valores laborales. Puedes escribirlos a mano.",
            AnswerType.Document, null, 39, TaskKind.DocumentRequest, Blocks: false),
        new(BankStatementsRequest, null, "Sube tus extractos bancarios de 2025 (CSV, XLSX o PDF)",
            "Opcional: ayuda a identificar depósitos, transferencias entre tus cuentas y duplicados.",
            AnswerType.Document, null, 73, TaskKind.DocumentRequest, Blocks: false),
    ];

    public static QuestionDef? Find(string code) => Questions.FirstOrDefault(q => q.Code == code);

    /// <summary>Mapeo de fuentes de ingreso a hechos "unsupported.*" (sí/no).</summary>
    public static readonly (string Source, string FactCode)[] SourceFlags =
    [
        ("pension", FactCodes.HasPensionIncome), ("capital", FactCodes.HasCapitalIncome), ("non_labor", FactCodes.HasNonLaborIncome),
        ("dividends", FactCodes.HasDividends), ("occasional", FactCodes.HasOccasionalGains),
    ];
}
