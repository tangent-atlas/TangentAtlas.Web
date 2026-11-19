using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AtlasLedger.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Identity.Web;
using TangentAtlas.Web.Ledger;

namespace TangentAtlas.Web.Tests;

/// <summary>The site connected to a ledger: sign-in returns a code; the signed-in page reads the ledger as the person.</summary>
public class LedgerSiteFactory : ConfiguredSiteFactory
{
    public const string Scope = "api://99999999-8888-7777-6666-555555555555/Ledger.Read";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Ledger:Server", "http://ledger.test/");
        builder.UseSetting("Ledger:Scope", Scope);
        builder.UseSetting("Ledger:CertificateThumbprint", "0000000000000000000000000000000000000000");
        base.ConfigureWebHost(builder);
    }
}

/// <summary>
/// The ledger-connected site with a credential it can load: a throwaway certificate, put in the current user's store for
/// the test and taken out after it.
/// </summary>
public class CredentialedLedgerSiteFactory : ConfiguredSiteFactory
{
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;

    public CredentialedLedgerSiteFactory()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Tangent Atlas Website (test)", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.PersistKeySet);
        using var store = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Ledger:Server", "http://ledger.test/");
        builder.UseSetting("Ledger:Scope", LedgerSiteFactory.Scope);
        builder.UseSetting("Ledger:CertificateThumbprint", certificate.Thumbprint);
        base.ConfigureWebHost(builder);
    }

    public override async ValueTask DisposeAsync()
    {
        using (var store = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser))
        {
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
            store.Remove(certificate);
        }

        certificate.Dispose();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A person signed in to the ledger-connected site; the ledger and the identity provider are stand-ins.</summary>
public class SignedInLedgerSiteFactory : LedgerSiteFactory
{
    public StandInLedger Ledger { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Each request starts the person's live ledger afresh, so a test sees the stand-in as that test set it.
        builder.UseSetting("Ledger:KeepAlive", "00:00:00");
        builder.UseSetting("Ledger:TenantRefresh", "00:00:00.200");
        builder.ConfigureServices(services =>
        {
            SignedInSiteFactory.SignIn(services);
            services.AddSingleton<ITokenAcquisition>(new StandInTokens());
            services.AddHttpClient(LiveLedgers.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Ledger);
        });
    }

    /// <summary>Hands out a fixed token; the stand-in ledger checks it.</summary>
    private sealed class StandInTokens : ITokenAcquisition
    {
        public Task<string> GetAccessTokenForUserAsync(IEnumerable<string> scopes, string? authenticationScheme, string? tenantId = null, string? userFlow = null, ClaimsPrincipal? user = null, TokenAcquisitionOptions? tokenAcquisitionOptions = null) =>
            Task.FromResult(scopes.SequenceEqual([Scope]) && authenticationScheme == "OpenIdConnect" ? "the-persons-token" : "wrong-scope-or-scheme");

        public Task<Microsoft.Identity.Client.AuthenticationResult> GetAuthenticationResultForUserAsync(IEnumerable<string> scopes, string? authenticationScheme, string? tenantId = null, string? userFlow = null, ClaimsPrincipal? user = null, TokenAcquisitionOptions? tokenAcquisitionOptions = null) =>
            throw new NotSupportedException();

        public Task<string> GetAccessTokenForAppAsync(string scope, string? authenticationScheme, string? tenant = null, TokenAcquisitionOptions? tokenAcquisitionOptions = null) =>
            throw new NotSupportedException();

        public Task<Microsoft.Identity.Client.AuthenticationResult> GetAuthenticationResultForAppAsync(string scope, string? authenticationScheme, string? tenant = null, TokenAcquisitionOptions? tokenAcquisitionOptions = null) =>
            throw new NotSupportedException();

        public void ReplyForbiddenWithWwwAuthenticateHeader(IEnumerable<string> scopes, Microsoft.Identity.Client.MsalUiRequiredException msalServiceException, string? authenticationScheme, HttpResponse? httpResponse = null) =>
            throw new NotSupportedException();

        public string GetEffectiveAuthenticationScheme(string? authenticationScheme) => authenticationScheme ?? "OpenIdConnect";

