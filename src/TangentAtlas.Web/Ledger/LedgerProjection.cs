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
    /// <param name="myUserIds">The person's own user key in each tenant, as the ledger told them: what their favorites are
    /// keyed by.  A tenant missing here shows no favorites.</param>
    public static LedgerView Project(IReadOnlyList<Guid> tenantIds, AtlasLedger.Model.Ledger replica, IReadOnlyDictionary<Guid, Guid>? myUserIds = null)
    {
        myUserIds ??= new Dictionary<Guid, Guid>();
        var favorites = new List<PortfolioView>();
        var accounts = new List<AccountView>();
        var portfolios = new List<PortfolioView>();
        var unfiled = new List<AccountView>();
        DateTimeOffset? asOf = null;
        foreach (var tenantId in tenantIds)
        {
            if (replica.FindPartition(tenantId) is not { } partition)
            {
                continue;
            }

            lock (partition.SyncRoot)
            {
                var views = partition.Accounts.OrderBy(account => account.AccountId).Select(account => Project(account, partition)).ToArray();
                accounts.AddRange(views);
                var byId = views.ToDictionary(view => view.AccountId);

                // The household is the root and is never shown: the tree starts at its portfolios.  A portfolio outside the
                // tree is shown at the top too, so nothing disappears.
                var top = partition.Nodes
                    .Where(node => node.NodeType == NodeType.Household && node.NodeStatus == NodeStatus.Active)
                    .SelectMany(household => Children(household))
                    .Concat(partition.Portfolios.Select(portfolio => portfolio.Node).Where(node => node.EdgeByChild is null).OrderBy(node => node.DisplayName, StringComparer.CurrentCulture))
                    .Where(node => node.NodeType == NodeType.Portfolio && node.NodeStatus == NodeStatus.Active);
                var starred = myUserIds.TryGetValue(tenantId, out var me)
                    ? partition.Favorites.Where(favorite => favorite.UserId == me).OrderBy(favorite => favorite.Position).Select(favorite => favorite.PortfolioId).ToArray()
                    : [];
                var tops = top.Select(node => ProjectPortfolio(node, byId, [], starred)).ToArray();
                portfolios.AddRange(tops);
                var all = tops.SelectMany(Flatten).ToDictionary(portfolio => portfolio.PortfolioId);
                favorites.AddRange(starred.Where(all.ContainsKey).Select(portfolioId => all[portfolioId]));
                unfiled.AddRange(partition.Accounts
                    .Where(account => account.Node.NodeStatus == NodeStatus.Active && account.Node.EdgeByChild?.Parent is not { NodeType: NodeType.Portfolio, NodeStatus: NodeStatus.Active })
                    .Select(account => byId[account.AccountId]));
                foreach (var price in partition.EffectivePrices)
                {
                    asOf = asOf is null || price.AsOf > asOf ? price.AsOf : asOf;
                }
            }
        }

        return new LedgerView(tenantIds, asOf, accounts, portfolios, unfiled, favorites);
    }

    /// <summary>A node's children, in the household's order (position, then name).</summary>
    private static IEnumerable<Node> Children(Node node) =>
        node.EdgesByParent.OrderBy(edge => edge.Position).ThenBy(edge => edge.Child.DisplayName, StringComparer.CurrentCulture).Select(edge => edge.Child);

    private static PortfolioView ProjectPortfolio(Node node, IReadOnlyDictionary<Guid, AccountView> accounts, HashSet<Guid> seen, IReadOnlyCollection<Guid> starred)
    {
        seen.Add(node.NodeId);
        var children = Children(node).Where(child => child.NodeStatus == NodeStatus.Active && !seen.Contains(child.NodeId)).ToArray();
        var portfolios = children.Where(child => child.NodeType == NodeType.Portfolio).Select(child => ProjectPortfolio(child, accounts, seen, starred)).ToArray();
        var direct = children.Where(child => child.NodeType == NodeType.Account && accounts.ContainsKey(child.NodeId)).Select(child => accounts[child.NodeId]).ToArray();
        var all = direct.Concat(portfolios.SelectMany(AllAccounts)).ToArray();
        decimal? worth = all.Any(account => account.MarketValue is null) ? null : all.Sum(account => account.MarketValue!.Value);
        // The ledger writes a portfolio's statistics row before it has measured a close (no From, nothing paid in yet):
        // until it has one, there are no statistics to show.
        var statistics = node.Portfolio?.PortfolioStatistic is { From: not null } measured ? measured : null;
        return new PortfolioView(
            node.NodeId,
            string.IsNullOrWhiteSpace(node.DisplayName) ? "Portfolio" : node.DisplayName,
            worth,
            AddUp(all.SelectMany(account => account.Holdings), worth),
            statistics is null
                ? null
                : new PerformanceView(statistics.From, statistics.Through, statistics.Returns, statistics.TotalReturn, statistics.AnnualizedReturn, statistics.Volatility, statistics.SharpeRatio, statistics.MaximumDrawdown, statistics.IsRanked),
            // Before its first measured close the books have no total yet: what its accounts have had paid in so far.
            statistics?.NetFlows ?? all.Sum(account => account.NetContributions),
            portfolios,
            direct,
            starred.Contains(node.NodeId),
            node.Portfolio?.PortfolioDays.OrderBy(day => day.Date).Select(day => new DayView(day.Date, day.Value, day.Flow, day.Return)).ToArray() ?? [],
            GoverningModel(node, out var inherited)?.Name,
            inherited,
            Lineage(node).SelectMany(ancestor => ancestor.Portfolio?.Restrictions ?? []).Select(restriction => restriction.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>A portfolio and every portfolio in it, at any depth.</summary>
    public static IEnumerable<PortfolioView> Flatten(PortfolioView portfolio) => portfolio.Portfolios.SelectMany(Flatten).Prepend(portfolio);

    /// <summary>A portfolio, wherever it is in the person's tree.</summary>
    public static PortfolioView? FindPortfolio(LedgerView? view, Guid portfolioId) =>
        view?.Portfolios.SelectMany(Flatten).FirstOrDefault(portfolio => portfolio.PortfolioId == portfolioId);

    /// <summary>A node and its parents, nearest first, stopping before a loop comes round.</summary>
    private static IEnumerable<Node> Lineage(Node node)
    {
        var seen = new HashSet<Guid>();
        for (var current = node; current is not null && seen.Add(current.NodeId); current = current.EdgeByChild?.Parent)
        {
            yield return current;
        }
    }

    /// <summary>The model that governs a portfolio: its own, or the nearest active parent's.</summary>
    private static Model? GoverningModel(Node node, out bool inherited)
    {
        inherited = false;
        foreach (var current in Lineage(node))
        {
            if (current.Portfolio is { Model: { } model } && current.NodeStatus == NodeStatus.Active)
            {
                inherited = current.NodeId != node.NodeId;
                return model;
            }
        }

        return null;
    }

    /// <summary>Every account in a portfolio, at any depth.</summary>
    public static IEnumerable<AccountView> AllAccounts(PortfolioView portfolio) => portfolio.Accounts.Concat(portfolio.Portfolios.SelectMany(AllAccounts));

    /// <summary>Holdings added up across accounts, asset by asset: the largest first, cash last.</summary>
    private static HoldingView[] AddUp(IEnumerable<HoldingView> holdings, decimal? worth) =>
        holdings
            .GroupBy(holding => (holding.Symbol, holding.IsCash))
            .Select(group =>
            {
                var first = group.First();
                decimal? value = group.Any(holding => holding.MarketValue is null) ? null : group.Sum(holding => holding.MarketValue!.Value);
                return first with
                {
                    Quantity = group.Sum(holding => holding.Quantity),
                    MarketValue = value,
                    Weight = value is { } amount && worth is > 0m ? amount / worth.Value : null,
                    TargetWeight = null,
                };
            })
            .OrderBy(holding => holding.IsCash)
            .ThenByDescending(holding => holding.MarketValue ?? 0m)
            .ThenBy(holding => holding.Symbol, StringComparer.Ordinal)
            .ToArray();

    private static AccountView Project(Account account, LedgerPartition partition)
    {
        var positions = account.Positions.Select(position => (Position: position, Value: MarketValue(position, partition))).ToArray();
        decimal? worth = positions.Any(item => item.Value is null) ? null : positions.Sum(item => item.Value!.Value);
        var contributions = account.AccountFlow?.NetContributions ?? 0m;
        var model = GoverningModel(account);
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

    /// <summary>
    /// The model that governs an account, as the ledger decides it: its portfolio's, or else the nearest parent
    /// portfolio's that has one.  Closed portfolios govern nothing; a loop in the tree governs nothing.
    /// </summary>
    private static Model? GoverningModel(Account account)
    {
        var seen = new HashSet<Guid> { account.AccountId };
        for (var parent = account.Node.EdgeByChild?.Parent; parent is not null && seen.Add(parent.NodeId); parent = parent.EdgeByChild?.Parent)
        {
            if (parent.Portfolio is { Model: { } model } && parent.NodeStatus == NodeStatus.Active)
            {
                return model;
            }
        }

        return null;
    }
}
