namespace MiRenta.Domain.Finance;

public sealed class FinancialAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public string Institution { get; set; } = string.Empty;
    /// <summary>Últimos dígitos (nunca el número completo).</summary>
    public string? MaskedNumber { get; set; }
    public string Kind { get; set; } = "bank";
    public string Currency { get; set; } = "COP";
    public decimal? BalanceAtYearEnd { get; set; }
    public Guid? SourceDocumentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Movimiento normalizado. Los originales nunca se eliminan al conciliar.</summary>
public sealed class FinancialTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Guid? AccountId { get; set; }
    public Guid SourceDocumentId { get; set; }
    public string SourceLocator { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    /// <summary>Positivo = abono; negativo = cargo.</summary>
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "COP";
    public string? Counterparty { get; set; }
    public string Description { get; set; } = string.Empty;
    /// <summary>Clasificación bancaria (lo que dice el extracto).</summary>
    public string? BankCategory { get; set; }
    /// <summary>Clasificación tributaria, distinta de la bancaria. Nunca se asume ingreso gravable.</summary>
    public TaxTreatment TaxTreatment { get; set; } = TaxTreatment.Unclassified;
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Pending;
    /// <summary>Clave determinista para idempotencia (documento + fila + fecha + importe + descripción).</summary>
    public string DedupKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum TaxTreatment
{
    Unclassified = 0,
    LaborIncome = 1,
    FeesIncome = 2,
    OwnTransfer = 3,
    Loan = 4,
    LoanRepayment = 5,
    Reversal = 6,
    NonTaxable = 7,
    Expense = 8,
    OtherIncome = 9,
    Excluded = 10,
}

public enum ReviewStatus { Pending = 0, Proposed = 1, Confirmed = 2 }

/// <summary>Posible duplicado, transferencia propia, reverso o abono por explicar.</summary>
public sealed class Reconciliation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public ReconciliationKind Kind { get; set; }
    public string TransactionIdsJson { get; set; } = "[]";
    public string Explanation { get; set; } = string.Empty;
    public bool IsOpen { get; set; } = true;
    public string? Resolution { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public DateTime? ResolvedAt { get; set; }
    /// <summary>Clave determinista: reprocesar no duplica la conciliación.</summary>
    public string DedupKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ReconciliationKind { PossibleDuplicate = 0, PossibleOwnTransfer = 1, PossibleReversal = 2, UnexplainedDeposit = 3 }
