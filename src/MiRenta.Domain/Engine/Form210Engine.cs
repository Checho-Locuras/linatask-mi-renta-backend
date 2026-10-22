using System.Globalization;
using System.Text.Json;
using MiRenta.Domain.Facts;
using MiRenta.Domain.Returns;
using C = MiRenta.Domain.Engine.RuleCatalog2025.Codes;

namespace MiRenta.Domain.Engine;

public sealed record EngineFact(Guid Id, string Code, decimal? Amount, string? TextValue, FactStatus Status, FactSource Source);

public sealed record EngineInput(int TaxYear, IReadOnlyList<EngineFact> Facts, IReadOnlyList<TaxRule> Rules);

public sealed record EngineIssue(string Code, string Message);

public sealed record EngineLine(
    string Concept, string Label, decimal Value, string RuleCode, string Formula,
    IReadOnlyList<Guid> SourceFactIds, IReadOnlyList<string> Warnings, int Order);

public sealed record ObligationAssessment(bool? MustFile, IReadOnlyList<string> Reasons);

public sealed record EngineResult(
    string EngineVersion, string RuleSetVersion, CalculationStatus Status,
    IReadOnlyList<EngineIssue> Blocking, IReadOnlyList<EngineIssue> Warnings,
    IReadOnlyList<EngineLine> Lines, ObligationAssessment Obligation,
    IReadOnlyList<string> RulesUsed)
{
    /// <summary>Bloqueado solo por valores que debe ingresar un contador: las líneas son una vista previa.</summary>
    public bool IsPreview => Status == CalculationStatus.Blocked && Lines.Count > 0;
}

/// <summary>
/// Motor determinista del formulario 210 (alcance MVP: residente fiscal con rentas de trabajo laborales en la cédula
/// general). Función pura: mismos hechos + mismas reglas ⇒ mismo resultado. Aritmética decimal. No inventa reglas:
/// si un concepto requerido depende de una regla sin implementar, o falta un hecho material, el cálculo queda
/// bloqueado con su motivo. Los números de casilla del formulario oficial no se asignan aquí (mapeo sin verificar).
/// </summary>
public static class Form210Engine
{
    public const string EngineVersion = "engine-210-0.1.0";

    /// <summary>Hechos que deben existir (aceptados) para poder calcular.</summary>
    public static readonly string[] RequiredFacts =
    [
        FactCodes.FiscalResident, FactCodes.IvaResponsible,
        FactCodes.LaborGrossIncome, FactCodes.LaborMandatoryPension, FactCodes.LaborMandatoryHealth, FactCodes.LaborWithholding,
        FactCodes.GrossAssets, FactCodes.Debts, FactCodes.DependentsCount,
        FactCodes.HasPensionIncome, FactCodes.HasCapitalIncome, FactCodes.HasNonLaborIncome, FactCodes.HasDividends,
        FactCodes.HasOccasionalGains,
    ];