        public Task ReplyForbiddenWithWwwAuthenticateHeaderAsync(IEnumerable<string> scopes, Microsoft.Identity.Client.MsalUiRequiredException msalServiceException, HttpResponse? httpResponse = null) =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// A stand-in for the ledger's sync feed: a tenant holding the Joe 10 paper account (two holdings and cash), and
/// optionally another tenant first.  It answers the generated sync client as the ledger does: a snapshot first, then
/// what changed since (<see cref="Publish"/>), long polling while nothing has.
/// </summary>
public sealed class StandInLedger : HttpMessageHandler
{
    public const string Tenant = "4a6f6531-3020-7061-7065-720000000001";
    public const string Account = "1f6ff65f-711f-8986-a0c0-e238561bf4b6";
    public const string Aapl = "a392f7e0-339b-83d7-b1ff-bf8b60012fb7";
    private const string Msft = "b392f7e0-339b-83d7-b1ff-bf8b60012fb7";
    private const string Dollar = "c392f7e0-339b-83d7-b1ff-bf8b60012fb7";
    private const string Broker = "d392f7e0-339b-83d7-b1ff-bf8b60012fb7";
    private const string Model = "e392f7e0-339b-83d7-b1ff-bf8b60012fb7";

    public const string OtherTenant = "0f0f0f0f-0000-0000-0000-00000000000a";

    public const string OtherAccount = "0f0f0f0f-0000-0000-0000-00000000000b";

    public static readonly Guid Epoch = Guid.Parse("00000000-0000-0000-0000-0000000e0c40");

    public static readonly JsonSerializerOptions Json = CreateJson();

    private readonly Lock gate = new();
    private readonly List<(long Sequence, Guid TenantId, LedgerSyncChanges Changes)> published = [];
    private TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long sequence = 100;

    public bool IsMember { get; set; } = true;

    /// <summary>The Joe 10's tenant no longer admits the person: its feed answers 404 (as the ledger does to a non-member).</summary>
    public bool IsRevoked { get; set; }

    /// <summary>The person also belongs to another tenant, listed first (as the walkthrough's sample firm is in production).</summary>
    public bool AlsoInAnotherTenant { get; set; }

    /// <summary>What the person's tenant list fails with, if anything: a timeout, as after the page was left idle.</summary>
    public Exception? TenantsFailWith { get; set; }

    public List<string> Requests { get; } = [];

