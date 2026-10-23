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

        Assert.Contains("A thermostat for <em>all your money.</em>", html);
        Assert.Contains("You set it; Tangent Atlas keeps it there.", html);
        Assert.Contains("Has your best pick become your biggest bet?", html);
        Assert.Contains("respond with discipline, not panic", html);
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
    [InlineData("/how-it-works", "How it works", "Link your accounts. We never see your passwords.")]
    [InlineData("/your-money", "Your money, one place", "Every account, every rule, one screen.")]
    [InlineData("/when-the-storm-hits", "When the storm hits", "You decided in March. In October, your rules respond.")]
    [InlineData("/built-in-the-open", "Built in the open", "Read the code")]
    public async Task EachMenuItemOpensAPageHeadedByItsName(string path, string headline, string content)
    {
        var html = await client.GetStringAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains($"<h1 class=\"ta-page-headline\">{headline}</h1>", html);
        Assert.Contains(content, html);
        Assert.Contains("class=\"ta-nav-link active\"", html);   // the menu marks the page you are on
    }

    [Fact]
    public async Task TheMenuReachesEveryPage()
    {
        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        foreach (var page in new[] { "how-it-works", "your-money", "when-the-storm-hits", "built-in-the-open" })
        {
            Assert.Contains($"href=\"{page}\"", html);
        }
        Assert.DoesNotContain("NVDA", html);
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
