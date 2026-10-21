using MiRenta.Domain.Engine;
using MiRenta.Domain.Facts;
using MiRenta.Domain.Returns;
using Xunit;

namespace MiRenta.Tests.Engine;

public class Form210EngineTests
{
    private const decimal Uvt = 49_799m;

    private static readonly Dictionary<string, (decimal? A, string? T)> Base = new()
    {
        [FactCodes.FiscalResident] = (null, "yes"), [FactCodes.IvaResponsible] = (null, "no"),
        [FactCodes.HasPensionIncome] = (null, "no"), [FactCodes.HasCapitalIncome] = (null, "no"), [FactCodes.HasNonLaborIncome] = (null, "no"),
        [FactCodes.HasDividends] = (null, "no"), [FactCodes.HasOccasionalGains] = (null, "no"),
        [FactCodes.LaborGrossIncome] = (120_000_000m, null), [FactCodes.LaborMandatoryPension] = (4_800_000m, null),
        [FactCodes.LaborMandatoryHealth] = (4_800_000m, null), [FactCodes.LaborWithholding] = (5_000_000m, null),
        [FactCodes.GrossAssets] = (80_000_000m, null), [FactCodes.Debts] = (10_000_000m, null), [FactCodes.DependentsCount] = (0m, null),
        [FactCodes.FeesGrossIncome] = (0m, null),
    };

    private static readonly Dictionary<string, (decimal? A, string? T)> Accountant = new()
    {
        [FactCodes.AdvancePaymentNextYear] = (0m, null), [FactCodes.PreviousYearAdvance] = (0m, null),
    };

    private static EngineInput Input(Dictionary<string, (decimal? A, string? T)> facts, Dictionary<string, (decimal? A, string? T)>? accountant = null,
        Func<TaxRule, TaxRule>? rule = null, FactStatus status = FactStatus.Accepted)
    {
        var list = facts.Select(kv => new EngineFact(Guid.NewGuid(), kv.Key, kv.Value.A, kv.Value.T, status, FactSource.UserAnswer))
            .Concat((accountant ?? []).Select(kv => new EngineFact(Guid.NewGuid(), kv.Key, kv.Value.A, kv.Value.T, FactStatus.Accepted, FactSource.Accountant)))
            .ToList();
        var rules = RuleCatalog2025.Build().Select(r => rule is null ? r : rule(r)).ToList();
        return new EngineInput(2025, list, rules);
    }

    private static decimal Line(EngineResult r, string concept) => r.Lines.Single(l => l.Concept == concept).Value;

    private static Dictionary<string, (decimal? A, string? T)> With(params (string Code, decimal? A, string? T)[] changes)
    {
        var d = new Dictionary<string, (decimal? A, string? T)>(Base);
        foreach (var (c, a, t) in changes) d[c] = (a, t);
        return d;
    }

    [Fact]
    public void Salaried_case_is_computed_with_decimal_rounding_and_traceability()
    {
        var r = Form210Engine.Calculate(Input(Base, Accountant));
        Assert.Equal(CalculationStatus.Preliminary, r.Status); // reglas pendientes de aprobación profesional
        Assert.Empty(r.Blocking);
        Assert.Equal(9_600_000m, Line(r, "labor.incr"));
        Assert.Equal(110_400_000m, Line(r, "labor.net"));
        Assert.Equal(27_600_000m, Line(r, "exempt.labor_25"));
        Assert.Equal(27_600_000m, Line(r, "limit.allowed"));
        Assert.Equal(82_800_000m, Line(r, "taxable.general"));
        // (82.800.000 − 1.090 UVT) × 19 % = 5.418.627,1 → múltiplo de mil más cercano.
        Assert.Equal(5_419_000m, Line(r, "tax.income"));
        Assert.Equal(419_000m, Line(r, "balance.to_pay"));
        Assert.Equal(0m, Line(r, "balance.in_favor"));
        Assert.Equal(70_000_000m, Line(r, "patrimony.net"));
        Assert.All(r.Lines, l => Assert.Null(typeof(EngineLine).GetProperty("BoxNumber")));
        Assert.NotEmpty(r.Lines.Single(l => l.Concept == "labor.gross").SourceFactIds);
        Assert.Contains(r.Lines, l => l.Warnings.Contains("Regla pendiente de aprobación profesional."));
        Assert.True(r.Obligation.MustFile);
    }

