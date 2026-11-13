// File: src/eh.DockManager.Server/Program.cs
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Web;
using eh.DockManager.Server.Data;
using eh.DockManager.Server.Docker;
using eh.DockManager.Server.Kubernetes;
using eh.DockManager.Server.McpAndWebhooks;
using eh.DockManager.Server.Models;
using eh.DockManager.Server.Portal;
using eh.DockManager.Server.Security;
using eh.DockManager.Server.Stacks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;

// ──────────────────────────────────────────────────────────────────────────────
// Container health probe: the distroless image has no shell, so the binary probes itself.
// ──────────────────────────────────────────────────────────────────────────────
if (args.Contains("--health-probe"))
{
    var probePort = int.TryParse(Environment.GetEnvironmentVariable("EHDM_PORTAL_PORT"), out var pp0) ? pp0 : 443;
    using var probeHandler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    };
    using var probeClient = new HttpClient(probeHandler) { Timeout = TimeSpan.FromSeconds(4) };
    try
    {
        using var response = await probeClient.GetAsync($"https://127.0.0.1:{probePort}/api/system/health");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        Environment.ExitCode = 1;
    }
    return;
}

// ──────────────────────────────────────────────────────────────────────────────
// Agent mode: a tiny mTLS reverse proxy in front of the local Docker socket.
// ──────────────────────────────────────────────────────────────────────────────
if (args.Contains("--agent"))
{
    await EhAgent.RunAsync(args);
    return;
}

Directory.CreateDirectory(EhConstants.DataDir);
Directory.CreateDirectory(EhConstants.StacksDir);
Directory.CreateDirectory(EhConstants.CertsDir);
Directory.CreateDirectory(EhConstants.KeysDir);

// Eagerly construct the security/state primitives that Kestrel and startup logic need.
var bootstrapLoggerFactory = LoggerFactory.Create(b =>
    b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
var vault = new CryptoVault(EhConstants.MasterKeyPath);
var certificateManager = new CertificateManager(bootstrapLoggerFactory.CreateLogger<CertificateManager>());
var store = new SqliteWalStore(EhConstants.DbPath);
await store.InitializeAsync();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(vault);
builder.Services.AddSingleton(certificateManager);
builder.Services.AddSingleton(store);

// Persist DataProtection keys under /data so cookie sessions survive restarts on a read-only rootfs.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(EhConstants.KeysDir))
    .SetApplicationName("eh.DockManager");
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("saml").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("osv");
builder.Services.AddHttpClient("webhook").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
// ACME needs long-poll style requests while the CA validates an http-01 challenge.
builder.Services.AddHttpClient("acme").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// ── Core services ────────────────────────────────────────────────────────────
builder.Services.AddSingleton<DockerClientFactory>();
builder.Services.AddSingleton<DockerCveScanner>();
builder.Services.AddSingleton<ComposeParser>();
builder.Services.AddSingleton<ComposeSchemaValidator>();
builder.Services.AddSingleton<ComposeStackService>();
builder.Services.AddSingleton<KubeClusterService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<ExternalIdentityService>();
builder.Services.AddSingleton<PasskeyService>();
builder.Services.AddSingleton<SamlAssertionReplayProtector>();
builder.Services.AddSingleton<SamlSpService>();
builder.Services.AddSingleton<SamlIdpService>();
builder.Services.AddSingleton<McpApiKeyService>();
builder.Services.AddSingleton<McpServer>();
builder.Services.AddSingleton<WebhookNotifier>();
builder.Services.AddSingleton<IEventNotifier>(sp => sp.GetRequiredService<WebhookNotifier>());
builder.Services.AddSingleton<DynamicProxyConfigProvider>();
builder.Services.AddSingleton<PortalProxyConfigurator>();
builder.Services.AddSingleton<WakeOnLanService>();
// Registered as the concrete singleton *and* as the hosted service, so both the background
// worker and direct callers (portal refresh, MCP) share the same reconciler instance.
builder.Services.AddSingleton<PortalDiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PortalDiscoveryService>());
builder.Services.AddHostedService<StackAutoUpdateWorker>();
builder.Services.AddSingleton<AcmeChallengeStore>();
builder.Services.AddSingleton<AcmeClient>();
builder.Services.AddHostedService<AcmeRenewalWorker>();
builder.Services.AddSingleton<NetworkMetricsTracker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkMetricsTracker>());

// ── Response compression (Zstd > Brotli > Gzip) ──────────────────────────────
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<ZstandardCompressionProvider>();
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/json", "application/problem+json", "text/event-stream", "image/svg+xml"
    });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Optimal);

builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
    o.Preload = false;
});

// ── YARP reverse proxy backed by our dynamic provider ────────────────────────
builder.Services.AddReverseProxy();
builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<DynamicProxyConfigProvider>());

// ── Authentication & authorization ───────────────────────────────────────────
var googleConfig = await store.GetConfigObjectAsync("oidc.google", new OidcProviderConfig { Provider = "google" });
var microsoftConfig = await store.GetConfigObjectAsync("oidc.microsoft", new OidcProviderConfig { Provider = "microsoft" });

var authBuilder = builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = "eh.cookie";
    o.DefaultChallengeScheme = "eh.cookie";
});
authBuilder.AddCookie("eh.cookie", o =>
{
    o.Cookie.Name = EhConstants.AdminSessionCookie;
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.SlidingExpiration = true;
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
    o.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    o.Events.OnRedirectToAccessDenied = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

void AddOidcProvider(OidcProviderConfig config, string scheme, string authority, string[] scopes)
{
    if (!config.Enabled || string.IsNullOrWhiteSpace(config.ClientId)) return;
    authBuilder.AddOpenIdConnect(scheme, config.DisplayName ?? scheme, o =>
    {
        o.Authority = config.Authority ?? authority;
        o.ClientId = config.ClientId;
        o.ClientSecret = config.ClientSecret;
        o.ResponseType = "code";
        o.UsePkce = true;
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = true;
        o.CallbackPath = "/signin-" + scheme;
        o.Scope.Clear();
        foreach (var scope in scopes) o.Scope.Add(scope);
        o.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = async ctx =>
            {
                var principal = ctx.Principal;
                var email = principal?.FindFirst(ClaimTypes.Email)?.Value
                            ?? principal?.FindFirst("email")?.Value
                            ?? principal?.FindFirst("preferred_username")?.Value;
                var displayName = principal?.FindFirst("name")?.Value;
                if (string.IsNullOrWhiteSpace(email))
                {
                    ctx.Fail("Identity provider did not return an email claim.");
                    return;
                }

                var external = ctx.HttpContext.RequestServices.GetRequiredService<ExternalIdentityService>();
                var notifier = ctx.HttpContext.RequestServices.GetRequiredService<IEventNotifier>();
                var provider = scheme == "google" ? IdentityProvider.Google : IdentityProvider.Microsoft;
                var (user, isNew) = await external.ResolveAsync(provider, email, displayName, ct: ctx.HttpContext.RequestAborted);
                if (isNew)
                {
                    await notifier.NotifyAsync("user.pending", "New user awaiting approval",
                        $"{email} signed in via {scheme} and requires approval.",
                        new { email, provider = scheme }, ctx.HttpContext.RequestAborted);
                }

                if (user.Role == UserRole.Pending || !user.Approved || user.Disabled)
                    ctx.Fail("Your account is pending administrator approval.");
            }
        };
    });
}

AddOidcProvider(googleConfig, "google", "https://accounts.google.com",
    new[] { "openid", "profile", "email" });
AddOidcProvider(microsoftConfig, "microsoft", "https://login.microsoftonline.com/common/v2.0",
    new[] { "openid", "profile", "email" });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("eh-authenticated", p => p.RequireAuthenticatedUser());
    o.AddPolicy("eh-admin", p => p.RequireRole(nameof(UserRole.Admin)));
});

// ── Kestrel: HTTP redirect, portal/proxy port, and admin/mTLS port ──────────
var httpPort = int.TryParse(Environment.GetEnvironmentVariable("EHDM_HTTP_PORT"), out var hp) ? hp : 80;
var portalPort = int.TryParse(Environment.GetEnvironmentVariable("EHDM_PORTAL_PORT"), out var pp) ? pp : 443;
var adminPort = int.TryParse(Environment.GetEnvironmentVariable("EHDM_ADMIN_PORT"), out var ap) ? ap : 8443;
builder.Services.AddHttpsRedirection(o => o.HttpsPort = portalPort);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = null;
    kestrel.ConfigureHttpsDefaults(https =>
    {
        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        https.ServerCertificateSelector = (_, _) => certificateManager.ServerCertificate;
    });

    kestrel.ListenAnyIP(httpPort);
    kestrel.ListenAnyIP(portalPort, lo =>
    {
        lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2AndHttp3;
        lo.UseHttps();
    });
    kestrel.ListenAnyIP(adminPort, lo =>
    {
        lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2AndHttp3;
        lo.UseHttps();
    });
});

var app = builder.Build();
var services = app.Services;

// ── Startup initialisation ───────────────────────────────────────────────────
var startupLog = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var authService = services.GetRequiredService<AuthService>();
var samlSp = services.GetRequiredService<SamlSpService>();
var samlIdp = services.GetRequiredService<SamlIdpService>();
var portalConfigurator = services.GetRequiredService<PortalProxyConfigurator>();

await EnsureLocalDockerHostAsync();
await samlSp.ReloadAsync();
await samlIdp.ReloadAsync();

// Empty-string values are treated as unset: the compose file always exports
// EHDM_ADMIN_PASSWORD (empty by default), so a plain ?? fallback would bootstrap
// the admin with an empty password.
var adminUser = Environment.GetEnvironmentVariable("EHDM_ADMIN_USER") is { Length: > 0 } u ? u : "admin";
var adminPassword = Environment.GetEnvironmentVariable("EHDM_ADMIN_PASSWORD") is { Length: > 0 } p
    ? p
    : Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
if (await authService.EnsureBootstrapAdminAsync(adminUser, adminPassword) is not null)
    startupLog.LogWarning("Initial admin credentials -> user: {User} password: {Password}", adminUser, adminPassword);

await portalConfigurator.RebuildAsync();

// Keep the TLS certificate aligned with the persisted portal domain so a domain saved in the UI
// (or a changed EHDM_PORTAL_DOMAIN) is covered on the next start without a manual regeneration.
certificateManager.SetPortalDomain(portalConfigurator.PortalDomain);
{
    var certInfo = certificateManager.GetServerCertificateInfo();
    var portalDomain = portalConfigurator.PortalDomain;
    var covered = certInfo.DnsNames.Any(n => string.Equals(n, portalDomain, StringComparison.OrdinalIgnoreCase))
        || certInfo.DnsNames.Any(n =>
            n.StartsWith("*.", StringComparison.Ordinal) &&
            portalDomain.EndsWith(n[1..], StringComparison.OrdinalIgnoreCase));
    // When an ACME certificate is active it is authoritative: the renewal worker owns its
    // lifecycle, so do not overwrite it with an internally signed leaf for the portal domain.
    if (!certificateManager.IsAcmeActive && !covered)
    {
        var lanIp = await store.GetConfigAsync("portal.lanIp");
        var ips = new List<string> { "127.0.0.1", "::1" };
        if (!string.IsNullOrWhiteSpace(lanIp)) ips.Add(lanIp);
        certificateManager.RegenerateServerCertificate(new[] { portalDomain, "*." + portalDomain, "localhost" }, ips);
        startupLog.LogInformation("Server certificate re-issued for portal domain {Domain}.", portalDomain);
    }
}

// ── Pipeline ─────────────────────────────────────────────────────────────────
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    // Private control plane: keep it out of search indexes even if robots.txt is ignored.
    ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

app.UseHsts();

// ACME http-01 challenge responder. It runs before the HTTPS redirect because the certificate
// authority must fetch the token over plain http://<domain>/.well-known/acme-challenge/<token>.
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/.well-known/acme-challenge"))
    {
        await next();
        return;
    }
    var token = ctx.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
    var challenges = ctx.RequestServices.GetRequiredService<AcmeChallengeStore>();
    if (token.Length > 0 && challenges.TryGet(token, out var keyAuthorization))
    {
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync(keyAuthorization);
        return;
    }
    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
});

app.UseHttpsRedirection();

// BREACH mitigation: never compress credential-bearing auth traffic.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/auth"))
        ctx.Request.Headers.Remove("Accept-Encoding");
    await next();
});
app.UseResponseCompression();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseMiddleware<ServerlessWakeMiddleware>();

app.UseDefaultFiles();
app.UseMiddleware<PrecompressedStaticMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// ──────────────────────────────────────────────────────────────────────────────
// Handlers
// ──────────────────────────────────────────────────────────────────────────────
var compose = services.GetRequiredService<ComposeStackService>();
var composeParser = services.GetRequiredService<ComposeParser>();
var dockerFactory = services.GetRequiredService<DockerClientFactory>();
var cveScanner = services.GetRequiredService<DockerCveScanner>();
var kube = services.GetRequiredService<KubeClusterService>();
var passkeys = services.GetRequiredService<PasskeyService>();
var acme = services.GetRequiredService<AcmeClient>();
var mcpServer = services.GetRequiredService<McpServer>();
var mcpKeys = services.GetRequiredService<McpApiKeyService>();
var notifier = services.GetRequiredService<WebhookNotifier>();
var wol = services.GetRequiredService<WakeOnLanService>();
var networkTracker = services.GetRequiredService<NetworkMetricsTracker>();
var exporter = new ContainerToComposeExporter();
var bootId = Guid.NewGuid().ToString("n")[..12];
var appVersion = ComputeAppVersion(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"));

async Task<DockerSocketClient> ClientForAsync(HttpContext ctx, CancellationToken ct)
{
    var hostId = ctx.Request.Query["hostId"].FirstOrDefault();
    if (long.TryParse(hostId, out var id)) return await dockerFactory.GetAsync(id, ct);
    return await dockerFactory.GetDefaultAsync(ct);
}

long? CurrentUserId(HttpContext ctx) =>
    long.TryParse(ctx.User.FindFirst(EhClaims.UserId)?.Value, out var id) ? id : null;

// ── Auth: local, TOTP, session ───────────────────────────────────────────────
var auth = app.MapGroup("/api/auth");

auth.MapGet("/me", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Json(new { authenticated = false });
    return Results.Json(new
    {
        authenticated = true,
        id = CurrentUserId(ctx),
        username = ctx.User.FindFirst(EhClaims.Username)?.Value,
        email = ctx.User.FindFirst(EhClaims.Email)?.Value,
        role = ctx.User.FindFirst(EhClaims.Role)?.Value,
        provider = ctx.User.FindFirst(EhClaims.Provider)?.Value,
        amr = ctx.User.FindFirst(EhClaims.Amr)?.Value
    });
});

