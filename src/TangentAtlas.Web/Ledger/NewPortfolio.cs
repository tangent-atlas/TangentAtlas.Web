using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AtlasLedger.Model;
using AtlasLedger.Model.Replica;

namespace TangentAtlas.Web.Ledger;

/// <summary>One holding of a new portfolio: a ticker and its percent of what is invested.</summary>
/// <param name="Ticker">The ticker, as the market feed and the market history know it (SPY, AAPL).</param>
/// <param name="Percent">Its share of what is invested, in percent (60 for 60%).</param>
/// <param name="Kind">What it is, for a ticker the ledger doesn't know yet: a fund or a company's stock.</param>
public sealed record NewHolding(string Ticker, decimal Percent, AssetType Kind = AssetType.Equity);

/// <summary>
/// A new portfolio, as the person sets it up: what it holds (chosen holdings by percent: fixed weights, rebalanced), how
/// much is invested (fully, or under a volatility target, the thermostat), its name and place in the tree, and the Play
/// Money it starts with.
/// </summary>
/// <param name="VolatilityTarget">The volatility to hold, a year (0.15 for 15%); null to stay fully invested.</param>
/// <param name="ParentId">The household (a portfolio at the first level) or a portfolio to put it in.</param>
/// <param name="StartingCash">The Play Money deposited into its account, in dollars.</param>
/// <param name="Star">Whether to star it, so it shows on the person's dashboard.</param>
public sealed record NewPortfolioPlan(
    Guid TenantId,
    string Name,
    Guid ParentId,
    IReadOnlyList<NewHolding> Holdings,
    decimal? VolatilityTarget,
    decimal StartingCash,
    bool Star);

/// <summary>A place a new portfolio can go: the household (the first level), or a portfolio, with its depth in the tree.</summary>
public sealed record PortfolioPlace(Guid NodeId, string Name, int Depth);

/// <summary>A portfolio's measured volatility, to set a new one's target from by hand.</summary>
public sealed record MeasuredVolatility(Guid PortfolioId, string Name, decimal Volatility);

/// <summary>
/// What a new portfolio needs from the person's tenant: where it can go, what is already there (tickers, account numbers,
/// positions in the tree, the person's stars), and the Play Money broker and the dollar its account uses.
/// </summary>
public sealed record NewPortfolioContext(
    Guid TenantId,
    Guid HouseholdId,
    IReadOnlyList<PortfolioPlace> Places,
    IReadOnlyDictionary<Guid, int> NextPosition,
    IReadOnlyDictionary<string, (Guid AssetId, AssetType Kind)> Tickers,
    IReadOnlySet<string> AccountNumbers,
    Guid? PlayMoneyBrokerId,
    Guid? DollarId,
    Guid? MyUserId,
    int NextFavoritePosition,
    IReadOnlyList<MeasuredVolatility> Volatilities);

/// <summary>A script of rows for the ledger's batch endpoint: applied in one transaction, all or nothing.</summary>
public sealed record LedgerScript(IReadOnlyList<LedgerScriptOperation> Operations);

/// <summary>One row to insert; its key supplied here, as every key is.</summary>
public sealed record LedgerScriptOperation(string Action, string Table, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Row);

/// <summary>What the website writes for a new portfolio: the rows in one batch, then the Play Money deposit as one transaction.</summary>
public sealed record NewPortfolioWrite(Guid PortfolioId, Guid AccountId, LedgerScript Rows, TransactionMessage Deposit);

/// <summary>
/// Builds the ledger writes for a new portfolio.  Everything is created as one script of rows whose keys are made here (the
/// client supplies every key): the model (fixed weights; fully invested, or a volatility target the ledger's thermostat
/// holds), its weights, any ticker the tenant doesn't know yet, the portfolio and its place in the tree, a Play Money
/// account in it, and the person's star.  The deposit follows as an event; the ledger buys at its next rebalance.
/// </summary>
public static partial class NewPortfolios
{
    /// <summary>The longest name a portfolio can have (the ledger's display name).</summary>
    public const int MaximumNameLength = 16;

    /// <summary>The most Play Money a portfolio starts with.</summary>
    public const decimal MaximumStartingCash = 100_000_000m;

    /// <summary>The Joe 10's thermostat window: 21 daily returns.</summary>
    public const int VolatilityWindow = 21;

    /// <summary>The Joe 10's band: no trade smaller than half a percent of the account.</summary>
    public const decimal Band = 0.005m;

