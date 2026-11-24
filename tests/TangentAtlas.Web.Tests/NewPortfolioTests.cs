using System.Security.Claims;
using System.Text.Json;
using AtlasLedger.Model;
using Microsoft.Extensions.DependencyInjection;
using TangentAtlas.Web.Ledger;

namespace TangentAtlas.Web.Tests;

/// <summary>A new portfolio's checks and the rows it writes: the plan alone, against a household as the Replica has it.</summary>
public class NewPortfolioPlanTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();
    private static readonly Guid Joe10 = Guid.NewGuid();
    private static readonly Guid Aapl = Guid.NewGuid();
    private static readonly Guid Dollar = Guid.NewGuid();
    private static readonly Guid Broker = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly DateTimeOffset Sunday = new(2026, 11, 22, 16, 50, 0, TimeSpan.Zero);

    private static NewPortfolioContext Context(Guid? broker = null) => new(
        Tenant,
        Household,
        [new PortfolioPlace(Household, "Top level", 0), new PortfolioPlace(Joe10, "Joe 10", 1)],
        new Dictionary<Guid, int> { [Household] = 2, [Joe10] = 3 },
        new Dictionary<string, (Guid, AssetType)>(StringComparer.OrdinalIgnoreCase) { ["AAPL"] = (Aapl, AssetType.Equity), ["USD"] = (Dollar, AssetType.Cash) },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PAPER-100010", "PAPER-100020", "BOFA-CHK-4821" },
        broker ?? Broker,
        Dollar,
        Me,
        2,
        [new MeasuredVolatility(Joe10, "Joe 10", 0.1843m)]);

    private static NewPortfolioPlan Benchmark(params NewHolding[] holdings) =>
        new(Tenant, "SPY Benchmark 2", Household, holdings.Length == 0 ? [new NewHolding("spy", 100m, AssetType.Fund)] : holdings, 0.18m, 1_000_000m, true);

    [Fact]
    public void ABenchmarkOfSpyUnderAVolatilityTargetIsAModelAPortfolioAnAccountAndAStar()
    {
        var keys = 0;
        var write = NewPortfolios.Build(Benchmark(), Context(), Sunday, () => new Guid(++keys, 0, 0, new byte[8]));
        var operations = write.Rows.Operations;

        Assert.All(operations, operation => Assert.Equal("Insert", operation.Action));
        Assert.Equal(
            ["Model", "Node", "Asset", "Fund", "Symbol", "Weight", "Node", "Portfolio", "Edge", "Node", "Account", "Brokerage", "Symbol", "Edge", "Favorite"],
            operations.Select(operation => operation.Table));

        var model = Row<ModelDto>(operations, "Model");
        Assert.Equal((ModelKind.FixedWeights, CashRule.VolatilityTarget, (decimal?)0.18m, (int?)21, 0.005m), (model.ModelKind, model.CashRule, model.VolatilityTarget, model.VolatilityWindow, model.Band));
        Assert.Equal("SPY Benchmark 2: SPY at 18% volatility", model.Name);

        // SPY is new to the household: a fund, priced by its ticker.  Its weight is the whole mix; the thermostat sets the cash.
        var spy = Row<SymbolDto>(operations, "Symbol");
        Assert.Equal((SymbolScheme.Ticker, "SPY"), (spy.Scheme, spy.Code));
        Assert.Equal(1m, Row<WeightDto>(operations, "Weight").Fraction);

        // The portfolio goes last in the household, from today (New York's), with its model.
        var edges = Rows<EdgeDto>(operations, "Edge");
        Assert.Equal((Household, 2, new DateOnly(2026, 11, 22)), (edges[0].ParentNodeId, edges[0].Position, edges[0].Since));
        Assert.Equal((write.PortfolioId, write.AccountId), (edges[0].ChildNodeId, edges[1].ChildNodeId));
        Assert.Equal(model.ModelId, Row<PortfolioDto>(operations, "Portfolio").ModelId);

        // Its Play Money account, at the household's Play Money broker, numbered after the others.
        var account = Row<AccountDto>(operations, "Account");
        Assert.Equal((AccountType.Brokerage, Broker, "PAPER-100030", false), (account.AccountType, account.InstitutionId, account.ExternalAccountId, account.IsReadOnly));
        Assert.Equal(new FavoriteDto { UserId = Me, PortfolioId = write.PortfolioId, Position = 2 }.PortfolioId, Row<FavoriteDto>(operations, "Favorite").PortfolioId);
        Assert.Equal(2, Row<FavoriteDto>(operations, "Favorite").Position);

        // The deposit: Play Money, in dollars, into the new account.
        var deposit = Assert.Single(write.Deposit.Events);
        Assert.Equal((EventType.CashMovement, EventSource.Simulator), (deposit.EventType, deposit.Source));
        Assert.Contains(write.AccountId.ToString(), deposit.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AKnownTickerIsReusedAndFullyInvestedHasNoTarget()
    {
        var plan = Benchmark(new NewHolding("AAPL", 60m), new NewHolding("QQQ", 40m, AssetType.Fund)) with { VolatilityTarget = null, Star = false, ParentId = Joe10 };
        var write = NewPortfolios.Build(plan, Context(), Sunday, Guid.NewGuid);
        var operations = write.Rows.Operations;

        var model = Row<ModelDto>(operations, "Model");
        Assert.Equal((CashRule.None, (decimal?)null), (model.CashRule, model.VolatilityTarget));
        Assert.Equal("SPY Benchmark 2: AAPL 60%, QQQ 40%, fully invested", model.Name);
        var weights = Rows<WeightDto>(operations, "Weight");
        Assert.Equal((Aapl, 0.6m), (weights[0].AssetId, weights[0].Fraction));
        Assert.Equal(0.4m, weights[1].Fraction);
        Assert.Single(Rows<AssetDto>(operations, "Asset"));   // QQQ only
        Assert.Equal((Joe10, 3), (Rows<EdgeDto>(operations, "Edge")[0].ParentNodeId, Rows<EdgeDto>(operations, "Edge")[0].Position));
        Assert.Empty(Rows<FavoriteDto>(operations, "Favorite"));
    }

    [Fact]
    public void AHouseholdWithoutAPlayMoneyBrokerGetsOne()
    {
        var context = Context() with { PlayMoneyBrokerId = null };
        var operations = NewPortfolios.Build(Benchmark(), context, Sunday, Guid.NewGuid).Rows.Operations;

        var broker = Row<InstitutionDto>(operations, "Institution");
        Assert.Equal((NewPortfolios.PlayMoneyBroker, EventSource.Simulator), (broker.Name, broker.Source));
        Assert.Equal(broker.InstitutionId, Row<AccountDto>(operations, "Account").InstitutionId);
    }

    [Theory]
    [InlineData("", "Give it a name.")]
    [InlineData("A very long portfolio name", "Keep the name to 16 characters.")]
    public void TheNameIsNeededAndShort(string name, string problem) =>
        Assert.Contains(problem, NewPortfolios.Problems(Benchmark() with { Name = name }, Context()));

    [Fact]
    public void ThePercentsAddUpToAHundredAndEachTickerOnce()
    {
        var problems = NewPortfolios.Problems(Benchmark(new NewHolding("SPY", 50m), new NewHolding("spy", 40m), new NewHolding("not a ticker", 0m), new NewHolding("USD", 10m)), Context());

        Assert.DoesNotContain(problems, problem => problem.StartsWith("The percents", StringComparison.Ordinal));   // 50 + 40 + 0 + 10
        Assert.Contains("SPY is listed twice.", problems);
        Assert.Contains("NOT A TICKER isn't a ticker (letters, digits, a dot or a dash, up to ten).", problems);
        Assert.Contains("NOT A TICKER needs a percent above zero.", problems);
        Assert.Contains("USD is cash: the cash comes from how much is invested.", problems);
        Assert.Contains("The percents add up to 90%; they must add up to 100%.", NewPortfolios.Problems(Benchmark(new NewHolding("SPY", 90m)), Context()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.01)]
    public void TheVolatilityTargetIsAboveZeroAndAtMostOne(double target) =>
        Assert.Contains("The volatility target is from 0.01 to 1.00.", NewPortfolios.Problems(Benchmark() with { VolatilityTarget = (decimal)target }, Context()));

    [Fact]
    public void APlanWithProblemsIsNotBuilt() =>
        Assert.Throws<ArgumentException>(() => NewPortfolios.Build(Benchmark() with { Name = "" }, Context(), Sunday, Guid.NewGuid));

    private static T Row<T>(IReadOnlyList<LedgerScriptOperation> operations, string table) => Rows<T>(operations, table).First();

    private static T[] Rows<T>(IReadOnlyList<LedgerScriptOperation> operations, string table) =>
        [.. operations.Where(operation => operation.Table == table).Select(operation => operation.Row!.Value.Deserialize<T>(StandInLedger.Json)!)];
}

/// <summary>A new portfolio written as the person, through their live ledger: it comes back by the sync, starred.</summary>
public class NewPortfolioWriteTests(SignedInLedgerSiteFactory factory) : IClassFixture<SignedInLedgerSiteFactory>
{
    private static readonly ClaimsPrincipal Person = new(new ClaimsIdentity(SignedInSiteFactory.Claims, "Test"));

    [Fact]
    public async Task ABenchmarkIsWrittenAsThePersonAndAppearsAmongTheirFavorites()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var lease = await factory.Services.GetRequiredService<LiveLedgers>().AcquireAsync(Person, cancellationToken);
        var context = Assert.Single(lease!.NewPortfolioContexts());
        Assert.Equal(Guid.Parse(StandInLedger.Tenant), context.TenantId);
        Assert.Equal(["Top level", "Joe 10"], context.Places.Select(place => place.Name));
        Assert.Equal(0.255758m, Assert.Single(context.Volatilities).Volatility);   // the Joe 10's, to set the target from

        var plan = new NewPortfolioPlan(context.TenantId, "SPY Benchmark", context.HouseholdId, [new NewHolding("SPY", 100m, AssetType.Fund)], 0.26m, 1_000_000m, true);
        var portfolioId = await lease.CreatePortfolioAsync(plan, cancellationToken);

        List<(string Path, string Body)> writes;
        lock (factory.Ledger.Writes)
        {
            writes = [.. factory.Ledger.Writes];
        }

        Assert.Equal([$"/v1/tenants/{StandInLedger.Tenant}/batch", $"/v1/tenants/{StandInLedger.Tenant}/events"], writes.Select(write => write.Path));
        var view = lease.View!;
        var portfolio = LedgerProjection.FindPortfolio(view, portfolioId);
        Assert.NotNull(portfolio);
        Assert.Equal(("SPY Benchmark", true), (portfolio!.Name, portfolio.IsFavorite));
        Assert.Equal(["Joe 10", "SPY Benchmark"], view.Favorites.Select(favorite => favorite.Name));
        var account = Assert.Single(portfolio.Accounts);
        Assert.Equal(("Play Money", "••0020", 1_000_000m), (account.Name, account.Number, account.NetContributions));
        Assert.StartsWith("SPY Benchmark: SPY at 26% volatility", portfolio.ModelName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlanWithProblemsIsNotSent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var lease = await factory.Services.GetRequiredService<LiveLedgers>().AcquireAsync(Person, cancellationToken);
        var context = lease!.NewPortfolioContexts().Single();
        int before;
        lock (factory.Ledger.Writes)
        {
            before = factory.Ledger.Writes.Count;
        }

        var exception = await Assert.ThrowsAsync<LedgerWriteException>(() => lease.CreatePortfolioAsync(new NewPortfolioPlan(context.TenantId, "Half", context.HouseholdId, [new NewHolding("SPY", 50m)], null, 1_000m, false), cancellationToken));
        Assert.Equal("The percents add up to 50%; they must add up to 100%.", exception.Message);
        lock (factory.Ledger.Writes)
        {
            Assert.Equal(before, factory.Ledger.Writes.Count);
        }
    }

    [Fact]
    public async Task ThePreviewsReturnsAreWorkedOutOnceAndShared()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var lease = await factory.Services.GetRequiredService<LiveLedgers>().AcquireAsync(Person, cancellationToken);
        var returns = factory.Services.GetRequiredService<MarketReturns>();
        var tenantId = Guid.Parse(StandInLedger.Tenant);
        Task<MarketHistoryAnswer> Fetch(IReadOnlyList<string> tickers, CancellationToken token) => lease!.GetMarketHistoryAsync(tenantId, tickers, token);

        var first = await returns.GetAsync(Fetch, ["spy", "QQQ", "NOPE"], cancellationToken);
        var asked = factory.Ledger.MarketRequests;
        var again = await returns.GetAsync(Fetch, ["SPY"], cancellationToken);

        Assert.Equal(["QQQ", "SPY"], first.Tickers.Keys.Order());
        Assert.True(first.Tickers["SPY"].Returns.Length > 1_300);   // five years and a quarter of daily returns
        Assert.Equal(first.Tickers["SPY"].Days.Length, first.Tickers["SPY"].Returns.Length);
        Assert.True(first.Missing.ContainsKey("NOPE"));
        Assert.Equal(new DateOnly(2021, 11, 20).DayNumber, first.From);   // five years before the last close
        Assert.Equal(asked, factory.Ledger.MarketRequests);   // SPY again: from what our server already worked out
        Assert.Same(first.Tickers["SPY"], again.Tickers["SPY"]);
    }

    [Fact]
    public async Task TheFormIsOfferedFromTheDashboard()
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var dashboard = await client.GetStringAsync("/dashboard", TestContext.Current.CancellationToken);
        Assert.Contains("href=\"portfolios/new\"", dashboard, StringComparison.Ordinal);

        var form = await client.GetStringAsync("/portfolios/new", TestContext.Current.CancellationToken);
        Assert.Contains("What it holds", form, StringComparison.Ordinal);
        Assert.Contains("Volatility target", form, StringComparison.Ordinal);
        Assert.Contains("Joe 10 0.26", form, StringComparison.Ordinal);   // the measured figure, to use
        Assert.Contains("Create portfolio", form, StringComparison.Ordinal);
        Assert.Contains("what these settings would have done over the last five years", form, StringComparison.Ordinal);
        Assert.Contains("data-preview-target", form, StringComparison.Ordinal);
        Assert.Contains("js/preview", form, StringComparison.Ordinal);
    }
}