auth.MapPost("/login", async (HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<LoginRequest>(cancellationToken: ctx.RequestAborted);
    if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Username and password are required." });

    var (outcome, user) = await authService.PasswordLoginAsync(req.Username, req.Password, req.TotpCode,
        ctx.RequestAborted);
    switch (outcome)
    {
        case AuthService.LoginOutcome.RequiresTotp:
            return Results.Json(new { requiresTotp = true }, statusCode: StatusCodes.Status401Unauthorized);
        case AuthService.LoginOutcome.Pending:
            return Results.Json(new { error = "Account pending approval." }, statusCode: StatusCodes.Status403Forbidden);
        case AuthService.LoginOutcome.Disabled:
            return Results.Json(new { error = "Account disabled." }, statusCode: StatusCodes.Status403Forbidden);
        case AuthService.LoginOutcome.InvalidCredentials:
            return Results.Json(new { error = "Invalid credentials." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var principal = authService.BuildPrincipal(user!, "pwd");
    await ctx.SignInAsync("eh.cookie", principal, new AuthenticationProperties { IsPersistent = true });
    await store.AddAuditAsync(new AuditLog
    {
        Actor = ActorKind.WebUi, ActorDetail = user!.Username, Action = "auth.login",
        Target = user.Username, Timestamp = DateTimeOffset.UtcNow
    }, ctx.RequestAborted);
    return Results.Json(new { authenticated = true, role = user.Role.ToString() });
});

auth.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync("eh.cookie");
    return Results.Ok(new { ok = true });
}).RequireAuthorization("eh-authenticated");

auth.MapPost("/totp/setup", async (HttpContext ctx) =>
{
    var userId = CurrentUserId(ctx);
    if (userId is null) return Results.Unauthorized();
    var user = await store.GetUserByIdAsync(userId.Value, ctx.RequestAborted);
    if (user is null) return Results.NotFound();
    var secret = TotpService.GenerateSecret();
    user.TotpSecret = secret;
    user.TotpEnabled = false;
    await store.UpsertUserAsync(user, ctx.RequestAborted);
    var uri = TotpService.BuildOtpAuthUri("eh.DockManager", user.Username, secret);
    return Results.Json(new { secret, otpauthUri = uri, qrSvg = TotpService.BuildQrSvg(uri) });
}).RequireAuthorization("eh-authenticated");

auth.MapPost("/totp/enable", async (HttpContext ctx) =>
{
    var userId = CurrentUserId(ctx);
    if (userId is null) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<TotpRequest>(cancellationToken: ctx.RequestAborted);
    var user = await store.GetUserByIdAsync(userId.Value, ctx.RequestAborted);
    if (user?.TotpSecret is null) return Results.BadRequest(new { error = "Run setup first." });
    if (req is null || !TotpService.ValidateCode(user.TotpSecret, req.Code))
        return Results.BadRequest(new { error = "Invalid code." });
    user.TotpEnabled = true;
    await store.UpsertUserAsync(user, ctx.RequestAborted);
    return Results.Ok(new { enabled = true });
}).RequireAuthorization("eh-authenticated");

auth.MapPost("/totp/disable", async (HttpContext ctx) =>
{
    var userId = CurrentUserId(ctx);
    if (userId is null) return Results.Unauthorized();
    var user = await store.GetUserByIdAsync(userId.Value, ctx.RequestAborted);
    if (user is null) return Results.NotFound();
    user.TotpEnabled = false;
    user.TotpSecret = null;
    await store.UpsertUserAsync(user, ctx.RequestAborted);
    return Results.Ok(new { enabled = false });
}).RequireAuthorization("eh-authenticated");

// ── Auth: passkeys ───────────────────────────────────────────────────────────
auth.MapGet("/passkey/list", async (HttpContext ctx) =>
{
    var userId = CurrentUserId(ctx);
    if (userId is null) return Results.Unauthorized();
    var list = await store.GetPasskeysAsync(userId.Value, ctx.RequestAborted);
    return Results.Json(list.Select(p => new { p.Id, p.Name, p.CreatedAt, p.LastUsedAt, p.BackupEligible }));
}).RequireAuthorization("eh-authenticated");

auth.MapPost("/passkey/register/begin", async (HttpContext ctx) =>
{
    var userId = CurrentUserId(ctx);
    if (userId is null) return Results.Unauthorized();
    var user = await store.GetUserByIdAsync(userId.Value, ctx.RequestAborted);
    if (user is null) return Results.NotFound();
    var (flowId, optionsJson) = passkeys.BeginRegistration(user);
    return Results.Json(new { flowId, options = JsonNode.Parse(optionsJson) });
}).RequireAuthorization("eh-authenticated");

auth.MapPost("/passkey/register/complete", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<PasskeyCompleteRequest>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    try
    {
        var passkey = await passkeys.CompleteRegistrationAsync(body.FlowId, body.Credential,
            body.Name ?? "Passkey", ctx.RequestAborted);
        return Results.Json(new { ok = true, passkey.Id, passkey.Name });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("eh-authenticated");

auth.MapPost("/passkey/login/begin", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<PasskeyLoginBeginRequest>(cancellationToken: ctx.RequestAborted);
    User? user = null;
    if (!string.IsNullOrWhiteSpace(body?.Username))
        user = await store.GetUserByUsernameAsync(body.Username, ctx.RequestAborted);
    var (flowId, optionsJson) = passkeys.BeginLogin(user);
    return Results.Json(new { flowId, options = JsonNode.Parse(optionsJson) });
});

auth.MapPost("/passkey/login/complete", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<PasskeyCompleteRequest>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    try
    {
        var (user, _) = await passkeys.CompleteLoginAsync(body.FlowId, body.Credential, ctx.RequestAborted);
        if (!user.Approved || user.Role == UserRole.Pending || user.Disabled)
            return Results.Json(new { error = "Account pending approval." }, statusCode: StatusCodes.Status403Forbidden);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await store.UpsertUserAsync(user, ctx.RequestAborted);
        await ctx.SignInAsync("eh.cookie", authService.BuildPrincipal(user, "passkey"),
            new AuthenticationProperties { IsPersistent = true });
        return Results.Json(new { authenticated = true, role = user.Role.ToString() });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

auth.MapDelete("/passkey/{id:long}", async (HttpContext ctx, long id) =>
{
    var userId = CurrentUserId(ctx);
    if (userId is null) return Results.Unauthorized();
    var list = await store.GetPasskeysAsync(userId.Value, ctx.RequestAborted);
    if (list.All(p => p.Id != id)) return Results.NotFound();
    await store.DeletePasskeyAsync(id, ctx.RequestAborted);
    return Results.Ok(new { ok = true });
}).RequireAuthorization("eh-authenticated");

// ── Auth: OIDC challenge ─────────────────────────────────────────────────────
auth.MapGet("/oidc/providers", async (CancellationToken ct) =>
    Results.Json(new
    {
        google = (await store.GetConfigObjectAsync("oidc.google", new OidcProviderConfig(), ct)).Enabled,
        microsoft = (await store.GetConfigObjectAsync("oidc.microsoft", new OidcProviderConfig(), ct)).Enabled
    }));

auth.MapGet("/oidc/{provider}/challenge", (HttpContext ctx, string provider) =>
    Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, new[] { provider }));

// ── Auth: SAML 2.0 SP ────────────────────────────────────────────────────────
auth.MapGet("/saml2/metadata", () =>
{
    if (!samlSp.IsEnabled) return Results.NotFound(new { error = "SAML SP is not enabled." });
    return Results.Content(samlSp.BuildMetadataXml(), "application/samlmetadata+xml");
}).AllowAnonymous();

auth.MapGet("/saml2/login", async (HttpContext ctx) =>
{
    if (!samlSp.IsEnabled) return Results.NotFound(new { error = "SAML SP is not enabled." });
    var returnUrl = ctx.Request.Query["returnUrl"].FirstOrDefault() ?? "/";
    var acsUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api/auth/saml2/acs";
    var (url, requestId) = samlSp.BuildLoginRedirect(returnUrl, acsUrl);
    ctx.Response.Cookies.Append(EhConstants.SamlReplayCacheKey, requestId, new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.None, MaxAge = TimeSpan.FromMinutes(10)
    });
    return Results.Redirect(url);
}).AllowAnonymous();

auth.MapPost("/saml2/acs", async (HttpContext ctx) =>
{
    try
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        var result = samlSp.HandleAcs(true, query, form.AsNameValueCollection(), ctx.Request.QueryString.Value ?? "");
        var email = result.NameId
                    ?? result.Identity.Get(ClaimTypes.Email)
                    ?? result.Identity.Get(ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(email))
            return Results.BadRequest(new { error = "SAML assertion did not contain an email/name identifier." });

        var external = services.GetRequiredService<ExternalIdentityService>();
        var (user, isNew) = await external.ResolveAsync(IdentityProvider.Saml, email,
            result.Identity.Get(ClaimTypes.Name), result.NameId, ctx.RequestAborted);
        if (isNew)
            await notifier.NotifyAsync("user.pending", "New SAML user awaiting approval",
                $"{email} authenticated via SAML and requires approval.", new { email }, ctx.RequestAborted);
        if (user.Role == UserRole.Pending || !user.Approved || user.Disabled)
            return Results.Content("<h1>Account pending approval</h1>", "text/html");

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await store.UpsertUserAsync(user, ctx.RequestAborted);
        await ctx.SignInAsync("eh.cookie", authService.BuildPrincipal(user, "saml"),
            new AuthenticationProperties { IsPersistent = true });
        return Results.Redirect("/");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).AllowAnonymous();

// ── Auth providers configuration ─────────────────────────────────────────────
var security = app.MapGroup("/api/security").RequireAuthorization("eh-admin");

security.MapGet("/oidc/{provider}", async (string provider, CancellationToken ct) =>
{
    var cfg = await store.GetConfigObjectAsync("oidc." + provider, new OidcProviderConfig { Provider = provider }, ct);
    return Results.Json(new
    {
        cfg.Provider, cfg.Enabled, cfg.ClientId, cfg.Authority, cfg.DisplayName,
        hasSecret = !string.IsNullOrEmpty(cfg.ClientSecret)
    });
});

security.MapPut("/oidc/{provider}", async (string provider, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<OidcProviderConfig>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    body.Provider = provider;
    if (string.IsNullOrEmpty(body.ClientSecret))
    {
        var existing = await store.GetConfigObjectAsync("oidc." + provider, new OidcProviderConfig(), ctx.RequestAborted);
        body.ClientSecret = existing.ClientSecret;
    }
    else
    {
        body.ClientSecret = vault.EncryptString(body.ClientSecret);
    }
    await store.SetConfigObjectAsync("oidc." + provider, body, ctx.RequestAborted);
    return Results.Ok(new { saved = true, restartRequired = true });
});

security.MapGet("/saml/sp", async (CancellationToken ct) =>
    Results.Json(await store.GetConfigObjectAsync("saml.sp", new SamlSpConfig(), ct)));

security.MapPut("/saml/sp", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<SamlSpConfig>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    await store.SetConfigObjectAsync("saml.sp", body, ctx.RequestAborted);
    await samlSp.ReloadAsync(ctx.RequestAborted);
    return Results.Ok(new { saved = true });
});

security.MapGet("/saml/idp", async (CancellationToken ct) =>
    Results.Json(await store.GetConfigObjectAsync("saml.idp", new SamlIdpConfig(), ct)));

security.MapPut("/saml/idp", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<SamlIdpConfig>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    await store.SetConfigObjectAsync("saml.idp", body, ctx.RequestAborted);
    await samlIdp.ReloadAsync(ctx.RequestAborted);
    return Results.Ok(new { saved = true });
});

// ── SAML IdP endpoints ───────────────────────────────────────────────────────
app.MapGet("/api/saml/idp/metadata", () => Results.Content(samlIdp.GetMetadataXml(), "application/samlmetadata+xml"))
    .AllowAnonymous();

app.MapMethods("/api/saml/idp/sso", new[] { "GET", "POST" }, async (HttpContext ctx) =>
{
    if (!samlIdp.Config.Enabled) return Results.NotFound(new { error = "SAML IdP is not enabled." });
    var acs = ctx.Request.Query["acs"].FirstOrDefault()
              ?? ctx.Request.Form["acs"].FirstOrDefault();
    var user = ctx.User;
    if (user.Identity?.IsAuthenticated != true)
        return Results.Redirect("/api/auth/saml2/login?returnUrl=" + Uri.EscapeDataString(ctx.Request.Path));

    var email = user.FindFirst(EhClaims.Email)?.Value ?? user.FindFirst(EhClaims.Username)?.Value ?? "user";
    var attributes = new Dictionary<string, string>
    {
        ["email"] = email,
        ["name"] = user.FindFirst(ClaimTypes.Name)?.Value ?? email,
        ["role"] = user.FindFirst(EhClaims.Role)?.Value ?? "Viewer"
    };
    var responseXml = samlIdp.BuildSignedResponse(acs ?? samlIdp.Config.SsoUrl, null, null, email, attributes);
    var html = "<html><body onload=\"document.forms[0].submit()\">" +
               $"<form method=\"post\" action=\"{WebUtility.HtmlEncode(acs)}\">" +
               $"<input type=\"hidden\" name=\"SAMLResponse\" value=\"{responseXml}\"/>" +
               "</form></body></html>";
    return Results.Content(html, "text/html");
}).AllowAnonymous();

// ── Users ────────────────────────────────────────────────────────────────────
var users = app.MapGroup("/api/users").RequireAuthorization("eh-admin");
users.MapGet("/", async (CancellationToken ct) =>
    Results.Json((await store.GetUsersAsync(ct)).Select(u => new
    {
        u.Id, u.Username, u.Email, Role = u.Role.ToString(), Provider = u.Provider.ToString(),
        u.Approved, u.Disabled, u.TotpEnabled, u.CreatedAt, u.LastLoginAt, passkeys = 0
    })));
users.MapGet("/pending", async (CancellationToken ct) =>
    Results.Json((await store.GetUsersAsync(ct))
        .Where(u => u.Role == UserRole.Pending || !u.Approved)
        .Select(u => new { u.Id, u.Username, u.Email, Provider = u.Provider.ToString(), u.CreatedAt })));
users.MapPost("/{id:long}/approve", async (long id, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ApproveRequest>(cancellationToken: ctx.RequestAborted);
    var role = body?.Role ?? nameof(UserRole.Viewer);
    if (!Enum.TryParse<UserRole>(role, true, out var parsed)) parsed = UserRole.Viewer;
    var ok = await authService.ApproveUserAsync(id, parsed, ctx.RequestAborted);
    return ok ? Results.Ok(new { approved = true, role = parsed.ToString() }) : Results.NotFound();
});
users.MapDelete("/{id:long}", async (long id, HttpContext ctx) =>
{
    if (CurrentUserId(ctx) == id) return Results.BadRequest(new { error = "You cannot delete yourself." });
    await store.DeleteUserAsync(id, ctx.RequestAborted);
    return Results.Ok(new { deleted = true });
});
users.MapPut("/{id:long}", async (long id, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<UpdateUserRequest>(cancellationToken: ctx.RequestAborted);
    var user = await store.GetUserByIdAsync(id, ctx.RequestAborted);
    if (user is null) return Results.NotFound();
    if (!string.IsNullOrWhiteSpace(body?.Role))
    {
        if (!Enum.TryParse<UserRole>(body!.Role, true, out var role))
            return Results.BadRequest(new { error = "Invalid role." });
        if (CurrentUserId(ctx) == id && role != UserRole.Admin)
            return Results.BadRequest(new { error = "You cannot remove your own admin role." });
        user.Role = role;
        if (role != UserRole.Pending) user.Approved = true;
    }
    if (body?.Disabled is bool disabled)
    {
        if (CurrentUserId(ctx) == id && disabled)
            return Results.BadRequest(new { error = "You cannot disable your own account." });
        user.Disabled = disabled;
    }
    await store.UpsertUserAsync(user, ctx.RequestAborted);
    return Results.Ok(new { updated = true, role = user.Role.ToString(), disabled = user.Disabled });
});
users.MapPost("/", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<CreateUserRequest>(cancellationToken: ctx.RequestAborted);
    if (string.IsNullOrWhiteSpace(body?.Username) || string.IsNullOrWhiteSpace(body.Password))
        return Results.BadRequest(new { error = "Username and password are required." });
    if (body.Password.Length < 8)
        return Results.BadRequest(new { error = "Password must be at least 8 characters." });
    var username = body.Username.Trim();
    if (await store.GetUserByUsernameAsync(username, ctx.RequestAborted) is not null)
        return Results.Conflict(new { error = "A user with that name already exists." });
    if (!Enum.TryParse<UserRole>(body.Role ?? nameof(UserRole.Viewer), true, out var role)) role = UserRole.Viewer;
    var (hash, salt) = PasswordHasher.Hash(body.Password);
    var user = new User
    {
        Username = username,
        Email = body.Email?.Trim() ?? string.Empty,
        Role = role,
        Provider = IdentityProvider.Local,
        PasswordHash = hash,
        PasswordSalt = salt,
        Approved = true,
        Disabled = false
    };
    await store.UpsertUserAsync(user, ctx.RequestAborted);
    await store.AddAuditAsync(new AuditLog
    {
        Actor = ActorKind.WebUi, ActorDetail = ctx.User.FindFirst(EhClaims.Username)?.Value,
        Action = "user.create", Target = user.Username, Timestamp = DateTimeOffset.UtcNow
    }, ctx.RequestAborted);
    return Results.Json(new { user.Id, user.Username, role = user.Role.ToString() });
});

