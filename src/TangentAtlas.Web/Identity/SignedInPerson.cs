using System.Security.Claims;

namespace TangentAtlas.Web.Identity;

/// <summary>What the pages show of the signed-in person, from their ID token's claims.</summary>
public sealed record SignedInPerson(string ObjectId, string? Email, string DisplayName)
{
    public static SignedInPerson? From(ClaimsPrincipal user)
    {
        var objectId = user.FindFirst(SiteSignIn.ObjectIdClaim)?.Value;
        if (string.IsNullOrEmpty(objectId))
        {
            return null;
        }
        var email = user.FindFirst("email")?.Value ?? user.FindFirst("preferred_username")?.Value;
        var name = user.FindFirst("name")?.Value;
        // An email sign-up that collects no name leaves Entra's placeholder, "unknown".
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            name = email ?? "there";
        }
        return new SignedInPerson(objectId, email, name);
    }
}
