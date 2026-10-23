using TangentAtlas.Web.Components;
using TangentAtlas.Web.Identity;

var builder = WebApplication.CreateBuilder(args);

// On a developer's computer, Deploy-EntraApplications.ps1 writes the external tenant's identifiers here.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile(SiteSignIn.DeveloperSettingsPath, optional: true, reloadOnChange: false);
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSiteSignIn(builder.Configuration);

var app = builder.Build();

if (!app.Services.GetRequiredService<IdentityProviderOptions>().IsConfigured)
{
    app.Logger.LogWarning("IdentityProvider:Authority and IdentityProvider:ClientId are not set: nobody can sign in, and the signed-in pages are refused.");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
// Partner preview: the site is shared by link only, so no page, file or redirect is to be indexed (decisions, October 22).
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    await next();
});
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAccountEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