// ── MCP API keys ─────────────────────────────────────────────────────────────
var mcpKeysGroup = app.MapGroup("/api/mcp/keys").RequireAuthorization("eh-admin");
mcpKeysGroup.MapGet("/", async (CancellationToken ct) =>
    Results.Json((await store.GetMcpApiKeysAsync(ct)).Select(k => new
    {
        k.Id, k.Name, k.Prefix, k.Scopes, k.Enabled, k.CreatedAt, k.LastUsedAt
    })));
string? ActorOf(HttpContext ctx) => ctx.User.FindFirst(EhClaims.Username)?.Value;

mcpKeysGroup.MapPost("/", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<CreateKeyRequest>(cancellationToken: ctx.RequestAborted);
    if (string.IsNullOrWhiteSpace(body?.Name)) return Results.BadRequest(new { error = "Name required." });
    var (key, plaintext) = await mcpKeys.CreateAsync(body.Name, body.Scopes ?? "read,write", ctx.RequestAborted);
    await store.AddAuditAsync(new AuditLog
    {
        Actor = ActorKind.WebUi, ActorDetail = ActorOf(ctx), Action = "mcp.key.create",
        Target = key.Name, Detail = key.Scopes, Timestamp = DateTimeOffset.UtcNow
    }, ctx.RequestAborted);
    return Results.Json(new { key.Id, key.Name, key.Prefix, key.Scopes, token = plaintext });
});
// Update a key's name, scopes and/or enabled flag without revealing the secret.
mcpKeysGroup.MapPut("/{id:long}", async (long id, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<UpdateMcpKeyRequest>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    var key = await store.GetMcpApiKeyByIdAsync(id, ctx.RequestAborted);
    if (key is null) return Results.NotFound();
    if (!string.IsNullOrWhiteSpace(body.Name)) key.Name = body.Name.Trim();
    if (body.Enabled is not null) key.Enabled = body.Enabled.Value;
    if (!string.IsNullOrWhiteSpace(body.Scopes)) key.Scopes = body.Scopes.Trim();
    await store.UpsertMcpApiKeyAsync(key, ctx.RequestAborted);
    await store.AddAuditAsync(new AuditLog
    {
        Actor = ActorKind.WebUi, ActorDetail = ActorOf(ctx), Action = "mcp.key.update",
        Target = key.Name, Detail = key.Enabled ? key.Scopes : "disabled", Timestamp = DateTimeOffset.UtcNow
    }, ctx.RequestAborted);
    return Results.Json(new { updated = true, key.Id, key.Name, key.Scopes, key.Enabled });
});
// Rotate the secret: the new token is returned once and the old one stops working immediately.
mcpKeysGroup.MapPost("/{id:long}/rotate", async (long id, HttpContext ctx, CancellationToken ct) =>
{
    var rotated = await mcpKeys.RotateAsync(id, ct);
    if (rotated is null) return Results.NotFound();
    await store.AddAuditAsync(new AuditLog
    {
        Actor = ActorKind.WebUi, ActorDetail = ActorOf(ctx), Action = "mcp.key.rotate",
        Target = rotated.Value.Key.Name, Timestamp = DateTimeOffset.UtcNow
    }, ct);
    return Results.Json(new { id = rotated.Value.Key.Id, name = rotated.Value.Key.Name, token = rotated.Value.Plaintext });
});
mcpKeysGroup.MapDelete("/{id:long}", async (long id, HttpContext ctx, CancellationToken ct) =>
{
    var key = await store.GetMcpApiKeyByIdAsync(id, ct);
    await store.DeleteMcpApiKeyAsync(id, ct);
    await store.AddAuditAsync(new AuditLog
    {
        Actor = ActorKind.WebUi, ActorDetail = ActorOf(ctx), Action = "mcp.key.revoke",
        Target = key?.Name ?? id.ToString(), Timestamp = DateTimeOffset.UtcNow
    }, ct);
    return Results.Ok(new { deleted = true });
});

// ── System ───────────────────────────────────────────────────────────────────
var system = app.MapGroup("/api/system");
system.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    time = DateTimeOffset.UtcNow,
    appVersion,
    bootId
})).AllowAnonymous();

system.MapGet("/info", async (HttpContext ctx, CancellationToken ct) =>
{
    var hosts = await store.GetDockerHostsAsync(ct);
    var clusters = await store.GetKubeClustersAsync(ct);
    return Results.Json(new
    {
        version = "1.0.0",
        appVersion,
        bootId,
        portalDomain = portalConfigurator.PortalDomain,
        dataDirectory = EhConstants.DataDir,
        uptimeSeconds = Environment.TickCount64 / 1000,
        routes = services.GetRequiredService<DynamicProxyConfigProvider>().RouteCount,
        dockerHosts = hosts.Count,
        kubeClusters = clusters.Count,
        samlSpEnabled = samlSp.IsEnabled,
        samlIdpEnabled = samlIdp.Config.Enabled,
        authentication = ctx.User.Identity?.IsAuthenticated == true
    });
});

system.MapGet("/audit", async (HttpContext ctx, CancellationToken ct) =>
{
    var limit = int.TryParse(ctx.Request.Query["limit"], out var l) ? Math.Clamp(l, 1, 1000) : 200;
    return Results.Json(await store.GetAuditLogsAsync(limit, ct));
}).RequireAuthorization("eh-admin");

// Aggregate dashboard: overall totals for the selected host, a per-container table, and one
// panel per configured host. Results are cached briefly so a poll or a second viewer does not
// multiply the Docker stats fan-out.
system.MapGet("/dashboard", async (HttpContext ctx, CancellationToken ct) =>
{
    long? hostId = long.TryParse(ctx.Request.Query["hostId"].FirstOrDefault(), out var parsed) ? parsed : null;
    var fresh = ctx.Request.Query["fresh"].FirstOrDefault() is "1" or "true";
    var json = await DashboardAggregator.BuildAsync(dockerFactory, store, networkTracker, hostId, fresh, ct);
    return Results.Content(json, "application/json");
}).RequireAuthorization("eh-admin");

// Host network detail: 1h/24h traffic, packet drops/errors, TCP connection resets, interfaces and
// the sampled timeline. Served from the in-memory ring buffer the background tracker fills.
system.MapGet("/network", (HttpContext ctx) =>
{
    long? hostId = long.TryParse(ctx.Request.Query["hostId"].FirstOrDefault(), out var parsed) ? parsed : null;
    return Results.Json(networkTracker.GetHostNetworkSummary(hostId));
}).RequireAuthorization("eh-admin");

system.MapPost("/ssl/regenerate", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<RegenerateCertRequest>(cancellationToken: ctx.RequestAborted)
               ?? new RegenerateCertRequest();
    var cert = certificateManager.RegenerateServerCertificate(body.DnsNames, body.IpAddresses);
    await store.AddAuditAsync(new AuditLog
    {
        Actor = ActorKind.WebUi, ActorDetail = ctx.User.FindFirst(EhClaims.Username)?.Value,
        Action = "ssl.regenerate", Detail = cert.Subject, Timestamp = DateTimeOffset.UtcNow
    }, ctx.RequestAborted);
    return Results.Json(new { ok = true, subject = cert.Subject, thumbprint = cert.Thumbprint, expires = cert.NotAfter });
}).RequireAuthorization("eh-admin");

system.MapGet("/ssl/download", () =>
    Results.Text(certificateManager.CaCertificatePem, "application/x-pem-file", Encoding.UTF8)).RequireAuthorization("eh-admin");

// ── System settings (server identity, portal domain, certificate) ─────────────
system.MapGet("/settings", async (CancellationToken ct) =>
{
    var lanIp = await store.GetConfigAsync("portal.lanIp", ct);
    var persistedDomain = await store.GetConfigAsync("portal.domain", ct);
    var envDomain = Environment.GetEnvironmentVariable("EHDM_PORTAL_DOMAIN");
    var source = !string.IsNullOrWhiteSpace(persistedDomain)
        ? "database"
        : !string.IsNullOrWhiteSpace(envDomain) ? "EHDM_PORTAL_DOMAIN" : "default";
    var cert = certificateManager.GetServerCertificateInfo();
    return Results.Json(new
    {
        portalDomain = portalConfigurator.PortalDomain,
        portalDomainSource = source,
        portalDomainFromEnv = envDomain ?? string.Empty,
        lanIp = string.IsNullOrWhiteSpace(lanIp)
            ? Environment.GetEnvironmentVariable("EHDM_LAN_IP") ?? string.Empty
            : lanIp,
        dataDirectory = EhConstants.DataDir,
        version = "1.0.0",
        certificate = new { cert.Subject, cert.Thumbprint, cert.NotAfter, cert.DnsNames, cert.IpAddresses }
    });
}).RequireAuthorization("eh-admin");

system.MapPut("/settings", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<SystemSettingsRequest>(cancellationToken: ctx.RequestAborted)
               ?? new SystemSettingsRequest(null, null, false);
    var domain = body.PortalDomain?.Trim().Trim('.').ToLowerInvariant();
    if (!string.IsNullOrWhiteSpace(domain))
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(domain,
                "^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$"))
            return Results.BadRequest(new { error = "Enter a valid DNS name such as dock.lan or home.example.com." });
        await portalConfigurator.SetPortalDomainAsync(domain, ctx.RequestAborted);
        certificateManager.SetPortalDomain(domain);
    }

    var lanIp = body.LanIp?.Trim();
    if (!string.IsNullOrWhiteSpace(lanIp)) await store.SetConfigAsync("portal.lanIp", lanIp, ctx.RequestAborted);

    if (body.RegenerateCertificate && !string.IsNullOrWhiteSpace(domain))
    {
        var dns = new List<string> { domain, "*." + domain, "localhost" };
        var ips = new List<string> { "127.0.0.1", "::1" };
        if (!string.IsNullOrWhiteSpace(lanIp)) ips.Add(lanIp);
        certificateManager.RegenerateServerCertificate(dns, ips);
    }

    await store.AddAuditAsync(new AuditLog
    {
        Actor = ActorKind.WebUi, ActorDetail = ctx.User.FindFirst(EhClaims.Username)?.Value,
        Action = "settings.update", Target = portalConfigurator.PortalDomain,
        Detail = body.RegenerateCertificate ? "domain+certificate" : "domain",
        Timestamp = DateTimeOffset.UtcNow
    }, ctx.RequestAborted);

    var cert = certificateManager.GetServerCertificateInfo();
    return Results.Json(new
    {
        ok = true,
        portalDomain = portalConfigurator.PortalDomain,
        certificate = new { cert.Subject, cert.Thumbprint, cert.NotAfter, cert.DnsNames, cert.IpAddresses }
    });
}).RequireAuthorization("eh-admin");

// ── Let's Encrypt (ACME v2, http-01) ─────────────────────────────────────────
system.MapGet("/acme", async (CancellationToken ct) =>
{
    var config = await store.GetConfigObjectAsync("acme", new AcmeConfig(), ct);
    var cert = certificateManager.GetServerCertificateInfo();
    return Results.Json(new
    {
        config,
        directory = AcmeClient.DirectoryFor(config),
        active = certificateManager.IsAcmeActive,
        certificate = new { cert.Subject, cert.NotAfter, cert.DnsNames, cert.IpAddresses }
    });
}).RequireAuthorization("eh-admin");

system.MapPut("/acme", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<AcmeConfig>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    var existing = await store.GetConfigObjectAsync("acme", new AcmeConfig(), ctx.RequestAborted);
    body.LastError = existing.LastError;
    body.LastIssuedAt = existing.LastIssuedAt;
    await store.SetConfigObjectAsync("acme", body, ctx.RequestAborted);
    return Results.Json(new { saved = true, directory = AcmeClient.DirectoryFor(body) });
}).RequireAuthorization("eh-admin");

system.MapPost("/acme/issue", async (HttpContext ctx, CancellationToken ct) =>
{
    var config = await store.GetConfigObjectAsync("acme", new AcmeConfig(), ct);
    var domains = (config.Domain ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (domains.Length == 0) return Results.BadRequest(new { error = "Set the domain to certify first." });

    var result = await acme.IssueAsync(config, domains,
        Path.Combine(EhConstants.CertsDir, "acme-account.pem"), ct);
    config.LastError = result.Success ? null : result.Error;
    if (result.Success) config.LastIssuedAt = DateTimeOffset.UtcNow;
    await store.SetConfigObjectAsync("acme", config, ct);

    if (result.Success && result.Certificate is not null)
        certificateManager.InstallExternalServerCertificate(result.Certificate, result.Domains, result.ChainPem);

    await store.AddAuditAsync(new AuditLog
    {
        Actor = ActorKind.WebUi, ActorDetail = ctx.User.FindFirst(EhClaims.Username)?.Value,
        Action = "acme.issue", Target = config.Domain,
        Detail = result.Success ? "issued" : result.Error,
        Success = result.Success, Timestamp = DateTimeOffset.UtcNow
    }, ct);

    return Results.Json(new { ok = result.Success, domains = result.Domains, notAfter = result.NotAfter, error = result.Error });
}).RequireAuthorization("eh-admin");

// ── Stacks ───────────────────────────────────────────────────────────────────
var stacks = app.MapGroup("/api/stacks").RequireAuthorization("eh-admin");

stacks.MapPost("/convert/docker-run", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ConvertRequest>(cancellationToken: ctx.RequestAborted);
    if (string.IsNullOrWhiteSpace(body?.Source)) return Results.BadRequest(new { error = "command required" });
    var doc = new DockerRunConverter().Convert(body.Source);
    return Results.Json(new { yaml = composeParser.Serialize(doc), name = doc.Name });
});

stacks.MapPost("/convert/compose-to-k8s", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ConvertRequest>(cancellationToken: ctx.RequestAborted);
    ComposeDocument doc;
    if (!string.IsNullOrWhiteSpace(body?.Source))
        doc = composeParser.Parse(body.Source, "converted");
    else if (!string.IsNullOrWhiteSpace(body?.Name))
        doc = await compose.LoadAsync(body.Name, ctx.RequestAborted);
    else
        return Results.BadRequest(new { error = "yaml or stack name required" });
    return Results.Text(new K8sConverter().ToYaml(doc), "application/yaml");
});

