using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Cursory.Blazor.Components;
using Cursory.Blazor.Hubs;
using Cursory.Blazor.Services;
using Cursory.Core.Models;
using Cursory.Core.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCursoryCore(builder.Configuration);

// Server-authoritative physics tick. Runs at 30Hz, broadcasts WorldSnapshot to all clients.
builder.Services.AddHostedService<GameLoopService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR(options =>
{
    // Game loop broadcasts at 30Hz; clients send cursor pos at 30Hz. Keep payloads small.
    options.MaximumReceiveMessageSize = 8 * 1024;
});

// Cookie auth — same hardening pattern as StreetSamurai, adapted to username-based login.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/api/auth/logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Name = "Cursory.Auth";
        // SecurePolicy: Always in production (HTTPS only). Allow HTTP in dev so the
        // launch profile's http endpoint can sign you in without an HTTPS cert.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

        // Re-validate SecurityStamp on every request — rejects sessions after
        // password/role change, matching StreetSamurai.
        options.Events.OnValidatePrincipal = async context =>
        {
            var userId = context.Principal?.FindFirstValue("UserId");
            var stamp = context.Principal?.FindFirstValue("SecurityStamp");
            if (userId == null || stamp == null)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync();
                return;
            }
            var users = context.HttpContext.RequestServices.GetRequiredService<UserRepository>();
            var user = users.GetById(userId);
            if (user == null || user.SecurityStamp != stamp)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync();
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery();

// Per-IP rate limiting on login.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

// One-shot seed of the two predetermined accounts. Bypasses the password policy
// for these two only (operator chose the passwords verbatim).
using (var scope = app.Services.CreateScope())
{
    var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
    auth.SeedUser("GunGreenEyes", "GunGreenEyes", "Happygirl1005", UserRoles.Player, "#D85A30");
    auth.SeedUser("GideonKain",   "GideonKain",   "Happygirl1005", UserRoles.Player, "#378ADD");
}

// Azure App Service terminates TLS at the load balancer and forwards as HTTP with
// X-Forwarded-Proto. Honour the header so UseHttpsRedirection / Secure cookies see
// the original scheme. Has to run before authentication.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
// Azure App Service sits behind a load balancer outside the loopback range, so trust
// any proxy. The default loopback-only allowlist would drop the header in production.
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Cursory.Shared.Components.Pages.Home).Assembly);

app.MapHub<RoomHub>("/hubs/room");

// Login form POST — username + password, antiforgery + rate limit + open-redirect guard.
app.MapPost("/api/auth/login", async (HttpContext ctx, AuthService auth, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(ctx); }
    catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; return; }

    var form = await ctx.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();
    if (!AuthService.IsLocalUrl(returnUrl)) returnUrl = "/";

    var user = auth.Authenticate(username, password);
    if (user == null) { ctx.Response.Redirect("/login?error=invalid"); return; }

    var claims = new[]
    {
        new Claim(ClaimTypes.Name, user.DisplayName),
        new Claim(ClaimTypes.Role, user.Role),
        new Claim("UserId", user.Id),
        new Claim("Username", user.Username),
        new Claim("Color", user.Color),
        new Claim("SecurityStamp", user.SecurityStamp),
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    ctx.Response.Redirect(returnUrl);
}).RequireRateLimiting("login");

app.MapPost("/api/auth/logout", async (HttpContext ctx, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(ctx); }
    catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; return; }
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Redirect("/login");
});

app.Run();
