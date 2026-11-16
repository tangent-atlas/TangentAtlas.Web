namespace TangentAtlas.Web.Ledger;

/// <summary>A holding, named and valued.</summary>
/// <param name="Name">What the page calls it: its ticker, or its currency's cash ("USD cash").</param>
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
    bool IsPaper,
    decimal? MarketValue,
    decimal Cash,
    decimal NetContributions,
    decimal? Gain,
    string? ModelName,
    IReadOnlyList<HoldingView> Holdings,
    PerformanceView? Performance,
    IReadOnlyList<DayView> Days);

/// <summary>What the person sees of their tenants: a person may belong to several (their household, an adviser's firm).</summary>
/// <param name="TenantIds">Their tenants, as the ledger lists them.</param>
/// <param name="AsOf">The time of the latest price their values use; null while they have none.</param>
/// <param name="Accounts">Every tenant's accounts, tenant by tenant.</param>
public sealed record LedgerView(IReadOnlyList<Guid> TenantIds, DateTimeOffset? AsOf, IReadOnlyList<AccountView> Accounts);

/// <summary>The ledger couldn't be read (down, or answering with an error); the page says so.</summary>
public sealed class LedgerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