    /// <summary>The Play Money broker's name, when the tenant has none yet.</summary>
    public const string PlayMoneyBroker = "Play Money Brokerage";

    private static readonly JsonSerializerOptions Json = LiveLedgers.Json;

    /// <summary>
    /// Reads what a new portfolio needs from one tenant of the person's Replica, under its lock; null if the tenant has no
    /// household to put it in.
    /// </summary>
    public static NewPortfolioContext? ContextOf(LedgerPartition partition, Guid tenantId, Guid? myUserId)
    {
        ArgumentNullException.ThrowIfNull(partition);
        lock (partition.SyncRoot)
        {
            if (partition.Nodes.FirstOrDefault(node => node.NodeType == NodeType.Household && node.NodeStatus == NodeStatus.Active) is not { } household)
            {
                return null;
            }

            var places = new List<PortfolioPlace> { new(household.NodeId, "Top level", 0) };
            var volatilities = new List<MeasuredVolatility>();
            void Walk(Node node, int depth, HashSet<Guid> seen)
            {
                foreach (var child in node.EdgesByParent.OrderBy(edge => edge.Position).ThenBy(edge => edge.Child.DisplayName, StringComparer.CurrentCulture).Select(edge => edge.Child))
                {
                    if (child.NodeType != NodeType.Portfolio || child.NodeStatus != NodeStatus.Active || !seen.Add(child.NodeId))
                    {
                        continue;
                    }

                    places.Add(new PortfolioPlace(child.NodeId, child.DisplayName, depth));
                    if (child.Portfolio?.PortfolioStatistic is { From: not null, Volatility: { } volatility })
                    {
                        volatilities.Add(new MeasuredVolatility(child.NodeId, child.DisplayName, volatility));
                    }

                    Walk(child, depth + 1, seen);
                }
            }

            Walk(household, 1, [household.NodeId]);
            var nextPosition = partition.Nodes
                .Where(node => node.NodeType is NodeType.Household or NodeType.Portfolio)
                .ToDictionary(node => node.NodeId, node => node.EdgesByParent.Select(edge => edge.Position).DefaultIfEmpty(-1).Max() + 1);
            var tickers = new Dictionary<string, (Guid, AssetType)>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in partition.Symbols.Where(symbol => symbol.Scheme == SymbolScheme.Ticker))
            {
                if (partition.FindAsset(symbol.NodeId) is { } asset)
                {
                    tickers[symbol.Code] = (asset.AssetId, asset.AssetType);
                }
            }

            var dollar = partition.FindSymbol(SymbolScheme.Currency, CurrencyCode.UnitedStatesDollar)?.NodeId;
            var broker = partition.Institutions.Where(institution => institution.Source == EventSource.Simulator).OrderBy(institution => institution.InstitutionId).FirstOrDefault()?.InstitutionId;
            var numbers = partition.Symbols.Where(symbol => symbol.Scheme == SymbolScheme.Custodian).Select(symbol => symbol.Code)
                .Concat(partition.Accounts.Select(account => account.ExternalAccountId).OfType<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextFavorite = myUserId is { } me ? partition.Favorites.Where(favorite => favorite.UserId == me).Select(favorite => favorite.Position).DefaultIfEmpty(-1).Max() + 1 : 0;
            return new NewPortfolioContext(tenantId, household.NodeId, places, nextPosition, tickers, numbers, broker, dollar, myUserId, nextFavorite, volatilities);
        }
    }

    /// <summary>What is wrong with a plan, in the person's words; empty when it can be written.</summary>
    public static IReadOnlyList<string> Problems(NewPortfolioPlan plan, NewPortfolioContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        var problems = new List<string>();
        var name = plan.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            problems.Add("Give it a name.");
        }
        else if (name.Length > MaximumNameLength)
        {
            problems.Add($"Keep the name to {MaximumNameLength} characters.");
        }

        if (plan.Holdings.Count == 0)
        {
            problems.Add("Add at least one holding.");
        }

        foreach (var holding in plan.Holdings)
        {
            var ticker = Normalize(holding.Ticker);
            if (!TickerPattern().IsMatch(ticker))
            {
                problems.Add(ticker.Length == 0 ? "Every holding needs a ticker." : $"{ticker} isn't a ticker (letters, digits, a dot or a dash, up to ten).");
            }
            else if (context.Tickers.TryGetValue(ticker, out var known) && known.Kind == AssetType.Cash)
            {
                problems.Add($"{ticker} is cash: the cash comes from how much is invested.");
            }

            if (holding.Percent <= 0m)
            {
                problems.Add($"{(ticker.Length == 0 ? "A holding" : ticker)} needs a percent above zero.");
            }
        }

