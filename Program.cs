using Azure.Identity;
using JJKeller.SdsManagementDashboard.Models;
using JJKeller.SdsManagementDashboard.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

// --- Secrets come from Key Vault, never from source control. A config key ':' maps to a
//     secret name '--' (e.g. ConnectionStrings:SdsProdDb -> ConnectionStrings--SdsProdDb).
//     Locally, DefaultAzureCredential uses your `az login`; in Azure it uses the app's
//     managed identity. See README for which secrets to provision and where.
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

// The app is hosted as a virtual application under the existing web UI host, so it runs
// beneath a path prefix. Override 'PathBase' per environment if that changes.
var pathBase = builder.Configuration["PathBase"] ?? "/sds_management_dashboard_v2";

// --- Okta OIDC (confidential web app) backed by a cookie session. There is no anonymous
//     access: the FallbackPolicy below forces every request through an authenticated user.
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "sds_dashboard_auth";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = builder.Configuration["Okta:Authority"];
        options.ClientId = builder.Configuration["Okta:ClientId"];
        options.ClientSecret = builder.Configuration["Okta:ClientSecret"];
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = "name";
    });

builder.Services.AddAuthorization(options =>
{
    // No unauthenticated endpoints anywhere in this app.
    options.FallbackPolicy = options.DefaultPolicy;
});

builder.Services.AddSingleton<SdsRequestQueryService>();

var app = builder.Build();

app.UsePathBase(pathBase);
app.UseAuthentication();
app.UseAuthorization();

// Read-only admin data endpoint. Requires an authenticated Okta user (fallback policy).
// Date filter is optional; it defaults to "Fulfilled on = Yesterday" when omitted.
app.MapGet("/api/sds-requests", async (
    string? field,
    string? op,
    DateOnly? from,
    DateOnly? to,
    SdsRequestQueryService service,
    CancellationToken ct) =>
{
    if (!SdsRequestFilter.TryCreate(field, op, from, to, out var filter, out var error))
    {
        return Results.BadRequest(new { error });
    }

    var result = await service.GetRequestsAsync(filter, ct);
    return Results.Json(result);
});

// Sign the user out of both the local cookie and Okta.
app.MapGet("/signout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.SignOut(
        new AuthenticationProperties { RedirectUri = pathBase },
        new[] { OpenIdConnectDefaults.AuthenticationScheme });
});

// Serve the single-page UI shell. Hitting this unauthenticated triggers the Okta challenge.
var indexHtmlPath = Path.Combine(app.Environment.ContentRootPath, "App", "index.html");
app.MapGet("/", () => Results.Content(File.ReadAllText(indexHtmlPath), "text/html; charset=utf-8"));

app.Run();
