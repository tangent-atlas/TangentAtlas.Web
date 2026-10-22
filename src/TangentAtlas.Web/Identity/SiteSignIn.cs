using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

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
/// The website only identifies people; it asks for no access tokens and holds no secret (the ID token comes back by form
/// post).  Without the identity provider's settings nobody can sign in and every signed-in page is refused: it fails closed,
/// while the public pages keep working.
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
        services.AddSingleton(provider);

        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = provider.IsConfigured
                ? OpenIdConnectDefaults.AuthenticationScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
        });

        authentication.AddCookie(options =>
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
        });

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
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;         // keep oid, name and email as Entra names them
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.TokenValidationParameters.NameClaimType = "name";
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";

                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    if (context.Properties.Items.TryGetValue(SignUpProperty, out var signUp) && signUp == "true")
                    {
                        context.ProtocolMessage.Prompt = "create";   // open the user flow on its sign-up page
                    }
                    return Task.CompletedTask;
                };
                options.Events.OnTokenValidated = context =>
                {
                    if (string.IsNullOrEmpty(context.Principal?.FindFirst(ObjectIdClaim)?.Value))
                    {
                        context.Fail("The ID token has no oid claim.");
                    }
                    return Task.CompletedTask;
                };
            });
        }

        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        return services;
    }
}