        foreach (var repeated in plan.Holdings.GroupBy(holding => Normalize(holding.Ticker), StringComparer.OrdinalIgnoreCase).Where(group => group.Key.Length > 0 && group.Count() > 1))
        {
            problems.Add($"{repeated.Key} is listed twice.");
        }

        var total = plan.Holdings.Sum(holding => holding.Percent);
        if (plan.Holdings.Count > 0 && Math.Abs(total - 100m) > 0.001m)
        {
            problems.Add($"The percents add up to {total.ToString("0.##", CultureInfo.InvariantCulture)}%; they must add up to 100%.");
        }

        if (plan.VolatilityTarget is { } target && (target <= 0m || target > 1m))
        {
            problems.Add("The volatility target is from 0.01 to 1.00.");
        }

        if (plan.StartingCash <= 0m || plan.StartingCash > MaximumStartingCash)
        {
            problems.Add($"Start it with between $1 and {MaximumStartingCash.ToString("C0", CultureInfo.GetCultureInfo("en-US"))} of Play Money.");
        }

        if (!context.Places.Any(place => place.NodeId == plan.ParentId))
        {
            problems.Add("Choose where it goes.");
        }

        if (context.DollarId is null)
        {
            problems.Add("Your household has no US dollar to hold its cash in. Ask us to set it up.");
        }

        return problems;
    }