    public static EngineResult Calculate(EngineInput input)
    {
        var ctx = new Ctx(input);
        if (input.TaxYear != RuleCatalog2025.TaxYear)
        {
            ctx.Block("year.unsupported", $"El año gravable {input.TaxYear} no tiene catálogo de reglas.");
            return ctx.Result(new ObligationAssessment(null, []));
        }

        var uvt = ctx.Param(C.Uvt, "value");
        foreach (var code in RequiredFacts) ctx.Require(code);

        // Alcance.
        if (ctx.Text(FactCodes.FiscalResident) is { } res && res != "yes")
            ctx.BlockRule(C.NonResident, "scope.non_resident", "Los no residentes fiscales están fuera del alcance de este asistente.");
        foreach (var code in new[] { FactCodes.HasPensionIncome, FactCodes.HasCapitalIncome, FactCodes.HasNonLaborIncome, FactCodes.HasDividends, FactCodes.HasOccasionalGains })
            if (ctx.Text(code) == "yes")
                ctx.Block("scope." + code, $"Declaraste {FactCodes.Label(code)}: el asistente no calcula ese concepto y el caso requiere un profesional ({code}).");
        if ((ctx.Amount(FactCodes.FeesGrossIncome) ?? 0) > 0)
            ctx.BlockRule(C.FeesIncome, "scope.fees", "Los honorarios (rentas no laborales) no están implementados.");

        // Anticipos: solo los ingresa un contador.
        ctx.RequireAccountant(FactCodes.AdvancePaymentNextYear, C.AdvancePayment);
        ctx.RequireAccountant(FactCodes.PreviousYearAdvance, C.AdvancePayment);

        var dependents = ctx.Amount(FactCodes.DependentsCount) ?? 0;
        if (dependents > 0) ctx.RequireAccountant(FactCodes.AccountantDependentsAdditional, C.DeductionDependentsAdditional);

        foreach (var f in ctx.Accepted.Values)
            if (f.Amount < 0) ctx.Block("fact.negative", $"El valor de {FactCodes.Label(f.Code)} no puede ser negativo.");

        var obligation = Obligation(ctx, uvt);
        // Si lo único que falta son valores que debe ingresar un contador, se calcula una VISTA PREVIA (estado
        // Blocked, con líneas marcadas) usando 0 en esos conceptos. Nunca se genera borrador a partir de ella.
        if (uvt is null || ctx.Blocking.Any(b => !b.Code.StartsWith("accountant.required.", StringComparison.Ordinal)))
            return ctx.Result(obligation);
        ctx.Preview = ctx.Blocking.Count > 0;

        var u = uvt.Value;
        decimal R(decimal v) => RoundThousand(ctx, v);

        // Patrimonio.
        var assets = R(ctx.Amount(FactCodes.GrossAssets)!.Value);
        var debts = R(ctx.Amount(FactCodes.Debts)!.Value);
        ctx.Line("patrimony.gross", "Patrimonio bruto", assets, "fact", "patrimony.gross_assets", FactCodes.GrossAssets);
        ctx.Line("patrimony.debts", "Deudas", debts, "fact", "patrimony.debts", FactCodes.Debts);
        ctx.Line("patrimony.net", "Patrimonio líquido", Math.Max(0, assets - debts), "derived", "max(0, patrimonio bruto − deudas)");

        // Rentas de trabajo laborales.
        var gross = R(ctx.Amount(FactCodes.LaborGrossIncome)!.Value);
        var incr = R(ctx.Amount(FactCodes.LaborMandatoryPension)!.Value + ctx.Amount(FactCodes.LaborMandatoryHealth)!.Value);
        if (incr > gross)
        {
            ctx.Block("incr.exceeds_income", "Los aportes obligatorios superan el ingreso bruto: revisa el certificado.");
            return ctx.Result(obligation);
        }
        ctx.Line("labor.gross", "Ingresos brutos por rentas de trabajo", gross, "fact", "labor.gross_income", FactCodes.LaborGrossIncome);
        ctx.Line("labor.incr", "Ingresos no constitutivos de renta (aportes obligatorios a pensión y salud)", incr, C.IncrMandatoryContributions,
            "aportes obligatorios pensión + salud", FactCodes.LaborMandatoryPension, FactCodes.LaborMandatoryHealth);
        var net = gross - incr;
        ctx.Line("labor.net", "Renta líquida (ingresos − INCR)", net, "derived", "ingresos brutos − INCR");

        // Deducciones.
        decimal deductions = 0;
        if (dependents > 0)
        {
            var cap = ctx.Param(C.DeductionDependents, "annualCapUvt")!.Value * u;
            var rate = ctx.Param(C.DeductionDependents, "rate")!.Value;
            var v = R(Math.Min(gross * rate, cap));
            deductions += v;
            ctx.Line("deduction.dependents", "Deducción por dependientes (art. 387)", v, C.DeductionDependents,
                $"min(10% × ingresos brutos, {Fmt(cap)})", FactCodes.DependentsCount);
        }
        if (ctx.Amount(FactCodes.HousingLoanInterest) is decimal housing and > 0)
        {
            var cap = ctx.Param(C.DeductionHousingInterest, "annualCapUvt")!.Value * u;
            var v = R(Math.Min(housing, cap));
            deductions += v;
            ctx.Line("deduction.housing", "Intereses de vivienda (art. 119)", v, C.DeductionHousingInterest, $"min(intereses, {Fmt(cap)})", FactCodes.HousingLoanInterest);
        }
        if (ctx.Amount(FactCodes.PrepaidHealth) is decimal prepaid and > 0)
        {
            var cap = ctx.Param(C.DeductionPrepaidHealth, "annualCapUvt")!.Value * u;
            var v = R(Math.Min(prepaid, cap));
            deductions += v;
            ctx.Line("deduction.prepaid_health", "Medicina prepagada (art. 387)", v, C.DeductionPrepaidHealth, $"min(pagos, {Fmt(cap)})", FactCodes.PrepaidHealth);
        }

        // Renta exenta del 25 %.
        var exemptCap = ctx.Param(C.ExemptLabor25, "annualCapUvt")!.Value * u;
        var exempt = R(Math.Min(Math.Max(0, net - deductions) * ctx.Param(C.ExemptLabor25, "rate")!.Value, exemptCap));
        ctx.Line("exempt.labor_25", "Renta exenta 25 % (art. 206 num. 10)", exempt, C.ExemptLabor25,
            $"min(25% × (renta líquida − deducciones), {Fmt(exemptCap)})");

        // Límite 40 % / 1.340 UVT.
        var limitCap = ctx.Param(C.LimitExemptAndDeductions, "annualCapUvt")!.Value * u;
        var limit = R(Math.Min(net * ctx.Param(C.LimitExemptAndDeductions, "rate")!.Value, limitCap));
        var claimed = deductions + exempt;
        var allowed = Math.Min(claimed, limit);
        ctx.Line("limit.claimed", "Rentas exentas y deducciones solicitadas", claimed, "derived", "deducciones + renta exenta 25 %");
        ctx.Line("limit.allowed", "Rentas exentas y deducciones imputables (límite art. 336)", allowed, C.LimitExemptAndDeductions,
            $"min(solicitadas, 40% × renta líquida, {Fmt(limitCap)})");

        var additional = R(ctx.Amount(FactCodes.AccountantDependentsAdditional) ?? 0);
        if (dependents > 0)
            ctx.Line("deduction.dependents_additional", "Deducción adicional por dependientes (art. 336 num. 3), ingresada por contador", additional,
                C.DeductionDependentsAdditional, "valor del contador", FactCodes.AccountantDependentsAdditional);

        var taxable = Math.Max(0, net - allowed - additional);
        ctx.Line("taxable.general", "Renta líquida gravable cédula general", taxable, "derived", "renta líquida − imputables − adicional dependientes");

        // Impuesto (art. 241).
        var taxUvt = TaxInUvt(ctx, taxable / u);
        var tax = R(taxUvt * u);
        ctx.Line("tax.income", "Impuesto sobre la renta líquida gravable", tax, C.TaxRates241,
            $"tabla art. 241 sobre {taxable / u:0.##} UVT = {taxUvt:0.####} UVT × {Fmt(u)}");

        var advanceNext = R(ctx.Amount(FactCodes.AdvancePaymentNextYear) ?? 0);
        var advancePrev = R(ctx.Amount(FactCodes.PreviousYearAdvance) ?? 0);
        var withholding = R(ctx.Amount(FactCodes.LaborWithholding)!.Value);
        ctx.Line("advance.next", "Anticipo renta año siguiente (ingresado por contador)", advanceNext, C.AdvancePayment, "valor del contador", FactCodes.AdvancePaymentNextYear);
        ctx.Line("advance.previous", "Anticipo liquidado el año anterior (ingresado por contador)", advancePrev, C.AdvancePayment, "valor del contador", FactCodes.PreviousYearAdvance);
        ctx.Line("withholding.total", "Retenciones en la fuente del año", withholding, "fact", "labor.withholding", FactCodes.LaborWithholding);

        var balance = tax + advanceNext - advancePrev - withholding;
        ctx.Line("balance.to_pay", "Saldo a pagar", Math.Max(0, balance), "derived", "impuesto + anticipo siguiente − anticipo anterior − retenciones");
        ctx.Line("balance.in_favor", "Saldo a favor", Math.Max(0, -balance), "derived", "−(impuesto + anticipo siguiente − anticipo anterior − retenciones)");

        return ctx.Result(obligation);
    }

