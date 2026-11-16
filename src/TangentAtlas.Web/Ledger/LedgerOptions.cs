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
    /// that cannot be exported: how the site proves itself on a developer's computer when it redeems a sign-in's code.
    /// </summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// In Azure, the vault that holds the website's certificate (https://kv-tangentatlas-prod.vault.azure.net/).  The site
    /// reads it as its managed identity (AZURE_CLIENT_ID names it); nothing secret is in its settings.
    /// </summary>
    public string? KeyVaultUrl { get; set; }

    /// <summary>The certificate's name in that vault.</summary>
    public string? KeyVaultCertificateName { get; set; }

    /// <summary>
    /// How long a person's live ledger keeps running after their last page closes, so the next page (or the same page
    /// reconnecting) starts from it rather than from a new snapshot.  Zero stops it at once.
    /// </summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromMinutes(5);

    private bool HasVaultCertificate => Uri.TryCreate(KeyVaultUrl, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(KeyVaultCertificateName);

    public bool IsConfigured =>
        Uri.TryCreate(Server, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(Scope)
        && (HasVaultCertificate || !string.IsNullOrWhiteSpace(CertificateThumbprint));

    /// <summary>The website's credential, as Microsoft.Identity.Web describes it: the vault's certificate, or the store's.</summary>
    public CredentialDescription Credential() => HasVaultCertificate
        ? CertificateDescription.FromKeyVault(KeyVaultUrl!, KeyVaultCertificateName!)
        : CertificateDescription.FromStoreWithThumbprint(CertificateThumbprint!, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser, System.Security.Cryptography.X509Certificates.StoreName.My);
}
