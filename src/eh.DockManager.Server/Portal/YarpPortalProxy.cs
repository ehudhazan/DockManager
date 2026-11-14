// File: src/eh.DockManager.Server/Portal/YarpPortalProxy.cs
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using eh.DockManager.Server.Data;
using eh.DockManager.Server.Docker;
using eh.DockManager.Server.Models;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace eh.DockManager.Server.Portal;

/// <summary>
/// Mutable YARP configuration provider. Route/cluster updates are published atomically with a
/// <see cref="CancellationTokenSource"/> so the proxy reloads in memory with zero restarts.
/// </summary>
public sealed class DynamicProxyConfigProvider : IProxyConfigProvider
{
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private CancellationTokenSource _changeToken = new();

    public IProxyConfig GetConfig() => _snapshot;

    public void Update(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        var previous = Interlocked.Exchange(ref _changeToken, new CancellationTokenSource());
        _snapshot = new Snapshot(routes, clusters, _changeToken.Token);
        try { previous.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
        previous.Dispose();
    }

    public int RouteCount => _snapshot.Routes.Count;

    private sealed class Snapshot : IProxyConfig
    {
        public Snapshot(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters,
            CancellationToken changeToken)
        {
            Routes = routes;
            Clusters = clusters;
            ChangeToken = new CancellationChangeToken(changeToken);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }
        public IReadOnlyList<ClusterConfig> Clusters { get; }
        public IChangeToken ChangeToken { get; }

        public static readonly Snapshot Empty = new(Array.Empty<RouteConfig>(), Array.Empty<ClusterConfig>(),
            CancellationToken.None);
    }
}

/// <summary>Builds YARP routes and clusters from the persisted portal app catalogue.</summary>
public sealed class PortalProxyConfigurator
{
    private readonly SqliteWalStore _store;
    private readonly DynamicProxyConfigProvider _provider;
    private readonly ILogger<PortalProxyConfigurator> _log;
    private volatile string _portalDomain;

    public PortalProxyConfigurator(SqliteWalStore store, DynamicProxyConfigProvider provider,
        IConfiguration configuration, ILogger<PortalProxyConfigurator> log)
    {
        _store = store;
        _provider = provider;
        _log = log;
        _portalDomain = configuration["EHDM_PORTAL_DOMAIN"] ?? EhConstants.DefaultPortalDomain;
    }

    public string PortalDomain => _portalDomain;

    /// <summary>Persists a new portal domain and rebuilds the proxy routes so every subdomain follows it.</summary>
    public async Task SetPortalDomainAsync(string domain, CancellationToken ct = default)
    {
        var normalized = domain.Trim().Trim('.').ToLowerInvariant();
        if (normalized.Length == 0) return;
        await _store.SetConfigAsync("portal.domain", normalized, ct).ConfigureAwait(false);
        _portalDomain = normalized;
        await RebuildAsync(ct).ConfigureAwait(false);
    }

    public async Task RebuildAsync(CancellationToken ct = default)
    {
        var persistedDomain = await _store.GetConfigAsync("portal.domain", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(persistedDomain)) _portalDomain = persistedDomain.Trim();
        var apps = await _store.GetPortalAppsAsync(ct).ConfigureAwait(false);
        var clusters = new List<ClusterConfig>();
        var routes = new List<RouteConfig>();

        // Hidden apps are only removed from the landing page; their route is kept so the
        // published URL keeps working (an admin unhides them again from the Portal view).
        foreach (var app in apps)
        {
            var address = ResolveAddress(app);
            if (address is null)
            {
                // Manual external link with no proxy target; intentionally not proxied.
                continue;
            }

            var clusterId = "portal-" + app.Subdomain;
            clusters.Add(new ClusterConfig
            {
                ClusterId = clusterId,
                LoadBalancingPolicy = "RoundRobin",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["primary"] = new DestinationConfig { Address = address }
                },
                Metadata = new Dictionary<string, string>
                {
                    ["portal.subdomain"] = app.Subdomain,
                    ["portal.container"] = app.ContainerName ?? string.Empty,
                    ["portal.autoscale"] = app.Autoscale.ToString()
                }
            });

            routes.Add(new RouteConfig
            {
                RouteId = "portal-route-" + app.Subdomain,
                ClusterId = clusterId,
                Order = 100,
                Match = new RouteMatch
                {
                    Hosts = new[] { app.Subdomain + "." + _portalDomain },
                    Path = "/{**catch-all}"
                },
                AuthorizationPolicy = app.Auth switch
                {
                    PortalAuthMode.Admin => "eh-admin",
                    PortalAuthMode.Sso => "eh-authenticated",
                    _ => null
                },
                Metadata = new Dictionary<string, string>
                {
                    ["portal.auth"] = app.Auth.ToString(),
                    ["portal.subdomain"] = app.Subdomain
                }
            });
        }

