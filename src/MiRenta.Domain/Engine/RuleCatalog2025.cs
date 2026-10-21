using MiRenta.Domain.Returns;

namespace MiRenta.Domain.Engine;

/// <summary>
/// Catálogo de reglas del año gravable 2025 para personas naturales residentes (cédula general, rentas de trabajo
/// laborales). Cada regla cita su norma. Ninguna está aprobada por un profesional tributario: todas quedan en
/// <see cref="RuleStatus.PendingProfessionalApproval"/> hasta que un usuario con rol de reglas las apruebe (queda
/// registrada su identidad y fecha). Las que no tienen fuente verificable quedan en NotImplemented y bloquean los
/// casos que dependen de ellas.
/// </summary>
public static class RuleCatalog2025
{
    public const int TaxYear = 2025;
    public const string RuleSetVersion = "AG2025-r1";

    public static class Codes
    {
        public const string Uvt = "uvt";
        public const string ObligationThresholds = "obligation.thresholds";
        public const string IncrMandatoryContributions = "income.incr_mandatory_contributions";
        public const string ExemptLabor25 = "exempt.labor_25";
        public const string DeductionDependents = "deduction.dependents_387";
        public const string DeductionDependentsAdditional = "deduction.dependents_additional_336";
        public const string DeductionHousingInterest = "deduction.housing_interest_119";
        public const string DeductionPrepaidHealth = "deduction.prepaid_health_387";
        public const string LimitExemptAndDeductions = "limit.exempt_and_deductions_336";
        public const string TaxRates241 = "tax.rates_241";
        public const string Rounding = "rounding.thousand_577";
        public const string AdvancePayment = "advance.807";
        public const string FeesIncome = "income.fees";
        public const string NonResident = "scope.non_resident";
    }

