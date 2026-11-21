namespace TangentAtlas.Web.Ledger;

/// <summary>A holding, named and valued.</summary>
/// <param name="Name">What the page calls it: its ticker, or its currency's code ("USD").</param>
/// <param name="Symbol">Its ticker or currency code, which also names its picture.</param>
/// <param name="FullName">Its full name, when the ledger has one.</param>
public sealed record HoldingView(string Name, string Symbol, string? FullName, decimal Quantity, decimal? Price, decimal? MarketValue, decimal? Weight, decimal? TargetWeight, bool IsCash);

/// <summary>An account at one close.</summary>
public sealed record DayView(DateOnly Date, decimal? Value, decimal Flow, decimal? Return);

/// <summary>An account's performance, as the ledger measures it from its daily returns.</summary>
public sealed record PerformanceView(
    DateOnly? From,
    DateOnly? Through,
    int Returns,
    decimal? TotalReturn,
    decimal? AnnualizedReturn,
    decimal? Volatility,
    decimal? SharpeRatio,
    decimal? MaximumDrawdown,
    bool IsRanked);

/// <summary>One account: where it is held, what it holds, what it's worth, how it has done.</summary>
/// <param name="Type">The ledger's kind of account: Brokerage, RetirementPlan, Deposit, CreditCard or Loan.</param>
/// <param name="Institution">Where it is held.</param>
/// <param name="Number">Its number there, shortened to its last four characters.</param>
public sealed record AccountView(
    Guid AccountId,
    string Name,
    string Type,
    string Institution,
    string? Number,
    decimal? MarketValue,
    decimal Cash,
    decimal NetContributions,
    decimal? Gain,
    string? ModelName,
    IReadOnlyList<HoldingView> Holdings,
    PerformanceView? Performance,
    IReadOnlyList<DayView> Days);

/// <summary>
/// A portfolio: what it's worth now (its accounts' values, live), what it holds across them, its books (measured by the
/// ledger at each close), and what it contains, in the household's order.
/// </summary>
/// <param name="Holdings">Its accounts' holdings added up, asset by asset: the largest first, cash last.</param>
/// <param name="Performance">Its statistics; null until the ledger has measured it.</param>
/// <param name="NetFlows">What has come in less what has gone out, over its books.</param>
/// <param name="Portfolios">The portfolios in it.</param>
/// <param name="Accounts">The accounts in it directly (not in its portfolios).</param>
/// <param name="IsFavorite">Whether the signed-in person has starred it.</param>
/// <param name="Days">Its books at each close.</param>
/// <param name="ModelName">The model that governs it: its own, or the nearest parent's; null when none does (tracked, not
/// managed).</param>
/// <param name="IsModelInherited">Whether that model is a parent's.</param>
/// <param name="Restricted">Its restricted list: its own and every parent's, by symbol.</param>
public sealed record PortfolioView(
    Guid PortfolioId,
    string Name,
    decimal? MarketValue,
    IReadOnlyList<HoldingView> Holdings,
    PerformanceView? Performance,
    decimal NetFlows,
    IReadOnlyList<PortfolioView> Portfolios,
    IReadOnlyList<AccountView> Accounts,
    bool IsFavorite,
    IReadOnlyList<DayView> Days,
    string? ModelName,
    bool IsModelInherited,
    IReadOnlyList<string> Restricted);

/// <summary>What the person sees of their tenants: a person may belong to several (their household, an adviser's firm).</summary>
/// <param name="TenantIds">Their tenants, as the ledger lists them.</param>
/// <param name="AsOf">The time of the latest price their values use; null while they have none.</param>
/// <param name="Accounts">Every tenant's accounts, tenant by tenant.</param>
/// <param name="Portfolios">Every tenant's first-level portfolios (the household itself is never shown), in the household's
/// order, tenant by tenant.</param>
/// <param name="Unfiled">Active accounts in no portfolio, so they don't disappear.</param>
/// <param name="Favorites">The portfolios the signed-in person has starred, in their order: the dashboard's cards.</param>
public sealed record LedgerView(
    IReadOnlyList<Guid> TenantIds,
    DateTimeOffset? AsOf,
    IReadOnlyList<AccountView> Accounts,
    IReadOnlyList<PortfolioView> Portfolios,
    IReadOnlyList<AccountView> Unfiled,
    IReadOnlyList<PortfolioView> Favorites);

/// <summary>The ledger couldn't be read (down, or answering with an error); the page says so.</summary>
public sealed class LedgerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
