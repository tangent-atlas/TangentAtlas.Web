namespace TangentAtlas.Web.Ledger;

/// <summary>A group of accounts in the dashboard's accounts panel.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Kinds">What it holds, in words.</param>
/// <param name="Types">The ledger's kinds of account it holds.</param>
/// <param name="IsDebt">Whether its accounts are owed rather than owned.</param>
/// <param name="Page">Its own page, if it has one yet.</param>
public sealed record AccountGroup(string Name, string Kinds, string[] Types, bool IsDebt, string? Page);

/// <summary>
/// The groups the accounts panel shows, as personal-finance dashboards group them: Banking, Investments, Assets,
/// Liabilities.  And sums across accounts: net worth, at the latest close and at each.
/// </summary>
public static class AccountGroups
{
    public static readonly AccountGroup[] All =
    [
        new("Banking", "Checking & savings", ["Deposit"], false, null),
        new("Investments", "Brokerage, 401(k), IRA & more", ["Brokerage", "RetirementPlan"], false, "investments"),
        new("Assets", "Real estate, vehicles & other", [], false, null),
        new("Liabilities", "Credit cards, mortgage, loans & other", ["CreditCard", "Loan"], true, null),
    ];

    public static bool IsDebt(AccountView account) => account.Type is "CreditCard" or "Loan";

    /// <summary>What an account adds to net worth: its value, or less what it owes.</summary>
    public static decimal Contribution(AccountView account) => IsDebt(account) ? -Math.Abs(account.MarketValue ?? 0m) : account.MarketValue ?? 0m;

    /// <summary>
    /// The score of several accounts together.  The ledger measures each account's returns; until it measures a
    /// household's (several accounts' combined returns, deposits taken out), only a single account has one.
    /// </summary>
    public static PerformanceView? PerformanceOf(IReadOnlyList<AccountView> accounts) => accounts.Count == 1 ? accounts[0].Performance : null;

    public static IReadOnlyList<AccountView> In(this IEnumerable<AccountView> accounts, AccountGroup group) =>
        accounts.Where(account => group.Types.Contains(account.Type)).ToArray();

    /// <summary>
    /// Net worth at each close: every account's value that day, or its last before (an account not yet open counts
    /// nothing), debts taken away.
    /// </summary>
    public static IReadOnlyList<(DateOnly Date, decimal Value)> NetWorth(IReadOnlyList<AccountView> accounts)
    {
        var dates = accounts.SelectMany(account => account.Days.Where(day => day.Value is not null).Select(day => day.Date)).Distinct().Order().ToArray();
        return dates.Select(date => (date, accounts.Sum(account =>
        {
            var last = account.Days.LastOrDefault(day => day.Date <= date && day.Value is not null);
            var value = last?.Value ?? 0m;
            return IsDebt(account) ? -Math.Abs(value) : value;
        }))).ToArray();
    }

    /// <summary>An account's value at each close.</summary>
    public static IReadOnlyList<(DateOnly Date, decimal Value)> Values(AccountView account) =>
        account.Days.Where(day => day.Value is not null).Select(day => (day.Date, day.Value!.Value)).ToArray();
}