    private static decimal TaxInUvt(Ctx ctx, decimal baseUvt)
    {
        var rule = ctx.Rule(C.TaxRates241)!;
        using var doc = JsonDocument.Parse(rule.ParametersJson);
        foreach (var b in doc.RootElement.GetProperty("brackets").EnumerateArray())
        {
            var from = b.GetProperty("fromUvt").GetDecimal();
            var to = b.GetProperty("toUvt").ValueKind == JsonValueKind.Null ? (decimal?)null : b.GetProperty("toUvt").GetDecimal();
            if (baseUvt > from && (to is null || baseUvt <= to))
                return (baseUvt - from) * b.GetProperty("rate").GetDecimal() + b.GetProperty("baseUvt").GetDecimal();
        }
        return 0;
    }

    private static ObligationAssessment Obligation(Ctx ctx, decimal? uvt)
    {
        if (uvt is null || ctx.Rule(C.ObligationThresholds) is null) return new ObligationAssessment(null, ["Sin umbrales verificados."]);
        var u = uvt.Value;
        var reasons = new List<string>();
        bool unknown = false;
        void Check(string code, string param, string label, bool strictlyGreater)
        {
            var threshold = ctx.Param(C.ObligationThresholds, param)!.Value * u;
            var v = ctx.Amount(code);
            if (v is null) { unknown = true; return; }
            if (strictlyGreater ? v > threshold : v >= threshold) reasons.Add($"{label} supera el umbral de {Fmt(threshold)}.");
        }
        Check(FactCodes.GrossAssets, "grossAssetsUvt", "El patrimonio bruto", true);
        var income = ctx.Amount(FactCodes.LaborGrossIncome) is { } li ? li + (ctx.Amount(FactCodes.FeesGrossIncome) ?? 0) : (decimal?)null;
        if (income is null) unknown = true;
        else if (income >= ctx.Param(C.ObligationThresholds, "grossIncomeUvt")!.Value * u) reasons.Add($"Los ingresos brutos alcanzan el umbral de {Fmt(ctx.Param(C.ObligationThresholds, "grossIncomeUvt")!.Value * u)}.");
        Check(FactCodes.CreditCardSpending, "creditCardUvt", "Los consumos con tarjeta de crédito", true);
        Check(FactCodes.TotalPurchases, "purchasesUvt", "Las compras y consumos", true);
        Check(FactCodes.TotalDeposits, "depositsUvt", "Las consignaciones y depósitos", true);
        if (ctx.Text(FactCodes.IvaResponsible) == "yes") reasons.Add("Es responsable de IVA.");
        else if (ctx.Text(FactCodes.IvaResponsible) is null) unknown = true;

        if (reasons.Count > 0) return new ObligationAssessment(true, reasons);
        if (unknown) return new ObligationAssessment(null, ["Faltan datos para determinar si está obligado a declarar."]);
        ctx.Warn("obligation.not_required", "Con los datos aportados no se superan los umbrales; la declaración sería voluntaria. Confírmalo con un profesional.");
        return new ObligationAssessment(false, ["No se supera ningún umbral con los datos aportados."]);
    }