    /// <summary>
    /// The writes for a plan with no problems.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="context">The tenant, as the person's Replica has it.</param>
    /// <param name="now">The time (the deposit's, and the trading day the portfolio is in its place from).</param>
    /// <param name="newKey">Makes a new key.</param>
    public static NewPortfolioWrite Build(NewPortfolioPlan plan, NewPortfolioContext context, DateTimeOffset now, Func<Guid> newKey)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(newKey);
        if (Problems(plan, context) is { Count: > 0 } problems)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(plan));
        }

        var rows = new List<LedgerScriptOperation>();
        void Insert<T>(string table, T row) => rows.Add(new LedgerScriptOperation("Insert", table, JsonSerializer.SerializeToElement(row, Json)));

        var name = plan.Name.Trim();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, LedgerPageTime.NewYork).DateTime);
        var (portfolioId, modelId, accountId) = (newKey(), newKey(), newKey());

        // The model: fixed weights, the person's percents of what is invested; the thermostat sets how much that is.
        var holdings = plan.Holdings.Select(holding => holding with { Ticker = Normalize(holding.Ticker) }).ToArray();
        Insert("Model", new ModelDto
        {
            ModelId = modelId,
            Name = Clip($"{name}: {Describe(holdings, plan.VolatilityTarget)}", 64),
            ModelKind = ModelKind.FixedWeights,
            CashRule = plan.VolatilityTarget is null ? CashRule.None : CashRule.VolatilityTarget,
            VolatilityTarget = plan.VolatilityTarget,
            VolatilityWindow = plan.VolatilityTarget is null ? null : VolatilityWindow,
            Band = Band,
        });

        foreach (var holding in holdings)
        {
            if (!context.Tickers.TryGetValue(holding.Ticker, out var asset))
            {
                // A ticker the household doesn't know yet: the market feed and the market history price it by its ticker.
                asset = (newKey(), holding.Kind == AssetType.Fund ? AssetType.Fund : AssetType.Equity);
                Insert("Node", new NodeDto { NodeId = asset.AssetId, DisplayName = Clip(holding.Ticker, 16), FullName = holding.Ticker, NodeStatus = NodeStatus.Active, NodeType = NodeType.Asset });
                Insert("Asset", new AssetDto { AssetId = asset.AssetId, AssetType = asset.Kind, QuoteCurrency = CurrencyCode.UnitedStatesDollar, QuantityFactor = 1m, PriceFactor = 1m });
                if (asset.Kind == AssetType.Fund)
                {
                    Insert("Fund", new FundDto { AssetId = asset.AssetId, FundType = FundType.ExchangeTradedFund });
                }
                else
                {
                    Insert("Equity", new EquityDto { AssetId = asset.AssetId, EquityType = EquityType.CommonStock });
                }

                Insert("Symbol", new SymbolDto { Scheme = SymbolScheme.Ticker, Code = holding.Ticker, NodeId = asset.AssetId });
            }

            Insert("Weight", new WeightDto { ModelId = modelId, AssetId = asset.AssetId, Fraction = Math.Round(holding.Percent / 100m, 6, MidpointRounding.ToZero) });
        }

        // The portfolio, last among its siblings.
        Insert("Node", new NodeDto { NodeId = portfolioId, DisplayName = name, FullName = name, NodeStatus = NodeStatus.Active, NodeType = NodeType.Portfolio });
        Insert("Portfolio", new PortfolioDto { PortfolioId = portfolioId, ModelId = modelId });
        Insert("Edge", new EdgeDto { ChildNodeId = portfolioId, ParentNodeId = plan.ParentId, Position = context.NextPosition.GetValueOrDefault(plan.ParentId), Since = today });

        // Its Play Money account, at the Play Money broker (added if the household has none yet).
        var brokerId = context.PlayMoneyBrokerId ?? newKey();
        if (context.PlayMoneyBrokerId is null)
        {
            Insert("Institution", new InstitutionDto { InstitutionId = brokerId, Name = PlayMoneyBroker, Source = EventSource.Simulator });
        }

        var number = NextAccountNumber(context.AccountNumbers);
        Insert("Node", new NodeDto { NodeId = accountId, DisplayName = "Play Money", FullName = Clip($"{name}'s Play Money account", 64), NodeStatus = NodeStatus.Active, NodeType = NodeType.Account });
        Insert("Account", new AccountDto { AccountId = accountId, AccountType = AccountType.Brokerage, Currency = CurrencyCode.UnitedStatesDollar, InstitutionId = brokerId, ExternalAccountId = number });
        Insert("Brokerage", new BrokerageDto { AccountId = accountId, BrokerageType = BrokerageType.Taxable });
        Insert("Symbol", new SymbolDto { Scheme = SymbolScheme.Custodian, Code = number, NodeId = accountId });
        Insert("Edge", new EdgeDto { ChildNodeId = accountId, ParentNodeId = portfolioId, Position = 0, Since = today });

        if (plan.Star && context.MyUserId is { } me)
        {
            Insert("Favorite", new FavoriteDto { UserId = me, PortfolioId = portfolioId, Position = context.NextFavoritePosition });
        }

        // The Play Money, deposited now.
        var deposit = new CashMovementEvent
        {
            AccountId = accountId,
            TradeDate = today,
            Method = CashMethod.Ach,
            Items = [new ActivityItem(context.DollarId!.Value, plan.StartingCash, plan.StartingCash)],
        };
        var transaction = new TransactionMessage
        {
            TransactionId = newKey(),
            Events = [EventMessages.From(deposit, newKey(), EventSource.Simulator, $"play-money-deposit-{accountId:N}", now, now)],
        };

        return new NewPortfolioWrite(portfolioId, accountId, new LedgerScript(rows), transaction);
    }

    /// <summary>A ticker as the ledger keeps it: trimmed, in capitals.</summary>
    public static string Normalize(string? ticker) => (ticker ?? "").Trim().ToUpperInvariant();

    /// <summary>"SPY at 15% volatility", "AAPL 60%, MSFT 40%, fully invested".</summary>
    public static string Describe(IReadOnlyList<NewHolding> holdings, decimal? volatilityTarget)
    {
        ArgumentNullException.ThrowIfNull(holdings);
        var mix = holdings.Count == 1
            ? Normalize(holdings[0].Ticker)
            : string.Join(", ", holdings.Select(holding => $"{Normalize(holding.Ticker)} {holding.Percent.ToString("0.##", CultureInfo.InvariantCulture)}%"));
        return volatilityTarget is { } target
            ? $"{mix} at {(target * 100m).ToString("0.##", CultureInfo.InvariantCulture)}% volatility"
            : $"{mix}, fully invested";
    }

    /// <summary>The next Play Money account number: PAPER- and six digits, after the highest the household has.</summary>
    private static string NextAccountNumber(IReadOnlySet<string> taken)
    {
        var highest = taken
            .Select(code => PaperNumber().Match(code))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(100_000)
            .Max();
        var next = (highest / 10 * 10) + 10;
        while (taken.Contains($"PAPER-{next}"))
        {
            next += 10;
        }

        return $"PAPER-{next}";
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    [GeneratedRegex("^[A-Z0-9][A-Z0-9.\\-]{0,9}$")]
    private static partial Regex TickerPattern();

    [GeneratedRegex("^PAPER-(\\d{6})$", RegexOptions.IgnoreCase)]
    private static partial Regex PaperNumber();
}

/// <summary>The exchange's clock: trading days are New York's.</summary>
internal static class LedgerPageTime
{
    public static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
}