        _provider.Update(routes, clusters);
        _log.LogInformation("Portal proxy reconfigured: {Routes} routes, {Clusters} clusters.",
            routes.Count, clusters.Count);
    }

    private static string? ResolveAddress(PortalApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.ExternalUrl)) return app.ExternalUrl.TrimEnd('/');
        var port = app.TargetPort ?? 80;
        if (!string.IsNullOrWhiteSpace(app.ContainerName))
            return $"http://{app.ContainerName}:{port}";
        return null;
    }
}

/// <summary>IEEE 802.3 Wake-on-LAN magic packet sender.</summary>
public sealed class WakeOnLanService
{
    private readonly ILogger<WakeOnLanService> _log;

    public WakeOnLanService(ILogger<WakeOnLanService> log) => _log = log;

    public async Task<bool> WakeAsync(string mac, string broadcast = "255.255.255.255", int port = 9,
        CancellationToken ct = default)
    {
        var macBytes = ParseMac(mac);
        if (macBytes is null)
        {
            _log.LogWarning("Invalid Wake-on-LAN MAC address '{Mac}'.", mac);
            return false;
        }

        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 1; i <= 16; i++)
            Buffer.BlockCopy(macBytes, 0, packet, i * 6, 6);

        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            await udp.SendAsync(packet, packet.Length, broadcast, port).ConfigureAwait(false);
            _log.LogInformation("Sent Wake-on-LAN magic packet to {Mac} via {Broadcast}:{Port}.", mac, broadcast, port);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send Wake-on-LAN packet to {Mac}.", mac);
            return false;
        }
    }

    private static byte[]? ParseMac(string mac)
    {
        var cleaned = new string(mac.Where(c => Uri.IsHexDigit(c)).ToArray());
        if (cleaned.Length != 12) return null;
        var result = new byte[6];
        for (int i = 0; i < 6; i++)
            result[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        return result;
    }
}

/// <summary>
/// Starts scale-to-zero containers (or wakes a sleeping Docker host) before a request is proxied.
/// Polls TCP readiness for up to 8 seconds so the first request succeeds transparently.
/// </summary>
public sealed class ServerlessWakeMiddleware
{
    private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(8);

    private readonly RequestDelegate _next;
    private readonly SqliteWalStore _store;
    private readonly DockerClientFactory _docker;
    private readonly WakeOnLanService _wol;
    private readonly PortalProxyConfigurator _configurator;
    private readonly ILogger<ServerlessWakeMiddleware> _log;

    public ServerlessWakeMiddleware(RequestDelegate next, SqliteWalStore store, DockerClientFactory docker,
        WakeOnLanService wol, PortalProxyConfigurator configurator, ILogger<ServerlessWakeMiddleware> log)
    {
        _next = next;
        _store = store;
        _docker = docker;
        _wol = wol;
        _configurator = configurator;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        var suffix = "." + _configurator.PortalDomain;
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var subdomain = host[..^suffix.Length];
        var app = await _store.GetPortalAppBySubdomainAsync(subdomain, context.RequestAborted).ConfigureAwait(false);
        if (app is null || app.Autoscale == AutoscaleMode.None || string.IsNullOrEmpty(app.ContainerName))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            await EnsureAwakeAsync(app, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Scale-to-zero wake failed for {Subdomain}; forwarding anyway.", subdomain);
        }

        await _next(context).ConfigureAwait(false);
    }

