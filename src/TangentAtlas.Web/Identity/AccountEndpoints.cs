using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace TangentAtlas.Web.Identity;

/// <summary>
/// Sign-in, sign-up and sign-out.  Each hands the person to the external tenant's user flow and back; the pages
/// themselves never see a password.
/// </summary>
public static class AccountEndpoints
{
    /// <summary>Where a person lands after signing in or signing up.</summary>
    public const string SignedInHome = "/accounts";

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var account = endpoints.MapGroup("/account");

        account.MapGet("/signin", (string? returnUrl, IdentityProviderOptions provider) =>
            provider.IsConfigured
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = LocalOrHome(returnUrl) },
                    [OpenIdConnectDefaults.AuthenticationScheme])
                : Results.LocalRedirect("/account/unavailable"));

        // Reached from the sign-up page's form, which requires the person to tick that they have read the beta notice.
        account.MapGet("/signup", (string? understood, IdentityProviderOptions provider) =>
        {
            if (understood != "yes")
            {
                return Results.LocalRedirect("/signup");
            }
            if (!provider.IsConfigured)
            {
                return Results.LocalRedirect("/account/unavailable");
            }
            var properties = new AuthenticationProperties { RedirectUri = SignedInHome };
            properties.Items[SiteSignIn.SignUpProperty] = "true";
            return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
        });

        account.MapPost("/signout", async (HttpContext context, IAntiforgery antiforgery, IdentityProviderOptions provider) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }
            var properties = new AuthenticationProperties { RedirectUri = "/" };
            return provider.IsConfigured
                ? Results.SignOut(properties,
                    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme])
                : Results.SignOut(properties, [CookieAuthenticationDefaults.AuthenticationScheme]);
        });

        return endpoints;
    }

    /// <summary>A return address on this site, or the signed-in home: never another site's address.</summary>
    public static string LocalOrHome(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : SignedInHome;
}
