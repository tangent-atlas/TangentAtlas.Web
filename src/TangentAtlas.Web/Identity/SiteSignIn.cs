using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using TangentAtlas.Web.Ledger;

namespace TangentAtlas.Web.Identity;

/// <summary>
/// The external tenant people sign in to (Entra External ID).  Identifiers only: TangentAtlas.Platform's
/// Deploy-EntraApplications.ps1 sets them as the web app's settings (IdentityProvider__Authority, IdentityProvider__ClientId)
/// and, on a developer's computer, writes them to %APPDATA%\TangentAtlas\website.json.
/// </summary>
public sealed class IdentityProviderOptions
{
    public const string SectionName = "IdentityProvider";

    /// <summary>The tenant's issuer, https://&lt;tenant ID&gt;.ciamlogin.com/&lt;tenant ID&gt;/v2.0.</summary>
    public string? Authority { get; set; }

    /// <summary>The client ID of the 'Tangent Atlas Website' registration.</summary>
    public string? ClientId { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>
/// Sign-in for the website: OpenID Connect against the external tenant's sign-up-and-sign-in user flow, kept in a cookie.
/// Without the ledger's settings the website only identifies people: it asks for no access tokens and holds no credential
/// (the ID token comes back by form post).  With them (<see cref="LedgerOptions"/>), it also reads the ledger as the
/// person: the sign-in returns a code, redeemed with the site's credential (a certificate whose key never leaves the
/// computer's store) for the person's access token, Ledger.Read only.  Without the identity provider's settings nobody can
/// sign in and every signed-in page is refused: it fails closed, while the public pages keep working.
/// </summary>
public static class SiteSignIn
{
    /// <summary>The authentication property that turns a challenge into sign-up (prompt=create).</summary>
    public const string SignUpProperty = "tangentatlas.signup";

    /// <summary>The claim every signed-in person must carry: their object ID in the external tenant (bind on oid, never sub).</summary>
    public const string ObjectIdClaim = "oid";

    /// <summary>Where Deploy-EntraApplications.ps1 writes the identifiers on a developer's computer.</summary>
    public static string DeveloperSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TangentAtlas", "website.json");

    public static IServiceCollection AddSiteSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration.GetSection(IdentityProviderOptions.SectionName).Get<IdentityProviderOptions>() ?? new();
        var ledger = configuration.GetSection(LedgerOptions.SectionName).Get<LedgerOptions>() ?? new();
        services.AddSingleton(provider);
        services.AddSingleton(ledger);

        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = provider.IsConfigured
                ? OpenIdConnectDefaults.AuthenticationScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
        });

        if (provider.IsConfigured && ledger.IsConfigured)
        {
            // With the ledger: the sign-in returns a code, which the site redeems with its credential for the person's
            // access token to the ledger (delegated Ledger.Read) and keeps in its token cache (in memory: after a restart
            // the person signs in again, silently while their session at the identity provider lasts).
            authentication.AddMicrosoftIdentityWebApp(
                    identity =>
                    {
                        // Microsoft.Identity.Web takes the authority as its instance and tenant: from
                        // https://<tenant>.ciamlogin.com/<tenant>/v2.0, https://<tenant>.ciamlogin.com/ and <tenant>.
                        var authority = new Uri(provider.Authority!);
                        identity.Instance = authority.GetLeftPart(UriPartial.Authority) + "/";
                        identity.TenantId = authority.Segments.Length > 1 ? authority.Segments[1].TrimEnd('/') : null;
                        identity.ClientId = provider.ClientId;
                        identity.ClientCredentials = [ledger.Credential()];
                    },
                    ConfigureCookie,
                    OpenIdConnectDefaults.AuthenticationScheme,
                    CookieAuthenticationDefaults.AuthenticationScheme)
                .EnableTokenAcquisitionToCallDownstreamApi([.. ledger.Scopes])
                .AddInMemoryTokenCaches();
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                ConfigureOpenIdConnect(options);
                options.Scope.Add("email");
            });
            // The signed-in pages' live ledgers.  A sync request waits up to 30 seconds for a change (a long poll), so the
            // client waits longer than that before it calls the ledger unreachable.
            services.AddHttpClient(LiveLedgers.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(ledger.Server!);
                client.Timeout = TimeSpan.FromSeconds(75);
            });
            services.AddSingleton<LiveLedgers>();
        }
        else
        {
            authentication.AddCookie(ConfigureCookie);
            if (provider.IsConfigured)
            {
                authentication.AddOpenIdConnect(options =>
                {
                    options.Authority = provider.Authority;
                    options.ClientId = provider.ClientId;
                    options.ResponseType = OpenIdConnectResponseType.IdToken;
                    options.ResponseMode = OpenIdConnectResponseMode.FormPost;
                    options.UsePkce = false;                  // no authorization code is redeemed
                    options.SaveTokens = false;
                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.Scope.Add("email");
                    ConfigureOpenIdConnect(options);
                });
            }
        }

        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "TangentAtlas.SignIn";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        // Reached only when sign-in is not configured: the challenge falls back to the cookie scheme.
        options.LoginPath = "/account/unavailable";
        options.AccessDeniedPath = "/account/unavailable";
    }

    /// <summary>What both ways of signing in share: the callbacks, the claims as Entra names them, sign-up, and oid.</summary>
    private static void ConfigureOpenIdConnect(OpenIdConnectOptions options)
    {
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;         // keep oid, name and email as Entra names them
        if (options.TokenHandler is JsonWebTokenHandler handler)
        {
            handler.MapInboundClaims = false;     // Microsoft.Identity.Web puts in a handler of its own, which maps them
        }
        options.TokenValidationParameters.NameClaimType = "name";
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";

        var redirect = options.Events.OnRedirectToIdentityProvider;
        options.Events.OnRedirectToIdentityProvider = async context =>
        {
            if (context.Properties.Items.TryGetValue(SignUpProperty, out var signUp) && signUp == "true")
            {
                context.ProtocolMessage.Prompt = "create";   // open the user flow on its sign-up page
            }
            await redirect(context);
        };
        var validated = options.Events.OnTokenValidated;
        options.Events.OnTokenValidated = async context =>
        {
            await validated(context);
            if (string.IsNullOrEmpty(context.Principal?.FindFirst(ObjectIdClaim)?.Value))
            {
                context.Fail("The ID token has no oid claim.");
            }
        };
    }
}