// Schema/value validation for the YAML editor: parse errors carry a line/column, unknown keys are
// warnings, and enum-valued properties and port syntax are checked. Warnings never block a save.
stacks.MapPost("/validate", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ValidateRequest>(cancellationToken: ctx.RequestAborted);
    var validator = ctx.RequestServices.GetRequiredService<ComposeSchemaValidator>();
    var result = validator.Validate(body?.Yaml ?? "");
    return Results.Json(new
    {
        valid = result.Valid,
        name = result.Name,
        serviceCount = result.ServiceCount,
        issues = result.Issues
    });
});

stacks.MapPost("/import", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ImportRequest>(cancellationToken: ctx.RequestAborted);
    if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Url))
        return Results.BadRequest(new { error = "name and url required" });
    using var client = new HttpClient();
    var yaml = await client.GetStringAsync(body.Url, ctx.RequestAborted);
    await compose.SaveAsync(body.Name, yaml, ctx.RequestAborted);
    return Results.Json(new { imported = true, name = body.Name });
});

stacks.MapPost("/secrets", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<SecretRequest>(cancellationToken: ctx.RequestAborted);
    if (string.IsNullOrWhiteSpace(body?.Name)) return Results.BadRequest();
    await compose.SetSecretAsync(body.Name, body.Value ?? "", ctx.RequestAborted);
    return Results.Ok(new { saved = true });
});

stacks.MapGet("/", async (CancellationToken ct) =>
{
    var metas = (await store.GetStacksAsync(ct)).ToDictionary(m => m.Name);
    var result = compose.ListStackNames().Select(name =>
    {
        metas.TryGetValue(name, out var meta);
        return new
        {
            name,
            state = (meta?.State ?? StackState.Unknown).ToString(),
            autoUpdateEnabled = meta?.AutoUpdateEnabled ?? false,
            autoUpdateIntervalMinutes = meta?.AutoUpdateIntervalMinutes ?? 1440,
            pruneOldImages = meta?.PruneOldImages ?? true,
            rollbackOnFailure = meta?.RollbackOnFailure ?? true,
            lastDeployedAt = meta?.LastDeployedAt,
            lastUpdatedAt = meta?.LastUpdatedAt,
            updateCount = meta?.UpdateCount ?? 0,
            rollbackCount = meta?.RollbackCount ?? 0,
            lastError = meta?.LastError
        };
    });
    return Results.Json(result);
});

stacks.MapGet("/{name}", async (string name, CancellationToken ct) =>
{
    try
    {
        var path = compose.ComposePath(name);
        var yaml = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        var meta = await store.GetStackAsync(name, ct);
        return Results.Json(new { name, yaml, meta });
    }
    catch (FileNotFoundException) { return Results.NotFound(); }
});

// Per-stack operational stats: state, auto-update counters and a per-service container breakdown.
stacks.MapGet("/{name}/stats", async (string name, CancellationToken ct) =>
{
    ComposeDocument doc;
    try { doc = await compose.LoadAsync(name, ct); }
    catch (FileNotFoundException) { return Results.NotFound(); }

    var client = await dockerFactory.GetDefaultAsync(ct);
    var meta = await store.GetStackAsync(name, ct);
    var services = new List<object>();
    int running = 0, total = 0;
    double cpuTotal = 0, memTotal = 0, memLimitTotal = 0;
    long rxTotal = 0, txTotal = 0, sizeTotal = 0, restarts = 0;

    foreach (var svc in doc.Services.Values)
    {
        var containerName = svc.ContainerName ?? $"{name}-{svc.Name}";
        total++;
        var state = "missing";
        var status = "not created";
        var health = "none";
        var startedAt = (DateTimeOffset?)null;
        long restartCount = 0;
        double cpuPercent = 0, memoryMb = 0, memoryLimitMb = 0, memoryPeakMb = 0;
        long sizeRwBytes = 0;
        long rx = 0, tx = 0, rxErr = 0, txErr = 0, rxDrop = 0, txDrop = 0, pids = 0;
        int cores = 1;

        try
        {
            using var inspect = await client.InspectContainerAsync(containerName, ct);
            if (inspect.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (inspect.RootElement.TryGetProperty("State", out var st))
                {
                    if (st.TryGetProperty("Status", out var stx)) state = stx.GetString() ?? state;
                    if (st.TryGetProperty("Running", out var rn) && rn.GetBoolean()) { /* counted below */ }
                    if (st.TryGetProperty("RestartCount", out var rc) && rc.ValueKind == JsonValueKind.Number)
                        restartCount = rc.GetInt64();
                    if (st.TryGetProperty("StartedAt", out var sa) && sa.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(sa.GetString(), out var parsedStart))
                        startedAt = parsedStart;
                    if (st.TryGetProperty("Health", out var h) && h.ValueKind == JsonValueKind.Object
                        && h.TryGetProperty("Status", out var hst) && hst.ValueKind == JsonValueKind.String)
                        health = hst.GetString() ?? health;
                }
                if (inspect.RootElement.TryGetProperty("SizeRw", out var srw) && srw.ValueKind == JsonValueKind.Number)
                    sizeRwBytes = srw.GetInt64();
                var displayStatus = inspect.RootElement.TryGetProperty("State", out var stt)
                    && stt.TryGetProperty("Status", out var sttx) ? sttx.GetString() : null;
                _ = displayStatus;
            }
        }
        catch { /* container not created yet */ }

        if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
        {
            running++;
            try
            {
                await using var stream = await client.GetStatsStreamAsync(containerName, stream: false, ct);
                using var reader = new StreamReader(stream);
                var payload = await reader.ReadToEndAsync(ct);
                if (NetworkMetricsTracker.ParseExtendedMetric(containerName, payload) is { } m)
                {
                    cpuPercent = m.CpuPercent;
                    cores = m.CpuCores;
                    memoryMb = m.MemoryMb;
                    memoryLimitMb = m.MemoryLimitMb;
                    memoryPeakMb = m.MemoryPeakMb;
                    pids = m.PidsCurrent;
                    rx = m.NetworkRxBytes; tx = m.NetworkTxBytes;
                    rxErr = m.NetworkRxErrors; txErr = m.NetworkTxErrors;
                    rxDrop = m.NetworkRxDropped; txDrop = m.NetworkTxDropped;
                }
            }
            catch { /* stats unavailable */ }

            try
            {
                using var inspect = await client.InspectContainerAsync(containerName, ct);
                if (inspect.RootElement.TryGetProperty("State", out var st)
                    && st.TryGetProperty("Status", out var stx) && stx.ValueKind == JsonValueKind.String)
                    status = stx.GetString() ?? status;
            }
            catch { }
        }
        else if (state != "missing")
        {
            status = state;
        }

        cpuTotal += cpuPercent;
        memTotal += memoryMb;
        memLimitTotal += memoryLimitMb;
        rxTotal += rx; txTotal += tx;
        sizeTotal += sizeRwBytes;
        restarts += restartCount;

        services.Add(new
        {
            name = svc.Name,
            container = containerName,
            image = svc.Image,
            state,
            status,
            health,
            startedAt,
            restartCount,
            cpuPercent,
            cpuCores = cores,
            memoryMb,
            memoryLimitMb,
            memoryPeakMb,
            pidsCurrent = pids,
            sizeRwBytes,
            networkRxBytes = rx,
            networkTxBytes = tx,
            networkRxErrors = rxErr,
            networkTxErrors = txErr,
            networkRxDropped = rxDrop,
            networkTxDropped = txDrop,
            ports = svc.Ports
        });
    }

    return Results.Json(new
    {
        name,
        state = (meta?.State ?? StackState.Unknown).ToString(),
        services,
        totals = new
        {
            servicesTotal = total,
            servicesRunning = running,
            cpuPercent = Math.Round(cpuTotal, 2),
            memoryMb = Math.Round(memTotal, 1),
            memoryLimitMb = Math.Round(memLimitTotal, 1),
            networkRxBytes = rxTotal,
            networkTxBytes = txTotal,
            sizeRwBytes = sizeTotal,
            restartCount = restarts
        },
        autoUpdate = new
        {
            enabled = meta?.AutoUpdateEnabled ?? false,
            intervalMinutes = meta?.AutoUpdateIntervalMinutes ?? 1440,
            pruneOldImages = meta?.PruneOldImages ?? true,
            rollbackOnFailure = meta?.RollbackOnFailure ?? true,
            lastDeployedAt = meta?.LastDeployedAt,
            lastUpdatedAt = meta?.LastUpdatedAt,
            lastUpdateCheckAt = meta?.LastUpdateCheckAt,
            updateCount = meta?.UpdateCount ?? 0,
            rollbackCount = meta?.RollbackCount ?? 0,
            lastError = meta?.LastError
        }
    });
});

stacks.MapPut("/{name}", async (string name, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<SaveStackRequest>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    var doc = await compose.SaveAsync(name, body.Yaml, ctx.RequestAborted);
    if (body.AutoUpdateEnabled is not null)
    {
        var meta = await store.GetStackAsync(name, ctx.RequestAborted) ?? new StackMeta { Name = name };
        meta.AutoUpdateEnabled = body.AutoUpdateEnabled.Value;
        if (body.AutoUpdateIntervalMinutes is not null) meta.AutoUpdateIntervalMinutes = body.AutoUpdateIntervalMinutes.Value;
        if (body.PruneOldImages is not null) meta.PruneOldImages = body.PruneOldImages.Value;
        if (body.RollbackOnFailure is not null) meta.RollbackOnFailure = body.RollbackOnFailure.Value;
        await store.UpsertStackAsync(meta, ctx.RequestAborted);
    }
    return Results.Json(new { saved = true, services = doc.Services.Keys });
});

stacks.MapDelete("/{name}", async (string name, CancellationToken ct) =>
{
    await compose.DeleteAsync(name, ct);
    return Results.Ok(new { deleted = true });
});

stacks.MapPost("/{name}/deploy", async (string name, HttpContext ctx) =>
{
    var hostId = long.TryParse(ctx.Request.Query["hostId"], out var h) ? h : (long?)null;
    var result = await compose.DeployAsync(name, ActorKind.WebUi,
        ctx.User.FindFirst(EhClaims.Username)?.Value, hostId: hostId, ct: ctx.RequestAborted);
    await notifier.NotifyAsync("stack.deploy", $"Stack '{name}' deployed", result.Success ? "Deploy succeeded." : result.Error!,
        result, ctx.RequestAborted);
    return Results.Json(result);
});

stacks.MapPost("/{name}/action", async (string name, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<StackActionRequest>(cancellationToken: ctx.RequestAborted);
    switch (body?.Action)
    {
        case "start": await compose.StartStackAsync(name, ActorKind.WebUi, ctx.RequestAborted); break;
        case "stop": await compose.StopStackAsync(name, ActorKind.WebUi, ctx.RequestAborted); break;
        case "restart": await compose.RestartStackAsync(name, ActorKind.WebUi, ctx.RequestAborted); break;
        case "down": await compose.DownStackAsync(name, ActorKind.WebUi, ctx.RequestAborted); break;
        case "update":
            var updated = await compose.AutoUpdateAsync(name, ctx.RequestAborted);
            return Results.Json(updated);
        default: return Results.BadRequest(new { error = "Unknown action." });
    }
    return Results.Ok(new { ok = true });
});

stacks.MapPut("/{name}/autoupdate", async (string name, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<AutoUpdateRequest>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    var meta = await store.GetStackAsync(name, ctx.RequestAborted) ?? new StackMeta { Name = name };
    meta.AutoUpdateEnabled = body.Enabled;
    meta.AutoUpdateIntervalMinutes = body.IntervalMinutes;
    meta.PruneOldImages = body.PruneOldImages;
    meta.RollbackOnFailure = body.RollbackOnFailure;
    await store.UpsertStackAsync(meta, ctx.RequestAborted);
    return Results.Json(new { saved = true });
});

// ── Containers ───────────────────────────────────────────────────────────────
var containers = app.MapGroup("/api/containers").RequireAuthorization("eh-admin");

// Create and optionally start a container from an image with command, env and port mappings.
containers.MapPost("/", async (HttpContext ctx, CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<RunContainerRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Image))
        return Results.BadRequest(new { error = "Image is required." });

    var name = string.IsNullOrWhiteSpace(body.Name) ? "app-" + Guid.NewGuid().ToString("n")[..8] : body.Name.Trim();

    var exposed = new Dictionary<string, object>();
    var bindings = new Dictionary<string, object>();
    foreach (var spec in body.Ports ?? Array.Empty<string>())
    {
        var raw = spec.Trim();
        if (raw.Length == 0) continue;
        var protocol = "tcp";
        var slash = raw.LastIndexOf('/');
        if (slash >= 0)
        {
            protocol = raw[(slash + 1)..].ToLowerInvariant();
            raw = raw[..slash];
        }
        string hostPort;
        string containerPort;
        var colon = raw.IndexOf(':');
        if (colon >= 0)
        {
            hostPort = raw[..colon].Trim();
            containerPort = raw[(colon + 1)..].Trim();
        }
        else
        {
            hostPort = string.Empty;
            containerPort = raw;
        }
        if (!int.TryParse(containerPort, out var parsedContainer) || parsedContainer <= 0)
            return Results.BadRequest(new { error = $"Invalid port mapping: {spec}" });
        if (hostPort.Length > 0 && (!int.TryParse(hostPort, out var parsedHost) || parsedHost <= 0))
            return Results.BadRequest(new { error = $"Invalid host port: {spec}" });
        var key = $"{parsedContainer}/{protocol}";
        exposed[key] = new { };
        bindings[key] = new[] { new { HostIp = string.Empty, HostPort = hostPort } };
    }

    var environment = (body.Env ?? Array.Empty<string>())
        .Select(entry => entry.Trim())
        .Where(entry => entry.Length > 0)
        .ToArray();
    if (environment.Any(entry => !entry.Contains('=')))
        return Results.BadRequest(new { error = "Environment entries must be KEY=VALUE." });

    var restart = string.IsNullOrWhiteSpace(body.RestartPolicy) ? "unless-stopped" : body.RestartPolicy.Trim();
    if (restart is not ("no" or "always" or "unless-stopped" or "on-failure")) restart = "unless-stopped";

    var config = JsonSerializer.Serialize(new
    {
        Image = body.Image,
        Cmd = string.IsNullOrWhiteSpace(body.Command) ? null : ShellSplit(body.Command),
        Env = environment.Length > 0 ? environment : null,
        ExposedPorts = exposed.Count > 0 ? exposed : null,
        HostConfig = new
        {
            PortBindings = bindings.Count > 0 ? bindings : null,
            RestartPolicy = new { Name = restart }
        },
        Tty = false,
        OpenStdin = false,
        AttachStdout = false,
        AttachStderr = false
    });

    var client = await ClientForAsync(ctx, ct);
    try
    {
        var id = await client.CreateContainerAsync(name, config, ct);
        if (body.Start is not false) await client.StartContainerAsync(id, ct);
        await store.AddAuditAsync(new AuditLog
        {
            Actor = ActorKind.WebUi, ActorDetail = ctx.User.FindFirst(EhClaims.Username)?.Value,
            Action = "container.create", Target = name, Detail = body.Image, Timestamp = DateTimeOffset.UtcNow
        }, ct);
        return Results.Json(new { id, name });
    }
    catch (DockerApiException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

containers.MapGet("/", async (HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    using var doc = await client.ListContainersAsync(true, ct);
    return Results.Content(doc.RootElement.GetRawText(), "application/json");
});

containers.MapGet("/{id}", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    using var doc = await client.InspectContainerAsync(id, ct);
    if (doc.RootElement.ValueKind == JsonValueKind.Null) return Results.NotFound();
    return Results.Content(doc.RootElement.GetRawText(), "application/json");
});

