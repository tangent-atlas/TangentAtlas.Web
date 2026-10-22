using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace TangentAtlas.Web.Tests;

/// <summary>The site with no identity provider: nobody can sign in.</summary>
public class UnconfiguredSiteFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("IdentityProvider:Authority", "");
        builder.UseSetting("IdentityProvider:ClientId", "");
    }
}

/// <summary>
/// The site with an identity provider whose metadata is supplied here, so a challenge redirects without the network.
/// </summary>
public class ConfiguredSiteFactory : WebApplicationFactory<Program>
{
    public const string Authority = "https://11111111-2222-3333-4444-555555555555.ciamlogin.com/11111111-2222-3333-4444-555555555555/v2.0";
    public const string AuthorizationEndpoint = "https://11111111-2222-3333-4444-555555555555.ciamlogin.com/11111111-2222-3333-4444-555555555555/oauth2/v2.0/authorize";
    public const string ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("IdentityProvider:Authority", Authority);
        builder.UseSetting("IdentityProvider:ClientId", ClientId);
        builder.ConfigureServices(services =>
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Authority,
                    AuthorizationEndpoint = AuthorizationEndpoint,
                };
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
            }));
    }
}

/// <summary>The site with a person already signed in, through a test scheme that stands in for the cookie.</summary>
public class SignedInSiteFactory : ConfiguredSiteFactory
{
    public static readonly Claim[] Claims =
    [
        new("oid", "00000000-0000-0000-0000-000000000001"),
        new("name", "unknown"),
        new("email", "pat@example.com"),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SignedInHandler>(SignedInHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = SignedInHandler.SchemeName;
                options.DefaultAuthenticateScheme = SignedInHandler.SchemeName;
            });
        });
    }

    private sealed class SignedInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "SignedIn";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(Claims, SchemeName, "name", "roles")), SchemeName)));
    }
}
