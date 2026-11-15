using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;

namespace TangentAtlas.Web.Ledger;

/// <summary>
/// The ledger the signed-in pages read, as the signed-in person.  Identifiers only, plus where the website's credential
/// is: TangentAtlas.Platform's Deploy-EntraApplications.ps1 writes them to %APPDATA%\TangentAtlas\website.json on a
/// developer's computer.  Without them the site signs people in as before (an ID token only) and shows no ledger data.
/// </summary>
public sealed class LedgerOptions
{
    public const string SectionName = "Ledger";

    /// <summary>The ledger server, such as http://127.0.0.1:5180 (TangentAtlas.LedgerService's Start-LocalLedger.ps1).</summary>
    public string? Server { get; set; }

    /// <summary>The permission the site asks for, as the person: api://&lt;Ledger API client ID&gt;/Ledger.Read.</summary>
    public string? Scope { get; set; }

    /// <summary>
    /// The thumbprint of the website's development certificate, in the current user's personal store with a private key
    /// that cannot be exported: how the site proves itself when it redeems a sign-in's code.  Nothing secret is stored.
    /// </summary>
    public string? CertificateThumbprint { get; set; }

    public bool IsConfigured =>
        Uri.TryCreate(Server, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(Scope) && !string.IsNullOrWhiteSpace(CertificateThumbprint);

    /// <summary>The website's credential, as Microsoft.Identity.Web describes it.</summary>
    public CredentialDescription Credential() =>
        CertificateDescription.FromStoreWithThumbprint(CertificateThumbprint!, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser, System.Security.Cryptography.X509Certificates.StoreName.My);
}

/// <summary>A holding, named and valued.</summary>
/// <param name="Name">What the page calls it: its ticker, or its currency's cash ("USD cash").</param>
/// <param name="Symbol">Its ticker or currency code, which also names its picture.</param>
/// <param name="FullName">Its full name, when the ledger has one.</param>
public sealed record HoldingView(string Name, string Symbol, string? FullName, decimal Quantity, decimal? Price, decimal? MarketValue, decimal? Weight, decimal? TargetWeight, bool IsCash);

/// <summary>An account at one close.</summary>
public sealed record DayView(DateOnly Date, decimal? Value, decimal Flow, decimal? Return);

/// <summary>An account's performance, as the ledger measures it from its daily returns.</summary>
public sealed record PerformanceView(
    DateOnly? From,
    DateOnly? Through,
    int Returns,
    decimal? TotalReturn,
    decimal? AnnualizedReturn,
    decimal? Volatility,
    decimal? SharpeRatio,
    decimal? MaximumDrawdown,
    bool IsRanked);

/// <summary>One account: where it is held, what it holds, what it's worth, how it has done.</summary>
/// <param name="Type">The ledger's kind of account: Brokerage, RetirementPlan, Deposit, CreditCard or Loan.</param>
/// <param name="Institution">Where it is held.</param>
/// <param name="Number">Its number there, shortened to its last four characters.</param>
public sealed record AccountView(
    Guid AccountId,
    string Name,
    string Type,
    string Institution,
    string? Number,
    bool IsPaper,
    decimal? MarketValue,
    decimal Cash,
    decimal NetContributions,
    decimal? Gain,
    string? ModelName,
    IReadOnlyList<HoldingView> Holdings,
    PerformanceView? Performance,
    IReadOnlyList<DayView> Days);

/// <summary>What the person sees of their tenant.</summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="AsOf">When the ledger's rules last derived it; null while they haven't.</param>
/// <param name="Accounts">Its accounts.</param>
public sealed record LedgerView(Guid TenantId, DateTimeOffset? AsOf, IReadOnlyList<AccountView> Accounts);

/// <summary>The ledger couldn't be read (down, or answering with an error); the page says so.</summary>
public sealed class LedgerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the signed-in person's tenant from the ledger with their own access token (delegated Ledger.Read): the ledger
/// decides what they may see (its tenants of theirs; 404 for any other), never the website.
/// </summary>
public sealed class LedgerReader(HttpClient http, ITokenAcquisition tokens, LedgerOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the person's first tenant, or null if they belong to none.
    /// </summary>
    /// <exception cref="MicrosoftIdentityWebChallengeUserException">The person must sign in again (the site lost their token).</exception>
    /// <exception cref="LedgerUnavailableException">The ledger couldn't be read.</exception>
    public async Task<LedgerView?> ReadAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        // The scheme that signed the person in and holds their token: the site's default scheme is the cookie, which
        // Microsoft.Identity.Web can't take its settings from.
        var token = await tokens.GetAccessTokenForUserAsync([options.Scope!], authenticationScheme: OpenIdConnectDefaults.AuthenticationScheme, user: user);
        var tenants = await GetAsync<Guid[]>(token, "v1/me/tenants", cancellationToken) ?? [];
        if (tenants.Length == 0)
        {
            return null;
        }

        var tenantId = tenants[0];
        var symbols = await GetAsync<SymbolRow[]>(token, $"v1/tenants/{tenantId}/symbol", cancellationToken) ?? [];
        var nodes = await GetAsync<NodeRow[]>(token, $"v1/tenants/{tenantId}/node", cancellationToken) ?? [];
        var institutions = await GetAsync<InstitutionRow[]>(token, $"v1/tenants/{tenantId}/institution", cancellationToken) ?? [];
        var accounts = await GetAsync<AccountRow[]>(token, $"v1/tenants/{tenantId}/account", cancellationToken) ?? [];
        var holdings = await GetAsync<HoldingsRow>(token, $"v1/tenants/{tenantId}/holdings", cancellationToken, conflictIsEmpty: true);
        var statistics = await GetAsync<StatisticsRow>(token, $"v1/tenants/{tenantId}/statistics?days=true", cancellationToken, conflictIsEmpty: true);

        string NameOf(Guid nodeId) =>
            symbols.Where(symbol => symbol.NodeId == nodeId && symbol.Scheme == "Ticker").Select(symbol => symbol.Code).FirstOrDefault()
            ?? symbols.Where(symbol => symbol.NodeId == nodeId && symbol.Scheme == "Currency").Select(symbol => symbol.Code + " cash").FirstOrDefault()
            ?? nodes.FirstOrDefault(node => node.NodeId == nodeId)?.DisplayName
            ?? nodeId.ToString()[..8];

        string SymbolOf(Guid nodeId) =>
            symbols.Where(symbol => symbol.NodeId == nodeId && symbol.Scheme is "Ticker" or "Currency").Select(symbol => symbol.Code).FirstOrDefault()
            ?? NameOf(nodeId);

        var paperInstitutions = institutions.Where(institution => institution.Source == "Simulator").Select(institution => institution.InstitutionId).ToHashSet();
        var views = new List<AccountView>();
        foreach (var summary in holdings?.Accounts ?? [])
        {
            var account = accounts.FirstOrDefault(item => item.AccountId == summary.AccountId);
            var performance = statistics?.Accounts.FirstOrDefault(item => item.Statistics.AccountId == summary.AccountId);
            var institution = institutions.FirstOrDefault(item => item.InstitutionId == account?.InstitutionId);
            var number = account?.ExternalAccountId is { Length: > 0 } external ? "••" + external[Math.Max(0, external.Length - 4)..] : null;
            views.Add(new AccountView(
                summary.AccountId,
                nodes.FirstOrDefault(node => node.NodeId == summary.AccountId)?.DisplayName ?? "Account",
                account?.AccountType ?? "Brokerage",
                institution?.Name ?? "",
                number,
                institution is not null && paperInstitutions.Contains(institution.InstitutionId),
                summary.MarketValue,
                summary.Cash,
                summary.NetContributions,
                summary.Gain,
                summary.ModelName,
                (holdings?.Holdings ?? [])
                    .Where(holding => holding.AccountId == summary.AccountId && holding.Quantity != 0m)
                    .OrderBy(holding => holding.AssetType == "Cash")
                    .ThenByDescending(holding => holding.MarketValue ?? 0m)
                    .Select(holding => new HoldingView(
                        NameOf(holding.AssetId),
                        SymbolOf(holding.AssetId),
                        nodes.FirstOrDefault(node => node.NodeId == holding.AssetId)?.FullName,
                        holding.Quantity, holding.Price, holding.MarketValue, holding.Weight, holding.TargetWeight, holding.AssetType == "Cash"))
                    .ToArray() is var held ? WithCashTarget(held) : [],
                performance?.Statistics is { } item
                    ? new PerformanceView(item.From, item.Through, item.Returns, item.TotalReturn, item.AnnualizedReturn, item.Volatility, item.SharpeRatio, item.MaximumDrawdown, item.IsRanked)
                    : null,
                (performance?.Days ?? []).Select(day => new DayView(day.Date, day.Value, day.Flow, day.Return)).ToArray()));
        }

        return new LedgerView(tenantId, holdings?.AsOf, views);
    }

