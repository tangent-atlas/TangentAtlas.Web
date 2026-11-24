using System.Collections.Concurrent;
using AtlasLedger.Model;

namespace TangentAtlas.Web.Ledger;

/// <summary>One ticker's daily total returns: the day (its day number) and the return, oldest first.</summary>
public sealed record TickerReturns(int[] Days, double[] Returns);

/// <summary>
/// What the browser needs to work out a preview: each ticker's daily total returns, the daily risk-free rate as [day, rate]
/// pairs, the period (from, through, as day numbers), the thermostat's window, and the tickers that couldn't be had.  No
/// prices: those stay on our servers.
/// </summary>
public sealed record PreviewData(int From, int Through, int Window, double[][] RiskFree, Dictionary<string, TickerReturns> Tickers, Dictionary<string, string> Missing);

/// <summary>The ledger's market history, as it answers (raw: closes, dividends and splits).</summary>
public sealed record MarketHistoryAnswer(DateOnly From, DateOnly Through, IReadOnlyList<MarketTickerAnswer> Tickers, IReadOnlyList<MarketRateAnswer> RiskFreeRates, Dictionary<string, string> Missing);

public sealed record MarketTickerAnswer(string Ticker, IReadOnlyList<MarketCloseAnswer> Closes, IReadOnlyList<MarketDividendAnswer> Dividends, IReadOnlyList<MarketSplitAnswer> Splits);

public sealed record MarketCloseAnswer(DateOnly Date, decimal Close);

public sealed record MarketDividendAnswer(DateOnly ExDate, decimal Amount);

public sealed record MarketSplitAnswer(DateOnly Date, decimal Ratio);

public sealed record MarketRateAnswer(DateOnly Date, decimal Rate);

/// <summary>
/// The heavy part of the five-year preview, done on our server once per ticker and shared by everyone: the ledger's closes,
/// dividends and splits turned into daily total returns by the shared function (<see cref="TickerHistory.TotalReturns"/>,
/// the one the ledger's thermostat uses).  Kept for the day; the next market day's close brings a fresh copy.  The browser
/// gets only the returns, and does the light part itself (wwwroot/js/preview.js).
/// </summary>
public sealed class MarketReturns(TimeProvider clock)
{
    /// <summary>The years a preview covers.</summary>
    public const int Years = 5;

    /// <summary>How long a ticker's returns are kept before the ledger is asked again.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, TickerReturns Returns)> tickers = new(StringComparer.OrdinalIgnoreCase);
    private (DateTimeOffset At, DateOnly From, DateOnly Through, double[][] Rates)? period;

    /// <summary>Gets how many times the ledger has been asked (for tests).</summary>
    public int Fetches { get; private set; }

    /// <summary>
    /// The preview data for some tickers: from the cache, and from the ledger (as the person) for what isn't in it.
    /// </summary>
    /// <param name="fetch">Asks the ledger for the raw history of some tickers.</param>
    /// <param name="wanted">The tickers.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<PreviewData> GetAsync(Func<IReadOnlyList<string>, CancellationToken, Task<MarketHistoryAnswer>> fetch, IEnumerable<string> wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(wanted);
        var now = clock.GetUtcNow();
        var names = wanted.Select(NewPortfolios.Normalize).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var stale = names.Where(name => !tickers.TryGetValue(name, out var held) || now - held.At >= KeepFor).ToList();
        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (stale.Count > 0 || period is not { } current || now - current.At >= KeepFor)
        {
            // The ledger takes up to 20 at a time; the period and the rate come with any answer.
            var batches = stale.Count == 0 ? [[]] : stale.Chunk(20).ToArray();
            foreach (var batch in batches)
            {
                var answer = await fetch(batch.Length == 0 ? names.Take(1).DefaultIfEmpty("SPY").ToArray() : batch, cancellationToken);
                Fetches++;
                foreach (var history in answer.Tickers)
                {
                    tickers[history.Ticker] = (now, Digest(history));
                }

                foreach (var (name, why) in answer.Missing)
                {
                    missing[name] = why;
                }

                period = (now, answer.Through.AddYears(-Years), answer.Through, answer.RiskFreeRates.Select(rate => new[] { (double)rate.Date.DayNumber, (double)rate.Rate }).ToArray());
            }
        }

        var (_, from, through, rates) = period!.Value;
        var found = new Dictionary<string, TickerReturns>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (tickers.TryGetValue(name, out var held))
            {
                found[name] = held.Returns;
            }
            else
            {
                missing.TryAdd(name, "No history for it.");
            }
        }

        return new PreviewData(from.DayNumber, through.DayNumber, Thermostat.DefaultWindow, rates, found, missing);
    }

    /// <summary>The heavy part: closes, dividends and splits into daily total returns, by the shared function.</summary>
    public static TickerReturns Digest(MarketTickerAnswer history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var returns = new TickerHistory(
                history.Ticker,
                history.Closes.ToDictionary(close => close.Date, close => close.Close),
                history.Dividends.GroupBy(dividend => dividend.ExDate).ToDictionary(group => group.Key, group => group.Sum(dividend => dividend.Amount)),
                history.Splits.GroupBy(split => split.Date).ToDictionary(group => group.Key, group => group.Aggregate(1m, (product, split) => product * split.Ratio)))
            .TotalReturns()
            .OrderBy(pair => pair.Key)
            .ToArray();
        return new TickerReturns(returns.Select(pair => pair.Key.DayNumber).ToArray(), returns.Select(pair => pair.Value).ToArray());
    }
}
