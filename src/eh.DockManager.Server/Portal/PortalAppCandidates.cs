// File: src/eh.DockManager.Server/Portal/PortalAppCandidates.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using eh.DockManager.Server.Data;
using eh.DockManager.Server.Docker;
using eh.DockManager.Server.Kubernetes;
using eh.DockManager.Server.Models;

namespace eh.DockManager.Server.Portal;

/// <summary>One app that could be added to the portal catalogue (Docker container, K8s Service/Ingress/HTTPRoute).</summary>
public sealed record PortalCandidate(
    string Key,
    string Source,
    string Name,
    string Subdomain,
    string Category,
    string? ContainerId,
    string? ContainerName,
    long? HostId,
    int? TargetPort,
    string? ExternalUrl,
    string Detail,
    bool Present);

/// <summary>The candidate list for one Docker host or Kubernetes cluster.</summary>
public sealed record PortalCandidatesResponse(string Kind, long Id, string Label, IReadOnlyList<PortalCandidate> Items);

/// <summary>One app in a bulk-add request.</summary>
public sealed record BulkPortalAppItem(
    string? Subdomain,
    string? Name,
    string? Category,
    string? Auth,
    string? Autoscale,
    int? TargetPort,
    string? ContainerId,
    string? ContainerName,
    long? HostId,
    string? ExternalUrl);

public sealed record BulkPortalAppsRequest(List<BulkPortalAppItem>? Apps);

public sealed record BulkAddResult(int Added, IReadOnlyList<string> Conflicts);

/// <summary>
/// Builds the "available apps" list behind the Portal's bulk-add dialog. For a Docker host it
/// lists the host's containers that are not yet in the catalogue; for a Kubernetes cluster it
/// lists the public Services (LoadBalancer/NodePort with an external address), Ingresses and
/// Gateway API HTTPRoutes. A candidate whose container/URL is already catalogued is returned
/// with <see cref="PortalCandidate.Present"/> set so the UI can show it as already added.
/// </summary>
public sealed class PortalCandidateService
{
    private readonly SqliteWalStore _store;
    private readonly DockerClientFactory _docker;
    private readonly KubeClusterService _kube;
    private readonly ILogger<PortalCandidateService> _log;

    public PortalCandidateService(SqliteWalStore store, DockerClientFactory docker,
        KubeClusterService kube, ILogger<PortalCandidateService> log)
    {
        _store = store;
        _docker = docker;
        _kube = kube;
        _log = log;
    }

