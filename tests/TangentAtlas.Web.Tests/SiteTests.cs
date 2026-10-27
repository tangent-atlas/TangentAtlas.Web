using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using TangentAtlas.Web.Identity;

namespace TangentAtlas.Web.Tests;

public class PublicPagesTests(UnconfiguredSiteFactory factory) : IClassFixture<UnconfiguredSiteFactory>
{
    private readonly HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task TheLandingPageCarriesTheHeadlineAndSignUp()
    {
        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("A thermostat for <em>all</em> your money.", html);
        Assert.Contains("Set the temperature, and Tangent Atlas keeps your money from running too hot or too cold.", html);
        Assert.Contains("Has your best pick become your biggest bet?", html);
        Assert.Contains("respond with discipline, not panic", html);
        Assert.Contains("Why buy the fund when you can own the index?", html);
        Assert.DoesNotContain("Dividends piling up?", html);
        Assert.Contains("Checking gets topped off first.", html);
        Assert.Equal(4, html.Split("class=\"ta-rule-graphic\"").Length - 1);   // every rule has the same size and layout
        Assert.Contains("href=\"/signup\"", html);
        Assert.Contains("href=\"/account/signin\"", html);
        Assert.DoesNotContain("founding", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("read-only", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("S&amp;P", html);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/signup")]
    [InlineData("/robots.txt")]
    public async Task NothingIsIndexed(string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("noindex", string.Join(",", response.Headers.GetValues("X-Robots-Tag")));
    }

    [Fact]
    public async Task RobotsAreTurnedAway()
    {
        var robots = await client.GetStringAsync("/robots.txt", TestContext.Current.CancellationToken);

        Assert.Contains("Disallow: /", robots);
    }

    [Theory]
    [InlineData("/your-net-worth", "Your net worth", "Every account, every rule, one screen.")]
    [InlineData("/have-a-plan", "Have a plan", "Why do you own what you own?")]
    [InlineData("/when-the-storm-hits", "When the storm hits", "Turn it down. Don’t switch it off.")]
    [InlineData("/you-hold-the-keys", "You hold the keys", "A new company should have to earn your trust.")]
    [InlineData("/how-it-works", "How it works", "Link your accounts. We never see your passwords.")]
    public async Task EachMenuItemOpensAPageHeadedByItsName(string path, string headline, string content)
    {
        var html = await client.GetStringAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains($"<h1 class=\"ta-page-headline\">{headline}</h1>", html);
        Assert.Contains(content, html);
        Assert.Contains("class=\"ta-nav-link active\"", html);   // the menu marks the page you are on
    }

    [Theory]
    [InlineData("/", "Keep your bank, your broker and your stocks.")]
    [InlineData("/your-net-worth", "Finally, the whole picture.")]
    [InlineData("/have-a-plan", "Your index. Your shares. Your plan.")]
    [InlineData("/when-the-storm-hits", "Respond with discipline, not panic.")]
    [InlineData("/you-hold-the-keys", "Your house, your rules.")]
    public async Task EachPitchPageEndsWithItsLineAndSignUp(string path, string closing)
    {
        var html = await client.GetStringAsync(path, TestContext.Current.CancellationToken);

        var close = html.IndexOf($"<p class=\"ta-page-close\">{closing}</p>", StringComparison.Ordinal);
        Assert.True(close > 0);
        Assert.Contains("href=\"/signup\" class=\"ta-btn ta-btn-primary ta-btn-large\"", html[close..]);
    }

    [Fact]
    public async Task ThePlanPagesFiguresAreLabelledAsBacktested()
    {
        var html = await client.GetStringAsync("/have-a-plan", TestContext.Current.CancellationToken);

        Assert.Contains("The same return. Less risk. More cash.", html);
        Assert.Contains("These are backtested results", html);
        Assert.True(html.IndexOf("These are backtested results", StringComparison.Ordinal) > html.IndexOf("The same return.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheNetWorthPageNamesRealInstitutionsAndShowsTheFigures()
    {
        var html = await client.GetStringAsync("/your-net-worth", TestContext.Current.CancellationToken);

        foreach (var name in new[] { "Bank of America", "Schwab", "American Express", "Discover", "Fidelity" })
        {
            Assert.Contains(name, html);
        }
        foreach (var figure in new[] { "12-month return", "Volatility", "Sharpe ratio" })
        {
            Assert.Contains($"<div class=\"ta-net-label\">{figure}</div>", html);   // on the same line as what you own and owe
        }
        Assert.DoesNotContain("Your card issuer", html);
        Assert.Contains("7 accounts, 6 rules, 2 warnings, 0 errors", html);
        Assert.Equal(2, html.Split("class=\"ta-warning\"").Length - 1);   // checking and brokerage carry the warnings
        Assert.DoesNotContain("An example.", html);
    }

    [Fact]
    public async Task TheStormPageIsAboutRaisingCash()
    {
        var html = await client.GetStringAsync("/when-the-storm-hits", TestContext.Current.CancellationToken);

        Assert.Contains("Sooner or later, everyone needs cash", html);
        Assert.Contains("Don’t change your plan. Adjust it.", html);
    }

    [Fact]
    public async Task TheKeysPageGivesFourProofs()
    {
        var html = await client.GetStringAsync("/you-hold-the-keys", TestContext.Current.CancellationToken);

        foreach (var proof in new[] { "Your money stays in your name", "We never see your passwords", "You decide how far it goes", "Read the code" })
        {
            Assert.Contains($"<div class=\"ta-trust-title\">{proof}</div>", html);
        }
    }

    [Fact]
    public async Task TheMenuReachesEveryPage()
    {
        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        foreach (var page in new[] { "your-net-worth", "have-a-plan", "when-the-storm-hits", "you-hold-the-keys", "how-it-works" })
        {
            Assert.Contains($"href=\"{page}\"", html);
        }
        Assert.DoesNotContain("NVDA", html);
    }

    [Theory]
    [InlineData("/built-in-the-open")]
    [InlineData("/under-one-roof")]
    [InlineData("/all-under-one-roof")]
    [InlineData("/your-money")]
    public async Task EachPageHasOnlyTheAddressOfItsMenuLabel(string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/when-the-storm-hits")]
    [InlineData("/signup")]
    public async Task EveryPageLinksToThePrivacyStatement(string path)
    {
        var html = await client.GetStringAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/privacy\"", html);
    }

    [Fact]
    public async Task ThePrivacyStatementSaysWhatWeCollect()
    {
        var html = await client.GetStringAsync("/privacy", TestContext.Current.CancellationToken);

        Assert.Contains("<h1 class=\"ta-page-headline\">Privacy</h1>", html);
        Assert.Contains("What we collect", html);
        Assert.Contains("we never see it", html);
    }

    [Fact]
    public async Task TheSignUpPageRequiresTheBetaNoticeToBeAcknowledged()
    {
        var html = await client.GetStringAsync("/signup", TestContext.Current.CancellationToken);

        Assert.Contains("It's a beta.", html);
        Assert.Contains("name=\"understood\" value=\"yes\" required", html);
        Assert.Contains("action=\"/account/signup\"", html);
    }

    [Fact]
    public async Task SignUpWithoutTheAcknowledgementGoesBackToTheNotice()
    {
        var response = await client.GetAsync("/account/signup", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signup", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/accounts")]
    [InlineData("/account/signin")]
    [InlineData("/account/signup?understood=yes")]
    public async Task WithoutAnIdentityProviderNobodyGetsIn(string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith("/account/unavailable", location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString);
    }

    [Fact]
    public async Task TheUnavailablePageSays503()
    {
        var response = await client.GetAsync("/account/unavailable", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

public class SignInTests(ConfiguredSiteFactory factory) : IClassFixture<ConfiguredSiteFactory>
{
    private readonly HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task TheSignedInPageSendsAStrangerToTheExternalTenant()
    {
        var response = await client.GetAsync("/accounts", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(ConfiguredSiteFactory.AuthorizationEndpoint, location);
        Assert.Contains($"client_id={ConfiguredSiteFactory.ClientId}", location);
        Assert.Contains("response_type=id_token", location);
        Assert.Contains("response_mode=form_post", location);
        Assert.DoesNotContain("prompt=create", location);
    }

    [Fact]
    public async Task SignUpOpensTheUserFlowOnItsSignUpPage()
    {
        var response = await client.GetAsync("/account/signup?understood=yes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(ConfiguredSiteFactory.AuthorizationEndpoint, location);
        Assert.Contains("prompt=create", location);
    }

    [Fact]
    public async Task SignInGoesToTheExternalTenant()
    {
        var response = await client.GetAsync("/account/signin", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(ConfiguredSiteFactory.AuthorizationEndpoint, response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task SignOutRefusesARequestWithoutTheFormToken()
    {
        var response = await client.PostAsync("/account/signout", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public class SignedInTests(SignedInSiteFactory factory) : IClassFixture<SignedInSiteFactory>
{
    private readonly HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task TheSignedInPageGreetsThePersonAndSaysWhereTheBetaStands()
    {
        var html = await client.GetStringAsync("/accounts", TestContext.Current.CancellationToken);

        Assert.Contains("Welcome, pat@example.com", html);   // Entra's placeholder name "unknown" is not shown
        Assert.Contains("Linking accounts is the next step to open.", html);
        Assert.Contains("action=\"/account/signout\"", html);
    }

    [Fact]
    public async Task TheLandingPageOffersTheSignedInPageInsteadOfSignUp()
    {
        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("My accounts", html);
        Assert.DoesNotContain("href=\"/signup\"", html);
    }
}

public class AccountRulesTests
{
    [Theory]
    [InlineData(null, "/accounts")]
    [InlineData("", "/accounts")]
    [InlineData("/accounts", "/accounts")]
    [InlineData("https://evil.example/", "/accounts")]
    [InlineData("//evil.example/", "/accounts")]
    [InlineData("/\\evil.example/", "/accounts")]
    public void SignInReturnsOnlyToThisSite(string? returnUrl, string expected) =>
        Assert.Equal(expected, AccountEndpoints.LocalOrHome(returnUrl));

    [Fact]
    public void APersonWithoutAnObjectIdIsNobody() =>
        Assert.Null(SignedInPerson.From(new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "pat@example.com")], "test"))));

    [Fact]
    public void ARealNameIsShown()
    {
        var person = SignedInPerson.From(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "x"), new Claim("name", "Pat Lee"), new Claim("email", "pat@example.com")], "test")));

        Assert.Equal("Pat Lee", person?.DisplayName);
    }
}