    [Fact]
    public void High_income_applies_caps_of_art_206_336_387_and_119()
    {
        var facts = With((FactCodes.LaborGrossIncome, 400_000_000m, null), (FactCodes.LaborMandatoryPension, 16_000_000m, null),
            (FactCodes.LaborMandatoryHealth, 16_000_000m, null), (FactCodes.DependentsCount, 1m, null),
            (FactCodes.HousingLoanInterest, 30_000_000m, null), (FactCodes.PrepaidHealth, 3_000_000m, null), (FactCodes.LaborWithholding, 60_000_000m, null));
        var acc = new Dictionary<string, (decimal? A, string? T)>(Accountant) { [FactCodes.AccountantDependentsAdditional] = (72 * Uvt, null) };
        var r = Form210Engine.Calculate(Input(facts, acc));
        Assert.Empty(r.Blocking);
        Assert.Equal(19_123_000m, Line(r, "deduction.dependents"));     // 384 UVT
        Assert.Equal(30_000_000m, Line(r, "deduction.housing"));
        Assert.Equal(3_000_000m, Line(r, "deduction.prepaid_health"));
        Assert.Equal(39_341_000m, Line(r, "exempt.labor_25"));          // 790 UVT
        Assert.Equal(66_731_000m, Line(r, "limit.allowed"));            // 1.340 UVT
        Assert.Equal(3_586_000m, Line(r, "deduction.dependents_additional"));
        Assert.Equal(297_683_000m, Line(r, "taxable.general"));
        Assert.Equal(70_099_000m, Line(r, "tax.income"));               // tramo 33 %
        Assert.Equal(10_099_000m, Line(r, "balance.to_pay"));
    }

    [Theory]
    [InlineData(1090, 0)]          // tope del tramo 0 %
    [InlineData(1700, 115.9)]      // (1700 − 1090) × 19 % = 115,9 UVT (la tabla redondea a 116 como base del siguiente tramo)
    [InlineData(4100, 788)]        // (4100 − 1700) × 28 % + 116 = 788
    [InlineData(31000.0001, 10352.000039)]
    public void Tax_table_brackets_match_article_241(double baseUvt, double expectedUvt)
    {
        var taxable = (decimal)baseUvt * Uvt;
        // Ingreso tal que la renta gravable sea exactamente la base: sin INCR ni exenciones no se puede (25 % siempre aplica),
        // así que se prueba la tabla directamente a través del parámetro publicado.
        var brackets = System.Text.Json.JsonDocument.Parse(RuleCatalog2025.Build().Single(r => r.Code == RuleCatalog2025.Codes.TaxRates241).ParametersJson)
            .RootElement.GetProperty("brackets").EnumerateArray().ToList();
        var b = (decimal)baseUvt;
        decimal tax = 0;
        foreach (var x in brackets)
        {
            var from = x.GetProperty("fromUvt").GetDecimal();
            var to = x.GetProperty("toUvt").ValueKind == System.Text.Json.JsonValueKind.Null ? decimal.MaxValue : x.GetProperty("toUvt").GetDecimal();
            if (b > from && b <= to) tax = (b - from) * x.GetProperty("rate").GetDecimal() + x.GetProperty("baseUvt").GetDecimal();
        }
        Assert.Equal((decimal)expectedUvt, decimal.Round(tax, 6));
        Assert.True(taxable > 0);
    }

    [Fact]
    public void Missing_or_unconfirmed_facts_block_the_calculation()
    {
        var facts = new Dictionary<string, (decimal? A, string? T)>(Base);
        facts.Remove(FactCodes.LaborWithholding);
        var r = Form210Engine.Calculate(Input(facts, Accountant));
        Assert.Equal(CalculationStatus.Blocked, r.Status);
        Assert.Contains(r.Blocking, b => b.Code == "fact.missing." + FactCodes.LaborWithholding);
        Assert.Empty(r.Lines);

        var proposed = Form210Engine.Calculate(Input(Base, Accountant, status: FactStatus.Proposed));
        Assert.Equal(CalculationStatus.Blocked, proposed.Status);
        Assert.All(Form210Engine.RequiredFacts, code => Assert.Contains(proposed.Blocking, b => b.Code == "fact.missing." + code));
    }

    [Theory]
    [InlineData(FactCodes.HasPensionIncome)]
    [InlineData(FactCodes.HasDividends)]
    [InlineData(FactCodes.HasOccasionalGains)]
    public void Unsupported_income_blocks(string code)
    {
        var r = Form210Engine.Calculate(Input(With((code, null, "yes")), Accountant));
        Assert.Equal(CalculationStatus.Blocked, r.Status);
        Assert.Contains(r.Blocking, b => b.Code == "scope." + code);
    }

    [Fact]
    public void Non_resident_and_fees_are_out_of_scope()
    {
        Assert.Contains(Form210Engine.Calculate(Input(With((FactCodes.FiscalResident, null, "no")), Accountant)).Blocking, b => b.Code == "scope.non_resident");
        Assert.Contains(Form210Engine.Calculate(Input(With((FactCodes.FeesGrossIncome, 1_000_000m, null)), Accountant)).Blocking, b => b.Code == "scope.fees");
    }

    [Fact]
    public void Without_accountant_inputs_only_a_preview_is_produced()
    {
        var r = Form210Engine.Calculate(Input(Base));
        Assert.Equal(CalculationStatus.Blocked, r.Status);
        Assert.True(r.IsPreview);
        Assert.All(r.Blocking, b => Assert.StartsWith("accountant.required.", b.Code));
        Assert.Contains(r.Lines.Single(l => l.Concept == "advance.next").Warnings, w => w.Contains("contador"));

        // Si el valor lo ingresa el propio contribuyente (no un contador), sigue bloqueado.
        var self = Base.ToDictionary();
        var input = Input(self);
        var facts = input.Facts.Concat([new EngineFact(Guid.NewGuid(), FactCodes.AdvancePaymentNextYear, 0, null, FactStatus.Accepted, FactSource.UserAnswer)]).ToList();
        Assert.Contains(Form210Engine.Calculate(input with { Facts = facts }).Blocking, b => b.Code == "accountant.required." + FactCodes.AdvancePaymentNextYear);
    }

