using System.Globalization;

namespace TangentAtlas.Web.Ledger;

/// <summary>One segment of a portfolio card's holdings bar.</summary>
/// <param name="Label">What it shows in bold: a ticker, "N more" or "Cash".</param>
/// <param name="Amount">Its value, short ("$110k").</param>
/// <param name="Width">Its share of the bar, in percent.</param>
/// <param name="CssClass">Its colour.</param>
public sealed record HoldingsSegment(string Label, string Amount, decimal Width, string CssClass);

/// <summary>One figure in a portfolio card's row: a label and its value.</summary>
public sealed record CardFigure(string Label, string Value);

/// <summary>
/// The dashboard's portfolio cards, in the layout of Ken's portfolios on Your plan: the holdings bar (the three largest
/// holdings, the rest together, then cash), then the row of figures (return, volatility, Sharpe ratio, worst drop, and what
/// the money put in grew to).
/// </summary>
public static class PortfolioCards
{
    private const int LargestShown = 3;

    /// <summary>The bar of a portfolio's holdings; empty while nothing is valued.</summary>
    public static IReadOnlyList<HoldingsSegment> Bar(PortfolioView portfolio)
    {
        ArgumentNullException.ThrowIfNull(portfolio);
        if (portfolio.MarketValue is not > 0m || portfolio.Holdings.Any(holding => holding.MarketValue is null))
        {
            return [];
        }

        var total = portfolio.MarketValue.Value;
        var invested = portfolio.Holdings.Where(holding => !holding.IsCash && holding.MarketValue > 0m).ToArray();
        var cash = portfolio.Holdings.Where(holding => holding.IsCash).Sum(holding => holding.MarketValue!.Value);
        string[] colours = ["ta-holdings-c1", "ta-holdings-c3", "ta-holdings-c2"];
        var segments = invested
            .Take(LargestShown)
            .Select((holding, index) => Segment(holding.Name, holding.MarketValue!.Value, total, colours[index]))
            .ToList();
        var rest = invested.Skip(LargestShown).ToArray();
        if (rest.Length > 0)
        {
            segments.Add(Segment($"{rest.Length} more", rest.Sum(holding => holding.MarketValue!.Value), total, "ta-holdings-rest"));
        }

        if (cash > 0m)
        {
            segments.Add(Segment("Cash", cash, total, "ta-holdings-cash"));
        }

        return segments;
    }

    /// <summary>The bar in words, for a screen reader.</summary>
    public static string Describe(PortfolioView portfolio, IReadOnlyList<HoldingsSegment> bar) =>
        $"{portfolio.Name}: " + string.Join(", ", bar.Select(segment => $"{segment.Label} {segment.Amount}"));

    /// <summary>
    /// The row of figures.  Returns are annualized only over a year or more (252 market days); before that the card shows
    /// the return so far, labelled so.
    /// </summary>
    public static IReadOnlyList<CardFigure> Figures(PortfolioView portfolio)
    {
        ArgumentNullException.ThrowIfNull(portfolio);
        var performance = portfolio.Performance;
        var english = CultureInfo.GetCultureInfo("en-US");
        string Percent(decimal? value, string format = "P1") => value is { } number ? number.ToString(format, english) : "—";
        var yearOrMore = performance is { Returns: >= 252 };
        return
        [
            yearOrMore ? new CardFigure("Annual return", Percent(performance!.AnnualizedReturn)) : new CardFigure("Return so far", Percent(performance?.TotalReturn)),
            new CardFigure("Volatility", Percent(performance?.Volatility)),
            new CardFigure("Sharpe ratio", performance?.SharpeRatio is { } sharpe ? sharpe.ToString("0.00", english) : "—"),
            new CardFigure("Worst drop", performance?.MaximumDrawdown is { } drop ? (drop == 0m ? "0%" : "−" + drop.ToString("P1", english)) : "—"),
            new CardFigure($"{portfolio.NetFlows.ToString("C0", english)} grew to", Short(portfolio.MarketValue)),
        ];
    }

    /// <summary>When the figures are measured from, for the caption.</summary>
    public static string Since(PortfolioView portfolio) =>
        portfolio.Performance?.From is { } from ? $"Since {from.ToString("MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US"))}" : "Not measured yet: figures follow its first close.";

    /// <summary>A dollar amount, short: $830k, $1,054k; under $10,000 in full.</summary>
    public static string Short(decimal? value)
    {
        if (value is not { } amount)
        {
            return "—";
        }

        var english = CultureInfo.GetCultureInfo("en-US");
        return Math.Abs(amount) < 10_000m ? amount.ToString("C0", english) : (amount / 1000m).ToString("$#,0", english) + "k";
    }

    private static HoldingsSegment Segment(string label, decimal value, decimal total, string css) =>
        new(label, Short(value), Math.Round(value / total * 100m, 1), css);
}
