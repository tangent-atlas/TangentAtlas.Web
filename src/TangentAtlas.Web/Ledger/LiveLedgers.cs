using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AtlasLedger.Model;
using AtlasLedger.Model.Replica;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using TangentAtlas.Web.Identity;

namespace TangentAtlas.Web.Ledger;

/// <summary>
/// Keeps each signed-in person's tenants live for the pages that show them.  One Replica per person, shared by every page
/// they have open and kept a few minutes after the last one closes (<see cref="LedgerOptions.KeepAlive"/>), so moving
/// between pages, or a page reconnecting, starts from what is already here.  The Replica is filled and kept current by
/// the ledger's generated sync client, one per tenant: a snapshot, then long polls for the rows changed since, each
/// request made with the person's own token (delegated Ledger.Read).  The ledger decides what they may see; the website
/// only asks for the tables its pages read.
/// </summary>
public sealed class LiveLedgers(
    IHttpClientFactory httpClients,
    IServiceScopeFactory scopes,
    LedgerOptions options,
    ILogger<LiveLedgers> logger) : IAsyncDisposable
{
    /// <summary>The name of the HTTP client that reaches the ledger.</summary>
    public const string HttpClientName = "ledger";

    /// <summary>The tables the signed-in pages read; the sync client adds their parents (accounts, assets, nodes, models).</summary>
    public static readonly IReadOnlyList<string> Tables =
        ["Position", "EffectivePrice", "CurrencyRate", "AccountFlow", "AccountDay", "AccountStatistic", "Weight", "Symbol", "Institution"];

    /// <summary>The ledger's JSON: web names, enums as their names.</summary>
    internal static readonly JsonSerializerOptions Json = CreateJson();

    private readonly Lock gate = new();
    private readonly Dictionary<string, LiveLedger> ledgers = [];

    /// <summary>
    /// Leases the person's live ledger, starting it if it isn't running: returns once it holds a snapshot of every one of
    /// their tenants (at once, if it already does).  Dispose the lease when the page closes.
    /// </summary>
    /// <returns>The lease; null if the sign-in names no person.</returns>
    /// <exception cref="MicrosoftIdentityWebChallengeUserException">The person must sign in again (the site lost their token).</exception>
    /// <exception cref="LedgerUnavailableException">The ledger couldn't be read.</exception>
    public async Task<LiveLedgerLease?> AcquireAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (KeyOf(user) is not { } key)
        {
            return null;
        }

        LiveLedger ledger;
        lock (gate)
        {
            if (!ledgers.TryGetValue(key, out var found) || found.IsStopped)
            {
                found = new LiveLedger(key, user, options.Scope!, httpClients.CreateClient(HttpClientName), scopes.CreateScope(), logger, Fail);
                ledgers[key] = found;
            }

            ledger = found;
            ledger.Leases++;
            ledger.Generation++;   // cancels a pending stop
        }

        try
        {
            await ledger.Ready.WaitAsync(cancellationToken);
        }
        catch
        {
            Release(ledger);
            throw;
        }

        return new LiveLedgerLease(ledger, Release);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            foreach (var ledger in ledgers.Values)
            {
                ledger.Stop();
            }

            ledgers.Clear();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>One person's key: their object ID (and directory, when the sign-in names it).</summary>
    private static string? KeyOf(ClaimsPrincipal user) =>
        user.FindFirst(SiteSignIn.ObjectIdClaim)?.Value is { Length: > 0 } objectId
            ? $"{user.FindFirst("tid")?.Value}/{objectId}"
            : null;

    private static JsonSerializerOptions CreateJson()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter());
        return json;
    }

    private void Release(LiveLedger ledger)
    {
        int generation;
        lock (gate)
        {
            if (--ledger.Leases > 0)
            {
                return;
            }

            generation = ++ledger.Generation;
            if (options.KeepAlive <= TimeSpan.Zero || !ledger.Ready.IsCompletedSuccessfully || ledger.Problem is not null)
            {
                Remove(ledger);
                return;
            }
        }

        _ = StopLaterAsync(ledger, generation);
    }

    private async Task StopLaterAsync(LiveLedger ledger, int generation)
    {
        await Task.Delay(options.KeepAlive).ConfigureAwait(false);
        lock (gate)
        {
            if (ledger.Leases == 0 && ledger.Generation == generation)
            {
                Remove(ledger);
            }
        }
    }

    /// <summary>A ledger that stopped following (its token refused, the ledger and the website out of step): the next page starts afresh.</summary>
    private void Fail(LiveLedger ledger)
    {
        lock (gate)
        {
            Remove(ledger);
        }
    }

    private void Remove(LiveLedger ledger)
    {
        if (ledgers.TryGetValue(ledger.Key, out var current) && ReferenceEquals(current, ledger))
        {
            ledgers.Remove(ledger.Key);
        }

        ledger.Stop();
    }

    /// <summary>One person's Replica of their tenants, kept current.</summary>
    internal sealed class LiveLedger
    {
        private readonly AtlasLedger.Model.Ledger replica = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly HttpClient http;
        private readonly IServiceScope scope;
        private readonly ClaimsPrincipal user;
        private readonly string ledgerScope;
        private readonly ILogger logger;
        private readonly Action<LiveLedger> fail;
        private readonly Lock viewGate = new();
        private readonly List<Task> following = [];
        private IReadOnlyList<Guid> tenantIds = [];
        private volatile LedgerView? view;
        private volatile string? problem;

        public LiveLedger(string key, ClaimsPrincipal user, string ledgerScope, HttpClient http, IServiceScope scope, ILogger logger, Action<LiveLedger> fail)
        {
            Key = key;
            this.user = user;
            this.ledgerScope = ledgerScope;
            this.http = http;
            this.scope = scope;
            this.logger = logger;
            this.fail = fail;

            // On the thread pool, not under the caller's lock; and without the caller's context: the ledger outlives the
            // request that starts it, so nothing of that request (its HttpContext) should travel with its loops.
            using (ExecutionContext.SuppressFlow())
            {
                Ready = Task.Run(StartAsync);
            }
        }

        public event Action? Changed;

        public string Key { get; }

        /// <summary>Completes when every tenant has its first snapshot; fails with why it couldn't start.</summary>
        public Task Ready { get; }

        /// <summary>The pages' view; null if the person belongs to no tenant.</summary>
        public LedgerView? View => view;

        /// <summary>Why it stopped following the ledger, if it did: the view is then as it last was.</summary>
        public string? Problem => problem;

        public bool IsStopped => stopping.IsCancellationRequested;

        // Guarded by the owner's gate.
        public int Leases { get; set; }

        public int Generation { get; set; }

        public void Stop()
        {
            if (stopping.IsCancellationRequested)
            {
                return;
            }

            stopping.Cancel();
            Task[] tasks;
            lock (viewGate)
            {
                tasks = [.. following];
            }

            _ = Task.WhenAll(tasks).ContinueWith(_ => scope.Dispose(), TaskScheduler.Default);
        }

        private async Task StartAsync()
        {
            var cancellationToken = stopping.Token;
            try
            {
                var tenants = await GetTenantsAsync(cancellationToken);
                var clients = tenants.Select(tenantId => new LedgerSyncClient(http, replica, tenantId, Tables, AuthorizeAsync, Json)).ToArray();
                await Task.WhenAll(clients.Select(client => client.SyncAsync(TimeSpan.Zero, cancellationToken)));
                lock (viewGate)
                {
                    tenantIds = tenants;
                    view = tenants.Count == 0 ? null : LedgerProjection.Project(tenants, replica);
                    foreach (var client in clients)
                    {
                        client.Synced += (_, _) => Refresh();
                        following.Add(FollowAsync(client));
                    }
                }
            }
            catch (LedgerSyncException exception)
            {
                throw new LedgerUnavailableException($"The ledger refused to share your accounts ({(int)exception.StatusCode}): {exception.Message}", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new LedgerUnavailableException($"The ledger at {http.BaseAddress} can't be reached.", exception);
            }
        }

        private async Task<IReadOnlyList<Guid>> GetTenantsAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/me/tenants");
            AtlasLedger.Model.Ledger.AddContractHeaders(request.Headers);
            await AuthorizeAsync(request, cancellationToken);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new LedgerUnavailableException($"The ledger answered {(int)response.StatusCode} to v1/me/tenants.");
            }

            return await response.Content.ReadFromJsonAsync<Guid[]>(Json, cancellationToken) ?? [];
        }

        /// <summary>Signs a request in as the person: their access token to the ledger, from the site's token cache.</summary>
        private async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The scheme that signed the person in and holds their token: the site's default scheme is the cookie, which
            // Microsoft.Identity.Web can't take its settings from.
            var tokens = scope.ServiceProvider.GetRequiredService<ITokenAcquisition>();
            var token = await tokens.GetAccessTokenForUserAsync([ledgerScope], authenticationScheme: OpenIdConnectDefaults.AuthenticationScheme, user: user);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        /// <summary>Follows one tenant until stopped.  Network failures are retried by the client; a refusal ends it.</summary>
        private async Task FollowAsync(LedgerSyncClient client)
        {
            try
            {
                await client.RunAsync(stopping.Token);
            }
            catch (Exception) when (stopping.IsCancellationRequested)
            {
                // Stopped: the person's pages have all closed.
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Stopped following tenant {TenantId} for a person.", client.TenantId);
                problem = exception is MicrosoftIdentityWebChallengeUserException or Microsoft.Identity.Client.MsalUiRequiredException
                    ? "Your sign-in has expired: reload the page to see your accounts as they are now."
                    : "Your accounts stopped updating: reload the page to see them as they are now.";
                fail(this);
                Changed?.Invoke();
            }
        }

        private void Refresh()
        {
            lock (viewGate)
            {
                view = LedgerProjection.Project(tenantIds, replica);
            }

            Changed?.Invoke();
        }
    }
}

/// <summary>A page's hold on the person's live ledger: its view, and a signal when the view changes.</summary>
public sealed class LiveLedgerLease : IDisposable
{
    private readonly LiveLedgers.LiveLedger ledger;
    private readonly Action<LiveLedgers.LiveLedger> release;
    private int disposed;

    internal LiveLedgerLease(LiveLedgers.LiveLedger ledger, Action<LiveLedgers.LiveLedger> release)
    {
        this.ledger = ledger;
        this.release = release;
        ledger.Changed += OnChanged;
    }

    /// <summary>Raised, on a thread of the ledger's, when the view or the problem changes.</summary>
    public event Action? Changed;

    /// <summary>The person's accounts as they are now; null if they belong to no tenant.</summary>
    public LedgerView? View => ledger.View;

    /// <summary>Why the view stopped updating, if it did.</summary>
    public string? Problem => ledger.Problem;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            ledger.Changed -= OnChanged;
            release(ledger);
        }
    }

    private void OnChanged() => Changed?.Invoke();
}