    [Fact]
    public void Dependents_require_accountant_decision_on_additional_deduction()
    {
        var r = Form210Engine.Calculate(Input(With((FactCodes.DependentsCount, 2m, null)), Accountant));
        Assert.Contains(r.Blocking, b => b.Code == "accountant.required." + FactCodes.AccountantDependentsAdditional);
    }

    [Fact]
    public void A_missing_or_not_implemented_rule_blocks()
    {
        var r = Form210Engine.Calculate(Input(Base, Accountant, rule: x => x.Code == RuleCatalog2025.Codes.Uvt ? new TaxRule
        {
            Id = x.Id, Code = x.Code, TaxYear = x.TaxYear, RuleSetVersion = x.RuleSetVersion, ParametersJson = x.ParametersJson,
            Status = RuleStatus.NotImplemented,
        } : x));
        Assert.Equal(CalculationStatus.Blocked, r.Status);
        Assert.Contains(r.Blocking, b => b.Code == "rule.missing.uvt");
        Assert.Empty(r.Lines);

        var other = Form210Engine.Calculate(new EngineInput(2024, Input(Base, Accountant).Facts, RuleCatalog2025.Build()));
        Assert.Contains(other.Blocking, b => b.Code == "year.unsupported");
    }

    [Fact]
    public void Engine_is_deterministic_and_ready_for_approval_only_when_rules_are_approved()
    {
        var input = Input(Base, Accountant);
        var a = Form210Engine.Calculate(input);
        var b = Form210Engine.Calculate(input);
        Assert.Equal(a.Lines.Select(l => (l.Concept, l.Value)), b.Lines.Select(l => (l.Concept, l.Value)));

        var approved = Form210Engine.Calculate(Input(Base, Accountant, rule: x =>
        {
            if (x.Status == RuleStatus.PendingProfessionalApproval) x.Status = RuleStatus.Approved;
            return x;
        }));
        Assert.Equal(CalculationStatus.ReadyForApproval, approved.Status);
    }

    [Fact]
    public void Invalid_inputs_are_blocked()
    {
        Assert.Contains(Form210Engine.Calculate(Input(With((FactCodes.Debts, -1m, null)), Accountant)).Blocking, b => b.Code == "fact.negative");
        Assert.Contains(Form210Engine.Calculate(Input(With((FactCodes.LaborMandatoryPension, 200_000_000m, null)), Accountant)).Blocking,
            b => b.Code == "incr.exceeds_income");
    }

    [Fact]
    public void Obligation_thresholds_follow_uvt_values()
    {
        var low = With((FactCodes.LaborGrossIncome, 30_000_000m, null), (FactCodes.LaborMandatoryPension, 1_200_000m, null),
            (FactCodes.LaborMandatoryHealth, 1_200_000m, null), (FactCodes.LaborWithholding, 0m, null), (FactCodes.GrossAssets, 50_000_000m, null),
            (FactCodes.CreditCardSpending, 1_000_000m, null), (FactCodes.TotalPurchases, 20_000_000m, null), (FactCodes.TotalDeposits, 30_000_000m, null));
        var r = Form210Engine.Calculate(Input(low, Accountant));
        Assert.False(r.Obligation.MustFile);
        Assert.Contains(r.Warnings, w => w.Code == "obligation.not_required");

        // 1.400 UVT = 69.718.600: alcanzarlo con ingresos obliga a declarar.
        var atThreshold = new Dictionary<string, (decimal? A, string? T)>(low) { [FactCodes.LaborGrossIncome] = (1400 * Uvt, null) };
        Assert.True(Form210Engine.Calculate(Input(atThreshold, Accountant)).Obligation.MustFile);

        // Patrimonio: debe SUPERAR 4.500 UVT.
        var assets = new Dictionary<string, (decimal? A, string? T)>(low) { [FactCodes.GrossAssets] = (4500 * Uvt, null) };
        Assert.False(Form210Engine.Calculate(Input(assets, Accountant)).Obligation.MustFile);
        assets[FactCodes.GrossAssets] = (4500 * Uvt + 1, null);
        Assert.True(Form210Engine.Calculate(Input(assets, Accountant)).Obligation.MustFile);
    }

    [Fact]
    public void Rule_catalog_cites_sources_and_has_no_approved_rules()
    {
        var rules = RuleCatalog2025.Build();
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.LegalReference)));
        Assert.DoesNotContain(rules, r => r.Status == RuleStatus.Approved);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.Equal(RuleCatalog2025.DeterministicId("uvt"), rules.Single(r => r.Code == "uvt").Id);
    }
}
