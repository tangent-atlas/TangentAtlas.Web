namespace TangentAtlas.Web.Dashboard;

/// <summary>How a card's reading compares with its setting.</summary>
public enum CardState
{
    /// <summary>The reading is where the customer set it; nothing to do.</summary>
    OnTarget,

    /// <summary>Something is scheduled or waiting for the customer's approval.</summary>
    Waiting,

    /// <summary>The reading is outside the setting.</summary>
    Warning,
}

/// <summary>Something that needs the customer: an approval, a sign-in to renew, a rule out of bounds.</summary>
public sealed record NeedsYouItem(string What, string Why, string Action, CardState State);

/// <summary>One line of a thermostat card: what it is set to, what it reads now, what it is doing about it.</summary>
public sealed record SetNowDoing(string Set, string Now, string Doing);

/// <summary>A rule, drawn as a thermostat card.</summary>
public sealed record RuleCard(string Title, string Accounts, SetNowDoing Reading, CardState State);

/// <summary>The customer's personal index: the plan card.</summary>
/// <param name="CompanyCount">The first dial.</param>
/// <param name="Exclusions">The companies he leaves out.</param>
/// <param name="TargetVolatilityPercent">The second dial: the volatility the thermostat holds.</param>
/// <param name="VolatilityNowPercent">The volatility measured over the last 21 trading days.</param>
/// <param name="HeldIn">The accounts that hold the index.</param>
/// <param name="Value">The value of those accounts.</param>
/// <param name="Doing">What the index is doing about any difference.</param>
public sealed record IndexCard(
    int CompanyCount,
    IReadOnlyList<string> Exclusions,
    double TargetVolatilityPercent,
    double VolatilityNowPercent,
    string HeldIn,
    decimal Value,
    string Doing,
    CardState State);

/// <summary>An account, read-only.</summary>
public sealed record AccountLine(string Name, decimal Balance, bool IsOwed = false);

/// <summary>The accounts at one institution.</summary>
public sealed record InstitutionCard(string Institution, IReadOnlyList<AccountLine> Accounts);

/// <summary>Everything the signed-in dashboard shows.</summary>
public sealed record DashboardModel(
    string Owner,
    decimal Own,
    decimal Owe,
    IReadOnlyList<NeedsYouItem> NeedsYou,
    IndexCard Index,
    IReadOnlyList<RuleCard> Rules,
    IReadOnlyList<InstitutionCard> Institutions)
{
    public decimal NetWorth => Own - Owe;
}

/// <summary>
/// The dashboard prototype's data: Ken, the site's example customer, so every figure agrees with the public pages
/// (pitch notes, "Ken"). Replaced by the customer's own ledger tenant once accounts can be linked (roadmap S.4).
/// </summary>
/// <remarks>
/// Ken's eight accounts and six rules are those of "Your net worth": checking below its band and the brokerage drifted
/// from his model (the two warnings), three trades waiting. His model is now his personal index, the Ken 11 of
/// "Your plan": the 11 largest US companies less GOOGL, GOOG, TSLA and META, its risk held at his own 15.6%.
/// The index's volatility today and the transfer amount are invented for the prototype.
/// </remarks>
public static class SampleDashboard
{
    public static DashboardModel Ken { get; } = new(
        Owner: "Ken",
        Own: 505_090m,
        Owe: 5_090m,
        NeedsYou:
        [
            new("3 trades waiting for your approval", "They bring your Schwab brokerage back to your index.", "Review", CardState.Waiting),
            new("Checking is $820 below its band", "Your rule tops it up from Ally savings. The transfer is waiting for your approval.", "Review", CardState.Warning),
            new("Renew your Schwab sign-in", "Schwab asks every 7 days. Until you do, your index can’t trade.", "Renew", CardState.Waiting),
        ],
        Index: new(
            CompanyCount: 11,
            Exclusions: ["GOOGL", "GOOG", "TSLA", "META"],
            TargetVolatilityPercent: 15.6,
            VolatilityNowPercent: 15.9,
            HeldIn: "Schwab brokerage, Roth IRA and rollover IRA",
            Value: 438_820m,
            Doing: "Drifted from your index: 3 trades waiting for your approval",
            State: CardState.Warning),
        Rules:
        [
            new("Checking", "Bank of America",
                new("Between $5,000 and $10,000", "$4,180", "Top-up of $820 from Ally savings, waiting for your approval"),
                CardState.Warning),
            new("Credit cards", "American Express, Discover",
                new("Pay every card in full", "$5,090 owed", "American Express paid on the 14th; Discover set for the 21st"),
                CardState.OnTarget),
            new("Savings", "Ally",
                new("Keep $45,000", "$45,000", "Topped up this month"),
                CardState.OnTarget),
            new("Roth IRA", "Schwab",
                new("Max it out each year", "Maxed for 2026", "Nothing until January"),
                CardState.OnTarget),
        ],
        Institutions:
        [
            new("Bank of America", [new("Checking", 4_180m)]),
            new("Ally", [new("Savings", 45_000m)]),
            new("Chase", [new("Savings", 17_090m)]),
            new("Schwab", [new("Brokerage", 238_820m), new("Roth IRA", 105_000m), new("Rollover IRA", 95_000m)]),
            new("American Express", [new("Credit card", 3_850m, IsOwed: true)]),
            new("Discover", [new("Credit card", 1_240m, IsOwed: true)]),
        ]);
}
