using System.Runtime.CompilerServices;
using System.Text.Json;
using AtlasLedger.Model;
using Jint;
using TangentAtlas.Web.Ledger;

namespace TangentAtlas.Web.Tests;

/// <summary>
/// The five-year preview is worked out twice: by the shared C# (the ledger's thermostat and statistics) and by the browser's
/// script (wwwroot/js/preview.js), from the same daily returns.  These tests run the script in a JavaScript engine and
/// require the same figures, so the preview the person sees and the portfolio the ledger runs can't drift apart.
/// </summary>
public class PreviewParityTests
{
    private static readonly DateOnly Start = new(2020, 6, 1);

    public static TheoryData<string, double?> Settings() => new()
    {
        { "SPY:100", null },
        { "SPY:100", 0.15 },
        { "SPY:100", 0.05 },
        { "SPY:60,QQQ:40", 0.10 },
        { "SPY:60,QQQ:40", 0.5 },
        { "SPY:60,QQQ:40", 1.0 },
        { "QQQ:25,SPY:25,NVDA:50", 0.18 },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void TheBrowserWorksOutWhatTheSharedCodeDoes(string mix, double? target)
    {
        var holdings = mix.Split(',').Select(item => item.Split(':')).Select(parts => (Ticker: parts[0], Percent: decimal.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var histories = new[] { History("SPY", 1, 0.011), History("QQQ", 2, 0.015), History("NVDA", 3, 0.03) }.ToDictionary(history => history.Ticker);
        var rates = new Dictionary<DateOnly, decimal> { [Start] = 0.0012m, [Start.AddYears(2)] = 0.0425m, [Start.AddYears(4)] = 0.0381m };
        var through = histories["SPY"].Closes.Keys.Max();
        var from = through.AddYears(-MarketReturns.Years);

        // The shared C#, from the closes, as the ledger works it out.
        var expected = Preview.Run(holdings.Select(holding => (histories[holding.Ticker], holding.Percent)).ToArray(), (decimal?)target, 21, rates, from, 1_000_000m);

        // The browser, from the returns the website sends it.
        var data = new PreviewData(
            from.DayNumber,
            through.DayNumber,
            21,
            rates.Select(rate => new[] { (double)rate.Key.DayNumber, (double)rate.Value }).ToArray(),
            histories.Values.ToDictionary(history => history.Ticker, history => MarketReturns.Digest(Answer(history))),
            []);
        var actual = RunScript(data, holdings, target, 1_000_000m);

        Assert.Null(expected.Problem);
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("problem").ValueKind);
        Assert.Equal(expected.From!.Value.DayNumber, actual.GetProperty("from").GetInt32());
        Assert.Equal(expected.Through!.Value.DayNumber, actual.GetProperty("through").GetInt32());
        var figures = actual.GetProperty("figures");
        Assert.Equal(expected.Figures.Returns, figures.GetProperty("returns").GetInt32());
        Close(expected.Figures.TotalReturn, figures.GetProperty("totalReturn"), 0.000002m);
        Close(expected.Figures.AnnualizedReturn, figures.GetProperty("annualizedReturn"), 0.000002m);
        Close(expected.Figures.Volatility, figures.GetProperty("volatility"), 0.000002m);
        Close(expected.Figures.SharpeRatio, figures.GetProperty("sharpeRatio"), 0.000002m);
        Close(expected.Figures.MaximumDrawdown, figures.GetProperty("maximumDrawdown"), 0.000002m);
        Close(expected.EndingValue, actual.GetProperty("endingValue"), 0.02m);
        Close(expected.AverageInvested, actual.GetProperty("averageInvested"), 0.0001m);
        Assert.Equal(expected.Values.Count, actual.GetProperty("values").GetArrayLength());
    }

    [Fact]
    public void ATickerNotLoadedYetIsAskedFor()
    {
        var data = new PreviewData(0, 0, 21, [], new Dictionary<string, TickerReturns> { ["SPY"] = MarketReturns.Digest(Answer(History("SPY", 1, 0.01))) }, []);

        var actual = RunScript(data, [("SPY", 50m), ("QQQ", 50m)], 0.15, 1_000_000m);

        Assert.Equal(["QQQ"], actual.GetProperty("pending").EnumerateArray().Select(item => item.GetString()));
    }

    private static void Close(decimal? expected, JsonElement actual, decimal tolerance)
    {
        Assert.NotNull(expected);
        Assert.InRange(actual.GetDecimal(), expected!.Value - tolerance, expected.Value + tolerance);
    }

    private static JsonElement RunScript(PreviewData data, IEnumerable<(string Ticker, decimal Percent)> holdings, double? target, decimal startingValue)
    {
        var engine = new Engine();
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(ScriptPath()));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);   // as Blazor sends it to the browser
        var call = $"JSON.stringify(tangentPreview.compute({JsonSerializer.Serialize(data, json)}, {JsonSerializer.Serialize(holdings.Select(holding => new { ticker = holding.Ticker, percent = holding.Percent }), json)}, {(target is { } value ? value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "null")}, {startingValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}))";
        return JsonDocument.Parse(engine.Evaluate(call).AsString()).RootElement.Clone();
    }

    private static string ScriptPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "TangentAtlas.Web", "wwwroot", "js", "preview.js"));

    /// <summary>Six years of weekday closes moving at random (seeded), a quarterly dividend, and a 4-for-1 split in year three.</summary>
    private static TickerHistory History(string ticker, int seed, double dailyMove)
    {
        var random = new Random(seed);
        var closes = new Dictionary<DateOnly, decimal>();
        var price = 100m;
        var split = Start.AddYears(3);
        for (var day = Start; day <= Start.AddYears(6); day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            price *= 1m + (decimal)((random.NextDouble() - 0.48) * 2 * dailyMove);
            if (day == split)
            {
                price /= 4m;
            }

            closes[day] = Math.Round(price, 2);
        }

        var dividends = Enumerable.Range(0, 24).Select(quarter => Start.AddMonths(3 * quarter).AddDays(9)).Where(closes.ContainsKey).ToDictionary(day => day, _ => 0.45m);
        return new TickerHistory(ticker, closes, dividends, new Dictionary<DateOnly, decimal> { [split] = 4m });
    }

    private static MarketTickerAnswer Answer(TickerHistory history) =>
        new(
            history.Ticker,
            history.Closes.OrderBy(pair => pair.Key).Select(pair => new MarketCloseAnswer(pair.Key, pair.Value)).ToArray(),
            history.Dividends.Select(pair => new MarketDividendAnswer(pair.Key, pair.Value)).ToArray(),
            history.Splits.Select(pair => new MarketSplitAnswer(pair.Key, pair.Value)).ToArray());
}
