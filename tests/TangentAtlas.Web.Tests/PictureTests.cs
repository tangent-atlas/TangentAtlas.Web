using TangentAtlas.Web.Pictures;

namespace TangentAtlas.Web.Tests;

public sealed class PictureTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("pictures-").FullName;

    public PictureTests()
    {
        foreach (var (kind, name) in new[] { ("ticker_icons", "AAPL"), ("ticker_icons", "BRKB"), ("forex_icons", "USD") })
        {
            Directory.CreateDirectory(Path.Combine(folder, kind));
            File.WriteAllBytes(Path.Combine(folder, kind, name + ".png"), [0x89, 0x50, 0x4E, 0x47]);
        }
    }

    private PictureLibrary Library => new(new PictureOptions { Path = folder });

    [Theory]
    [InlineData(PictureKind.Ticker, "aapl", "/pictures/ticker/AAPL.png")]
    [InlineData(PictureKind.Ticker, "BRK-B", "/pictures/ticker/BRKB.png")]   // the icon set drops a class share's punctuation
    [InlineData(PictureKind.Ticker, "BRK.B", "/pictures/ticker/BRKB.png")]
    [InlineData(PictureKind.Currency, "USD", "/pictures/currency/USD.png")]
    [InlineData(PictureKind.Ticker, "MSFT", null)]
    [InlineData(PictureKind.Ticker, "../forex_icons/USD", null)]
    public void APictureIsFoundByItsName(PictureKind kind, string code, string? address) =>
        Assert.Equal(address, Library.Address(kind, code));

    [Theory]
    [InlineData("BRK-B", "BRKB")]
    [InlineData("GOOGL", "GOOG")]
    [InlineData("", "?")]
    public void WithoutAPictureTheLettersStandIn(string code, string letters) =>
        Assert.Equal(letters, PictureLibrary.Letters(code));

    public void Dispose() => Directory.Delete(folder, recursive: true);
}

public class LightLogoTests
{
    private static byte[] Logo(params (byte R, byte G, byte B, byte A)[] pixels) =>
        LightLogos.Encode(new LightLogos.Image(pixels.Length, 1, pixels.SelectMany(p => new[] { p.R, p.G, p.B, p.A }).ToArray()));

    [Fact]
    public void AWhiteLogoIsDrawnInDarkInkAndKeepsItsTransparency()
    {
        var shown = LightLogos.Decode(LightLogos.ForLightPage(Logo((255, 255, 255, 255), (250, 250, 250, 90), (0, 0, 0, 0))))!;

        Assert.Equal(new byte[] { 31, 42, 42, 255, 31, 42, 42, 90, 0, 0, 0, 0 }, shown.Rgba);
    }

    [Fact]
    public void ALogoInColourIsLeftAsItIs()
    {
        var png = Logo((244, 78, 36, 255), (255, 255, 255, 255), (0, 120, 212, 255));

        Assert.Same(png, LightLogos.ForLightPage(png));
    }

    [Fact]
    public void WhatIsNotAPngIsServedUnchanged()
    {
        byte[] bytes = [1, 2, 3];

        Assert.Same(bytes, LightLogos.ForLightPage(bytes));
    }
}

public class LedgerCredentialTests
{
    [Fact]
    public void InAzureTheSiteProvesItselfWithTheVaultsCertificate()
    {
        var options = new TangentAtlas.Web.Ledger.LedgerOptions
        {
            Server = "https://app-tangentatlas-ledger-prod.azurewebsites.net",
            Scope = "api://e82d5369-e139-4fd8-8c2f-8b3b3faa0ee6/Ledger.Read",
            KeyVaultUrl = "https://kv-tangentatlas-prod.vault.azure.net/",
            KeyVaultCertificateName = "website-ledger-client",
        };

        Assert.True(options.IsConfigured);
        var credential = options.Credential();
        Assert.Equal(Microsoft.Identity.Abstractions.CredentialSource.KeyVault, credential.SourceType);
        Assert.Equal("website-ledger-client", credential.KeyVaultCertificateName);
    }

    [Fact]
    public void WithoutACertificateTheLedgerIsNotConfigured() =>
        Assert.False(new TangentAtlas.Web.Ledger.LedgerOptions { Server = "https://ledger.example", Scope = "api://x/Ledger.Read" }.IsConfigured);
}