    /// <summary>Wakes the waiting long polls, as a commit would (they answer with whatever is now true).</summary>
    public void Wake()
    {
        TaskCompletionSource answered;
        lock (gate)
        {
            answered = next;
            next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        answered.TrySetResult();
    }

    /// <summary>Commits a change to a tenant: the next sync after it brings it, and a waiting long poll answers.</summary>
    public void Publish(Guid tenantId, LedgerSyncChanges changes)
    {
        TaskCompletionSource answered;
        lock (gate)
        {
            published.Add((++sequence, tenantId, changes));
            answered = next;
            next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        answered.TrySetResult();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        lock (Requests)
        {
            Requests.Add(path);
        }

        if (request.Headers.Authorization?.ToString() != "Bearer the-persons-token")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (request.RequestUri.AbsolutePath == "/v1/me/tenants")
        {
            if (TenantsFailWith is { } failure)
            {
                throw failure;
            }

            return Ok(!IsMember ? "[]" : AlsoInAnotherTenant ? $"[\"{OtherTenant}\",\"{Tenant}\"]" : $"[\"{Tenant}\"]");
        }

        var match = System.Text.RegularExpressions.Regex.Match(request.RequestUri.AbsolutePath, "^/v1/tenants/([0-9a-f-]{36})/changes$");
        if (!match.Success || request.Headers.GetValues("Bellerophon-Model-Fingerprint").Single() != AtlasLedger.Model.Ledger.Fingerprint)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var tenantId = Guid.Parse(match.Groups[1].Value);
        if (IsRevoked && tenantId == Guid.Parse(Tenant))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var since = long.Parse(query["since"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        if (since == 0 || query["epoch"] != Epoch.ToString())
        {
            return Ok(JsonSerializer.Serialize(new LedgerSyncResponse { Epoch = Epoch, Through = sequence, Reset = true, Tables = Snapshot(tenantId) }, Json));
        }

        var waitUntil = DateTimeOffset.UtcNow.AddSeconds(int.Parse(query["wait"] ?? "0", System.Globalization.CultureInfo.InvariantCulture));
        while (true)
        {
            Task changed;
            if (IsRevoked && tenantId == Guid.Parse(Tenant))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            lock (gate)
            {
                var latest = published.LastOrDefault(item => item.Sequence > since && item.TenantId == tenantId);
                if (latest.Changes is not null || DateTimeOffset.UtcNow >= waitUntil)
                {
                    return Ok(JsonSerializer.Serialize(new LedgerSyncResponse { Epoch = Epoch, Through = sequence, Tables = latest.Changes ?? new() }, Json));
                }

                changed = next.Task;
            }

            await Task.WhenAny(changed, Task.Delay(waitUntil - DateTimeOffset.UtcNow, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static JsonSerializerOptions CreateJson()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return json;
    }

    private static LedgerSyncTable<T> Rows<T>(params T[] rows) => new() { Rows = rows };

    private static LedgerSyncChanges Snapshot(Guid tenantId)
    {
        if (tenantId == Guid.Parse(OtherTenant))
        {
            return new LedgerSyncChanges
            {
                Node = Rows(new NodeDto { NodeId = Guid.Parse(OtherAccount), DisplayName = "Smith Trust", FullName = "Smith Trust", NodeType = NodeType.Account }),
                Account = Rows(new AccountDto { AccountId = Guid.Parse(OtherAccount), AccountType = AccountType.Brokerage, AccountRole = AccountRole.ManagedAccount, Currency = "USD" }),
            };
        }

        var close = DateTimeOffset.Parse("2026-11-13T21:35:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var (account, aapl, msft, dollar, broker, model) = (Guid.Parse(Account), Guid.Parse(Aapl), Guid.Parse(Msft), Guid.Parse(Dollar), Guid.Parse(Broker), Guid.Parse(Model));
        return new LedgerSyncChanges
        {
            CurrencyRate = Rows(new CurrencyRateDto { FromCurrency = "USD", ToCurrency = "USD", Rate = 1m }),
            Institution = Rows(new InstitutionDto { InstitutionId = broker, Name = "Tangent Atlas Paper Brokerage", Source = EventSource.Simulator }),
            Model = Rows(new ModelDto { ModelId = model, Name = "Joe 10", Band = 0.05m }),
            Node = Rows(
                new NodeDto { NodeId = account, DisplayName = "Joe 10", FullName = "Joe 10 paper account", NodeType = NodeType.Account },
                new NodeDto { NodeId = aapl, DisplayName = "Apple", FullName = "Apple Inc.", NodeType = NodeType.Asset },
                new NodeDto { NodeId = msft, DisplayName = "Microsoft", FullName = "Microsoft Corporation", NodeType = NodeType.Asset },
                new NodeDto { NodeId = dollar, DisplayName = "US dollar", FullName = "United States dollar", NodeType = NodeType.Asset }),
            Account = Rows(new AccountDto { AccountId = account, AccountType = AccountType.Brokerage, AccountRole = AccountRole.ManagedAccount, Currency = "USD", ExternalAccountId = "PAPER-100010", InstitutionId = broker, ModelId = model }),
            AccountDay = Rows(
                new AccountDayDto { AccountId = account, Date = new DateOnly(2026, 10, 15), Value = 999_978.82m, Flow = 1_000_000m },
                new AccountDayDto { AccountId = account, Date = new DateOnly(2026, 10, 16), Value = 990_730.22m, Flow = 0m, Return = -0.0092m },
                new AccountDayDto { AccountId = account, Date = new DateOnly(2026, 11, 13), Value = 1_048_545.32m, Flow = 0m, Return = -0.0045m }),
            AccountFlow = Rows(new AccountFlowDto { AccountId = account, NetContributions = 1_000_000m }),
            AccountStatistic = Rows(new AccountStatisticDto
            {
                AccountId = account, From = new DateOnly(2026, 10, 15), Through = new DateOnly(2026, 11, 13), Value = 1_048_545.32m, NetFlows = 1_000_000m, Returns = 21,
                TotalReturn = 0.048568m, AnnualizedReturn = 0.766676m, Volatility = 0.255758m, SharpeRatio = 2.187468m, MaximumDrawdown = 0.04146m, IsRanked = true,
            }),
            Asset = Rows(
                new AssetDto { AssetId = aapl, AssetType = AssetType.Equity, QuantityFactor = 1m, PriceFactor = 1m, QuoteCurrency = "USD" },
                new AssetDto { AssetId = msft, AssetType = AssetType.Equity, QuantityFactor = 1m, PriceFactor = 1m, QuoteCurrency = "USD" },
                new AssetDto { AssetId = dollar, AssetType = AssetType.Cash, QuantityFactor = 1m, PriceFactor = 1m, QuoteCurrency = "USD" }),
            EffectivePrice = Rows(
                new EffectivePriceDto { AssetId = aapl, Price = 342.10m, Currency = "USD", AsOf = close, Source = PriceSource.Market },
                new EffectivePriceDto { AssetId = msft, Price = 510.00m, Currency = "USD", AsOf = close, Source = PriceSource.Market },
                new EffectivePriceDto { AssetId = dollar, Price = 1m, Currency = "USD", AsOf = close.AddDays(-30), Source = PriceSource.Market }),
            Position = Rows(
                Holding(account, aapl, AssetType.Equity, 1052m),
                Holding(account, msft, AssetType.Equity, 1100m),
                Holding(account, dollar, AssetType.Cash, 127_656.12m)),
            Symbol = Rows(
                new SymbolDto { Scheme = "Ticker", Code = "AAPL", NodeId = aapl },
                new SymbolDto { Scheme = "Ticker", Code = "MSFT", NodeId = msft },
                new SymbolDto { Scheme = "Currency", Code = "USD", NodeId = dollar }),
            Weight = Rows(
                new WeightDto { ModelId = model, AssetId = aapl, Fraction = 0.36m },
                new WeightDto { ModelId = model, AssetId = msft, Fraction = 0.54m }),
        };
    }

    public static PositionDto Holding(Guid account, Guid asset, AssetType type, decimal quantity) => new()
    {
        AccountId = account, AssetId = asset, AssetType = type, Quantity = quantity, QuantityFactor = 1m, PriceFactor = 1m,
        AccountCurrency = "USD", QuoteCurrency = "USD", LastEffectiveTime = DateTimeOffset.Parse("2026-11-13T21:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
    };
}

public class LedgerSignInTests(LedgerSiteFactory factory) : IClassFixture<LedgerSiteFactory>
{
    private readonly HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task SignInAsksForACodeAndThePersonsLedgerReadPermission()
    {
        var response = await client.GetAsync("/account/signin", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Uri.UnescapeDataString(response.Headers.Location!.ToString());
        Assert.StartsWith(ConfiguredSiteFactory.AuthorizationEndpoint, location);
        Assert.Contains($"client_id={ConfiguredSiteFactory.ClientId}", location);
        Assert.Contains("response_type=code", location);
        Assert.Contains("offline_access", location);
        Assert.Contains("email", location);
        Assert.Contains(LedgerSiteFactory.Scope, location);
        Assert.Contains("code_challenge=", location);   // PKCE
        Assert.DoesNotContain("Ledger.Write", location);
    }

    /// <summary>
    /// The ID token's claims keep Entra's names (oid, name, email): Microsoft.Identity.Web brings a token handler of its
    /// own, which would otherwise rename oid, and sign-in would fail for want of it (found signing in, November 15).
    /// </summary>
    [Fact]
    public async Task TheIdTokensClaimsKeepEntrasNames()
    {
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme);
        var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(
            """{"oid":"00000000-0000-0000-0000-0000000000aa","name":"Pat","email":"pat@example.com"}""");
        var parameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            SignatureValidator = (text, _) => new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(text),
        };

        var result = await options.TokenHandler.ValidateTokenAsync(token, parameters);

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("00000000-0000-0000-0000-0000000000aa", result.ClaimsIdentity.FindFirst("oid")?.Value);
        Assert.Equal("pat@example.com", result.ClaimsIdentity.FindFirst("email")?.Value);
    }

    [Fact]
    public async Task SignUpStillOpensTheUserFlowOnItsSignUpPage()
    {
        var response = await client.GetAsync("/account/signup?understood=yes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("prompt=create", response.Headers.Location!.ToString());
    }
}

public class LedgerAccountsTests(SignedInLedgerSiteFactory factory) : IClassFixture<SignedInLedgerSiteFactory>
{
    private readonly HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task TheDashboardGroupsTheAccountsAndTotalsWhatThePersonOwns()
    {
        factory.Ledger.IsMember = true;
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/dashboard", TestContext.Current.CancellationToken));

        Assert.Contains("All accounts", html);
        Assert.Contains("$1,048,545.32", html);                       // the investments group, and all accounts
        Assert.Contains("Linking: coming soon", html);                // banking, assets, liabilities
        Assert.Contains($"href=\"accounts/{StandInLedger.Account}\"", html);
        Assert.Contains("href=\"investments\"", html);
        Assert.DoesNotContain("Net worth", html);                     // its chart is gone for now
        Assert.DoesNotContain("<polyline", html);
        Assert.Contains("2.19", html);                                // the Sharpe ratio, in the investments card
        // The status bar: the score of all investments (one account here, so its Sharpe ratio), under the top bar.
        Assert.Matches(@"class=""ta-status-bar"" aria-label=""Score: All investments""[\s\S]*?ta-score-value"">2\.19</div>", html);
        Assert.DoesNotContain("Your rules", html);
    }

    [Fact]
    public async Task APersonInTwoTenantsSeesTheAccountsOfBoth()
    {
        factory.Ledger.IsMember = true;
        factory.Ledger.AlsoInAnotherTenant = true;
        try
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync("/dashboard", TestContext.Current.CancellationToken));

            Assert.Contains("Smith Trust", html);
            Assert.Contains($"href=\"accounts/{StandInLedger.Account}\"", html);   // the Joe 10, in the second tenant
            Assert.Contains("$1,048,545.32", html);
        }
        finally
        {
            factory.Ledger.AlsoInAnotherTenant = false;
        }
    }

    [Fact]
    public async Task InvestmentsListsTheBrokerageAccount()
    {
        factory.Ledger.IsMember = true;
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/investments", TestContext.Current.CancellationToken));

        Assert.Contains($"href=\"accounts/{StandInLedger.Account}\"", html);
        Assert.Contains("Tangent Atlas Paper Brokerage · ••0010", html);
        Assert.Contains("$1,048,545", html);
        Assert.Contains("4.86%", html);
    }

    [Fact]
    public async Task TheContentsShowAsTilesUntilThePersonChoosesDetails()
    {
        factory.Ledger.IsMember = true;
        var page = $"/accounts/{StandInLedger.Account}";

        var tiles = WebUtility.HtmlDecode(await client.GetStringAsync(page, TestContext.Current.CancellationToken));
        Assert.Contains("ta-tiles-grid", tiles);
        // A tile: ticker, shares, price, value, in that order; no weight bar.
        Assert.Matches(@"ta-holding-symbol"">AAPL</div>\s*<div class=""ta-holding-shares"">1,052 shares</div>\s*<div class=""ta-holding-price""><span>\$342\.10</span> per share</div>\s*<div class=""ta-holding-value"">\$359,889\.20</div>", tiles);
        // Cash is an asset like any other: its code, its quantity, its price of $1.00, its value.
        Assert.Matches(@"ta-holding-symbol"">USD</div>\s*<div class=""ta-holding-shares"">127,656 shares</div>\s*<div class=""ta-holding-price""><span>\$1\.00</span> per share</div>\s*<div class=""ta-holding-value"">\$127,656\.12</div>", tiles);
        Assert.DoesNotContain("ta-drift", tiles);

        var choose = await client.GetAsync(ContentsViews.Link(ContentsView.Details, page), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, choose.StatusCode);
        Assert.Equal(page, choose.Headers.Location!.OriginalString);
        var cookie = choose.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(ContentsViews.Cookie));

        using var request = new HttpRequestMessage(HttpMethod.Get, page);
        request.Headers.Add("Cookie", cookie.Split(';')[0]);
        var details = WebUtility.HtmlDecode(await (await client.SendAsync(request, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("ta-details-name", details);
        Assert.DoesNotContain("ta-tiles-grid", details);
    }

    [Fact]
    public async Task ALivePageRemembersAViewWithoutLeaving()
    {
        // The live page changes the view in place and posts the choice; the next visit opens that way.
        var choose = await client.PostAsync("/contents-view/details", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, choose.StatusCode);
        var cookie = choose.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(ContentsViews.Cookie));
        Assert.StartsWith($"{ContentsViews.Cookie}=Details;", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        var unknown = await client.PostAsync("/contents-view/sideways", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.False(unknown.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task TheViewChoicesWorkBeforeThePageIsLive()
    {
        // Prerendered, each choice is still a link that sets the view and comes back, for the moment before the page is live.
        factory.Ledger.IsMember = true;
        var page = $"/accounts/{StandInLedger.Account}";
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(page, TestContext.Current.CancellationToken));

        Assert.Contains($"href=\"{ContentsViews.Link(ContentsView.Details, page)}\"", html);
        Assert.Contains($"href=\"{ContentsViews.Link(ContentsView.Tiles, page)}\"", html);
    }

    [Fact]
    public async Task ChoosingAViewReturnsOnlyToThisSite()
    {
        var response = await client.GetAsync("/contents-view/details?returnUrl=" + Uri.EscapeDataString("//evil.example/"), TestContext.Current.CancellationToken);

        Assert.Equal("/dashboard", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task TheAccountsPageShowsItsHoldingsAndPerformance()
    {
        factory.Ledger.IsMember = true;
        var html = await client.GetStringAsync($"/accounts/{StandInLedger.Account}", TestContext.Current.CancellationToken);

        Assert.Contains("Joe 10", html);
        Assert.DoesNotContain("Paper money", html);                // the name and the number's last four identify it
        Assert.Contains("Invested by the Joe 10 model.", html);
        Assert.Matches(@"class=""ta-status-bar"" aria-label=""Score: Joe 10""[\s\S]*?ta-score-value"">2\.19</div>", html);
        Assert.Contains("<polyline", html);        // the account's chart stays, last in its card
        Assert.Contains("$1,048,545", html);       // worth
        Assert.Contains("4.86%", html);            // total return
        Assert.Contains("25.58%", html);           // volatility
        Assert.Contains("2.19", html);             // Sharpe ratio
        Assert.Contains("worst drop 4.15%", html);
        Assert.Contains("AAPL", html);
        Assert.Contains("$359,889.20", html);
        Assert.Contains("USD", html);
        Assert.Contains("<polyline", html);        // the value at each close
        Assert.Contains("Updated November 13, 4:35 PM New York time.", html);
    }

    [Fact]
    public async Task SignedInPagesShowOnlyTheSignedInButtons()
    {
        var html = await client.GetStringAsync("/dashboard", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Your net worth", html);
        Assert.DoesNotContain("When the storm hits", html);
        Assert.Contains("href=\"investments\"", html);
        Assert.Contains("Sign out", html);
    }

    [Fact]
    public async Task AnotherAccountIsNotShown()
    {
        factory.Ledger.IsMember = true;
        var html = await client.GetStringAsync($"/accounts/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Contains("Not one of your accounts", html);
        Assert.DoesNotContain("AAPL", html);
    }

    [Fact]
    public async Task APersonWithNoTenantSeesThePageAsBefore()
    {
        factory.Ledger.IsMember = false;
        var html = await client.GetStringAsync("/dashboard", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Paper money", html);
        Assert.Contains("Linking accounts is the next step to open.", html);
        factory.Ledger.IsMember = true;
    }
}

public class LedgerTokenTests(CredentialedLedgerSiteFactory factory) : IClassFixture<CredentialedLedgerSiteFactory>
{
    /// <summary>
    /// The site's real token acquisition, for a person whose token it doesn't hold (as after a restart): it asks them to
    /// sign in again, rather than failing for want of its settings (found signing in, November 15: the default scheme is
    /// the cookie, which has none).
    /// </summary>
    [Fact]
    public async Task APersonWhoseTokenTheSiteLacksIsAskedToSignInAgain()
    {
        using var scope = factory.Services.CreateScope();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 7062);
        context.Request.Path = "/accounts";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "00000000-0000-0000-0000-0000000000aa"), new Claim("tid", "d400d921-89c1-4c47-8d91-16c7c2ed76cb")],
            "Test"));
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var ledgers = scope.ServiceProvider.GetRequiredService<LiveLedgers>();

        var exception = await Record.ExceptionAsync(() => ledgers.AcquireAsync(context.User, TestContext.Current.CancellationToken));

        Assert.True(
            exception is MicrosoftIdentityWebChallengeUserException or Microsoft.Identity.Client.MsalUiRequiredException,
            exception?.ToString());
    }
}

/// <summary>The signed-in pages follow the ledger: a Replica per person, kept current by the generated sync client.</summary>
public class LiveLedgerTests(SignedInLedgerSiteFactory factory) : IClassFixture<SignedInLedgerSiteFactory>
{
    private static readonly ClaimsPrincipal Person = new(new ClaimsIdentity(SignedInSiteFactory.Claims, "Test"));

    [Fact]
    public async Task TheSignedInPagesRunInteractivelyAndThePublicPagesDoNot()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var page in new[] { "/dashboard", "/investments", $"/accounts/{StandInLedger.Account}" })
        {
            var html = await client.GetStringAsync(page, TestContext.Current.CancellationToken);

            Assert.Contains("<!--Blazor:{\"type\":\"server\"", html);   // prerendered, then live over the circuit, frame and all
            Assert.Contains("Joe 10", html);
        }

        foreach (var page in new[] { "/", "/privacy", "/your-plan" })
        {
            Assert.DoesNotContain("<!--Blazor:{\"type\":\"server\"", await client.GetStringAsync(page, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task AnAccountsTitleComesFromTheViewModel()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync($"/accounts/{StandInLedger.Account}", TestContext.Current.CancellationToken);

        Assert.Contains("<title>Joe 10 · Tangent Atlas</title>", html);
        Assert.Matches("<div class=\"ta-app-title\">.*Investments.*›</span> Joe 10", html.ReplaceLineEndings(" "));
    }

    [Fact]
    public async Task AFailureStartingTheLedgerShowsTheNoticeAndTheNextLoadRecovers()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        factory.Ledger.TenantsFailWith = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 75 seconds elapsing.");
        try
        {
            var response = await client.GetAsync("/dashboard", TestContext.Current.CancellationToken);
            var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("An error occurred while processing your request", html);
            Assert.Contains("Your accounts couldn&#x27;t be loaded just now. Reload the page to try again.", html);
        }
        finally
        {
            factory.Ledger.TenantsFailWith = null;
        }

        var again = await client.GetStringAsync("/dashboard", TestContext.Current.CancellationToken);
        Assert.Contains("Joe 10", again);
        Assert.DoesNotContain("couldn&#x27;t be loaded", again);
    }

    [Fact]
    public async Task ThePagesAskOnlyForTheTablesTheyRead()
    {
        using var lease = await factory.Services.GetRequiredService<LiveLedgers>().AcquireAsync(Person, TestContext.Current.CancellationToken);

        string first;
        lock (factory.Ledger.Requests)
        {
            first = factory.Ledger.Requests.Last(path => path.Contains("/changes?since=0", StringComparison.Ordinal));
        }

        var tables = System.Web.HttpUtility.ParseQueryString(new Uri(new Uri("http://ledger.test"), first).Query)["tables"]!.Split(',');
        Assert.Contains("Position", tables);
        Assert.Contains("AccountStatistic", tables);
        Assert.Contains("Node", tables);          // a parent, added by the sync client
        Assert.DoesNotContain("Event", tables);
        Assert.DoesNotContain("Person", tables);
        Assert.DoesNotContain("Member", tables);
    }

    [Fact]
    public async Task APriceChangeReachesAnOpenPage()
    {
        using var lease = await factory.Services.GetRequiredService<LiveLedgers>().AcquireAsync(Person, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        Assert.Equal(359_889.20m, Aapl(lease!.View!).MarketValue);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.Changed += () => changed.TrySetResult();

        factory.Ledger.Publish(Guid.Parse(StandInLedger.Tenant), new LedgerSyncChanges
        {
            EffectivePrice = new LedgerSyncTable<EffectivePriceDto>
            {
                Rows = [new EffectivePriceDto { AssetId = Guid.Parse(StandInLedger.Aapl), Price = 350m, Currency = "USD", AsOf = DateTimeOffset.Parse("2026-11-16T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture), Source = PriceSource.Market }],
            },
        });

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var account = lease.View!.Accounts.Single(item => item.AccountId == Guid.Parse(StandInLedger.Account));
        Assert.Equal(368_200m, Aapl(lease.View).MarketValue);              // 1,052 shares × $350
        Assert.Equal(1_056_856.12m, account.MarketValue);                   // the account follows
        Assert.Equal(56_856.12m, account.Gain);
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture), lease.View.AsOf);
    }

    [Fact]
    public async Task APersonsPagesShareOneReplica()
    {
        var ledgers = factory.Services.GetRequiredService<LiveLedgers>();
        int Snapshots()
        {
            lock (factory.Ledger.Requests)
            {
                return factory.Ledger.Requests.Count(path => path.Contains("/changes?since=0", StringComparison.Ordinal));
            }
        }

        using var first = await ledgers.AcquireAsync(Person, TestContext.Current.CancellationToken);
        var before = Snapshots();
        using var second = await ledgers.AcquireAsync(Person, TestContext.Current.CancellationToken);

        Assert.Equal(before, Snapshots());
        Assert.Same(first!.View, second!.View);
    }

    [Fact]
    public async Task ATenantThePersonJoinsAppearsWithoutAReload()
    {
        factory.Ledger.IsMember = false;
        try
        {
            using var lease = await factory.Services.GetRequiredService<LiveLedgers>().AcquireAsync(Person, TestContext.Current.CancellationToken);
            Assert.Null(lease!.View);   // the dashboard says linking accounts is the next step
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lease.Changed += () => changed.TrySetResult();

            factory.Ledger.IsMember = true;   // made a user of the Joe 10's tenant

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(3, lease.View!.Accounts.Single(account => account.AccountId == Guid.Parse(StandInLedger.Account)).Holdings.Count);
            Assert.Null(lease.Problem);
        }
        finally
        {
            factory.Ledger.IsMember = true;
        }
    }

    [Fact]
    public async Task ATenantThePersonLeavesGoesWithoutAReload()
    {
        try
        {
            using var lease = await factory.Services.GetRequiredService<LiveLedgers>().AcquireAsync(Person, TestContext.Current.CancellationToken);
            Assert.NotNull(lease!.View);
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lease.Changed += () => changed.TrySetResult();

            factory.Ledger.IsRevoked = true;   // the tenant's feed refuses them before its list does
            factory.Ledger.Wake();

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(lease.View);
            Assert.Null(lease.Problem);   // leaving a tenant isn't a failure
        }
        finally
        {
            factory.Ledger.IsRevoked = false;
        }
    }

    private static HoldingView Aapl(LedgerView view) =>
        view.Accounts.Single(account => account.AccountId == Guid.Parse(StandInLedger.Account)).Holdings.Single(holding => holding.Symbol == "AAPL");
}

public class LedgerTokenWithoutRequestTests(CredentialedLedgerSiteFactory factory) : IClassFixture<CredentialedLedgerSiteFactory>
{
    /// <summary>
    /// The live ledger asks for the person's token with no request in hand (it outlives the page that started it): the
    /// site's real token acquisition still answers, asking a person whose token it lacks to sign in again.
    /// </summary>
    [Fact]
    public async Task TheTokenIsAskedForWithoutARequest()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "00000000-0000-0000-0000-0000000000ab"), new Claim("tid", "d400d921-89c1-4c47-8d91-16c7c2ed76cb")],
            "Test"));
        Assert.Null(factory.Services.GetRequiredService<IHttpContextAccessor>().HttpContext);

        var exception = await Record.ExceptionAsync(() => factory.Services.GetRequiredService<LiveLedgers>().AcquireAsync(user, TestContext.Current.CancellationToken));

        Assert.True(
            exception is MicrosoftIdentityWebChallengeUserException or Microsoft.Identity.Client.MsalUiRequiredException,
            exception?.ToString());
    }
}