    private static decimal RoundThousand(Ctx ctx, decimal v)
    {
        var m = ctx.Param(C.Rounding, "multiple") ?? 1000m;
        return Math.Round(v / m, 0, MidpointRounding.AwayFromZero) * m;
    }

    internal static string Fmt(decimal v) => "$" + v.ToString("#,0", CultureInfo.GetCultureInfo("es-CO"));

    private sealed class Ctx
    {
        public readonly Dictionary<string, EngineFact> Accepted = new();
        private readonly HashSet<string> _proposed = new();
        private readonly Dictionary<string, TaxRule> _rules;
        public readonly List<EngineIssue> Blocking = new();
        private readonly List<EngineIssue> _warnings = new();
        public readonly List<EngineLine> Lines = new();
        public bool Preview;
        private readonly HashSet<string> _rulesUsed = new();

        public Ctx(EngineInput input)
        {
            foreach (var f in input.Facts)
            {
                if (f.Status == FactStatus.Accepted) Accepted[f.Code] = f; // la última versión aceptada gana (vienen ordenadas)
                else if (f.Status == FactStatus.Proposed) _proposed.Add(f.Code);
            }
            _rules = input.Rules.Where(r => r.TaxYear == input.TaxYear && r.RuleSetVersion == RuleCatalog2025.RuleSetVersion)
                .ToDictionary(r => r.Code);
        }