containers.MapPost("/{id}/action", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ContainerActionRequest>(cancellationToken: ct);
    var client = await ClientForAsync(ctx, ct);
    switch (body?.Action)
    {
        case "start": await client.StartContainerAsync(id, ct); break;
        case "stop": await client.StopContainerAsync(id, 10, ct); break;
        case "restart": await client.RestartContainerAsync(id, 10, ct); break;
        case "kill": await client.KillContainerAsync(id, "SIGKILL", ct); break;
        case "pause": await client.PauseContainerAsync(id, ct); break;
        case "unpause": await client.UnpauseContainerAsync(id, ct); break;
        case "remove": await client.RemoveContainerAsync(id, true, true, ct); break;
        default: return Results.BadRequest(new { error = "Unknown action." });
    }
    await store.AddAuditAsync(new AuditLog
    {
        Actor = ActorKind.WebUi, ActorDetail = ctx.User.FindFirst(EhClaims.Username)?.Value,
        Action = "container." + body!.Action, Target = id, Timestamp = DateTimeOffset.UtcNow
    }, ct);
    return Results.Ok(new { ok = true });
});

containers.MapGet("/{id}/logs", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    var tail = int.TryParse(ctx.Request.Query["tail"], out var t) ? t : 300;
    var timestamps = ctx.Request.Query["timestamps"].FirstOrDefault() is not "0" and not "false";
    return TypedResults.ServerSentEvents(StreamingEndpoints.LogLinesAsync(client, id, tail, timestamps, ct));
});

// One-shot extended metrics plus the container's sampled traffic timeline (used by the inspector).
containers.MapGet("/{id}/metrics", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    string? payload = null;
    try
    {
        await using var stream = await client.GetStatsStreamAsync(id, stream: false, ct);
        using var reader = new StreamReader(stream);
        payload = await reader.ReadToEndAsync(ct);
    }
    catch
    {
        /* a stopped container may not report stats */
    }
    var stats = payload is null ? null : NetworkMetricsTracker.ParseExtendedMetric(id, payload);
    var network = networkTracker.GetContainerNetworkHistory(id);
    return Results.Json(new { stats, network });
});

// Container network timeline only.
containers.MapGet("/{id}/network", (string id) =>
    Results.Json(networkTracker.GetContainerNetworkHistory(id)));

containers.MapGet("/{id}/stats", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    return TypedResults.ServerSentEvents(StreamingEndpoints.StatsSamplesAsync(client, id, ct));
});

containers.MapGet("/{id}/archive", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    var path = ctx.Request.Query["path"].FirstOrDefault() ?? "/";
    var entries = await client.ListArchiveAsync(id, path, ct);
    return Results.Json(entries);
});

containers.MapGet("/{id}/archive/file", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    var path = ctx.Request.Query["path"].FirstOrDefault();
    if (string.IsNullOrEmpty(path)) return Results.BadRequest(new { error = "path required" });
    var bytes = await client.ReadArchiveFileAsync(id, path, ct);
    return Results.File(bytes, "application/octet-stream", Path.GetFileName(path));
});

containers.MapPut("/{id}/archive", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    var path = ctx.Request.Query["path"].FirstOrDefault() ?? "/";
    var fileName = ctx.Request.Query["fileName"].FirstOrDefault();
    if (string.IsNullOrEmpty(fileName)) return Results.BadRequest(new { error = "fileName required" });
    await client.PutArchiveAsync(id, path, fileName, ctx.Request.Body, ct);
    return Results.Ok(new { uploaded = true });
});

containers.MapPost("/{id}/export-compose", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    using var doc = await client.InspectContainerAsync(id, ct);
    if (doc.RootElement.ValueKind == JsonValueKind.Null) return Results.NotFound();
    var composeDoc = exporter.ToDocument(doc.RootElement, null, null);
    return Results.Json(new { yaml = composeParser.Serialize(composeDoc), name = composeDoc.Name });
});

containers.MapPost("/{id}/cve-scan", async (string id, CancellationToken ct) =>
{
    var findings = await cveScanner.ScanAsync(id, ct);
    if (findings.Count > 0)
        await notifier.NotifyAsync("cve.alert", "Vulnerabilities detected",
            $"{findings.Count} findings for container {id}.", new { container = id, count = findings.Count }, ct);
    return Results.Json(findings);
});

// ── Images & registries ──────────────────────────────────────────────────────
var images = app.MapGroup("/api/images").RequireAuthorization("eh-admin");
images.MapGet("/", async (HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    using var doc = await client.ListImagesAsync(ct);
    return Results.Content(doc.RootElement.GetRawText(), "application/json");
});
images.MapPost("/prune", async (HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    var dangling = ctx.Request.Query["dangling"].FirstOrDefault() == "true";
    using var doc = await client.PruneImagesAsync(dangling, ct);
    return Results.Content(doc.RootElement.GetRawText(), "application/json");
});
images.MapPost("/pull", async (HttpContext ctx, CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<PullRequest>(cancellationToken: ct);
    if (string.IsNullOrWhiteSpace(body?.Image)) return Results.BadRequest(new { error = "image required" });
    var client = await ClientForAsync(ctx, ct);
    Registry? registry = body.RegistryId is not null
        ? await store.GetRegistryByIdAsync(body.RegistryId.Value, ct) : null;
    return TypedResults.ServerSentEvents(StreamingEndpoints.PullProgressAsync(client, body.Image, body.Tag ?? "latest",
        registry, ct));
});
images.MapDelete("/{id}", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var client = await ClientForAsync(ctx, ct);
    await client.RemoveImageAsync(id, true, ct);
    return Results.Ok(new { removed = true });
});

var registries = app.MapGroup("/api/registries").RequireAuthorization("eh-admin");
registries.MapGet("/", async (CancellationToken ct) =>
    Results.Json((await store.GetRegistriesAsync(ct)).Select(r => new
    {
        r.Id, r.Name, r.Url, r.Username, r.IgnoreSslErrors, r.IsDefault, hasPassword = !string.IsNullOrEmpty(r.EncryptedPassword)
    })));
registries.MapPost("/", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<RegistryRequest>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    var registry = new Registry
    {
        Id = body.Id ?? 0, Name = body.Name, Url = body.Url, Username = body.Username,
        IgnoreSslErrors = body.IgnoreSslErrors, IsDefault = body.IsDefault
    };
    if (!string.IsNullOrEmpty(body.Password)) registry.EncryptedPassword = vault.EncryptString(body.Password);
    else if (body.Id is not null)
    {
        var existing = await store.GetRegistryByIdAsync(body.Id.Value, ctx.RequestAborted);
        registry.EncryptedPassword = existing?.EncryptedPassword;
    }
    await store.UpsertRegistryAsync(registry, ctx.RequestAborted);
    return Results.Json(new { registry.Id });
});
registries.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
{
    await store.DeleteRegistryAsync(id, ct);
    return Results.Ok(new { deleted = true });
});

// ── Docker hosts ─────────────────────────────────────────────────────────────
var hosts = app.MapGroup("/api/hosts").RequireAuthorization("eh-admin");
hosts.MapGet("/", async (CancellationToken ct) =>
    Results.Json((await store.GetDockerHostsAsync(ct)).Select(h => new
    {
        h.Id, h.Name, Kind = h.Kind.ToString(), h.Endpoint, h.Enabled, h.WakeOnLanMac,
        h.LastSeenAt, h.LastError
    })));
hosts.MapPost("/", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<DockerHostRequest>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    var host = new DockerHost
    {
        Id = body.Id ?? 0, Name = body.Name, Endpoint = body.Endpoint,
        Kind = Enum.TryParse<DockerHostKind>(body.Kind, true, out var k) ? k : DockerHostKind.UnixSocket,
        Enabled = body.Enabled, WakeOnLanMac = body.WakeOnLanMac, WakeOnLanBroadcast = body.WakeOnLanBroadcast,
        ClientCertPem = body.ClientCertPem, ClientKeyPem = body.ClientKeyPem, CaCertPem = body.CaCertPem
    };
    await store.UpsertDockerHostAsync(host, ctx.RequestAborted);
    dockerFactory.Invalidate(host.Id);
    return Results.Json(new { host.Id });
});
hosts.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
{
    await store.DeleteDockerHostAsync(id, ct);
    dockerFactory.Invalidate(id);
    return Results.Ok(new { deleted = true });
});
hosts.MapPost("/{id:long}/test", async (long id, CancellationToken ct) =>
{
    var client = await dockerFactory.GetAsync(id, ct);
    var ok = await client.PingAsync(ct);
    return Results.Json(new { online = ok });
});
hosts.MapPost("/{id:long}/wake", async (long id, CancellationToken ct) =>
{
    var host = await store.GetDockerHostByIdAsync(id, ct);
    if (host?.WakeOnLanMac is null) return Results.BadRequest(new { error = "No Wake-on-LAN MAC configured." });
    var ok = await wol.WakeAsync(host.WakeOnLanMac, host.WakeOnLanBroadcast ?? "255.255.255.255", host.WakeOnLanPort, ct);
    return Results.Json(new { sent = ok });
});
hosts.MapPost("/agent/enroll", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<EnrollRequest>(cancellationToken: ctx.RequestAborted);
    if (string.IsNullOrWhiteSpace(body?.Name)) return Results.BadRequest(new { error = "name required" });
    var (cert, certPem, keyPem) = certificateManager.IssueClientCertificate(body.Name);
    var port = body.Port ?? 2376;
    var composeSnippet = EhAgent.BuildAgentCompose(body.Name, certPem, keyPem, certificateManager.CaCertificatePem, port);
    return Results.Json(new
    {
        commonName = body.Name,
        thumbprint = cert.Thumbprint,
        clientCertificatePem = certPem,
        clientKeyPem = keyPem,
        caCertificatePem = certificateManager.CaCertificatePem,
        endpoint = $"tcp://{body.Host ?? "agent-host"}:{port}",
        compose = composeSnippet
    });
});

// ── Kubernetes clusters ──────────────────────────────────────────────────────
var clusters = app.MapGroup("/api/clusters").RequireAuthorization("eh-admin");
clusters.MapGet("/", async (CancellationToken ct) =>
    Results.Json((await store.GetKubeClustersAsync(ct)).Select(c => new
    {
        c.Id, c.Name, c.Enabled, c.ServerUrl, c.Context, c.LastSeenAt, c.LastError
    })));
clusters.MapPost("/", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<KubeClusterRequest>(cancellationToken: ctx.RequestAborted);
    if (body is null) return Results.BadRequest();
    var cluster = new KubeCluster
    {
        Id = body.Id ?? 0, Name = body.Name, Enabled = body.Enabled,
        KubeConfigYaml = body.KubeConfigYaml, Token = body.Token,
        ClientCertPem = body.ClientCertPem, ClientKeyPem = body.ClientKeyPem,
        CaCertPem = body.CaCertPem, ServerUrl = body.ServerUrl, Context = body.Context
    };
    await store.UpsertKubeClusterAsync(cluster, ctx.RequestAborted);
    return Results.Json(new { cluster.Id });
});
clusters.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
{
    await store.DeleteKubeClusterAsync(id, ct);
    return Results.Ok(new { deleted = true });
});
clusters.MapPost("/{id:long}/test", async (long id, CancellationToken ct) =>
{
    var cluster = await store.GetKubeClusterByIdAsync(id, ct);
    if (cluster is null) return Results.NotFound();
    return Results.Json(new { online = await kube.TestAsync(cluster, ct) });
});
clusters.MapGet("/{id:long}/pods", async (long id, HttpContext ctx, CancellationToken ct) =>
{
    var cluster = await store.GetKubeClusterByIdAsync(id, ct) ?? throw new InvalidOperationException("Cluster not found");
    return Results.Json(await kube.ListPodsAsync(cluster, ctx.Request.Query["namespace"].FirstOrDefault(), ct));
});
clusters.MapGet("/{id:long}/deployments", async (long id, HttpContext ctx, CancellationToken ct) =>
{
    var cluster = await store.GetKubeClusterByIdAsync(id, ct) ?? throw new InvalidOperationException("Cluster not found");
    return Results.Json(await kube.ListDeploymentsAsync(cluster, ctx.Request.Query["namespace"].FirstOrDefault(), ct));
});
clusters.MapGet("/{id:long}/services", async (long id, HttpContext ctx, CancellationToken ct) =>
{
    var cluster = await store.GetKubeClusterByIdAsync(id, ct) ?? throw new InvalidOperationException("Cluster not found");
    return Results.Json(await kube.ListServicesAsync(cluster, ctx.Request.Query["namespace"].FirstOrDefault(), ct));
});
clusters.MapGet("/{id:long}/httproutes", async (long id, HttpContext ctx, CancellationToken ct) =>
{
    var cluster = await store.GetKubeClusterByIdAsync(id, ct) ?? throw new InvalidOperationException("Cluster not found");
    return Results.Json(await kube.ListHttpRoutesAsync(cluster, ctx.Request.Query["namespace"].FirstOrDefault(), ct));
});
clusters.MapGet("/{id:long}/namespaces", async (long id, CancellationToken ct) =>
{
    var cluster = await store.GetKubeClusterByIdAsync(id, ct) ?? throw new InvalidOperationException("Cluster not found");
    return Results.Json(await kube.ListNamespacesAsync(cluster, ct));
});
clusters.MapPost("/{id:long}/apply", async (long id, HttpContext ctx, CancellationToken ct) =>
{
    var cluster = await store.GetKubeClusterByIdAsync(id, ct) ?? throw new InvalidOperationException("Cluster not found");
    var body = await ctx.Request.ReadFromJsonAsync<ApplyManifestRequest>(cancellationToken: ct);
    if (string.IsNullOrWhiteSpace(body?.Yaml)) return Results.BadRequest(new { error = "yaml required" });
    var applied = await kube.ApplyManifestAsync(cluster, body.Yaml, body.Namespace ?? "default", ct);
    return Results.Json(new { applied });
});
clusters.MapPost("/{id:long}/deployments/{ns}/{name}/restart", async (long id, string ns, string name, CancellationToken ct) =>
{
    var cluster = await store.GetKubeClusterByIdAsync(id, ct) ?? throw new InvalidOperationException("Cluster not found");
    return Results.Json(new { ok = await kube.RestartDeploymentAsync(cluster, name, ns, ct) });
});
clusters.MapPost("/{id:long}/deployments/{ns}/{name}/scale", async (long id, string ns, string name, HttpContext ctx, CancellationToken ct) =>
{
    var cluster = await store.GetKubeClusterByIdAsync(id, ct) ?? throw new InvalidOperationException("Cluster not found");
    var body = await ctx.Request.ReadFromJsonAsync<ScaleRequest>(cancellationToken: ct);
    return Results.Json(new { ok = await kube.ScaleDeploymentAsync(cluster, name, ns, body?.Replicas ?? 1, ct) });
});
clusters.MapGet("/{id:long}/pods/{ns}/{name}/logs", async (long id, string ns, string name, HttpContext ctx, CancellationToken ct) =>
{
    var cluster = await store.GetKubeClusterByIdAsync(id, ct) ?? throw new InvalidOperationException("Cluster not found");
    var podTimestamps = ctx.Request.Query["timestamps"].FirstOrDefault() is not "0" and not "false";
    var logs = await kube.GetPodLogsAsync(cluster, name, ns, ctx.Request.Query["container"].FirstOrDefault(), 500, ct, podTimestamps);
    return Results.Text(logs, "text/plain");
});