    /// <summary>Lists the containers on one Docker host that can be added to the portal.</summary>
    public async Task<PortalCandidatesResponse?> ForDockerHostAsync(long hostId, CancellationToken ct = default)
    {
        var host = await _store.GetDockerHostByIdAsync(hostId, ct).ConfigureAwait(false);
        if (host is null) return null;

        DockerSocketClient client;
        try
        {
            client = await _docker.GetAsync(hostId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cannot reach Docker host {Host} for portal candidates.", hostId);
            return new PortalCandidatesResponse("docker", hostId, host.Name, Array.Empty<PortalCandidate>());
        }

        JsonDocument containers;
        try
        {
            containers = await client.ListContainersAsync(true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cannot list containers on host {Host} for portal candidates.", hostId);
            return new PortalCandidatesResponse("docker", hostId, host.Name, Array.Empty<PortalCandidate>());
        }

        var catalogue = await _store.GetPortalAppsAsync(ct).ConfigureAwait(false);
        var presentIds = catalogue
            .Where(a => !string.IsNullOrWhiteSpace(a.ContainerId))
            .Select(a => a.ContainerId!)
            .ToHashSet(StringComparer.Ordinal);
        var presentSubdomains = catalogue.Select(a => a.Subdomain).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<PortalCandidate>();
        using (containers)
        {
            foreach (var container in containers.RootElement.EnumerateArray())
            {
                var id = container.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (id.Length == 0) continue;

                var labels = container.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object
                    ? l.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
                    : new Dictionary<string, string>();
                var name = container.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array
                    ? names.EnumerateArray().FirstOrDefault().GetString()?.TrimStart('/')
                    : null;
                var displayName = labels.GetValueOrDefault("eh.portal.name", name) ?? id[..Math.Min(12, id.Length)];
                var subdomain = PortalDiscoveryService.ResolveSubdomain(labels, name, id);
                var targetPort = PortalDiscoveryService.ResolvePort(labels, container);
                var image = container.TryGetProperty("Image", out var imgEl) ? imgEl.GetString() : null;
                var state = container.TryGetProperty("State", out var stEl) ? stEl.GetString() : null;
                var ports = DescribePorts(container);
                var category = labels.GetValueOrDefault("eh.portal.category", "Apps");
                var detail = string.Join(" · ", new[] { image, state, ports }.Where(s => !string.IsNullOrWhiteSpace(s)));

                items.Add(new PortalCandidate(
                    id, "container", displayName, subdomain, category,
                    id, name, hostId, targetPort, null, detail,
                    presentIds.Contains(id) || presentSubdomains.Contains(subdomain)));
            }
        }

        return new PortalCandidatesResponse("docker", hostId, host.Name, items);
    }

    /// <summary>Lists the public Services, Ingresses and HTTPRoutes of one Kubernetes cluster.</summary>
    public async Task<PortalCandidatesResponse?> ForClusterAsync(long clusterId, CancellationToken ct = default)
    {
        var cluster = await _store.GetKubeClusterByIdAsync(clusterId, ct).ConfigureAwait(false);
        if (cluster is null) return null;

        var catalogue = await _store.GetPortalAppsAsync(ct).ConfigureAwait(false);
        var presentSubdomains = catalogue.Select(a => a.Subdomain).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var presentUrls = catalogue
            .Where(a => !string.IsNullOrWhiteSpace(a.ExternalUrl))
            .Select(a => NormalizeUrl(a.ExternalUrl!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Present(string subdomain, string url) =>
            presentSubdomains.Contains(subdomain) || presentUrls.Contains(NormalizeUrl(url));

        var items = new List<PortalCandidate>();
        try
        {
            var services = await _kube.ListServicesAsync(cluster, null, ct).ConfigureAwait(false);
            foreach (var svc in Items(services))
            {
                var type = Str(svc?["spec"]?["type"]) ?? "ClusterIP";
                if (type is not ("LoadBalancer" or "NodePort")) continue;
                var name = Str(svc?["metadata"]?["name"]);
                var ns = Str(svc?["metadata"]?["namespace"]) ?? "default";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var address = LoadBalancerAddress(svc);
                if (address is null) continue;
                var port = FirstServicePort(svc) ?? 80;
                var url = BuildUrl(address, port);
                var subdomain = PortalDiscoveryService.Slug(name);
                if (subdomain.Length == 0) continue;
                items.Add(new PortalCandidate(
                    $"service/{ns}/{name}", "service", name, subdomain, "Kubernetes",
                    null, null, null, null, url,
                    $"Service ({type}) · {ns}/{name} · {address}:{port}",
                    Present(subdomain, url)));
            }

            var ingresses = await _kube.ListIngressesAsync(cluster, null, ct).ConfigureAwait(false);
            foreach (var ing in Items(ingresses))
            {
                var name = Str(ing?["metadata"]?["name"]);
                var ns = Str(ing?["metadata"]?["namespace"]) ?? "default";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var hosts = Strings(ing?["spec"]?["rules"]?.AsArray()?.Select(r => r?["host"]));
                if (hosts.Count == 0) continue;
                var url = "https://" + hosts[0];
                var subdomain = PortalDiscoveryService.Slug(name);
                if (subdomain.Length == 0) continue;
                items.Add(new PortalCandidate(
                    $"ingress/{ns}/{name}", "ingress", name, subdomain, "Kubernetes",
                    null, null, null, null, url,
                    $"Ingress · {ns}/{name} · {hosts[0]}" + (hosts.Count > 1 ? $" (+{hosts.Count - 1} more)" : ""),
                    Present(subdomain, url)));
            }

            var namespaces = await _kube.ListNamespacesAsync(cluster, ct).ConfigureAwait(false);
            var namespaceNames = Items(namespaces)
                .Select(n => Str(n["metadata"]?["name"]))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var ns in namespaceNames)
            {
                var routes = await _kube.ListHttpRoutesAsync(cluster, ns, ct).ConfigureAwait(false);
                foreach (var route in Items(routes))
                {
                    var name = Str(route["metadata"]?["name"]);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var hosts = Strings((route["spec"]?["hostnames"] as JsonArray)?.Select(n => (JsonNode?)n));
                    if (hosts.Count == 0) continue;
                    var url = "https://" + hosts[0];
                    var subdomain = PortalDiscoveryService.Slug(name);
                    if (subdomain.Length == 0) continue;
                    items.Add(new PortalCandidate(
                        $"httproute/{ns}/{name}", "httproute", name, subdomain, "Kubernetes",
                        null, null, null, null, url,
                        $"HTTPRoute · {ns}/{name} · {hosts[0]}",
                        Present(subdomain, url)));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cannot enumerate Kubernetes candidates for cluster {Cluster}.", clusterId);
        }

        return new PortalCandidatesResponse("k8s", clusterId, cluster.Name, items);
    }

    /// <summary>
    /// Adds a batch of apps as manual portal entries. Subdomains that are empty, missing a name,
    /// or already taken (by the catalogue or an earlier item in the batch) are returned as
    /// conflicts and nothing is written, so the caller can prompt for corrections.
    /// </summary>
    public async Task<BulkAddResult> AddBulkAsync(IReadOnlyList<BulkPortalAppItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return new BulkAddResult(0, Array.Empty<string>());

        var catalogue = await _store.GetPortalAppsAsync(ct).ConfigureAwait(false);
        var taken = new HashSet<string>(catalogue.Select(a => a.Subdomain), StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();
        var prepared = new List<PortalApp>();

        foreach (var item in items)
        {
            var subdomain = PortalDiscoveryService.Slug(item.Subdomain ?? "");
            var name = item.Name?.Trim() ?? "";
            if (subdomain.Length == 0 || name.Length == 0 || !taken.Add(subdomain))
            {
                conflicts.Add(subdomain.Length > 0 ? subdomain : "(empty)");
                continue;
            }

            prepared.Add(new PortalApp
            {
                Subdomain = subdomain,
                Name = name,
                Category = string.IsNullOrWhiteSpace(item.Category) ? "Apps" : item.Category!.Trim(),
                Auth = Enum.TryParse<PortalAuthMode>(item.Auth, true, out var auth) ? auth : PortalAuthMode.Sso,
                Autoscale = Enum.TryParse<AutoscaleMode>(item.Autoscale, true, out var scale) ? scale : AutoscaleMode.None,
                TargetPort = item.TargetPort,
                ContainerId = string.IsNullOrWhiteSpace(item.ContainerId) ? null : item.ContainerId!.Trim(),
                ContainerName = string.IsNullOrWhiteSpace(item.ContainerName) ? null : item.ContainerName!.Trim(),
                HostId = item.HostId,
                ExternalUrl = string.IsNullOrWhiteSpace(item.ExternalUrl) ? null : item.ExternalUrl!.Trim(),
                Manual = true,
                AutoDiscovered = false,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        if (conflicts.Count > 0) return new BulkAddResult(0, conflicts);

        foreach (var app in prepared)
            await _store.UpsertPortalAppAsync(app, ct).ConfigureAwait(false);

        return new BulkAddResult(prepared.Count, Array.Empty<string>());
    }

    private static IEnumerable<JsonNode> Items(JsonNode? list)
    {
        if (list is not JsonObject obj || obj["items"] is not JsonArray array) yield break;
        foreach (var node in array)
            if (node is not null) yield return node;
    }

    private static string? Str(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<string>(); }
        catch { return null; }
    }

    private static List<string> Strings(IEnumerable<JsonNode?>? nodes)
    {
        var values = new List<string>();
        if (nodes is null) return values;
        foreach (var node in nodes)
        {
            var value = Str(node);
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }
        return values;
    }

    private static string? LoadBalancerAddress(JsonNode? service)
    {
        if (service?["status"]?["loadBalancer"]?["ingress"] is JsonArray ingress)
        {
            foreach (var entry in ingress)
            {
                var ip = Str(entry?["ip"]);
                if (!string.IsNullOrWhiteSpace(ip)) return ip;
                var hostname = Str(entry?["hostname"]);
                if (!string.IsNullOrWhiteSpace(hostname)) return hostname;
            }
        }
        return Strings((service?["spec"]?["externalIPs"] as JsonArray)?.Select(n => (JsonNode?)n)).FirstOrDefault();
    }

    private static int? FirstServicePort(JsonNode? service)
    {
        if (service?["spec"]?["ports"] is not JsonArray ports) return null;
        foreach (var port in ports)
            if (port?["port"] is JsonValue value && value.TryGetValue<int>(out var number)) return number;
        return null;
    }

    private static string BuildUrl(string address, int? port)
    {
        if (address.Contains("://", StringComparison.Ordinal)) return address;
        var p = port ?? 80;
        var scheme = p is 443 or 8443 ? "https" : "http";
        return p is 80 or 443 ? $"{scheme}://{address}" : $"{scheme}://{address}:{p}";
    }

    private static string NormalizeUrl(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    private static string DescribePorts(JsonElement container)
    {
        if (!container.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var described = new List<string>();
        foreach (var port in ports.EnumerateArray())
        {
            var privatePort = port.TryGetProperty("PrivatePort", out var pp) && pp.ValueKind == JsonValueKind.Number
                ? pp.GetInt32() : (int?)null;
            var publicPort = port.TryGetProperty("PublicPort", out var pub) && pub.ValueKind == JsonValueKind.Number
                ? pub.GetInt32() : (int?)null;
            var proto = port.TryGetProperty("Type", out var t) ? t.GetString() ?? "tcp" : "tcp";
            if (privatePort is null) continue;
            described.Add(publicPort is not null ? $"{publicPort}->{privatePort}/{proto}" : $"{privatePort}/{proto}");
        }
        return described.Count == 0 ? string.Empty : "ports " + string.Join(", ", described);
    }
}
