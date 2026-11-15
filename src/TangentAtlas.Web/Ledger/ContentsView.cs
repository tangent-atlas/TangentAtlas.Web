namespace TangentAtlas.Web.Ledger;

/// <summary>How an account's contents are shown, as in File Explorer: tiles, or a line per holding.</summary>
public enum ContentsView
{
    Tiles,
    Details,
}

/// <summary>
/// Remembers the person's choice of view in a cookie (this browser only; nothing personal), so every account opens the
/// way they last looked at one.  Choosing is a plain link: /contents-view/{view}?returnUrl=…, then back to the page.
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
            if (Enum.TryParse<ContentsView>(view, ignoreCase: true, out var chosen))
            {
                context.Response.Cookies.Append(Cookie, chosen.ToString(), new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromDays(365),
                    IsEssential = true,
                });
            }

            // Back to a page of this site only.
            return Results.LocalRedirect(returnUrl is { Length: > 1 } local && local[0] == '/' && local[1] is not ('/' or '\\') ? local : AccountEndpointsHome);
        });
        return endpoints;
    }

    private const string AccountEndpointsHome = Identity.AccountEndpoints.SignedInHome;
}
