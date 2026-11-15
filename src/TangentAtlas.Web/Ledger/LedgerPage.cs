using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Web;
using TangentAtlas.Web.Identity;

namespace TangentAtlas.Web.Ledger;

/// <summary>
/// A signed-in page that shows the person's accounts from the ledger: reads them as the person, signs them in again once
/// if the site has lost their token (after a restart), and says so if the ledger can't be read.  Without the ledger's
/// settings it shows only what every signed-in page shows.
/// </summary>
public abstract class LedgerPage : ComponentBase
{
    protected static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    protected static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    [SupplyParameterFromQuery(Name = "signedIn")] private string? SignedIn { get; set; }

    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Inject] protected NavigationManager Navigation { get; set; } = default!;

    protected SignedInPerson? Person { get; private set; }

    /// <summary>The person's tenant as the ledger shows it; null without a ledger, or if they belong to none.</summary>
    protected LedgerView? Ledger { get; private set; }

    /// <summary>Why the ledger couldn't be read, if it couldn't.</summary>
    protected string? LedgerProblem { get; private set; }

    protected IReadOnlyList<AccountView> LedgerAccounts => Ledger?.Accounts ?? [];

    protected override async Task OnInitializedAsync()
    {
        if (AuthenticationState is null)
        {
            return;
        }

        var user = (await AuthenticationState).User;
        Person = SignedInPerson.From(user);
        if (Services.GetService(typeof(LedgerReader)) is not LedgerReader reader)
        {
            return;   // the site isn't connected to a ledger
        }

        try
        {
            Ledger = await reader.ReadAsync(user, CancellationToken.None);
        }
        catch (Exception exception) when (exception is MicrosoftIdentityWebChallengeUserException or Microsoft.Identity.Client.MsalUiRequiredException)
        {
            // The site no longer holds the person's ledger token (it restarted): sign in again, silently while their session
            // at the identity provider lasts.  Once only, so a lasting failure is shown rather than looping.
            if (SignedIn is null)
            {
                var path = "/" + Navigation.ToBaseRelativePath(Navigation.Uri).Split('?')[0];
                Navigation.NavigateTo($"/account/signin?returnUrl={Uri.EscapeDataString(path + "?signedIn=1")}", forceLoad: true);
                return;
            }

            LedgerProblem = "Your sign-in couldn't be used to read your accounts. Sign out and in again.";
        }
        catch (LedgerUnavailableException exception)
        {
            LedgerProblem = exception.Message;
        }
    }

    protected static string Money(decimal? value, int places = 0) => value is { } number ? number.ToString(places == 0 ? "C0" : "C2", English) : "—";

    protected static string Percent(decimal? value) => value is { } number ? number.ToString("P2", English) : "—";

    protected static string Number(decimal? value) => value is { } number ? number.ToString("0.00", English) : "—";

    protected string Updated => Ledger?.AsOf is { } asOf ? $"Updated {TimeZoneInfo.ConvertTime(asOf, NewYork).ToString("MMMM d, h:mm tt", English)} New York time." : "";

    /// <summary>An account's value at each close, as a line scaled to the chart.</summary>
    protected static (string Points, string Label)? Chart(IReadOnlyList<DayView> days)
    {
        var valued = days.Where(day => day.Value is not null).ToArray();
        if (valued.Length < 2)
        {
            return null;
        }

        var low = valued.Min(day => day.Value!.Value);
        var high = valued.Max(day => day.Value!.Value);
        var span = high == low ? 1m : high - low;
        var points = string.Join(' ', valued.Select((day, index) =>
            string.Create(CultureInfo.InvariantCulture, $"{index * 600m / (valued.Length - 1):0.#},{110m - ((day.Value!.Value - low) / span * 100m):0.#}")));
        var label = $"Value at each close, {valued[0].Date.ToString("MMMM d", English)} to {valued[^1].Date.ToString("MMMM d", English)}: from {Money(valued[0].Value)} to {Money(valued[^1].Value)}.";
        return (points, label);
    }
}