    public static IReadOnlyList<TaxRule> Build() =>
    [
        Rule(Codes.Uvt, "Valor de la UVT para 2025: $49.799.", """{"value":49799}""",
            "Resolución DIAN 000193 del 4 de diciembre de 2024, art. 1 (compilada en el art. 1.2.2 de la Resolución DIAN 000227 de 2025).",
            "https://normograma.dian.gov.co/dian/compilacion/docs/resolucion_dian_0193_2024.htm"),
        Rule(Codes.ObligationThresholds,
            "Obligación de declarar: patrimonio bruto > 4.500 UVT; ingresos brutos ≥ 1.400 UVT; consumos con tarjeta de crédito, compras y consumos, o consignaciones y depósitos > 1.400 UVT; o ser responsable de IVA.",
            """{"grossAssetsUvt":4500,"grossIncomeUvt":1400,"creditCardUvt":1400,"purchasesUvt":1400,"depositsUvt":1400}""",
            "Estatuto Tributario arts. 592, 593 y 594-3; Decreto 1625 de 2016, art. 1.6.1.13.2.7 (modificado por el Decreto 2229 de 2023).",
            "https://normograma.dian.gov.co/dian/compilacion/docs/estatuto_tributario_pr024.htm"),
        Rule(Codes.IncrMandatoryContributions,
            "Aportes obligatorios del trabajador a pensión y a salud: ingresos no constitutivos de renta.", "{}",
            "Estatuto Tributario arts. 55 y 56.", null),
        Rule(Codes.ExemptLabor25,
            "Renta exenta del 25% de los pagos laborales, calculada después de restar ingresos no constitutivos, deducciones y demás rentas exentas; tope anual 790 UVT.",
            """{"rate":0.25,"annualCapUvt":790}""",
            "Estatuto Tributario art. 206 num. 10 (modificado por la Ley 2277 de 2022, art. 9).", null),
        Rule(Codes.DeductionDependents,
            "Deducción por dependientes: hasta el 10% del ingreso bruto por rentas de trabajo, máximo 32 UVT mensuales (384 UVT al año).",
            """{"rate":0.10,"annualCapUvt":384}""",
            "Estatuto Tributario art. 387 (modificado por la Ley 1819 de 2016).", null),
        Rule(Codes.DeductionDependentsAdditional,
            "Deducción adicional de 72 UVT por dependiente (hasta 4) para rentas de trabajo, fuera del límite del 40%. Sin implementar: su interacción con la deducción del art. 387 debe definirla un profesional.",
            """{"perDependentUvt":72,"maxDependents":4}""",
            "Estatuto Tributario art. 336 num. 3 (adicionado por la Ley 2277 de 2022, art. 7).", null, RuleStatus.NotImplemented),
        Rule(Codes.DeductionHousingInterest,
            "Intereses de créditos de vivienda del contribuyente: deducibles hasta 1.200 UVT anuales.",
            """{"annualCapUvt":1200}""", "Estatuto Tributario art. 119.", null),
        Rule(Codes.DeductionPrepaidHealth,
            "Pagos de medicina prepagada y seguros de salud: hasta 16 UVT mensuales (192 UVT al año).",
            """{"annualCapUvt":192}""", "Estatuto Tributario art. 387.", null),
        Rule(Codes.LimitExemptAndDeductions,
            "Las rentas exentas y deducciones de la cédula general no pueden superar el 40% de los ingresos menos ingresos no constitutivos, ni 1.340 UVT anuales.",
            """{"rate":0.40,"annualCapUvt":1340}""",
            "Estatuto Tributario art. 336 num. 3 (modificado por la Ley 2277 de 2022, art. 7).", null),
        Rule(Codes.TaxRates241,
            "Tabla de tarifas marginales para personas naturales residentes (cédula general).",
            """{"brackets":[{"fromUvt":0,"toUvt":1090,"rate":0,"baseUvt":0},{"fromUvt":1090,"toUvt":1700,"rate":0.19,"baseUvt":0},{"fromUvt":1700,"toUvt":4100,"rate":0.28,"baseUvt":116},{"fromUvt":4100,"toUvt":8670,"rate":0.33,"baseUvt":788},{"fromUvt":8670,"toUvt":18970,"rate":0.35,"baseUvt":2296},{"fromUvt":18970,"toUvt":31000,"rate":0.37,"baseUvt":5901},{"fromUvt":31000,"toUvt":null,"rate":0.39,"baseUvt":10352}]}""",
            "Estatuto Tributario art. 241 (texto vigente, Ley 2010 de 2019, art. 34).", null),
        Rule(Codes.Rounding,
            "Los valores de las declaraciones se aproximan al múltiplo de mil más cercano.", """{"multiple":1000}""",
            "Estatuto Tributario art. 577.", null),
        Rule(Codes.AdvancePayment,
            "Anticipo del impuesto del año siguiente. Sin implementar: depende de los años declarados y del procedimiento elegido; debe ingresarlo un contador.",
            "{}", "Estatuto Tributario arts. 807 a 809.", null, RuleStatus.NotImplemented),
        Rule(Codes.FeesIncome,
            "Honorarios y compensación de servicios personales (rentas de trabajo no laborales). Sin implementar en el MVP.",
            "{}", "Estatuto Tributario arts. 103, 206 par. 5 y 336.", null, RuleStatus.NotImplemented),
        Rule(Codes.NonResident,
            "No residentes fiscales. Fuera del alcance del formulario 210 y de este MVP.", "{}",
            "Estatuto Tributario arts. 10 y 592.", null, RuleStatus.NotImplemented),
    ];

    private static TaxRule Rule(string code, string description, string parameters, string reference, string? url,
        RuleStatus status = RuleStatus.PendingProfessionalApproval) => new()
    {
        Id = DeterministicId(code),
        Code = code,
        TaxYear = TaxYear,
        RuleSetVersion = RuleSetVersion,
        Description = description,
        ParametersJson = parameters,
        LegalReference = reference,
        SourceUrl = url,
        Status = status,
    };

    /// <summary>Id estable por regla (para la semilla de la migración).</summary>
    public static Guid DeterministicId(string code)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"{RuleSetVersion}:{code}"));
        return new Guid(bytes);
    }
}
