namespace TangentAtlas.Web.Illustration;

/// <summary>How the customer sets the second dial: the risk.</summary>
public enum RiskSetting
{
    /// <summary>The thermostat: cash is reset so the index's volatility stays near the customer's setting (Constant Volatility).</summary>
    HoldSteady,

    /// <summary>A fixed amount in cash ("from experience, I need $100,000 in cash"); the risk moves with the market.</summary>
    FixedCash,

    /// <summary>No cash; the index's own risk.</summary>
    FullyInvested,
}

/// <summary>The two dials, as the visitor has set them.</summary>
/// <param name="CompanyCount">The number of largest companies in the index (the first dial).</param>
/// <param name="Risk">How the risk is set (the second dial).</param>
/// <param name="AccountValue">The money the index is for, in dollars.</param>
/// <param name="CashAmount">The cash kept, in dollars, when <paramref name="Risk"/> is <see cref="RiskSetting.FixedCash"/>.</param>
/// <param name="TargetVolatility">The volatility to hold, as a fraction (0.15 is 15%), when <paramref name="Risk"/> is <see cref="RiskSetting.HoldSteady"/>.</param>
public sealed record DialSettings(int CompanyCount, RiskSetting Risk, decimal AccountValue, decimal CashAmount, double TargetVolatility);

/// <summary>What the dials produce. Every weight is a fraction of the whole account, cash included.</summary>
/// <param name="CompanyCount">The number of companies held.</param>
/// <param name="LargestWeights">The weights of the largest companies, largest first (at most three).</param>
/// <param name="OtherCompaniesWeight">The weight of the companies after those.</param>
/// <param name="CashWeight">The weight in cash.</param>
/// <param name="Volatility">The account's volatility, as a fraction.</param>
/// <param name="FullyInvestedVolatility">The index's own volatility, with no cash.</param>
/// <param name="BadYearLow">The smaller end of the drop in a bad year, in dollars.</param>
/// <param name="BadYearHigh">The larger end of the drop in a bad year, in dollars.</param>
/// <param name="TargetAboveIndex">True when the setting asks for more risk than the index carries, so the thermostat holds no cash.</param>
public sealed record IndexIllustration(
    int CompanyCount,
    IReadOnlyList<double> LargestWeights,
    double OtherCompaniesWeight,
    double CashWeight,
    double Volatility,
    double FullyInvestedVolatility,
    decimal BadYearLow,
    decimal BadYearHigh,
    bool TargetAboveIndex);

/// <summary>
/// The home page's illustration of a personal index: an approximation, not a backtest and not a prediction.
/// </summary>
/// <remarks>
/// <para>
/// Company weights follow a smooth curve shaped like the US market's concentration (the largest company about 7.7%,
/// the ten largest about 40% of the 500). Volatility comes from the textbook one-factor model: every company shares the
/// market's movement, and each adds its own, which diversification shrinks with the sum of the squared weights.
/// "Cash today" assumes an average market. Worst drops in past bad years ran about two to three times the volatility
/// (decisions, November 5).
/// </para>
/// <para>
/// When the real figures exist (the every-N sweep on TA500, pre-registration §18), replace this class with a table read
/// from the research export; the page's markup does not change.
/// </para>
/// </remarks>
public static class PersonalIndexIllustration
{
    /// <summary>The size of our index of the largest US companies (TA500).</summary>
    public const int UniverseSize = 500;

    /// <summary>The movement every company shares with the market, as an annual volatility.</summary>
    public const double SharedVolatility = 0.16;

    /// <summary>The movement each large company has of its own, as an annual volatility.</summary>
    public const double CompanyVolatility = 0.25;

    /// <summary>The lowest and highest volatility the thermostat can be set to.</summary>
    public const double LowestTarget = 0.05, HighestTarget = 0.25;

    /// <summary>Worst drops in past bad years, as multiples of the volatility.</summary>
    public const double BadYearLowMultiple = 2.0, BadYearHighMultiple = 3.0;

    /// <summary>The multiple used to turn a loss the customer could live with into a setting: the middle of the range.</summary>
    public const double LossToSettingMultiple = 2.5;

    /// <summary>The stops on the first dial. The curve is steep at the low end and nearly flat past about 100.</summary>
    public static IReadOnlyList<int> CompanyStops { get; } = [5, 10, 15, 20, 25, 30, 50, 100, 200, 500];

    private const double CurveOffset = 4.5, CurveExponent = 1.35;

    private static readonly double[] MarketWeights = BuildMarketWeights();

    /// <summary>Builds the index the dials describe.</summary>
    public static IndexIllustration Illustrate(DialSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var count = Math.Clamp(settings.CompanyCount, 1, UniverseSize);
        var accountValue = Math.Max(0m, settings.AccountValue);

        var weights = IndexWeights(count);
        var herfindahl = weights.Sum(w => w * w);
        var fullyInvested = Math.Sqrt(SharedVolatility * SharedVolatility + CompanyVolatility * CompanyVolatility * herfindahl);

        var targetAboveIndex = false;
        var cash = settings.Risk switch
        {
            RiskSetting.FullyInvested => 0.0,
            RiskSetting.FixedCash => accountValue == 0m ? 0.0 : (double)Math.Clamp(settings.CashAmount / accountValue, 0m, 1m),
            RiskSetting.HoldSteady => CashToHold(settings.TargetVolatility, fullyInvested, out targetAboveIndex),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Risk, "Unknown risk setting."),
        };
        var invested = 1.0 - cash;
        var volatility = invested * fullyInvested;

        var largest = weights.Take(3).Select(w => w * invested).ToArray();
        var others = Math.Max(0.0, invested - largest.Sum());

        return new IndexIllustration(
            count,
            largest,
            others,
            cash,
            volatility,
            fullyInvested,
            Dollars(accountValue, volatility * BadYearLowMultiple),
            Dollars(accountValue, volatility * BadYearHighMultiple),
            targetAboveIndex);
    }

    /// <summary>The thermostat's setting for the loss a customer could live with in a bad year.</summary>
    public static double TargetForLoss(decimal loss, decimal accountValue)
    {
        if (accountValue <= 0m)
        {
            return LowestTarget;
        }
        var fraction = (double)(Math.Max(0m, loss) / accountValue);
        return Math.Clamp(fraction / LossToSettingMultiple, LowestTarget, HighestTarget);
    }

    /// <summary>The weights of the <paramref name="count"/> largest companies, by company size, adding to one.</summary>
    public static double[] IndexWeights(int count)
    {
        var top = MarketWeights.AsSpan(0, Math.Clamp(count, 1, UniverseSize)).ToArray();
        var total = top.Sum();
        for (var i = 0; i < top.Length; i++)
        {
            top[i] /= total;
        }
        return top;
    }

    private static double CashToHold(double target, double fullyInvested, out bool targetAboveIndex)
    {
        var clamped = Math.Clamp(target, LowestTarget, HighestTarget);
        targetAboveIndex = clamped >= fullyInvested;
        return targetAboveIndex ? 0.0 : 1.0 - clamped / fullyInvested;   // the thermostat can only lower risk: no borrowing
    }

    private static decimal Dollars(decimal accountValue, double fraction) =>
        Math.Round(accountValue * (decimal)Math.Min(1.0, fraction), 0, MidpointRounding.AwayFromZero);

    private static double[] BuildMarketWeights()
    {
        var weights = new double[UniverseSize];
        for (var rank = 1; rank <= UniverseSize; rank++)
        {
            weights[rank - 1] = Math.Pow(rank + CurveOffset, -CurveExponent);
        }
        var total = weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            weights[i] /= total;
        }
        return weights;
    }
}
