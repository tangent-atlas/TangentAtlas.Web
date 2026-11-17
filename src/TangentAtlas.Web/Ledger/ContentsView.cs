namespace TangentAtlas.Web.Ledger;

/// <summary>How an account's contents are shown, as in File Explorer: tiles, or a line per holding.</summary>
public enum ContentsView
{
    Tiles,
    Details,
}

/// <summary>The person's choice of view while the site is open: one per browser tab, shared by every page they move to.</summary>
public sealed class ContentsViewChoice
{
    public ContentsView View { get; set; }
}

/// <summary>
/// Remembers the person's choice of view in a cookie (this browser only; nothing personal), so every account opens the
/// way they last looked at one.  On a live page the choice changes in place and the page posts it to /contents-view/{view}
/// in the background; before the page is live, the same address is a plain link (?returnUrl=…) that comes back to the page.
/// </summary>
public static class ContentsViews
{
    public const string Cookie = "TangentAtlas.ContentsView";

    public static ContentsView From(HttpContext? context) =>
        context?.Request.Cookies[Cookie] is { } value && Enum.TryParse<ContentsView>(value, ignoreCase: true, out var view) ? view : ContentsView.Tiles;

    public static string Link(ContentsView view, string returnUrl) =>
        $"/contents-view/{view.ToString().ToLowerInvariant()}?returnUrl={Uri.EscapeDataString(returnUrl)}";

    public static IEndpointRouteBuilder MapContentsView(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/contents-view/{view}", (string view, string? returnUrl, HttpContext context) =>
        {
            Remember(context, view);

            // Back to a page of this site only.
            return Results.LocalRedirect(returnUrl is { Length: > 1 } local && local[0] == '/' && local[1] is not ('/' or '\\') ? local : AccountEndpointsHome);
        });

        // From a live page, which has already changed the view in place.
        endpoints.MapPost("/contents-view/{view}", (string view, HttpContext context) =>
            Remember(context, view) ? Results.NoContent() : Results.BadRequest());
        return endpoints;
    }

    private static bool Remember(HttpContext context, string view)
    {
        if (!Enum.TryParse<ContentsView>(view, ignoreCase: true, out var chosen) || !Enum.IsDefined(chosen))
        {
            return false;
        }

        context.Response.Cookies.Append(Cookie, chosen.ToString(), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(365),
            IsEssential = true,
        });
        return true;
    }

    private const string AccountEndpointsHome = Identity.AccountEndpoints.SignedInHome;
}