    /// <summary>
    /// A model's weights cover what it invests in; the cash it keeps is the rest.  Shown as cash's target when the ledger
    /// gives none and the other holdings have targets.
    /// </summary>
    private static HoldingView[] WithCashTarget(HoldingView[] holdings)
    {
        var targeted = holdings.Where(holding => !holding.IsCash && holding.TargetWeight is not null).ToArray();
        if (targeted.Length == 0)
        {
            return holdings;
        }

        var rest = Math.Max(0m, 1m - targeted.Sum(holding => holding.TargetWeight!.Value));
        return holdings.Select(holding => holding.IsCash && holding.TargetWeight is null ? holding with { TargetWeight = rest } : holding).ToArray();
    }

    private async Task<T?> GetAsync<T>(string token, string path, CancellationToken cancellationToken, bool conflictIsEmpty = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new LedgerUnavailableException($"The ledger at {http.BaseAddress} can't be reached.", exception);
        }

        using (response)
        {
            if (conflictIsEmpty && response.StatusCode == HttpStatusCode.Conflict)
            {
                return default;   // the ledger's rules haven't run yet
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new LedgerUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The ledger answered {(int)response.StatusCode} to {path}."));
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        }
    }

    // The ledger's JSON, read leniently: only the fields the pages show; enums arrive as their names.
    private sealed record SymbolRow(Guid NodeId, string Scheme, string Code);

