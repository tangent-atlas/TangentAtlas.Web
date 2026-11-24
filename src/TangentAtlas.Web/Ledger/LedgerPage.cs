using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Web;
using TangentAtlas.Web.Identity;

namespace TangentAtlas.Web.Ledger;

/// <summary>
/// A signed-in page, or the live part of one, that shows the person's accounts: it leases their live ledger, shows its
/// view, and (when it runs interactively) re-renders each time the ledger syncs a change.  It signs the person in again
/// once if the site has lost their token (after a restart), and says so if the ledger can't be read.  Without the ledger's
/// settings it shows only what every signed-in page shows.
/// </summary>
public abstract class LedgerPage : ComponentBase, IDisposable
{
    protected static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    protected static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private LiveLedgerLease? lease;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    [SupplyParameterFromQuery(Name = "signedIn")] private string? SignedIn { get; set; }

    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Inject] protected NavigationManager Navigation { get; set; } = default!;

    protected SignedInPerson? Person { get; private set; }

    /// <summary>The person's tenants as the ledger shows them; null without a ledger, or if they belong to none.</summary>
    protected LedgerView? Ledger { get; private set; }

    /// <summary>Why the ledger couldn't be read, or stopped updating, if it did.</summary>
    protected string? LedgerProblem { get; private set; }

    protected IReadOnlyList<AccountView> LedgerAccounts => Ledger?.Accounts ?? [];

    /// <summary>The page's hold on the person's live ledger, for writing to it; null without one.</summary>
    protected LiveLedgerLease? Lease => lease;

    protected override async Task OnInitializedAsync()
    {
        if (AuthenticationState is null)
        {
            return;
        }

        var user = (await AuthenticationState).User;
        Person = SignedInPerson.From(user);
        if (Services.GetService(typeof(LiveLedgers)) is not LiveLedgers ledgers)
        {
            return;   // the site isn't connected to a ledger
        }

        try
        {
            // At once when the person's ledger is already live (another page of theirs, or this page's prerendering).
            lease = await ledgers.AcquireAsync(user, CancellationToken.None);
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
            return;
        }
        catch (LedgerUnavailableException exception)
        {
            LedgerProblem = exception.Message;
            return;
        }

        if (lease is null)
        {
            return;
        }

        Ledger = lease.View;
        LedgerProblem = lease.Problem;
        if (RendererInfo.IsInteractive)
        {
            lease.Changed += OnLedgerChanged;
        }
    }

    public void Dispose()
    {
        OnDisposing();
        if (lease is not null)
        {
            lease.Changed -= OnLedgerChanged;
            lease.Dispose();
            lease = null;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>What a page lets go of when it closes, besides the ledger.</summary>
    protected virtual void OnDisposing()
    {
    }

    private void OnLedgerChanged() => _ = InvokeAsync(() =>
    {
        if (lease is { } current)
        {
            Ledger = current.View;
            LedgerProblem = current.Problem;
            StateHasChanged();
        }
    });

    protected static string Money(decimal? value, int places = 0) => value is { } number ? number.ToString(places == 0 ? "C0" : "C2", English) : "—";

    protected static string Percent(decimal? value) => value is { } number ? number.ToString("P2", English) : "—";

    protected static string Number(decimal? value) => value is { } number ? number.ToString("0.00", English) : "—";

    /// <summary>When the latest price the values use is from.</summary>
    protected string Updated => Ledger?.AsOf is { } asOf ? $"Updated {TimeZoneInfo.ConvertTime(asOf, NewYork).ToString("MMMM d, h:mm tt", English)} New York time." : "";
}