        public TaxRule? Rule(string code)
        {
            if (!_rules.TryGetValue(code, out var r) || r.Status == RuleStatus.NotImplemented)
            {
                Block("rule.missing." + code, $"La regla {code} no está implementada o no existe en el catálogo.");
                return null;
            }
            _rulesUsed.Add(code);
            return r;
        }

        public decimal? Param(string code, string name)
        {
            var r = Rule(code);
            if (r is null) return null;
            using var doc = JsonDocument.Parse(r.ParametersJson);
            if (!doc.RootElement.TryGetProperty(name, out var p)) { Block("rule.param." + code, $"Falta el parámetro {name} de {code}."); return null; }
            return p.GetDecimal();
        }

        public void Require(string code)
        {
            if (Accepted.ContainsKey(code)) return;
            Block("fact.missing." + code, _proposed.Contains(code)
                ? $"El dato «{FactCodes.Label(code)}» está propuesto pero no confirmado."
                : $"Falta el dato «{FactCodes.Label(code)}».");
        }

        /// <summary>Concepto de regla no implementada: solo se desbloquea si un contador ingresa el valor.</summary>
        public void RequireAccountant(string factCode, string ruleCode)
        {
            if (Accepted.TryGetValue(factCode, out var f) && f.Source == FactSource.Accountant) { _rulesUsed.Add(ruleCode); return; }
            Block("accountant.required." + factCode, $"Falta {FactCodes.Label(factCode)}: el asistente no lo calcula y debe ingresarlo un contador autorizado ({ruleCode}).");
        }

        public void BlockRule(string ruleCode, string code, string message) { _rulesUsed.Add(ruleCode); Block(code, message); }

        public decimal? Amount(string code) => Accepted.TryGetValue(code, out var f) ? f.Amount : null;
        public string? Text(string code) => Accepted.TryGetValue(code, out var f) ? f.TextValue : null;

        public void Block(string code, string message) { if (Blocking.All(b => b.Code != code)) Blocking.Add(new(code, message)); }
        public void Warn(string code, string message) => _warnings.Add(new(code, message));

        public void Line(string concept, string label, decimal value, string ruleCode, string formula, params string[] factCodes)
        {
            var ids = factCodes.Where(Accepted.ContainsKey).Select(c => Accepted[c].Id).ToList();
            var warnings = new List<string>();
            if (_rules.TryGetValue(ruleCode, out var r) && r.Status == RuleStatus.PendingProfessionalApproval)
                warnings.Add("Regla pendiente de aprobación profesional.");
            else if (r?.Status == RuleStatus.NotImplemented)
                warnings.Add("Concepto no calculado por el asistente: valor ingresado por un contador.");
            if (r?.Status == RuleStatus.NotImplemented && !factCodes.All(Accepted.ContainsKey))
                warnings.Add("Valor pendiente: lo debe ingresar un contador (vista previa con 0).");
            Lines.Add(new EngineLine(concept, label, value, ruleCode, formula, ids, warnings, Lines.Count + 1));
        }

        public EngineResult Result(ObligationAssessment obligation)
        {
            CalculationStatus status;
            if (Blocking.Count > 0) { status = CalculationStatus.Blocked; if (!Preview) Lines.Clear(); }
            // Las reglas sin implementar solo llegan aquí cubiertas por un valor de contador (revisión profesional).
            else if (_rulesUsed.Any(c => _rules.TryGetValue(c, out var r) && r.Status == RuleStatus.PendingProfessionalApproval))
                status = CalculationStatus.Preliminary;
            else status = CalculationStatus.ReadyForApproval;
            if (status == CalculationStatus.Preliminary)
                Warn("rules.pending", "Hay reglas pendientes de aprobación profesional: el resultado es preliminar.");
            return new EngineResult(EngineVersion, RuleCatalog2025.RuleSetVersion, status, Blocking.ToList(), _warnings.ToList(),
                Lines.ToList(), obligation, _rulesUsed.OrderBy(x => x).ToList());
        }
    }
}
