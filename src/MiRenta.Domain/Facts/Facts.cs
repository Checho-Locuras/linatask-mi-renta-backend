namespace MiRenta.Domain.Facts;

/// <summary>
/// Hecho tributario aceptado (o propuesto). Cambiar un valor crea una versión nueva y marca la anterior como
/// Superseded: nunca se destruye la evidencia ni el historial.
/// </summary>
public sealed class TaxFact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    /// <summary>Código del catálogo <see cref="FactCodes"/>.</summary>
    public string Code { get; set; } = string.Empty;
    public decimal? Amount { get; set; }
    public string? TextValue { get; set; }
    public FactSource Source { get; set; }
    public string EvidenceIdsJson { get; set; } = "[]";
    public FactStatus Status { get; set; } = FactStatus.Proposed;
    public int Version { get; set; } = 1;
    public Guid? SupersededById { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum FactSource { Document = 0, UserAnswer = 1, Accountant = 2, Derived = 3 }

public enum FactStatus { Proposed = 0, Accepted = 1, Superseded = 2, Rejected = 3 }

/// <summary>Catálogo de hechos que el motor sabe usar. Todo importe en pesos colombianos del año gravable.</summary>
public static class FactCodes
{
    // Perfil
    public const string FiscalResident = "profile.fiscal_resident";              // "yes" | "no"
    public const string IvaResponsible = "profile.iva_responsible";              // "yes" | "no"
    public const string IncomeSources = "profile.income_sources";                // lista separada por comas

    // Rentas de trabajo (cédula general) — certificado de ingresos y retenciones
    public const string LaborGrossIncome = "labor.gross_income";
    public const string LaborMandatoryPension = "labor.mandatory_pension_contributions";
    public const string LaborMandatoryHealth = "labor.mandatory_health_contributions";
    public const string LaborWithholding = "labor.withholding";
    public const string FeesGrossIncome = "fees.gross_income";
    public const string FeesWithholding = "fees.withholding";

    // Deducciones soportadas
    public const string DependentsCount = "deductions.dependents_count";
    public const string HousingLoanInterest = "deductions.housing_loan_interest";
    public const string PrepaidHealth = "deductions.prepaid_health";

    // Patrimonio y deudas al 31 de diciembre
    public const string GrossAssets = "patrimony.gross_assets";
    public const string Debts = "patrimony.debts";

    // Criterios de obligación de declarar (art. 592, 594-3 ET)
    public const string CreditCardSpending = "obligation.credit_card_spending";
    public const string TotalPurchases = "obligation.total_purchases";
    public const string TotalDeposits = "obligation.total_deposits";

    // Conceptos que el MVP NO calcula: si existen, bloquean el cálculo definitivo.
    public const string HasPensionIncome = "unsupported.pension_income";
    public const string HasCapitalIncome = "unsupported.capital_income";
    public const string HasNonLaborIncome = "unsupported.non_labor_income";
    public const string HasDividends = "unsupported.dividends";
    public const string HasOccasionalGains = "unsupported.occasional_gains";
    public const string AdvancePaymentNextYear = "accountant.advance_payment_next_year";
    public const string PreviousYearAdvance = "accountant.previous_year_advance";
    public const string AccountantDependentsAdditional = "accountant.dependents_additional_deduction";

    /// <summary>Hechos que solo puede registrar un contador autorizado.</summary>
    public static readonly HashSet<string> AccountantOnly = [AdvancePaymentNextYear, PreviousYearAdvance, AccountantDependentsAdditional];

    public static readonly HashSet<string> Monetary =
    [
        LaborGrossIncome, LaborMandatoryPension, LaborMandatoryHealth, LaborWithholding, FeesGrossIncome, FeesWithholding,
        HousingLoanInterest, PrepaidHealth, GrossAssets, Debts, CreditCardSpending, TotalPurchases, TotalDeposits,
        AdvancePaymentNextYear, PreviousYearAdvance, AccountantDependentsAdditional,
    ];

    public static readonly HashSet<string> All =
    [
        .. Monetary, FiscalResident, IvaResponsible, IncomeSources, DependentsCount, HasPensionIncome, HasCapitalIncome,
        HasNonLaborIncome, HasDividends, HasOccasionalGains,
    ];

    /// <summary>Respuestas sí/no.</summary>
    public static readonly HashSet<string> YesNo =
        [FiscalResident, IvaResponsible, HasPensionIncome, HasCapitalIncome, HasNonLaborIncome, HasDividends, HasOccasionalGains];
}