// ── Portal ───────────────────────────────────────────────────────────────────
var portal = app.MapGroup("/api/portal");
portal.MapGet("/apps", async (CancellationToken ct) => Results.Json(await store.GetPortalAppsAsync(ct)))
    .RequireAuthorization("eh-admin");
portal.MapPut("/apps", async (HttpContext ctx) =>
{
    var app = await ctx.Request.ReadFromJsonAsync<PortalApp>(cancellationToken: ctx.RequestAborted);
    if (app is null || string.IsNullOrWhiteSpace(app.Subdomain)) return Results.BadRequest();
    app.Manual = true;
    await store.UpsertPortalAppAsync(app, ctx.RequestAborted);
    await portalConfigurator.RebuildAsync(ctx.RequestAborted);
    return Results.Json(new { app.Id, app.Subdomain });
}).RequireAuthorization("eh-admin");
portal.MapDelete("/apps/{subdomain}", async (string subdomain, CancellationToken ct) =>
{
    await store.DeletePortalAppAsync(subdomain, ct);
    await portalConfigurator.RebuildAsync(ct);
    return Results.Ok(new { deleted = true });
}).RequireAuthorization("eh-admin");
// Persists a manual order. The list is the subdomains in display order; each gets SortOrder = index.
// Reordering never flips an auto-discovered app to Manual.
portal.MapPost("/apps/reorder", async (HttpContext ctx, CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ReorderRequest>(cancellationToken: ct);
    if (body?.Order is null || body.Order.Count == 0)
        return Results.BadRequest(new { error = "order required" });
    var updated = 0;
    for (var index = 0; index < body.Order.Count; index++)
    {
        var app = await store.GetPortalAppBySubdomainAsync(body.Order[index], ct);
        if (app is null) continue;
        app.SortOrder = index;
        await store.UpsertPortalAppAsync(app, ct);
        updated++;
    }
    await portalConfigurator.RebuildAsync(ct);
    return Results.Ok(new { reordered = updated });
}).RequireAuthorization("eh-admin");
portal.MapPost("/refresh", async (CancellationToken ct) =>
{
    var discovery = services.GetRequiredService<PortalDiscoveryService>();
    await discovery.ReconcileAsync(ct);
    return Results.Ok(new { refreshed = true });
}).RequireAuthorization("eh-admin");

portal.MapGet("/landing", async (HttpContext ctx, CancellationToken ct) =>
{
    var isAdmin = ctx.User.IsInRole(nameof(UserRole.Admin));
    var isAuthed = ctx.User.Identity?.IsAuthenticated == true;
    // Hidden apps stay proxied (see PortalProxyConfigurator); they are only omitted from the
    // list unless an administrator asks for them via the Portal "Show hidden" toggle.
    var includeHidden = isAdmin
        && ctx.Request.Query["includeHidden"].FirstOrDefault() is "1" or "true";
    var apps = (await store.GetPortalAppsAsync(ct))
        .Where(a => includeHidden || !a.Hidden)
        .ToList();
    var visible = apps.Where(a => a.Auth switch
    {
        PortalAuthMode.Admin => isAdmin,
        PortalAuthMode.Sso => isAuthed || a.Auth == PortalAuthMode.Sso,
        _ => true
    });
    var selfHost = Environment.MachineName;
    var selfOverride = Environment.GetEnvironmentVariable("EHDM_SELF_CONTAINER");
    string UrlFor(PortalApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.ExternalUrl)) return app.ExternalUrl;
        var isSelf =
            (selfOverride is { Length: > 0 } &&
             (string.Equals(app.ContainerName, selfOverride, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(app.Subdomain, selfOverride, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrWhiteSpace(app.ContainerId) &&
             app.ContainerId.StartsWith(selfHost, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(app.ContainerName, selfHost, StringComparison.OrdinalIgnoreCase);
        // DockManager itself is served at the root of the portal domain, not on a subdomain.
        return isSelf
            ? $"https://{portalConfigurator.PortalDomain}"
            : $"https://{app.Subdomain}.{portalConfigurator.PortalDomain}";
    }
    return Results.Json(new
    {
        domain = portalConfigurator.PortalDomain,
        authenticated = isAuthed,
        apps = visible.Select(a => new
        {
            a.Id, a.Subdomain, a.Name, a.Icon, a.Category, Auth = a.Auth.ToString(),
            Autoscale = a.Autoscale.ToString(), a.TargetPort, a.ContainerName, a.ExternalUrl,
            a.SortOrder, a.Hidden, a.Manual, a.AutoDiscovered, url = UrlFor(a)
        })
    });
}).AllowAnonymous();

// ── Webhooks ─────────────────────────────────────────────────────────────────
var webhooks = app.MapGroup("/api/webhooks").RequireAuthorization("eh-admin");
webhooks.MapGet("/", async (CancellationToken ct) =>
    Results.Json((await store.GetWebhooksAsync(ct)).Select(w => new
    {
        w.Id, w.Name, Kind = w.Kind.ToString(), w.Url, w.ChatId, w.Enabled, w.Events,
        hasSecret = !string.IsNullOrEmpty(w.Secret), w.LastFiredAt, w.LastStatus
    })));
webhooks.MapPost("/", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Webhook>(cancellationToken: ctx.RequestAborted);
    if (body is null || string.IsNullOrWhiteSpace(body.Url)) return Results.BadRequest();
    if (!string.IsNullOrEmpty(body.Secret)) body.Secret = vault.EncryptString(body.Secret);
    await store.UpsertWebhookAsync(body, ctx.RequestAborted);
    return Results.Json(new { body.Id });
});
webhooks.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
{
    await store.DeleteWebhookAsync(id, ct);
    return Results.Ok(new { deleted = true });
});
webhooks.MapPost("/{id:long}/test", async (long id, CancellationToken ct) =>
{
    var hooks = await store.GetWebhooksAsync(ct);
    var hook = hooks.FirstOrDefault(h => h.Id == id);
    if (hook is null) return Results.NotFound();
    // Test with the outbound secret decrypted in memory only.
    var result = await notifier.TestAsync(hook, ct);
    return Results.Json(new { result.LastStatus });
});

// ── Live provenance event feed (SSE) ─────────────────────────────────────────
app.MapGet("/api/events/stream", (HttpContext ctx, CancellationToken ct) =>
    TypedResults.ServerSentEvents(StreamingEndpoints.AuditEventsAsync(store, ct)))
    .RequireAuthorization("eh-authenticated");

// ── WebSocket: Docker exec ───────────────────────────────────────────────────
app.Map("/ws/exec/{id}", async (HttpContext ctx, string id) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync(
        new WebSocketAcceptContext { DangerousEnableCompression = true });
    var shell = ctx.Request.Query["cmd"].FirstOrDefault();
    var command = string.IsNullOrWhiteSpace(shell) ? new[] { "/bin/sh" } : ShellSplit(shell);
    var label = string.Join(' ', command);
    try
    {
        var client = await ClientForAsync(ctx, ctx.RequestAborted);
        var session = await client.CreateExecAsync(id, command, true, ct: ctx.RequestAborted);
        await using var hijack = await client.StartExecAsync(session, ctx.RequestAborted);
        await WebSocketBridge.BridgeAsync(socket, hijack, ctx.RequestAborted, label);
    }
    catch (Exception ex)
    {
        await WebSocketBridge.TrySendTextAsync(socket, BuildExecError(ex, label));
        await WebSocketBridge.CloseAsync(socket, 4001, "exec-failed");
    }
}).RequireAuthorization("eh-admin");

// ── WebSocket: Kubernetes pod exec ───────────────────────────────────────────
app.Map("/ws/k8s-exec/{clusterId:long}/{ns}/{pod}", async (HttpContext ctx, long clusterId, string ns, string pod) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    var cluster = await store.GetKubeClusterByIdAsync(clusterId, ctx.RequestAborted);
    if (cluster is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
    var container = ctx.Request.Query["container"].FirstOrDefault() ?? "";
    var shell = ctx.Request.Query["cmd"].FirstOrDefault();
    var command = string.IsNullOrWhiteSpace(shell) ? new[] { "/bin/sh" } : ShellSplit(shell);
    using var upstream = await kube.ExecAsync(cluster, pod, ns, container, command, ctx.RequestAborted);
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = true });
    await WebSocketBridge.BridgeK8sAsync(socket, upstream, ctx.RequestAborted);
}).RequireAuthorization("eh-admin");

// ── MCP server endpoint ──────────────────────────────────────────────────────
app.MapGet("/mcp", () => Results.Json(new
{
    server = "eh.DockManager",
    protocol = "JSON-RPC 2.0 Streamable HTTP",
    transport = "POST /mcp with Authorization: Bearer ehdm_..."
}));

app.MapPost("/mcp", async (HttpContext ctx, CancellationToken ct) =>
{
    var bearer = ctx.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
    var key = await mcpKeys.ValidateAsync(bearer ?? string.Empty, ct);
    if (key is null) return Results.Json(new { error = "invalid_token" }, statusCode: StatusCodes.Status401Unauthorized);

    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    var response = await mcpServer.HandleAsync(doc.RootElement, key, ctx.RequestAborted);
    return response is null ? Results.Accepted() : Results.Content(response.ToJsonString(), "application/json");
});

app.MapReverseProxy();

await app.RunAsync();

// ──────────────────────────────────────────────────────────────────────────────
// Startup helpers
// ──────────────────────────────────────────────────────────────────────────────
async Task EnsureLocalDockerHostAsync()
{
    var existing = await store.GetDockerHostsAsync();
    if (existing.Count > 0) return;
    await store.UpsertDockerHostAsync(new DockerHost
    {
        Name = "local",
        Kind = DockerHostKind.UnixSocket,
        Endpoint = "unix:///var/run/docker.sock",
        Enabled = true
    });
    startupLog.LogInformation("Seeded the local Docker host (unix:///var/run/docker.sock).");
}

static string[] ShellSplit(string command) => ComposeStackService.ShellSplit(command).ToArray();

/// <summary>A version token derived from the built UI entry document, so a redeploy that ships new
/// static resources is detectable by an open client (which reloads and refreshes the worker).</summary>
static string ComputeAppVersion(string webRoot)
{
    try
    {
        var index = Path.Combine(webRoot, "index.html");
        if (!File.Exists(index)) return "1.0.0";
        var html = File.ReadAllText(index);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(html));
        return "1.0.0+" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
    catch
    {
        return "1.0.0";
    }
}