    private sealed record NodeRow(Guid NodeId, string? DisplayName, string? FullName);

    private sealed record InstitutionRow(Guid InstitutionId, string? Name, string? Source);

    private sealed record AccountRow(Guid AccountId, Guid? InstitutionId, string? AccountType, string? ExternalAccountId);

    private sealed record HoldingRow(Guid AccountId, Guid AssetId, string AssetType, decimal Quantity, decimal? Price, decimal? MarketValue, decimal? Weight, decimal? TargetWeight);

    private sealed record SummaryRow(Guid AccountId, decimal? MarketValue, decimal Cash, decimal NetContributions, decimal? Gain, string? ModelName);

    private sealed record HoldingsRow(DateTimeOffset AsOf, HoldingRow[] Holdings, SummaryRow[] Accounts);

    private sealed record PerformanceRow(Guid AccountId, DateOnly? From, DateOnly? Through, int Returns, decimal? TotalReturn, decimal? AnnualizedReturn, decimal? Volatility, decimal? SharpeRatio, decimal? MaximumDrawdown, bool IsRanked);

    private sealed record DayRow(DateOnly Date, decimal? Value, decimal Flow, decimal? Return);

    private sealed record AccountStatisticsRow(PerformanceRow Statistics, DayRow[]? Days);

    private sealed record StatisticsRow(DateTimeOffset AsOf, AccountStatisticsRow[] Accounts);
}
