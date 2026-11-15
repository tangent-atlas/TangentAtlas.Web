using System.Text.RegularExpressions;

namespace TangentAtlas.Web.Pictures;

/// <summary>
/// Where the pictures that identify things come from: a folder of PNG files named by what they show, in the layout of
/// the NVSTly icon set (github.com/nvstly/icons): ticker_icons\AAPL.png, forex_icons\USD.png, crypto_icons\BTC.png.  The
/// pictures are not part of this repository: they are third-party logos, served from the folder they were downloaded to.
/// </summary>
public sealed class PictureOptions
{
    public const string SectionName = "Pictures";

    /// <summary>The folder (default: the icon set's clone under the developer's repos, nvstly\icons or icons).</summary>
    public string? Path { get; set; }
}

/// <summary>What a picture shows, and the folder it is kept in.</summary>
public enum PictureKind
{
    /// <summary>A company or fund, by ticker.</summary>
    Ticker,

    /// <summary>A currency, by its ISO code: its country's flag.</summary>
    Currency,

    /// <summary>A cryptocurrency, by its symbol.</summary>
    Crypto,
}

/// <summary>
/// Finds the picture for a thing, if there is one, and serves it.  Pages ask for a picture's address; with none, they
/// show the thing's letters instead.  Names are matched as the icon set spells them: upper case, and a class share's
/// punctuation dropped (BRK-B and BRK.B are BRKB).
/// </summary>
public sealed partial class PictureLibrary
{
    private static readonly Dictionary<PictureKind, string> Folders = new()
    {
        [PictureKind.Ticker] = "ticker_icons",
        [PictureKind.Currency] = "forex_icons",
        [PictureKind.Crypto] = "crypto_icons",
    };

    private readonly string? root;

    public PictureLibrary(PictureOptions options)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        root = new[]
            {
                options.Path,
                System.IO.Path.Combine(home, "source", "repos", "nvstly", "icons"),
                System.IO.Path.Combine(home, "source", "repos", "icons"),
            }
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(System.IO.Path.Combine(path, "ticker_icons")));
    }

    /// <summary>Whether a folder of pictures was found.</summary>
    public bool IsAvailable => root is not null;

    /// <summary>The address of a thing's picture, or null if there is none.</summary>
    public string? Address(PictureKind kind, string? code) =>
        File(kind, code) is { } found ? $"/pictures/{kind.ToString().ToLowerInvariant()}/{found.Name}" : null;

    /// <summary>The file of a thing's picture, or null if there is none.</summary>
    public (string Path, string Name)? File(PictureKind kind, string? code)
    {
        if (root is null || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var upper = code.Trim().ToUpperInvariant();
        foreach (var name in new[] { upper, Punctuation().Replace(upper, "") }.Distinct())
        {
            if (!SafeName().IsMatch(name))
            {
                continue;
            }

            var path = System.IO.Path.Combine(root, Folders[kind], name + ".png");
            if (System.IO.File.Exists(path))
            {
                return (path, name + ".png");
            }
        }

        return null;
    }

    /// <summary>The letters that stand in for a picture: up to four of the code's.</summary>
    public static string Letters(string code) => Punctuation().Replace(code.ToUpperInvariant(), "") is { Length: > 0 } letters ? letters[..Math.Min(4, letters.Length)] : "?";

    [GeneratedRegex("[^A-Z0-9]")]
    private static partial Regex Punctuation();

    [GeneratedRegex("^[A-Z0-9.\\-]{1,24}$")]
    private static partial Regex SafeName();
}

/// <summary>Serves the pictures: /pictures/{kind}/{name}.png, cached for a day.</summary>
public static class PictureEndpoints
{
    public static IServiceCollection AddPictures(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new PictureLibrary(configuration.GetSection(PictureOptions.SectionName).Get<PictureOptions>() ?? new()));
        return services;
    }

    public static IEndpointRouteBuilder MapPictures(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/pictures/{kind}/{name}", (string kind, string name, PictureLibrary library, HttpContext context) =>
        {
            if (!Enum.TryParse<PictureKind>(kind, ignoreCase: true, out var pictureKind)
                || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || library.File(pictureKind, name[..^4]) is not { } file)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(file.Path, "image/png");
        });
        return endpoints;
    }
}