    private async Task EnsureAwakeAsync(PortalApp app, CancellationToken ct)
    {
        var client = app.HostId.HasValue
            ? await _docker.GetAsync(app.HostId.Value, ct).ConfigureAwait(false)
            : await _docker.GetDefaultAsync(ct).ConfigureAwait(false);

        // Confirm the Docker host itself is reachable; wake it via WoL if it is asleep.
        if (!await client.PingAsync(ct).ConfigureAwait(false))
        {
            var hosts = await _store.GetDockerHostsAsync(ct).ConfigureAwait(false);
            var host = hosts.FirstOrDefault(h => h.Id == client.Host.Id && !string.IsNullOrEmpty(h.WakeOnLanMac));
            if (host is not null)
            {
                await _wol.WakeAsync(host.WakeOnLanMac!, host.WakeOnLanBroadcast ?? "255.255.255.255",
                    host.WakeOnLanPort, ct).ConfigureAwait(false);
                var deadline = DateTimeOffset.UtcNow + WakeTimeout;
                while (DateTimeOffset.UtcNow < deadline && !await client.PingAsync(ct).ConfigureAwait(false))
                    await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        using var inspect = await client.InspectContainerAsync(app.ContainerName!, ct).ConfigureAwait(false);
        if (inspect.RootElement.ValueKind != JsonValueKind.Object) return;

        var running = inspect.RootElement.TryGetProperty("State", out var state)
                      && state.TryGetProperty("Running", out var r) && r.GetBoolean();
        if (!running)
        {
            _log.LogInformation("Scale-to-zero: starting container {Container}.", app.ContainerName);
            await client.StartContainerAsync(app.ContainerName!, ct).ConfigureAwait(false);
        }

        await WaitForTcpAsync(client, app, ct).ConfigureAwait(false);
    }

    private static async Task WaitForTcpAsync(DockerSocketClient client, PortalApp app, CancellationToken ct)
    {
        string? host = null;
        int port = app.TargetPort ?? 80;

        using var inspect = await client.InspectContainerAsync(app.ContainerName!, ct).ConfigureAwait(false);
        if (inspect.RootElement.ValueKind == JsonValueKind.Object
            && inspect.RootElement.TryGetProperty("NetworkSettings", out var net)
            && net.TryGetProperty("Networks", out var networks)
            && networks.ValueKind == JsonValueKind.Object)
        {
            var first = networks.EnumerateObject().FirstOrDefault();
            if (first.Value.ValueKind == JsonValueKind.Object
                && first.Value.TryGetProperty("IPAddress", out var ip))
                host = ip.GetString();
        }
        if (string.IsNullOrEmpty(host)) return;

        var deadline = DateTimeOffset.UtcNow + WakeTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                var connect = tcp.ConnectAsync(host, port, ct).AsTask();
                var completed = await Task.WhenAny(connect, Task.Delay(750, ct)).ConfigureAwait(false);
                if (completed == connect && tcp.Connected)
                {
                    await connect.ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception)
            {
                // Retry until the deadline.
            }
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Background discovery engine. Watches Docker <c>/events</c> plus Kubernetes services and reconciles
/// the portal catalogue and the live YARP configuration.
/// </summary>
public sealed class PortalDiscoveryService : BackgroundService
{
    private readonly SqliteWalStore _store;
    private readonly DockerClientFactory _docker;
    private readonly PortalProxyConfigurator _configurator;
    private readonly ILogger<PortalDiscoveryService> _log;

    public PortalDiscoveryService(SqliteWalStore store, DockerClientFactory docker,
        PortalProxyConfigurator configurator, ILogger<PortalDiscoveryService> log)
    {
        _store = store;
        _docker = docker;
        _configurator = configurator;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Portal discovery cycle failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Reconciles portal apps from all enabled Docker hosts and rebuilds the YARP config.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var hosts = await _store.GetDockerHostsAsync(ct).ConfigureAwait(false);
        var seenContainerIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var host in hosts.Where(h => h.Enabled))
        {
            DockerSocketClient client;
            try { client = await _docker.GetAsync(host.Id, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "Skipping Docker host {Host}.", host.Name); continue; }

            JsonDocument containers;
            try { containers = await client.ListContainersAsync(true, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "Cannot list containers on {Host}.", host.Name); continue; }

            using (containers)
            {
                foreach (var container in containers.RootElement.EnumerateArray())
                {
                    var containerId = container.GetProperty("Id").GetString() ?? "";
                    var labels = container.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object
                        ? l.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
                        : new Dictionary<string, string>();
                    var name = container.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array
                        ? names.EnumerateArray().FirstOrDefault().GetString()?.TrimStart('/')
                        : null;

                    var expose = ShouldExpose(labels, container);
                    if (!expose)
                    {
                        var legacy = await _store.GetPortalAppBySubdomainAsync(Slug(name ?? containerId), ct)
                            .ConfigureAwait(false);
                        if (legacy is { AutoDiscovered: true })
                            await _store.DeleteAutoDiscoveredPortalAppAsync(containerId, ct).ConfigureAwait(false);
                        continue;
                    }

                    seenContainerIds.Add(containerId);
                    var subdomain = ResolveSubdomain(labels, name, containerId);
                    var port = ResolvePort(labels, container);
                    var existing = await _store.GetPortalAppBySubdomainAsync(subdomain, ct).ConfigureAwait(false);
                    if (existing is { Manual: true }) continue;

                    var app = existing ?? new PortalApp { Subdomain = subdomain, CreatedAt = DateTimeOffset.UtcNow };
                    app.Name = labels.GetValueOrDefault("eh.portal.name", name ?? subdomain);
                    app.Icon = labels.GetValueOrDefault("eh.portal.icon", app.Icon);
                    app.Category = labels.GetValueOrDefault("eh.portal.category", app.Category ?? "Apps");
                    app.TargetPort = port;
                    app.ContainerId = containerId;
                    app.ContainerName = name;
                    app.HostId = host.Id;
                    app.AutoDiscovered = true;
                    app.Auth = labels.GetValueOrDefault("eh.portal.auth") switch
                    {
                        "public" => PortalAuthMode.Public,
                        "admin" => PortalAuthMode.Admin,
                        "sso" => PortalAuthMode.Sso,
                        _ => app.Auth
                    };
                    app.Autoscale = labels.GetValueOrDefault("eh.portal.autoscale") switch
                    {
                        "idle-15m" => AutoscaleMode.Idle15m,
                        "idle-1h" => AutoscaleMode.Idle1h,
                        _ => AutoscaleMode.None
                    };
                    app.StackName = labels.GetValueOrDefault(EhConstants.ComposeProjectLabel, app.StackName);
                    await _store.UpsertPortalAppAsync(app, ct).ConfigureAwait(false);
                }
            }
        }

        await _configurator.RebuildAsync(ct).ConfigureAwait(false);
    }

    private static bool ShouldExpose(Dictionary<string, string> labels, JsonElement container)
    {
        if (labels.TryGetValue("eh.portal.enable", out var enabled))
            return enabled.Equals("true", StringComparison.OrdinalIgnoreCase) || enabled == "1";
        if (!container.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var p in ports.EnumerateArray())
        {
            if (p.TryGetProperty("PrivatePort", out var pp) && pp.ValueKind == JsonValueKind.Number
                && EhConstants.DiscoverablePorts.Contains(pp.GetInt32()))
                return true;
        }
        return false;
    }

    internal static string ResolveSubdomain(Dictionary<string, string> labels, string? name, string containerId)
    {
        if (labels.TryGetValue("eh.portal.subdomain", out var sub) && !string.IsNullOrWhiteSpace(sub))
            return Slug(sub);
        return Slug(name ?? containerId[..Math.Min(12, containerId.Length)]);
    }

    internal static int? ResolvePort(Dictionary<string, string> labels, JsonElement container)
    {
        if (labels.TryGetValue("eh.portal.port", out var portStr) && int.TryParse(portStr, out var explicitPort))
            return explicitPort;
        if (container.TryGetProperty("Ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
        {
            var candidates = ports.EnumerateArray()
                .Where(p => p.TryGetProperty("PrivatePort", out var pp) && pp.ValueKind == JsonValueKind.Number)
                .Select(p => p.GetProperty("PrivatePort").GetInt32())
                .ToList();
            var preferred = candidates.FirstOrDefault(c => EhConstants.DiscoverablePorts.Contains(c));
            if (preferred != 0) return preferred;
            if (candidates.Count > 0) return candidates[0];
        }
        return null;
    }

    internal static string Slug(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
    }
}
