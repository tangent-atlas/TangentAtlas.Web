using AtlasLedger.Model;
using AtlasLedger.Model.Replica;

namespace TangentAtlas.Web.Ledger;

/// <summary>
/// The pages' view model, as a query over the person's Replica: the rows the ledger keeps (accounts, positions, prices,
/// rates, flows, daily values, statistics) named and valued for the pages.  The ledger sends quantities and prices, not
/// market values: values are multiplied here, with the server's own formula (<see cref="Valuation"/>).
/// </summary>
public static class LedgerProjection
{
    /// <summary>
    /// Every tenant's accounts, tenant by tenant, each read under its partition's lock so a view never shows half a
    /// commit.
    /// </summary>
    /// <param name="tenantIds">The person's tenants, as the ledger lists them.</param>
    /// <param name="replica">The Replica that holds them.</param>
    public static LedgerView Project(IReadOnlyList<Guid> tenantIds, AtlasLedger.Model.Ledger replica)
    {
        var accounts = new List<AccountView>();
        DateTimeOffset? asOf = null;
        foreach (var tenantId in tenantIds)
        {
            if (replica.FindPartition(tenantId) is not { } partition)
            {
                continue;
            }

            lock (partition.SyncRoot)
            {
                accounts.AddRange(partition.Accounts.OrderBy(account => account.AccountId).Select(account => Project(account, partition)));
                foreach (var price in partition.EffectivePrices)
                {
                    asOf = asOf is null || price.AsOf > asOf ? price.AsOf : asOf;
                }
            }
        }

        return new LedgerView(tenantIds, asOf, accounts);
    }

    private static AccountView Project(Account account, LedgerPartition partition)
    {
        var positions = account.Positions.Select(position => (Position: position, Value: MarketValue(position, partition))).ToArray();
        decimal? worth = positions.Any(item => item.Value is null) ? null : positions.Sum(item => item.Value!.Value);
        var contributions = account.AccountFlow?.NetContributions ?? 0m;
        var model = account.Model;
        var targets = model?.Weights.ToDictionary(weight => weight.AssetId, weight => weight.Fraction) ?? [];

        // A model's weights cover what it invests in; the cash it keeps is the rest.
        var cashTarget = Math.Max(0m, 1m - (model?.Weights.Where(weight => weight.Asset.AssetType != AssetType.Cash).Sum(weight => weight.Fraction) ?? 0m));
        var holdings = positions
            .OrderBy(item => item.Position.AssetType == AssetType.Cash)
            .ThenByDescending(item => item.Value ?? 0m)
            .ThenBy(item => item.Position.AssetId)
            .Select(item =>
            {
                var position = item.Position;
                var node = position.Asset.Node;
                var isCash = position.AssetType == AssetType.Cash;
                decimal? target = model is null ? null : isCash ? targets.GetValueOrDefault(position.AssetId, cashTarget) : targets.GetValueOrDefault(position.AssetId);
                return new HoldingView(
                    NameOf(node),
                    SymbolOf(node),
                    string.IsNullOrWhiteSpace(node.FullName) ? null : node.FullName,
                    position.Quantity,
                    position.Asset.EffectivePrice?.Price,
                    item.Value,
                    item.Value is { } value && worth is > 0m ? value / worth.Value : null,
                    target,
                    isCash);
            })
            .ToArray();

        var institution = account.Institution;
        var statistics = account.AccountStatistic;
        return new AccountView(
            account.AccountId,
            string.IsNullOrWhiteSpace(account.Node.DisplayName) ? "Account" : account.Node.DisplayName,
            account.AccountType.ToString(),
            institution?.Name ?? "",
            account.ExternalAccountId is { Length: > 0 } external ? "••" + external[Math.Max(0, external.Length - 4)..] : null,
            worth,
            positions.Where(item => item.Position.AssetType == AssetType.Cash).Sum(item => item.Value ?? 0m),
            contributions,
            worth - contributions,
            model?.Name,
            holdings,
            statistics is null
                ? null
                : new PerformanceView(statistics.From, statistics.Through, statistics.Returns, statistics.TotalReturn, statistics.AnnualizedReturn, statistics.Volatility, statistics.SharpeRatio, statistics.MaximumDrawdown, statistics.IsRanked),
            account.AccountDays.OrderBy(day => day.Date).Select(day => new DayView(day.Date, day.Value, day.Flow, day.Return)).ToArray());
    }

    /// <summary>A holding's value in its account's currency, or null without a price or a rate: a value is never assumed.</summary>
    private static decimal? MarketValue(Position position, LedgerPartition partition) =>
        position.Asset.EffectivePrice is { } price && partition.FindCurrencyRate(position.QuoteCurrency, position.AccountCurrency) is { } rate
            ? Valuation.MarketValue(position.Quantity, position.QuantityFactor, price.Price, position.PriceFactor, rate.Rate)
            : null;

    /// <summary>What the page calls a holding: its ticker, or its currency's code (a currency is an asset like any other).</summary>
    private static string NameOf(Node node) =>
        node.Symbols.Where(symbol => symbol.Scheme == SymbolScheme.Ticker).Select(symbol => symbol.Code).Order(StringComparer.Ordinal).FirstOrDefault()
        ?? node.Symbols.Where(symbol => symbol.Scheme == SymbolScheme.Currency).Select(symbol => symbol.Code).Order(StringComparer.Ordinal).FirstOrDefault()
        ?? (string.IsNullOrWhiteSpace(node.DisplayName) ? node.NodeId.ToString()[..8] : node.DisplayName);

    /// <summary>Its ticker or currency code, which also names its picture.</summary>
    private static string SymbolOf(Node node) =>
        node.Symbols.Where(symbol => symbol.Scheme is SymbolScheme.Ticker or SymbolScheme.Currency).OrderBy(symbol => symbol.Scheme != SymbolScheme.Ticker).ThenBy(symbol => symbol.Code, StringComparer.Ordinal).Select(symbol => symbol.Code).FirstOrDefault()
        ?? NameOf(node);
}
