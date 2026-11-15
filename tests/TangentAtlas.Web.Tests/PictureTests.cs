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
