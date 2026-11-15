using System.Net;
using System.Security.Claims;
using System.Text;
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
        builder.ConfigureServices(services =>
        {
            SignedInSiteFactory.SignIn(services);
            services.AddSingleton<ITokenAcquisition>(new StandInTokens());
            services.AddHttpClient<LedgerReader>().ConfigurePrimaryHttpMessageHandler(() => Ledger);
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

/// <summary>The ledger's answers for a tenant holding the Joe 10 paper account (two holdings and cash).</summary>
public sealed class StandInLedger : HttpMessageHandler
{
    public const string Tenant = "4a6f6531-3020-7061-7065-720000000001";
    private const string Account = "1f6ff65f-711f-8986-a0c0-e238561bf4b6";
    private const string Aapl = "a392f7e0-339b-83d7-b1ff-bf8b60012fb7";
    private const string Msft = "b392f7e0-339b-83d7-b1ff-bf8b60012fb7";
    private const string Dollar = "c392f7e0-339b-83d7-b1ff-bf8b60012fb7";
    private const string Broker = "d392f7e0-339b-83d7-b1ff-bf8b60012fb7";

    public bool IsMember { get; set; } = true;

    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        Requests.Add(path);
        if (request.Headers.Authorization?.ToString() != "Bearer the-persons-token")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        var json = path switch
        {
            "/v1/me/tenants" => IsMember ? $"[\"{Tenant}\"]" : "[]",
            $"/v1/tenants/{Tenant}/symbol" => $"[{{\"nodeId\":\"{Aapl}\",\"scheme\":\"Ticker\",\"code\":\"AAPL\"}},{{\"nodeId\":\"{Msft}\",\"scheme\":\"Ticker\",\"code\":\"MSFT\"}},{{\"nodeId\":\"{Dollar}\",\"scheme\":\"Currency\",\"code\":\"USD\"}}]",
            $"/v1/tenants/{Tenant}/node" => $"[{{\"nodeId\":\"{Account}\",\"displayName\":\"Joe 10\"}}]",
            $"/v1/tenants/{Tenant}/institution" => $"[{{\"institutionId\":\"{Broker}\",\"source\":\"Simulator\"}}]",
            $"/v1/tenants/{Tenant}/account" => $"[{{\"accountId\":\"{Account}\",\"institutionId\":\"{Broker}\"}}]",
            $"/v1/tenants/{Tenant}/holdings" => $$"""
                {"asOf":"2026-11-13T21:35:00Z","holdings":[
                  {"accountId":"{{Account}}","assetId":"{{Aapl}}","assetType":"Equity","quantity":1052,"price":342.10,"marketValue":359889.20,"weight":0.343,"targetWeight":0.36},
                  {"accountId":"{{Account}}","assetId":"{{Msft}}","assetType":"Equity","quantity":1100,"price":510.00,"marketValue":561000.00,"weight":0.535,"targetWeight":0.54},
                  {"accountId":"{{Account}}","assetId":"{{Dollar}}","assetType":"Cash","quantity":127656.12,"price":1,"marketValue":127656.12,"weight":0.122,"targetWeight":0.10}],
                 "accounts":[{"accountId":"{{Account}}","marketValue":1048545.32,"cash":127656.12,"netContributions":1000000,"gain":48545.32,"modelName":"Joe 10"}],
                 "prices":[],"rates":[],"problems":[]}
                """,
            $"/v1/tenants/{Tenant}/statistics?days=true" => $$"""
                {"asOf":"2026-11-13T21:35:00Z","accounts":[{"statistics":{"accountId":"{{Account}}","from":"2026-10-15","through":"2026-11-13","value":1048545.32,"netFlows":1000000,"returns":21,
                  "totalReturn":0.048568,"annualizedReturn":0.766676,"volatility":0.255758,"sharpeRatio":2.187468,"maximumDrawdown":0.04146,"isRanked":true,"gaps":[]},
                  "days":[{"date":"2026-10-15","value":999978.82,"flow":1000000,"return":null},{"date":"2026-10-16","value":990730.22,"flow":0,"return":-0.0092},{"date":"2026-11-13","value":1048545.32,"flow":0,"return":-0.0045}]}]}
                """,
            _ => null,
        };
        return Task.FromResult(json is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
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
    public async Task TheSignedInPageShowsThePersonsPaperAccountFromTheLedger()
    {
        factory.Ledger.IsMember = true;
        var html = await client.GetStringAsync("/accounts", TestContext.Current.CancellationToken);

        Assert.Contains("Joe 10", html);
        Assert.Contains("Paper money", html);
        Assert.Contains("$1,048,545", html);       // worth
        Assert.Contains("4.86%", html);            // total return
        Assert.Contains("25.58%", html);           // volatility
        Assert.Contains("2.19", html);             // Sharpe ratio
        Assert.Contains("worst drop 4.15%", html);
        Assert.Contains("AAPL", html);
        Assert.Contains("$359,889.20", html);
        Assert.Contains("USD cash", html);
        Assert.Contains("<polyline", html);        // the value at each close
        Assert.Contains("Updated November 13, 4:35 PM New York time.", html);
    }

    [Fact]
    public async Task APersonWithNoTenantSeesThePageAsBefore()
    {
        factory.Ledger.IsMember = false;
        var html = await client.GetStringAsync("/accounts", TestContext.Current.CancellationToken);

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
        var reader = scope.ServiceProvider.GetRequiredService<LedgerReader>();

        var exception = await Record.ExceptionAsync(() => reader.ReadAsync(context.User, TestContext.Current.CancellationToken));

        Assert.True(
            exception is MicrosoftIdentityWebChallengeUserException or Microsoft.Identity.Client.MsalUiRequiredException,
            exception?.ToString());
    }
}