static string BuildExecError(Exception ex, string label)
{
    var raw = ex.Message ?? string.Empty;
    var noShell = raw.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("executable file not found", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("stat /bin", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("exec: \"", StringComparison.OrdinalIgnoreCase);
    var sb = new StringBuilder();
    sb.Append("\r\n\x1b[33m[exec] ")
      .Append(noShell ? "No usable shell was found in this container." : $"Could not start `{label}`.")
      .Append("\x1b[0m\r\n");
    if (noShell)
    {
        sb.Append("This image may be distroless and ship no shell (for example the DockManager container itself).\r\n");
        sb.Append("Try another interpreter from the Exec menu in Inspect: \x1b[36m/bin/bash\x1b[0m, \x1b[36m/bin/ash\x1b[0m, ")
          .Append("\x1b[36m/bin/busybox sh\x1b[0m.\r\n");
        sb.Append("If none exist, use \x1b[36mInspect \u2192 Files\x1b[0m to browse and download files instead.\r\n");
    }
    if (raw.Length > 0)
    {
        var trimmed = raw.Length > 500 ? raw[..500] + "\u2026" : raw;
        sb.Append("\x1b[90m").Append(trimmed.Replace("\r", string.Empty)).Append("\x1b[0m\r\n");
    }
    return sb.ToString();
}

// ──────────────────────────────────────────────────────────────────────────────
// Request/response DTOs
// ──────────────────────────────────────────────────────────────────────────────
internal sealed record LoginRequest(string Username, string Password, string? TotpCode);
internal sealed record TotpRequest(string Code);
internal sealed record PasskeyCompleteRequest(string FlowId, string Credential, string? Name);
internal sealed record PasskeyLoginBeginRequest(string? Username);
internal sealed record ApproveRequest(string? Role);
internal sealed record UpdateUserRequest(string? Role, bool? Disabled);
internal sealed record CreateUserRequest(string Username, string Password, string? Email, string? Role);
internal sealed record SystemSettingsRequest(string? PortalDomain, string? LanIp, bool RegenerateCertificate);
internal sealed record CreateKeyRequest(string Name, string? Scopes);
internal sealed record UpdateMcpKeyRequest(string? Name, bool? Enabled, string? Scopes);
internal sealed record RegenerateCertRequest
{
    public List<string>? DnsNames { get; init; }
    public List<string>? IpAddresses { get; init; }
}
internal sealed record ConvertRequest(string? Source, string? Name);
internal sealed record ImportRequest(string Name, string Url);
internal sealed record ValidateRequest(string? Yaml);
internal sealed record SecretRequest(string Name, string? Value);
internal sealed record SaveStackRequest
{
    public string Yaml { get; init; } = "";
    public bool? AutoUpdateEnabled { get; init; }
    public int? AutoUpdateIntervalMinutes { get; init; }
    public bool? PruneOldImages { get; init; }
    public bool? RollbackOnFailure { get; init; }
}
internal sealed record StackActionRequest(string Action);
internal sealed record AutoUpdateRequest(bool Enabled, int IntervalMinutes, bool PruneOldImages, bool RollbackOnFailure);
internal sealed record ContainerActionRequest(string Action);
internal sealed record RunContainerRequest(string Image, string? Name, string? Command, string[]? Env, string[]? Ports, string? RestartPolicy, bool? Start);
internal sealed record PullRequest(string Image, string? Tag, long? RegistryId);
internal sealed record RegistryRequest(long? Id, string Name, string Url, string? Username, string? Password,
    bool IgnoreSslErrors, bool IsDefault);
internal sealed record DockerHostRequest(long? Id, string Name, string Endpoint, string Kind, bool Enabled,
    string? WakeOnLanMac, string? WakeOnLanBroadcast, string? ClientCertPem, string? ClientKeyPem, string? CaCertPem);
internal sealed record EnrollRequest(string Name, string? Host, int? Port);
internal sealed record KubeClusterRequest(long? Id, string Name, bool Enabled, string? KubeConfigYaml, string? Token,
    string? ClientCertPem, string? ClientKeyPem, string? CaCertPem, string ServerUrl, string? Context);
internal sealed record ApplyManifestRequest(string Yaml, string? Namespace);
internal sealed record ScaleRequest(int Replicas);
internal sealed record ReorderRequest(List<string>? Order);

/// <summary>One parsed Docker stats sample (CPU %, memory MB, network bytes).</summary>
internal readonly record struct ContainerMetric(string ContainerId, double CpuPercent, double MemoryMb,
    double MemoryLimitMb, long NetworkRxBytes, long NetworkTxBytes);

/// <summary>Per-container row returned by <c>GET /api/system/dashboard</c>.</summary>
internal sealed record DashboardContainer(string Id, string Name, string Image, string State, string Status,
    double CpuPercent, int CpuCores, long CpuThrottledPeriods, double CpuThrottledMs,
    double MemoryMb, double MemoryLimitMb, double MemoryPeakMb, long MemoryFailCount,
    long PidsCurrent, long PidsLimit,
    long BlockReadBytes, long BlockWriteBytes,
    long NetworkRxBytes, long NetworkTxBytes, long NetworkRxPackets, long NetworkTxPackets,
    long NetworkRxErrors, long NetworkTxErrors, long NetworkRxDropped, long NetworkTxDropped,
    long SizeRwBytes, long SizeRootFsBytes, string[] Ports, string Created);

/// <summary>Aggregated totals for the selected host.</summary>
internal sealed record DashboardOverall(int ContainersTotal, int ContainersRunning, int ContainersPaused,
    int ContainersStopped, int Images,
    double CpuPercent, int CpuCores, double MemoryUsedMb, double MemoryTotalMb,
    long NetworkRxBytes, long NetworkTxBytes, int PublishedPorts,
    long DiskImagesBytes, long DiskContainersBytes, long DiskVolumesBytes, long DiskBuildCacheBytes,
    long DiskTotalBytes);

/// <summary>One host panel (local or remote) in the dashboard.</summary>
internal sealed record DashboardHost(long? Id, string Name, string Kind, bool Enabled, bool Online,
    int ContainersTotal, int ContainersRunning, int Images, double CpuPercent, int CpuCores,
    double MemoryUsedMb, double MemoryTotalMb, long DiskUsedBytes, string? Error);

// ──────────────────────────────────────────────────────────────────────────────
// Streaming helpers (SSE sources)
// ──────────────────────────────────────────────────────────────────────────────
internal static class StreamingEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async IAsyncEnumerable<string> LogLinesAsync(DockerSocketClient client, string id, int tail,
        bool timestamps, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Stream? stream = null;
        string? openError = null;
        try { stream = await client.GetLogsStreamAsync(id, true, tail, timestamps, ct).ConfigureAwait(false); }
        catch (Exception ex) { openError = ex.Message; }

        if (openError is not null || stream is null)
        {
            yield return JsonSerializer.Serialize(new { error = openError ?? "Unable to open log stream." }, Json);
            yield break;
        }

        await using (stream)
        {
            var pending = new StringBuilder();
            var enumerator = DockerStreamDemuxer.DemuxAsync(stream, ct).GetAsyncEnumerator(ct);
            string? streamError = null;
            try
            {
                while (true)
                {
                    bool hasNext;
                    try { hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                    catch (Exception ex) { streamError = ex.Message; break; }
                    if (!hasNext) break;

                    var (type, data) = enumerator.Current;
                    pending.Append(Encoding.UTF8.GetString(data));
                    var buffered = pending.ToString();
                    int idx;
                    while ((idx = buffered.IndexOf('\n')) >= 0)
                    {
                        var line = buffered[..idx];
                        buffered = buffered[(idx + 1)..];
                        yield return JsonSerializer.Serialize(new
                        {
                            stream = type.ToString().ToLowerInvariant(),
                            line
                        }, Json);
                    }
                    pending.Clear();
                    pending.Append(buffered);
                }

                if (streamError is not null)
                    yield return JsonSerializer.Serialize(new { error = "stream: " + streamError }, Json);
                else if (pending.Length > 0)
                    yield return JsonSerializer.Serialize(new { stream = "stdout", line = pending.ToString() }, Json);
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static async IAsyncEnumerable<string> StatsSamplesAsync(DockerSocketClient client, string id,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string payload = "";
            string? error = null;
            try
            {
                await using var stream = await client.GetStatsStreamAsync(id, stream: false, ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                payload = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) { error = ex.Message; }

            if (error is not null)
            {
                yield return JsonSerializer.Serialize(new { error }, Json);
                yield break;
            }

            yield return ComputeMetricJson(id, payload);

            bool canceled = false;
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { canceled = true; }
            if (canceled) yield break;
        }
    }

    /// <summary>Parses one Docker <c>/stats</c> payload into a metric, or <c>null</c> when it is
    /// unreadable. Shared by the SSE stats stream and the dashboard aggregate.</summary>
    public static ContainerMetric? TryComputeMetric(string id, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            double cpuPercent = 0;
            if (root.TryGetProperty("cpu_stats", out var cpu) && root.TryGetProperty("precpu_stats", out var pre)
                && cpu.TryGetProperty("cpu_usage", out var cpuUsage)
                && pre.TryGetProperty("cpu_usage", out var preUsage))
            {
                double cpuDelta = cpuUsage.GetProperty("total_usage").GetDouble()
                                  - preUsage.GetProperty("total_usage").GetDouble();
                double systemDelta = cpu.GetProperty("system_cpu_usage").GetDouble()
                                     - pre.GetProperty("system_cpu_usage").GetDouble();
                int cores = cpu.TryGetProperty("online_cpus", out var oc) && oc.ValueKind == JsonValueKind.Number
                    ? oc.GetInt32()
                    : (cpuUsage.TryGetProperty("percpu_usage", out var per) && per.ValueKind == JsonValueKind.Array
                        ? per.GetArrayLength() : 1);
                if (systemDelta > 0 && cpuDelta >= 0) cpuPercent = cpuDelta / systemDelta * cores * 100.0;
            }
            double memMb = 0, limitMb = 0;
            if (root.TryGetProperty("memory_stats", out var mem))
            {
                if (mem.TryGetProperty("usage", out var usage)) memMb = usage.GetDouble() / 1024.0 / 1024.0;
                if (mem.TryGetProperty("limit", out var limit)) limitMb = limit.GetDouble() / 1024.0 / 1024.0;
            }
            long rx = 0, tx = 0;
            if (root.TryGetProperty("networks", out var networks) && networks.ValueKind == JsonValueKind.Object)
            {
                foreach (var net in networks.EnumerateObject())
                {
                    if (net.Value.TryGetProperty("rx_bytes", out var r)) rx += r.GetInt64();
                    if (net.Value.TryGetProperty("tx_bytes", out var t)) tx += t.GetInt64();
                }
            }
            return new ContainerMetric(id, Math.Round(cpuPercent, 2), Math.Round(memMb, 1),
                Math.Round(limitMb, 1), rx, tx);
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeMetricJson(string id, string payload)
    {
        if (NetworkMetricsTracker.ParseExtendedMetric(id, payload) is not { } metric)
            return JsonSerializer.Serialize(new { error = "Unable to parse container stats." }, Json);
        return JsonSerializer.Serialize(new
        {
            containerId = metric.ContainerId,
            cpuPercent = metric.CpuPercent,
            cpuCores = metric.CpuCores,
            memoryMb = metric.MemoryMb,
            memoryLimitMb = metric.MemoryLimitMb,
            memoryPeakMb = metric.MemoryPeakMb,
            pidsCurrent = metric.PidsCurrent,
            pidsLimit = metric.PidsLimit,
            blockReadBytes = metric.BlockReadBytes,
            blockWriteBytes = metric.BlockWriteBytes,
            networkRxBytes = metric.NetworkRxBytes,
            networkTxBytes = metric.NetworkTxBytes,
            networkRxPackets = metric.NetworkRxPackets,
            networkTxPackets = metric.NetworkTxPackets,
            timestamp = DateTimeOffset.UtcNow
        }, Json);
    }

    public static async IAsyncEnumerable<string> PullProgressAsync(DockerSocketClient client, string image, string tag,
        Registry? registry, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Stream? stream = null;
        string? error = null;
        try { stream = await client.PullImageAsync(image, tag, registry, ct).ConfigureAwait(false); }
        catch (Exception ex) { error = ex.Message; }

        if (error is not null || stream is null)
        {
            yield return JsonSerializer.Serialize(new { error = error ?? "Unable to start pull." }, Json);
            yield break;
        }

        await using (stream)
        {
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                string? line = null;
                bool canceled = false;
                try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { canceled = true; }
                if (canceled) yield break;
                if (line is null) break;
                if (line.Length > 0) yield return line;
            }
        }
        yield return JsonSerializer.Serialize(new { status = "Pull complete", done = true }, Json);
    }

    public static async IAsyncEnumerable<string> AuditEventsAsync(SqliteWalStore store,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long lastId = 0;
        var initial = await store.GetAuditLogsAsync(1, ct).ConfigureAwait(false);
        if (initial.Count > 0) lastId = initial[0].Id;

        while (!ct.IsCancellationRequested)
        {
            List<AuditLog>? logs = null;
            try { logs = await store.GetAuditLogsAsync(100, ct).ConfigureAwait(false); }
            catch (Exception) { logs = null; }
            if (logs is null) yield break;

            foreach (var log in logs.Where(l => l.Id > lastId).OrderBy(l => l.Id))
            {
                lastId = Math.Max(lastId, log.Id);
                // Emit the same shape as GET /api/system/audit so the live feed and the
                // initial fetch agree (id, actor, actorDetail, action, target, detail, ...).
                yield return JsonSerializer.Serialize(new
                {
                    id = log.Id,
                    actor = log.Actor.ToString(),
                    actorDetail = log.ActorDetail,
                    action = log.Action,
                    target = log.Target,
                    detail = log.Detail,
                    success = log.Success,
                    timestamp = log.Timestamp
                }, Json);
            }

            bool canceled = false;
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { canceled = true; }
            if (canceled) yield break;
        }
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Dashboard aggregate: overall totals + per-container rows + one panel per host
// ──────────────────────────────────────────────────────────────────────────────
internal static class DashboardAggregator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Bounded fan-out: at most 8 concurrent Docker stats calls regardless of host count.
    private static readonly SemaphoreSlim Gate = new(8, 8);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (DateTimeOffset At, string Payload)> Cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(6);
    private static readonly DashboardOverall EmptyOverall =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static async Task<string> BuildAsync(DockerClientFactory factory, SqliteWalStore store,
        NetworkMetricsTracker tracker, long? hostId, bool fresh, CancellationToken ct)
    {
        var key = hostId?.ToString() ?? "default";
        if (!fresh)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < Ttl)
                    return cached.Payload;
            }
        }

        var payload = await BuildUncachedAsync(factory, store, tracker, hostId, ct).ConfigureAwait(false);
        lock (CacheLock)
        {
            Cache[key] = (DateTimeOffset.UtcNow, payload);
        }
        return payload;
    }

    private static async Task<string> BuildUncachedAsync(DockerClientFactory factory, SqliteWalStore store,
        NetworkMetricsTracker tracker, long? hostId, CancellationToken ct)
    {
        var hosts = await store.GetDockerHostsAsync(ct).ConfigureAwait(false);
        var snapshots = new List<(DockerHost Host, DashboardHost Panel, DashboardOverall Overall,
            List<DashboardContainer> Containers)>();

        if (hosts.Count == 0)
        {
            var only = await SnapshotAsync(factory, null, null, ct).ConfigureAwait(false);
            snapshots.Add((new DockerHost { Name = "local", Kind = DockerHostKind.UnixSocket, Enabled = true },
                only.Panel, only.Overall, only.Containers));
        }
        else
        {
            foreach (var host in hosts)
            {
                try
                {
                    var snapshot = await SnapshotAsync(factory, host.Id, host, ct).ConfigureAwait(false);
                    snapshots.Add((host, snapshot.Panel, snapshot.Overall, snapshot.Containers));
                }
                catch (Exception ex)
                {
                    var panel = new DashboardHost(host.Id, host.Name, host.Kind.ToString(), host.Enabled, false,
                        0, 0, 0, 0, 0, 0, 0, 0, ex.Message);
                    snapshots.Add((host, panel, EmptyOverall, new List<DashboardContainer>()));
                }
            }
        }

        var selected = hostId is null
            ? snapshots.FirstOrDefault(s => s.Host.Kind == DockerHostKind.UnixSocket)
            : snapshots.FirstOrDefault(s => s.Host.Id == hostId);
        if (selected.Host is null) selected = snapshots[0];

        // Traffic timeline / counter health, sampled by NetworkMetricsTracker in the background.
        var network = tracker.GetHostNetworkSummary(selected.Host.Id);
        return JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            hostId = selected.Host.Id,
            hostName = selected.Host.Name,
            overall = selected.Overall,
            containers = selected.Containers,
            hosts = snapshots.Select(s => s.Panel).ToArray(),
            network
        }, Json);
    }

    private static async Task<(DashboardOverall Overall, List<DashboardContainer> Containers, DashboardHost Panel)>
        SnapshotAsync(DockerClientFactory factory, long? hostId, DockerHost? host, CancellationToken ct)
    {
        var client = hostId is null
            ? await factory.GetDefaultAsync(ct).ConfigureAwait(false)
            : await factory.GetAsync(hostId.Value, ct).ConfigureAwait(false);

        using var info = await client.GetJsonAsync("/info", ct).ConfigureAwait(false);
        var cpuCores = GetInt(info.RootElement, "NCPU");
        var memTotalBytes = GetLong(info.RootElement, "MemTotal");
        var imageCount = GetInt(info.RootElement, "Images");

        long diskImages = 0, diskContainers = 0, diskVolumes = 0, diskBuild = 0;
        try
        {
            using var df = await client.SystemDfAsync(ct).ConfigureAwait(false);
            diskImages = SumArray(df.RootElement, "Images", "Size");
            diskContainers = SumArray(df.RootElement, "Containers", "SizeRw");
            diskVolumes = SumNested(df.RootElement, "Volumes", "UsageData", "Size");
            diskBuild = SumArray(df.RootElement, "BuildCache", "Size");
        }
        catch
        {
            // /system/df is best-effort; the rest of the dashboard still renders.
        }

        using var list = await client.GetJsonAsync("/containers/json?all=1&size=1", ct).ConfigureAwait(false);
        var rows = new List<DashboardContainer>();
        var running = new List<string>();
        var publishedPorts = 0;
        int paused = 0, stopped = 0;
        foreach (var container in list.RootElement.EnumerateArray())
        {
            var id = GetString(container, "Id");
            var name = FirstName(container) ?? (id.Length > 12 ? id[..12] : id);
            var state = GetString(container, "State");
            var (ports, hostPorts) = ReadPorts(container);
            var created = GetLong(container, "Created");
            rows.Add(new DashboardContainer(id, name, GetString(container, "Image"), state, GetString(container, "Status"), 0, cpuCores, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, GetLong(container, "SizeRw"), GetLong(container, "SizeRootFs"), ports, created > 0 ? DateTimeOffset.FromUnixTimeSeconds(created).ToString("o") : string.Empty));
            var lowered = state.ToLowerInvariant();
            if (lowered == "running")
            {
                running.Add(id);
                publishedPorts += hostPorts;
            }
            else if (lowered == "paused")
            {
                paused++;
            }
            else
            {
                stopped++;
            }
        }

        // Fan out one /stats call per running container, bounded by Gate, so a busy host cannot
        // overwhelm the Docker socket (or a remote daemon) with parallel requests.
        var metrics = new System.Collections.Concurrent.ConcurrentDictionary<string, ExtendedContainerMetric>();
        var tasks = running.Select(async id =>
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var stream = await client.GetStatsStreamAsync(id, stream: false, ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var payload = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                if (NetworkMetricsTracker.ParseExtendedMetric(id, payload) is { } metric) metrics[id] = metric;
            }
            catch
            {
                // A single container's stats failing must not fail the whole dashboard.
            }
            finally
            {
                Gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        double cpuTotal = 0, memUsed = 0;
        long rxTotal = 0, txTotal = 0, rxPktTotal = 0, txPktTotal = 0,
             rxErrTotal = 0, txErrTotal = 0, rxDropTotal = 0, txDropTotal = 0,
             blkReadTotal = 0, blkWriteTotal = 0;
        var detailed = new List<DashboardContainer>(rows.Count);
        foreach (var row in rows)
        {
            if (metrics.TryGetValue(row.Id, out var metric))
            {
                cpuTotal += metric.CpuPercent;
                memUsed += metric.MemoryMb;
                rxTotal += metric.NetworkRxBytes;
                txTotal += metric.NetworkTxBytes;
                rxPktTotal += metric.NetworkRxPackets;
                txPktTotal += metric.NetworkTxPackets;
                rxErrTotal += metric.NetworkRxErrors;
                txErrTotal += metric.NetworkTxErrors;
                rxDropTotal += metric.NetworkRxDropped;
                txDropTotal += metric.NetworkTxDropped;
                blkReadTotal += metric.BlockReadBytes;
                blkWriteTotal += metric.BlockWriteBytes;
                detailed.Add(row with
                {
                    CpuPercent = metric.CpuPercent,
                    CpuCores = metric.CpuCores,
                    CpuThrottledPeriods = metric.CpuThrottledPeriods,
                    CpuThrottledMs = metric.CpuThrottledMs,
                    MemoryMb = metric.MemoryMb,
                    MemoryLimitMb = metric.MemoryLimitMb,
                    MemoryPeakMb = metric.MemoryPeakMb,
                    MemoryFailCount = metric.MemoryFailCount,
                    PidsCurrent = metric.PidsCurrent,
                    PidsLimit = metric.PidsLimit,
                    BlockReadBytes = metric.BlockReadBytes,
                    BlockWriteBytes = metric.BlockWriteBytes,
                    NetworkRxBytes = metric.NetworkRxBytes,
                    NetworkTxBytes = metric.NetworkTxBytes,
                    NetworkRxPackets = metric.NetworkRxPackets,
                    NetworkTxPackets = metric.NetworkTxPackets,
                    NetworkRxErrors = metric.NetworkRxErrors,
                    NetworkTxErrors = metric.NetworkTxErrors,
                    NetworkRxDropped = metric.NetworkRxDropped,
                    NetworkTxDropped = metric.NetworkTxDropped
                });
            }
            else
            {
                detailed.Add(row);
            }
        }

        var diskTotal = diskImages + diskContainers + diskVolumes + diskBuild;
        var memTotalMb = Math.Round(memTotalBytes / 1048576.0, 1);
        var overall = new DashboardOverall(detailed.Count, running.Count, paused, stopped, imageCount,
            Math.Round(cpuTotal, 2), cpuCores, Math.Round(memUsed, 1), memTotalMb,
            rxTotal, txTotal, publishedPorts,
            diskImages, diskContainers, diskVolumes, diskBuild, diskTotal);
        var panel = new DashboardHost(hostId, host?.Name ?? "local", host?.Kind.ToString() ?? "UnixSocket",
            host?.Enabled ?? true, true, detailed.Count, running.Count, imageCount,
            Math.Round(cpuTotal, 2), cpuCores, Math.Round(memUsed, 1), memTotalMb, diskTotal, null);
        return (overall, detailed, panel);
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : 0;

    private static long GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64() : 0;

    private static string? FirstName(JsonElement container)
        => container.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Select(n => (n.GetString() ?? string.Empty).TrimStart('/'))
                .FirstOrDefault(n => n.Length > 0)
            : null;

    private static long SumArray(JsonElement root, string arrayName, string propertyName)
    {
        if (!root.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array) return 0;
        long total = 0;
        foreach (var item in array.EnumerateArray())
            if (item.TryGetProperty(propertyName, out var size) && size.ValueKind == JsonValueKind.Number)
                total += size.GetInt64();
        return total;
    }

    private static long SumNested(JsonElement root, string arrayName, string objectName, string propertyName)
    {
        if (!root.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array) return 0;
        long total = 0;
        foreach (var item in array.EnumerateArray())
            if (item.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
                && nested.TryGetProperty(propertyName, out var size) && size.ValueKind == JsonValueKind.Number)
                total += size.GetInt64();
        return total;
    }

    /// <summary>Docker port list rendered as "8080->80/tcp" (published) or "80/tcp" (exposed),
    /// ordered by the container port so the column never reshuffles between refreshes.</summary>
    private static (string[] Labels, int HostPorts) ReadPorts(JsonElement container)
    {
        if (!container.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
            return (Array.Empty<string>(), 0);
        var byContainerPort = new SortedDictionary<int, string>();
        var hostPorts = 0;
        foreach (var port in ports.EnumerateArray())
        {
            var privatePort = GetInt(port, "PrivatePort");
            var publicPort = GetInt(port, "PublicPort");
            var type = GetString(port, "Type");
            var protocol = type.Length > 0 ? type : "tcp";
            var label = publicPort > 0
                ? publicPort + "\u2192" + privatePort + "/" + protocol
                : privatePort + "/" + protocol;
            if (publicPort > 0) hostPorts++;
            byContainerPort[privatePort] = byContainerPort.TryGetValue(privatePort, out var existing)
                ? existing + ", " + label
                : label;
        }
        return (byContainerPort.Values.ToArray(), hostPorts);
    }
}

internal static class FormExtensions
{
    public static System.Collections.Specialized.NameValueCollection AsNameValueCollection(this IFormCollection form)
    {
        var nvc = new System.Collections.Specialized.NameValueCollection();
        foreach (var kv in form) nvc[kv.Key] = kv.Value.ToString();
        return nvc;
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Zero-CPU pre-compressed static file middleware (.zst / .br served directly)
// ──────────────────────────────────────────────────────────────────────────────
internal sealed class PrecompressedStaticMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _webRoot;
    private readonly ILogger<PrecompressedStaticMiddleware> _log;

    public PrecompressedStaticMiddleware(RequestDelegate next, IWebHostEnvironment env,
        ILogger<PrecompressedStaticMiddleware> log)
    {
        _next = next;
        _webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }
        if (ctx.Request.Path.StartsWithSegments("/api")
            || ctx.Request.Path.StartsWithSegments("/mcp")
            || ctx.Request.Path.StartsWithSegments("/ws")
            || ctx.Request.Path.StartsWithSegments("/signin-"))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        if (!Directory.Exists(_webRoot))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        var relative = (ctx.Request.Path.Value ?? "/").TrimStart('/');
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(_webRoot, relative));
        }
        catch
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        if (!candidate.StartsWith(_webRoot, StringComparison.Ordinal))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(candidate)) candidate = Path.Combine(candidate, "index.html");
        if (!File.Exists(candidate)) candidate = Path.Combine(_webRoot, "index.html");
        if (!File.Exists(candidate))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        var accept = ctx.Request.Headers.AcceptEncoding.ToString();
        var path = candidate;
        string? encoding = null;
        if (accept.Contains("zstd", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate + ".zst"))
        {
            path = candidate + ".zst";
            encoding = "zstd";
        }
        else if (accept.Contains("br", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate + ".br"))
        {
            path = candidate + ".br";
            encoding = "br";
        }

        ctx.Response.ContentType = ContentTypeFor(candidate);
        if (encoding is not null)
        {
            ctx.Response.Headers.ContentEncoding = encoding;
            ctx.Response.Headers.Vary = "Accept-Encoding";
        }
        // The entry document, the service worker and the manifest must never be cached (a redeploy
        // has to be picked up immediately); Vite's hashed assets are content-addressed, so immutable.
        var fileName = Path.GetFileName(candidate);
        var revalidate = fileName.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("sw.js", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("manifest.webmanifest", StringComparison.OrdinalIgnoreCase);
        ctx.Response.Headers.CacheControl = revalidate
            ? "no-cache"
            : "public, max-age=604800, immutable";

        await ctx.Response.SendFileAsync(path).ConfigureAwait(false);
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".map" => "application/json",
        ".txt" => "text/plain; charset=utf-8",
        ".webmanifest" => "application/manifest+json",
        _ => "application/octet-stream"
    };
}

// ──────────────────────────────────────────────────────────────────────────────
// WebSocket <-> duplex stream bridges (Docker exec and Kubernetes exec)
// ──────────────────────────────────────────────────────────────────────────────
internal static class WebSocketBridge
{
    public static async Task BridgeAsync(WebSocket socket, Stream stream, CancellationToken ct, string? shellLabel = null)
    {
        var clientToStream = PumpSocketToStreamAsync(socket, stream, ct);
        var streamToClient = PumpStreamToSocketAsync(stream, socket, ct, shellLabel);
        await Task.WhenAny(clientToStream, streamToClient).ConfigureAwait(false);
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception) { /* socket already closed */ }
    }

    /// <summary>Turns a raw Docker exec failure into an actionable terminal message.</summary>
    public static string FormatExecError(string raw, string label)
    {
        var noShell = raw.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("executable file not found", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("stat /bin", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.Append("\r\n\x1b[33m[exec] ")
          .Append(noShell ? "No usable shell was found in this container." : $"Could not start `{label}`.")
          .Append("\x1b[0m\r\n");
        if (noShell)
        {
            sb.Append("This image may be distroless and ship no shell (for example the DockManager container itself).\r\n");
            sb.Append("Try another interpreter from the Exec menu in Inspect: \x1b[36m/bin/bash\x1b[0m, \x1b[36m/bin/ash\x1b[0m, ")
              .Append("\x1b[36m/bin/busybox sh\x1b[0m.\r\n");
            sb.Append("If none exist, use \x1b[36mInspect \u2192 Files\x1b[0m to browse and download files instead.\r\n");
        }
        var trimmed = raw.Length > 500 ? raw[..500] + "\u2026" : raw;
        sb.Append("\x1b[90m").Append(trimmed.Replace("\r", string.Empty)).Append("\x1b[0m\r\n");
        return sb.ToString();
    }

    private static bool LooksLikeExecError(string text)
        => text.Contains("exec failed", StringComparison.OrdinalIgnoreCase)
        || text.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase)
        || text.Contains("executable file not found", StringComparison.OrdinalIgnoreCase)
        || text.Contains("stat /bin", StringComparison.OrdinalIgnoreCase);

    private static async Task PumpSocketToStreamAsync(WebSocket socket, Stream stream, CancellationToken ct)
    {
        var buffer = new byte[16384];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception) { /* peer disconnected */ }
    }

    private static async Task PumpStreamToSocketAsync(Stream stream, WebSocket socket, CancellationToken ct, string? shellLabel = null)
    {
        var buffer = new byte[16384];
        var first = true;
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;
                if (first && shellLabel is not null)
                {
                    first = false;
                    var text = Encoding.UTF8.GetString(buffer, 0, read);
                    if (LooksLikeExecError(text))
                    {
                        await TrySendTextAsync(socket, FormatExecError(text, shellLabel)).ConfigureAwait(false);
                        return;
                    }
                }
                first = false;
                await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception) { /* stream ended */ }
    }

    /// <summary>Sends a text frame if the socket is still open; failures are ignored.</summary>
    public static async Task TrySendTextAsync(WebSocket socket, string text)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true,
                    CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { /* peer gone */ }
    }

    /// <summary>Closes the socket with a specific status code/reason; failures are ignored.</summary>
    public static async Task CloseAsync(WebSocket socket, int code, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync((WebSocketCloseStatus)code, reason, CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (Exception) { /* socket already closed */ }
    }

    /// <summary>Bridges a Kubernetes v4 channel-protocol exec WebSocket to the browser.</summary>
    public static async Task BridgeK8sAsync(WebSocket browser, WebSocket upstream, CancellationToken ct)
    {
        var browserToPod = Task.Run(async () =>
        {
            var buffer = new byte[16384];
            try
            {
                while (browser.State == WebSocketState.Open && upstream.State == WebSocketState.Open
                       && !ct.IsCancellationRequested)
                {
                    var result = await browser.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    // Prefix stdin channel byte (0) for the v4 protocol.
                    var framed = new byte[result.Count + 1];
                    framed[0] = 0;
                    Buffer.BlockCopy(buffer, 0, framed, 1, result.Count);
                    await upstream.SendAsync(framed, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                }
            }
            catch (Exception) { }
        }, ct);

        var podToBrowser = Task.Run(async () =>
        {
            var buffer = new byte[16384];
            try
            {
                while (browser.State == WebSocketState.Open && upstream.State == WebSocketState.Open
                       && !ct.IsCancellationRequested)
                {
                    var result = await upstream.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.Count <= 1) continue;
                    await browser.SendAsync(buffer.AsMemory(1, result.Count - 1), WebSocketMessageType.Binary, true, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception) { }
        }, ct);

        await Task.WhenAny(browserToPod, podToBrowser).ConfigureAwait(false);
        try { await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); }
        catch (Exception) { }
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Single-binary agent mode (--agent): mTLS front-door for a remote Docker socket
// ──────────────────────────────────────────────────────────────────────────────
internal static class EhAgent
{
    public static async Task RunAsync(string[] args)
    {
        var port = GetIntArg(args, "--port", 2376);
        var socket = GetStringArg(args, "--socket", "/var/run/docker.sock");

        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var log = loggerFactory.CreateLogger("agent");

        // Self-signed server certificate for the mTLS endpoint; clients authenticate with a client cert.
        var serverCert = CreateSelfSigned();

        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.ListenAnyIP(port, lo => lo.UseHttps(https =>
            {
                https.ServerCertificate = serverCert;
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (cert, chain, errors) =>
                {
                    if (errors == SslPolicyErrors.None) return true;
                    log.LogWarning("Rejected agent client certificate: {Errors}", errors);
                    return false;
                };
            }));
        });

        using var unixHandler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                await s.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socket), ct).ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(s, ownsSocket: true);
            }
        };
        using var docker = new HttpClient(unixHandler) { BaseAddress = new Uri("http://docker") };

        var app = builder.Build();
        app.Run(async ctx =>
        {
            var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method),
                ctx.Request.Path + ctx.Request.QueryString);
            if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
                request.Content = new StreamContent(ctx.Request.Body);
            foreach (var header in ctx.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var response = await docker.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                ctx.RequestAborted).ConfigureAwait(false);
            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers)
                ctx.Response.Headers[header.Key] = header.Value.ToArray();
            foreach (var header in response.Content.Headers)
                ctx.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
        });

        log.LogInformation("eh.DockManager agent listening on :{Port} (mTLS) -> {Socket}", port, socket);
        await app.RunAsync();
    }

    public static string BuildAgentCompose(string name, string certPem, string keyPem, string caPem, int port)
    {
        var indent = (string s) => string.Join('\n', s.Split('\n').Select(l => "      " + l));
        return "# File: agent-compose.yml\n" +
               "services:\n" +
               $"  {name}:\n" +
               "    image: eh-dockmanager:latest\n" +
               $"    container_name: {name}\n" +
               "    restart: unless-stopped\n" +
               "    command: [\"/app/eh-dockmanager\", \"--agent\", \"--port\", \"" + port + "\"]\n" +
               "    ports:\n" +
               $"      - \"{port}:{port}\"\n" +
               "    volumes:\n" +
               "      - /var/run/docker.sock:/var/run/docker.sock:ro\n" +
               "      - ./agent-certs:/certs:ro\n" +
               "    environment:\n" +
               "      - EHDM_DATA_DIR=/data\n" +
               "    read_only: true\n" +
               "    security_opt: [no-new-privileges:true]\n" +
               "    cap_drop: [ALL]\n" +
               "# Save the client certificate, key and CA into ./agent-certs on the remote host:\n" +
               "#   client.crt:\n" + indent(certPem) + "\n" +
               "#   client.key:\n" + indent(keyPem) + "\n" +
               "#   ca.crt:\n" + indent(caPem) + "\n";
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=eh.DockManager Agent", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        using var cert = req.CreateSelfSigned(now, now.AddYears(5));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private static int GetIntArg(string[] args, string flag, int fallback)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : fallback;
    }

    private static string GetStringArg(string[] args, string flag, string fallback)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
}

