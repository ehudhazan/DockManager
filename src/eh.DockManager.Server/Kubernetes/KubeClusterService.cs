// File: src/eh.DockManager.Server/Kubernetes/KubeClusterService.cs
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using eh.DockManager.Server.Data;
using eh.DockManager.Server.Models;
using k8s;
using k8s.Models;
using YamlDotNet.Serialization;

namespace eh.DockManager.Server.Kubernetes;

/// <summary>
/// Manages multiple Kubernetes clusters imported as kubeconfig YAML or raw token/certificate material.
/// Uses <c>KubernetesClient</c> (>= 18.0.13, which carries the CVE-2025-9708 mTLS CA validation fix).
/// </summary>
public sealed class KubeClusterService
{
    private readonly SqliteWalStore _store;
    private readonly ILogger<KubeClusterService> _log;
    private readonly IDeserializer _yaml = new DeserializerBuilder().Build();

    public KubeClusterService(SqliteWalStore store, ILogger<KubeClusterService> log)
    {
        _store = store;
        _log = log;
    }

    public IKubernetes BuildClient(KubeCluster cluster)
    {
        var config = BuildConfiguration(cluster);
        return new k8s.Kubernetes(config);
    }

    public static KubernetesClientConfiguration BuildConfiguration(KubeCluster cluster)
    {
        if (!string.IsNullOrWhiteSpace(cluster.KubeConfigYaml))
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(cluster.KubeConfigYaml));
            return KubernetesClientConfiguration.BuildConfigFromConfigFile(stream,
                string.IsNullOrWhiteSpace(cluster.Context) ? null! : cluster.Context, null!);
        }

        var config = new KubernetesClientConfiguration
        {
            Host = cluster.ServerUrl,
            SkipTlsVerify = false
        };
        if (!string.IsNullOrWhiteSpace(cluster.Token)) config.AccessToken = cluster.Token;
        if (!string.IsNullOrWhiteSpace(cluster.CaCertPem))
        {
            var collection = new X509Certificate2Collection();
            foreach (var pem in SplitPem(cluster.CaCertPem))
                collection.Add(X509Certificate2.CreateFromPem(pem));
            config.SslCaCerts = collection;
        }
        if (!string.IsNullOrWhiteSpace(cluster.ClientCertPem)) config.ClientCertificateData = cluster.ClientCertPem;
        if (!string.IsNullOrWhiteSpace(cluster.ClientKeyPem)) config.ClientCertificateKeyData = cluster.ClientKeyPem;
        return config;
    }

    private static IEnumerable<string> SplitPem(string pem)
    {
        var marker = "-----END CERTIFICATE-----";
        int index = 0;
        while ((index = pem.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            var end = index + marker.Length;
            yield return pem[index..end];
            index = end;
        }
        if (index == 0) yield return pem;
    }

    public async Task<bool> TestAsync(KubeCluster cluster, CancellationToken ct = default)
    {
        try
        {
            var client = BuildClient(cluster);
            var response = await client.CoreV1.ListNamespaceWithHttpMessagesAsync(
                cancellationToken: ct).ConfigureAwait(false);
            cluster.LastSeenAt = DateTimeOffset.UtcNow;
            cluster.LastError = null;
            await _store.UpsertKubeClusterAsync(cluster, ct).ConfigureAwait(false);
            return response.Body is not null;
        }
        catch (Exception ex)
        {
            cluster.LastError = ex.Message;
            await _store.UpsertKubeClusterAsync(cluster, ct).ConfigureAwait(false);
            _log.LogWarning(ex, "Kubernetes cluster {Cluster} connectivity test failed.", cluster.Name);
            return false;
        }
    }

    public async Task<JsonNode> ListNamespacesAsync(KubeCluster cluster, CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        var response = await client.CoreV1.ListNamespaceWithHttpMessagesAsync(cancellationToken: ct)
            .ConfigureAwait(false);
        return JsonNode.Parse(JsonSerializer.Serialize(response.Body)) ?? new JsonArray();
    }

    public async Task<JsonNode> ListPodsAsync(KubeCluster cluster, string? ns, CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        JsonNode result;
        if (string.IsNullOrEmpty(ns) || ns == "*")
        {
            var response = await client.CoreV1.ListPodForAllNamespacesWithHttpMessagesAsync(cancellationToken: ct)
                .ConfigureAwait(false);
            result = JsonNode.Parse(JsonSerializer.Serialize(response.Body)) ?? new JsonArray();
        }
        else
        {
            var response = await client.CoreV1.ListNamespacedPodWithHttpMessagesAsync(ns, cancellationToken: ct)
                .ConfigureAwait(false);
            result = JsonNode.Parse(JsonSerializer.Serialize(response.Body)) ?? new JsonArray();
        }
        return result;
    }

    public async Task<JsonNode> ListDeploymentsAsync(KubeCluster cluster, string? ns, CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        if (string.IsNullOrEmpty(ns) || ns == "*")
        {
            var response = await client.AppsV1.ListDeploymentForAllNamespacesWithHttpMessagesAsync(cancellationToken: ct)
                .ConfigureAwait(false);
            return JsonNode.Parse(JsonSerializer.Serialize(response.Body)) ?? new JsonArray();
        }
        else
        {
            var response = await client.AppsV1.ListNamespacedDeploymentWithHttpMessagesAsync(ns, cancellationToken: ct)
                .ConfigureAwait(false);
            return JsonNode.Parse(JsonSerializer.Serialize(response.Body)) ?? new JsonArray();
        }
    }

    public async Task<JsonNode> ListServicesAsync(KubeCluster cluster, string? ns, CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        if (string.IsNullOrEmpty(ns) || ns == "*")
        {
            var response = await client.CoreV1.ListServiceForAllNamespacesWithHttpMessagesAsync(cancellationToken: ct)
                .ConfigureAwait(false);
            return JsonNode.Parse(JsonSerializer.Serialize(response.Body)) ?? new JsonArray();
        }
        else
        {
            var response = await client.CoreV1.ListNamespacedServiceWithHttpMessagesAsync(ns, cancellationToken: ct)
                .ConfigureAwait(false);
            return JsonNode.Parse(JsonSerializer.Serialize(response.Body)) ?? new JsonArray();
        }
    }

    /// <summary>Lists Gateway API HTTPRoutes through the custom-objects API.</summary>
    public async Task<JsonNode> ListHttpRoutesAsync(KubeCluster cluster, string? ns, CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        var targetNs = string.IsNullOrEmpty(ns) || ns == "*" ? "default" : ns;
        var response = await client.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(
            "gateway.networking.k8s.io", "v1", targetNs, "httproutes", cancellationToken: ct)
            .ConfigureAwait(false);
        return JsonNode.Parse(JsonSerializer.Serialize(response.Body)) ?? new JsonArray();
    }

    /// <summary>Lists Ingresses (networking.k8s.io/v1) across all namespaces, or one namespace.</summary>
    public async Task<JsonNode> ListIngressesAsync(KubeCluster cluster, string? ns, CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        if (string.IsNullOrEmpty(ns) || ns == "*")
        {
            var all = await client.NetworkingV1.ListIngressForAllNamespacesWithHttpMessagesAsync(cancellationToken: ct)
                .ConfigureAwait(false);
            return JsonNode.Parse(JsonSerializer.Serialize(all.Body)) ?? new JsonArray();
        }
        var response = await client.NetworkingV1.ListNamespacedIngressWithHttpMessagesAsync(ns, cancellationToken: ct)
            .ConfigureAwait(false);
        return JsonNode.Parse(JsonSerializer.Serialize(response.Body)) ?? new JsonArray();
    }

    public async Task<string> GetPodLogsAsync(KubeCluster cluster, string pod, string ns, string? container,
        int tailLines = 500, CancellationToken ct = default, bool timestamps = true)
    {
        var client = BuildClient(cluster);
        var response = await client.CoreV1.ReadNamespacedPodLogWithHttpMessagesAsync(
            pod, ns, container, follow: false, insecureSkipTLSVerifyBackend: null, limitBytes: null,
            pretty: null, previous: null, sinceSeconds: null, stream: null, tailLines: tailLines,
            timestamps: timestamps, cancellationToken: ct).ConfigureAwait(false);
        if (response.Body is null) return string.Empty;
        using var reader = new StreamReader(response.Body);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    public async Task StreamPodLogsAsync(KubeCluster cluster, string pod, string ns, string? container,
        Stream destination, CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        var response = await client.CoreV1.ReadNamespacedPodLogWithHttpMessagesAsync(
            pod, ns, container, follow: true, insecureSkipTLSVerifyBackend: null, limitBytes: null,
            pretty: null, previous: null, sinceSeconds: null, stream: null, tailLines: 200,
            timestamps: true, cancellationToken: ct).ConfigureAwait(false);
        if (response.Body is null) return;
        await response.Body.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    public async Task<bool> RestartDeploymentAsync(KubeCluster cluster, string name, string ns,
        CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        var get = await client.AppsV1.ReadNamespacedDeploymentWithHttpMessagesAsync(name, ns, cancellationToken: ct)
            .ConfigureAwait(false);
        var deployment = get.Body;
        if (deployment is null) return false;

        deployment.Spec ??= new V1DeploymentSpec();
        deployment.Spec.Template ??= new V1PodTemplateSpec();
        deployment.Spec.Template.Metadata ??= new V1ObjectMeta();
        deployment.Spec.Template.Metadata.Annotations ??= new Dictionary<string, string>();
        deployment.Spec.Template.Metadata.Annotations["kubectl.kubernetes.io/restartedAt"] =
            DateTimeOffset.UtcNow.ToString("O");

        await client.AppsV1.ReplaceNamespacedDeploymentWithHttpMessagesAsync(deployment, name, ns,
            cancellationToken: ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ScaleDeploymentAsync(KubeCluster cluster, string name, string ns, int replicas,
        CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        var get = await client.AppsV1.ReadNamespacedDeploymentWithHttpMessagesAsync(name, ns, cancellationToken: ct)
            .ConfigureAwait(false);
        var deployment = get.Body;
        if (deployment is null) return false;
        deployment.Spec ??= new V1DeploymentSpec();
        deployment.Spec.Replicas = replicas;
        await client.AppsV1.ReplaceNamespacedDeploymentWithHttpMessagesAsync(deployment, name, ns,
            cancellationToken: ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Opens an interactive exec WebSocket into a pod (v4 channel protocol).</summary>
    public async Task<WebSocket> ExecAsync(KubeCluster cluster, string pod, string ns, string? container,
        IEnumerable<string> command, CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        return await client.WebSocketNamespacedPodExecAsync(
            pod,
            ns,
            command.ToList(),
            container!,
            true,
            true,
            true,
            true,
            "v4.channel.k8s.io",
            null!,
            ct).ConfigureAwait(false);
    }

    /// <summary>Applies a multi-document Kubernetes manifest (best-effort create-or-replace).</summary>
    public async Task<List<string>> ApplyManifestAsync(KubeCluster cluster, string yaml, string defaultNamespace = "default",
        CancellationToken ct = default)
    {
        var client = BuildClient(cluster);
        var applied = new List<string>();
        foreach (var document in SplitDocuments(yaml))
        {
            var json = YamlToJson(document);
            if (json is not JsonObject obj) continue;
            var kind = obj["kind"]?.GetValue<string>();
            var meta = obj["metadata"] as JsonObject;
            var name = meta?["name"]?.GetValue<string>();
            var ns = meta?["namespace"]?.GetValue<string>() ?? defaultNamespace;
            if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(name)) continue;
            var payload = obj.ToJsonString();

            switch (kind)
            {
                case "Deployment":
                    await ApplyDeploymentAsync(client, payload, name, ns, ct).ConfigureAwait(false);
                    break;
                case "Service":
                    await ApplyServiceAsync(client, payload, name, ns, ct).ConfigureAwait(false);
                    break;
                case "Namespace":
                    await client.CoreV1.CreateNamespaceWithHttpMessagesAsync(
                        JsonSerializer.Deserialize<V1Namespace>(payload)!, cancellationToken: ct)
                        .ConfigureAwait(false);
                    break;
                case "ConfigMap":
                    await client.CoreV1.CreateNamespacedConfigMapWithHttpMessagesAsync(
                        JsonSerializer.Deserialize<V1ConfigMap>(payload)!, ns, cancellationToken: ct)
                        .ConfigureAwait(false);
                    break;
                case "HTTPRoute":
                    await client.CustomObjects.CreateNamespacedCustomObjectWithHttpMessagesAsync(
                        obj, "gateway.networking.k8s.io", "v1", ns, "httproutes", cancellationToken: ct)
                        .ConfigureAwait(false);
                    break;
                default:
                    _log.LogInformation("Skipping unsupported manifest kind {Kind}.", kind);
                    continue;
            }
            applied.Add($"{kind}/{name}");
        }
        return applied;
    }

    private async Task ApplyDeploymentAsync(IKubernetes client, string payload, string name, string ns,
        CancellationToken ct)
    {
        try
        {
            await client.AppsV1.CreateNamespacedDeploymentWithHttpMessagesAsync(
                JsonSerializer.Deserialize<V1Deployment>(payload)!, ns, cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            await client.AppsV1.ReplaceNamespacedDeploymentWithHttpMessagesAsync(
                JsonSerializer.Deserialize<V1Deployment>(payload)!, name, ns, cancellationToken: ct)
                .ConfigureAwait(false);
        }
    }

    private async Task ApplyServiceAsync(IKubernetes client, string payload, string name, string ns,
        CancellationToken ct)
    {
        try
        {
            await client.CoreV1.CreateNamespacedServiceWithHttpMessagesAsync(
                JsonSerializer.Deserialize<V1Service>(payload)!, ns, cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            await client.CoreV1.ReplaceNamespacedServiceWithHttpMessagesAsync(
                JsonSerializer.Deserialize<V1Service>(payload)!, name, ns, cancellationToken: ct)
                .ConfigureAwait(false);
        }
    }

    private IEnumerable<string> SplitDocuments(string yaml)
    {
        var docs = new List<string>();
        var current = new StringBuilder();
        foreach (var line in yaml.Split('\n'))
        {
            if (line.TrimEnd() == "---")
            {
                if (current.ToString().Trim().Length > 0) docs.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(line).Append('\n');
        }
        if (current.ToString().Trim().Length > 0) docs.Add(current.ToString());
        return docs;
    }

    /// <summary>Converts a YAML document into a <see cref="JsonNode"/> using YamlDotNet.</summary>
    public JsonNode YamlToJson(string yaml)
    {
        var root = _yaml.Deserialize<object?>(yaml);
        return ToNode(root) ?? new JsonObject();
    }

    private static JsonNode? ToNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case IDictionary<object, object> map:
                var obj = new JsonObject();
                foreach (var kv in map)
                    obj[kv.Key?.ToString() ?? ""] = ToNode(kv.Value);
                return obj;
            case IEnumerable<object> list:
                var arr = new JsonArray();
                foreach (var item in list) arr.Add(ToNode(item));
                return arr;
            case bool b:
                return JsonValue.Create(b);
            case int i:
                return JsonValue.Create(i);
            case long l:
                return JsonValue.Create(l);
            case double d:
                return JsonValue.Create(d);
            case string s:
                return JsonValue.Create(s);
            default:
                return JsonValue.Create(value.ToString());
        }
    }
}
