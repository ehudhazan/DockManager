// File: src/eh.DockManager.Server/Stacks/ComposeAndConverters.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using eh.DockManager.Server.Data;
using eh.DockManager.Server.Docker;
using eh.DockManager.Server.Models;
using eh.DockManager.Server.Security;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace eh.DockManager.Server.Stacks;

/// <summary>A single compose service definition.</summary>
public sealed class ComposeService
{
    public string Name { get; set; } = string.Empty;
    public string? Image { get; set; }
    public string? Build { get; set; }
    public string? ContainerName { get; set; }
    public bool? Privileged { get; set; }
    public string? Restart { get; set; }
    public string? NetworkMode { get; set; }
    public string? Platform { get; set; }
    public string? User { get; set; }
    public string? WorkingDir { get; set; }
    public string? Entrypoint { get; set; }
    public string? Command { get; set; }
    public List<string> Ports { get; set; } = new();
    public List<string> Volumes { get; set; } = new();
    public List<string> Networks { get; set; } = new();
    public List<string> DependsOn { get; set; } = new();
    public List<string> EnvFiles { get; set; } = new();
    public Dictionary<string, string> Environment { get; set; } = new();
    public Dictionary<string, string> Labels { get; set; } = new();
    public List<string> CapAdd { get; set; } = new();
    public List<string> CapDrop { get; set; } = new();
    public string? HealthcheckTest { get; set; }
    public int? HealthcheckIntervalSeconds { get; set; }
    public string? ImageId { get; set; }
    public string? ConfigHash { get; set; }
}

/// <summary>A parsed compose document (compose.yaml).</summary>
public sealed class ComposeDocument
{
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public Dictionary<string, ComposeService> Services { get; set; } = new();
    public List<string> Networks { get; set; } = new();
    public List<string> Volumes { get; set; } = new();
    public string RawYaml { get; set; } = string.Empty;

    public string ComputeHash()
    {
        using var sha = SHA256.Create();
        var canonical = new StringBuilder();
        foreach (var svc in Services.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            canonical.Append(svc.Name).Append('|').Append(svc.Image).Append('|')
                .Append(string.Join(',', svc.Ports.OrderBy(p => p, StringComparer.Ordinal))).Append('|')
                .Append(string.Join(',', svc.Volumes.OrderBy(v => v, StringComparer.Ordinal))).Append('|')
                .Append(string.Join(',', svc.Environment.OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => e.Key + "=" + e.Value))).Append('|')
                .Append(svc.Command).Append('\n');
        }
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}

/// <summary>Parses and emits compose YAML with full anchor/alias and merge-key support.</summary>
public sealed class ComposeParser
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .Build();

    public ComposeDocument Parse(string yaml, string defaultName = "stack")
    {
        Dictionary<object, object>? root;
        try
        {
            root = _deserializer.Deserialize<Dictionary<object, object>>(yaml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Invalid compose YAML: {ex.Message}", ex);
        }

        var doc = new ComposeDocument { RawYaml = yaml, Name = defaultName };
        if (root is null) return doc;

        if (root.TryGetValue("name", out var n) && n is string nameStr && !string.IsNullOrWhiteSpace(nameStr))
            doc.Name = nameStr;
        if (root.TryGetValue("version", out var v) && v is not null)
            doc.Version = v.ToString();

        if (root.TryGetValue("services", out var servicesObj) && servicesObj is IDictionary<object, object> services)
        {
            foreach (var kv in services)
            {
                var serviceName = kv.Key.ToString() ?? string.Empty;
                var map = ResolveMerge(kv.Value);
                doc.Services[serviceName] = MapService(serviceName, map);
            }
        }

        doc.Networks = TopLevelKeys(root, "networks");
        doc.Volumes = TopLevelKeys(root, "volumes");
        return doc;
    }

    private static List<string> TopLevelKeys(Dictionary<object, object> root, string key)
    {
        if (root.TryGetValue(key, out var value) && value is IDictionary<object, object> map)
            return map.Keys.Select(k => k.ToString() ?? "").Where(s => s.Length > 0).ToList();
        return new List<string>();
    }

    /// <summary>Resolves YAML merge keys (<c>&lt;&lt;</c>) recursively after alias expansion.</summary>
    private static Dictionary<object, object> ResolveMerge(object? value)
    {
        if (value is not IDictionary<object, object> source) return new Dictionary<object, object>();

        var result = new Dictionary<object, object>();
        // Merge keys first so explicit values win.
        foreach (var kv in source)
        {
            var key = kv.Key.ToString();
            if (key != "<<") continue;
            if (kv.Value is IDictionary<object, object> mergeMap)
                foreach (var mk in ResolveMerge(mergeMap)) result[mk.Key] = mk.Value;
            else if (kv.Value is IEnumerable<object> mergeList)
                foreach (var item in mergeList)
                    foreach (var mk in ResolveMerge(item)) result[mk.Key] = mk.Value;
        }
        foreach (var kv in source)
        {
            if (kv.Key.ToString() == "<<") continue;
            result[kv.Key] = kv.Value;
        }
        return result;
    }

    private static ComposeService MapService(string name, Dictionary<object, object> map)
    {
        var svc = new ComposeService { Name = name };
        object? Get(params string[] keys)
        {
            foreach (var key in keys)
                if (map.TryGetValue(key, out var value)) return value;
            return null;
        }

        svc.Image = Get("image")?.ToString();
        svc.Build = Get("build") is IDictionary<object, object> buildMap
            ? (buildMap.TryGetValue("context", out var ctx) ? ctx?.ToString() : ".")
            : Get("build")?.ToString();
        svc.ContainerName = Get("container_name")?.ToString();
        svc.Restart = Get("restart")?.ToString();
        svc.NetworkMode = Get("network_mode")?.ToString();
        svc.Platform = Get("platform")?.ToString();
        svc.User = Get("user")?.ToString();
        svc.WorkingDir = Get("working_dir")?.ToString();
        svc.Privileged = Get("privileged") is bool p ? p : null;
        svc.Entrypoint = Stringify(Get("entrypoint"));
        svc.Command = Stringify(Get("command"));
        svc.Ports = StringList(Get("ports"));
        svc.Volumes = StringList(Get("volumes"));
        svc.Networks = NetworkList(Get("networks"));
        svc.DependsOn = StringList(Get("depends_on"));
        svc.EnvFiles = StringList(Get("env_file"));
        svc.CapAdd = StringList(Get("cap_add"));
        svc.CapDrop = StringList(Get("cap_drop"));
        svc.Environment = KeyValues(Get("environment"));
        svc.Labels = KeyValues(Get("labels"));

        if (Get("healthcheck") is IDictionary<object, object> hc)
        {
            svc.HealthcheckTest = Stringify(hc.TryGetValue("test", out var t) ? t : null);
            if (hc.TryGetValue("interval", out var interval))
                svc.HealthcheckIntervalSeconds = ParseDuration(interval?.ToString());
        }
        return svc;
    }

    private static List<string> StringList(object? value)
    {
        if (value is null) return new List<string>();
        if (value is string s) return new List<string> { s };
        if (value is IEnumerable<object> list)
            return list.Select(i => i switch
            {
                IDictionary<object, object> m when m.Count > 0 => m.Values.First()?.ToString() ?? "",
                _ => i?.ToString() ?? ""
            }).Where(x => x.Length > 0).ToList();
        return new List<string>();
    }

    private static List<string> NetworkList(object? value)
    {
        if (value is IDictionary<object, object> map) return map.Keys.Select(k => k.ToString() ?? "").ToList();
        return StringList(value);
    }

    private static Dictionary<string, string> KeyValues(object? value)
    {
        var result = new Dictionary<string, string>();
        if (value is IDictionary<object, object> map)
        {
            foreach (var kv in map)
            {
                var key = kv.Key.ToString() ?? "";
                var val = kv.Value switch
                {
                    null => "",
                    bool b => b ? "true" : "false",
                    IDictionary<object, object> m => string.Join(" ", m.Select(x => x.Key + "=" + x.Value)),
                    _ => kv.Value?.ToString() ?? ""
                };
                if (key.Length > 0) result[key] = val;
            }
        }
        else if (value is IEnumerable<object> list)
        {
            foreach (var item in list)
            {
                var entry = item?.ToString() ?? "";
                int idx = entry.IndexOf('=');
                if (idx > 0) result[entry[..idx]] = entry[(idx + 1)..];
                else if (entry.Length > 0) result[entry] = "";
            }
        }
        return result;
    }

    private static string? Stringify(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;
        if (value is IEnumerable<object> list) return string.Join(" ", list.Select(x => x?.ToString()));
        return value.ToString();
    }

    private static int? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"^(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public string Serialize(ComposeDocument doc)
    {
        var services = new Dictionary<string, object>();
        foreach (var svc in doc.Services.Values)
        {
            var map = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(svc.Image)) map["image"] = svc.Image!;
            if (!string.IsNullOrEmpty(svc.ContainerName)) map["container_name"] = svc.ContainerName!;
            if (!string.IsNullOrEmpty(svc.Restart)) map["restart"] = svc.Restart!;
            if (svc.Ports.Count > 0) map["ports"] = svc.Ports;
            if (svc.Volumes.Count > 0) map["volumes"] = svc.Volumes;
            if (svc.Environment.Count > 0) map["environment"] = svc.Environment;
            if (svc.Labels.Count > 0) map["labels"] = svc.Labels;
            if (svc.Networks.Count > 0) map["networks"] = svc.Networks;
            if (svc.DependsOn.Count > 0) map["depends_on"] = svc.DependsOn;
            if (!string.IsNullOrEmpty(svc.Command)) map["command"] = svc.Command!;
            if (svc.Privileged == true) map["privileged"] = true;
            services[svc.Name] = map;
        }
        var root = new Dictionary<string, object> { ["name"] = doc.Name, ["services"] = services };
        if (doc.Networks.Count > 0)
            root["networks"] = doc.Networks.ToDictionary(n => n, _ => (object)new Dictionary<string, object>());
        return new SerializerBuilder().WithNamingConvention(NullNamingConvention.Instance).Build().Serialize(root);
    }
}

/// <summary>Result of a compose deploy/recreate operation.</summary>
public sealed record StackDeployResult(bool Success, string StackName, string? ConfigHash,
    List<string> Recreated, List<string> Started, string? Error);

/// <summary>
/// Owns the on-disk compose stacks under <c>/data/stacks/{name}/compose.yaml</c> and orchestrates them
/// against a Docker host, including selective hash-based recreation and vault substitution.
/// </summary>
public sealed class ComposeStackService
{
    private readonly SqliteWalStore _store;
    private readonly DockerClientFactory _docker;
    private readonly CryptoVault _vault;
    private readonly ComposeParser _parser;
    private readonly ILogger<ComposeStackService> _log;
    private readonly string _stacksDir;

    public ComposeStackService(SqliteWalStore store, DockerClientFactory docker, CryptoVault vault,
        ComposeParser parser, ILogger<ComposeStackService> log)
    {
        _store = store;
        _docker = docker;
        _vault = vault;
        _parser = parser;
        _log = log;
        _stacksDir = EhConstants.StacksDir;
        Directory.CreateDirectory(_stacksDir);
    }

    public string StackDirectory(string name) => Path.Combine(_stacksDir, name);
    public string ComposePath(string name) => Path.Combine(StackDirectory(name), "compose.yaml");
    public string EnvPath(string name) => Path.Combine(StackDirectory(name), ".env");

    public List<string> ListStackNames()
    {
        if (!Directory.Exists(_stacksDir)) return new List<string>();
        return Directory.EnumerateDirectories(_stacksDir)
            .Where(d => File.Exists(Path.Combine(d, "compose.yaml")) || File.Exists(Path.Combine(d, "docker-compose.yml")))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ComposeDocument> LoadAsync(string name, CancellationToken ct = default)
    {
        var dir = StackDirectory(name);
        var path = File.Exists(ComposePath(name)) ? ComposePath(name)
            : Path.Combine(dir, "docker-compose.yml");
        if (!File.Exists(path)) throw new FileNotFoundException($"Stack '{name}' not found.", path);
        var yaml = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var doc = _parser.Parse(yaml, name);
        doc.Name = name;
        return doc;
    }

    public async Task<ComposeDocument> SaveAsync(string name, string yaml, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Stack name is required.", nameof(name));
        name = SanitizeStackName(name);
        var doc = _parser.Parse(yaml, name);
        doc.Name = name;
        var dir = StackDirectory(name);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(ComposePath(name), yaml, ct).ConfigureAwait(false);

        var meta = await _store.GetStackAsync(name, ct).ConfigureAwait(false) ?? new StackMeta { Name = name };
        meta.ConfigHash = doc.ComputeHash();
        meta.State = StackState.Stopped;
        await _store.UpsertStackAsync(meta, ct).ConfigureAwait(false);
        return doc;
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var dir = StackDirectory(name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        await _store.DeleteStackAsync(name, ct).ConfigureAwait(false);
    }

    private static string SanitizeStackName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        return sb.ToString().Trim('-', '.');
    }

    // ── Secret vault substitution ─────────────────────────────────────────

    public Task SetSecretAsync(string secretName, string plaintext, CancellationToken ct = default)
        => _store.SetConfigAsync("secret:" + secretName, _vault.EncryptString(plaintext), ct);

    public async Task<string?> GetSecretAsync(string secretName, CancellationToken ct = default)
    {
        var enc = await _store.GetConfigAsync("secret:" + secretName, ct).ConfigureAwait(false);
        return _vault.DecryptOrNull(enc);
    }

    /// <summary>Resolves <c>${VAULT:NAME}</c> (and legacy <c>${NAME}</c> env fallbacks) in memory only.</summary>
    public async Task<Dictionary<string, string>> ResolveEnvironmentAsync(ComposeService service,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(service.Environment);
        foreach (var file in service.EnvFiles)
        {
            var dir = StackDirectory(service.Name);
            var path = Path.IsPathRooted(file) ? file : Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            foreach (var line in await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                int idx = trimmed.IndexOf('=');
                if (idx > 0) result[trimmed[..idx]] = trimmed[(idx + 1)..];
            }
        }

        foreach (var key in result.Keys.ToList())
        {
            var value = result[key];
            var match = Regex.Match(value, @"^\$\{VAULT:([A-Za-z0-9_\-.]+)\}$");
            if (match.Success)
            {
                result[key] = await GetSecretAsync(match.Groups[1].Value, ct).ConfigureAwait(false)
                              ?? throw new InvalidOperationException($"Vault secret '{match.Groups[1].Value}' is missing.");
            }
        }
        return result;
    }

    // ── Deploy / recreate ─────────────────────────────────────────────────

    public async Task<StackDeployResult> DeployAsync(string name, ActorKind actor, string? actorDetail,
        bool forceRecreate = false, long? hostId = null, CancellationToken ct = default)
    {
        var doc = await LoadAsync(name, ct).ConfigureAwait(false);
        var client = hostId.HasValue
            ? await _docker.GetAsync(hostId.Value, ct).ConfigureAwait(false)
            : await _docker.GetDefaultAsync(ct).ConfigureAwait(false);

        var meta = await _store.GetStackAsync(name, ct).ConfigureAwait(false) ?? new StackMeta { Name = name };
        var configHash = doc.ComputeHash();
        var recreated = new List<string>();
        var started = new List<string>();

        try
        {
            await _store.ClearSnapshotsAsync(name, ct).ConfigureAwait(false);

            foreach (var service in doc.Services.Values)
            {
                if (string.IsNullOrEmpty(service.Image) && string.IsNullOrEmpty(service.Build))
                    continue;

                var containerName = service.ContainerName ?? $"{name}-{service.Name}";
                var environment = await ResolveEnvironmentAsync(service, ct).ConfigureAwait(false);

                string? imageId = null;
                if (!string.IsNullOrEmpty(service.Image))
                {
                    var registry = await _store.GetRegistryForHostAsync(RegistryHost(service.Image), ct)
                        .ConfigureAwait(false);
                    await PullImageAsync(client, service.Image, registry, ct).ConfigureAwait(false);
                    using var inspected = await client.InspectImageAsync(service.Image, ct).ConfigureAwait(false);
                    imageId = inspected.RootElement.ValueKind == JsonValueKind.Object
                        ? inspected.RootElement.GetProperty("Id").GetString()
                        : null;
                }

                using var existing = await client.InspectContainerAsync(containerName, ct).ConfigureAwait(false);
                var exists = existing.RootElement.ValueKind == JsonValueKind.Object;

                bool needsRecreate = forceRecreate;
                if (exists)
                {
                    var previousHash = existing.RootElement.TryGetProperty("Config", out var cfg)
                        && cfg.TryGetProperty("Labels", out var labels)
                        && labels.TryGetProperty(EhConstants.ConfigHashLabel, out var h)
                            ? h.GetString() : null;
                    if (previousHash != configHash) needsRecreate = true;

                    var currentImageId = existing.RootElement.TryGetProperty("Image", out var img)
                        ? img.GetString() : null;
                    if (imageId is not null && currentImageId is not null && !string.Equals(currentImageId, imageId))
                        needsRecreate = true;

                    if (needsRecreate)
                    {
                        await _store.SaveSnapshotAsync(new ContainerImageSnapshot
                        {
                            StackName = name,
                            ContainerName = containerName,
                            Image = service.Image ?? "",
                            ImageId = currentImageId ?? imageId ?? "",
                            PreviousImageId = currentImageId,
                            CapturedAt = DateTimeOffset.UtcNow
                        }, ct).ConfigureAwait(false);
                        await client.StopContainerAsync(containerName, 10, ct).ConfigureAwait(false);
                        await client.RemoveContainerAsync(containerName, true, false, ct).ConfigureAwait(false);
                        recreated.Add(containerName);
                    }
                    else
                    {
                        await client.StartContainerAsync(containerName, ct).ConfigureAwait(false);
                        started.Add(containerName);
                        continue;
                    }
                }

                var createConfig = BuildCreateConfig(name, service, environment, configHash, actor, actorDetail);
                await client.CreateContainerAsync(containerName, createConfig, ct).ConfigureAwait(false);
                await client.StartContainerAsync(containerName, ct).ConfigureAwait(false);
                started.Add(containerName);
                service.ImageId = imageId;
            }

            meta.ConfigHash = configHash;
            meta.State = StackState.Running;
            meta.LastDeployedAt = DateTimeOffset.UtcNow;
            meta.LastError = null;
            await _store.UpsertStackAsync(meta, ct).ConfigureAwait(false);

            await _store.AddAuditAsync(new AuditLog
            {
                Actor = actor,
                ActorDetail = actorDetail,
                Action = "stack.deploy",
                Target = name,
                Detail = $"Recreated: [{string.Join(", ", recreated)}]; Started: [{string.Join(", ", started)}]",
                Timestamp = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);

            return new StackDeployResult(true, name, configHash, recreated, started, null);
        }
        catch (Exception ex)
        {
            meta.State = StackState.Degraded;
            meta.LastError = ex.Message;
            await _store.UpsertStackAsync(meta, ct).ConfigureAwait(false);
            _log.LogError(ex, "Deploying stack {Stack} failed.", name);
            return new StackDeployResult(false, name, configHash, recreated, started, ex.Message);
        }
    }

    private static string RegistryHost(string image)
    {
        var first = image.Split('/', 2)[0];
        return first.Contains('.') || first.Contains(':') ? first : "docker.io";
    }

    private async Task PullImageAsync(DockerSocketClient client, string image, Registry? registry,
        CancellationToken ct)
    {
        var parts = image.Split('/', 2);
        var last = parts[^1];
        var tagIndex = last.LastIndexOf(':');
        string fromImage = image, tag = "latest";
        if (tagIndex > 0 && !last.Contains('/'))
        {
            tag = last[(tagIndex + 1)..];
            fromImage = image[..image.LastIndexOf(':')];
        }
        await using var stream = await client.PullImageAsync(fromImage, tag, registry, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
                throw new DockerApiException(System.Net.HttpStatusCode.BadRequest, "images/create", line);
        }
    }

    private static string BuildCreateConfig(string stackName, ComposeService service,
        Dictionary<string, string> environment, string configHash, ActorKind actor, string? actorDetail)
    {
        var labels = new Dictionary<string, string>(service.Labels)
        {
            [EhConstants.ComposeProjectLabel] = stackName,
            [EhConstants.ComposeServiceLabel] = service.Name,
            [EhConstants.ActorLabel] = actor switch
            {
                ActorKind.Mcp => "mcp",
                ActorKind.Updater => "updater",
                ActorKind.Cli => "cli",
                _ => "webui"
            },
            [EhConstants.ConfigHashLabel] = configHash
        };
        if (!string.IsNullOrEmpty(actorDetail)) labels[EhConstants.ActorDetailLabel] = actorDetail;

        var exposedPorts = new Dictionary<string, object>();
        var portBindings = new Dictionary<string, object>();
        foreach (var port in service.Ports)
        {
            var (hostIp, hostPort, containerPort, protocol) = ParsePort(port);
            var key = containerPort + "/" + protocol;
            exposedPorts[key] = new Dictionary<string, object>();
            portBindings[key] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["HostIp"] = hostIp,
                    ["HostPort"] = hostPort
                }
            };
        }

        var binds = new List<string>();
        var volumes = new Dictionary<string, object>();
        foreach (var volume in service.Volumes)
        {
            if (volume.StartsWith('.')) binds.Add(Path.GetFullPath(volume));
            else binds.Add(volume);
        }

        var hostConfig = new Dictionary<string, object?>
        {
            ["PortBindings"] = portBindings,
            ["Binds"] = binds,
            ["RestartPolicy"] = new Dictionary<string, object?>
            {
                ["Name"] = string.IsNullOrEmpty(service.Restart) ? "unless-stopped" : service.Restart,
                ["MaximumRetryCount"] = 0
            },
            ["Privileged"] = service.Privileged ?? false,
            ["NetworkMode"] = service.NetworkMode ?? "bridge",
            ["CapAdd"] = service.CapAdd,
            ["CapDrop"] = service.CapDrop
        };

        var config = new Dictionary<string, object?>
        {
            ["Image"] = service.Image,
            ["Env"] = environment.Select(kv => kv.Key + "=" + kv.Value).ToArray(),
            ["Labels"] = labels,
            ["ExposedPorts"] = exposedPorts,
            ["Volumes"] = volumes,
            ["HostConfig"] = hostConfig
        };
        if (!string.IsNullOrEmpty(service.Command)) config["Cmd"] = ShellSplit(service.Command).ToArray();
        if (!string.IsNullOrEmpty(service.Entrypoint)) config["Entrypoint"] = ShellSplit(service.Entrypoint).ToArray();
        if (!string.IsNullOrEmpty(service.User)) config["User"] = service.User;
        if (!string.IsNullOrEmpty(service.WorkingDir)) config["WorkingDir"] = service.WorkingDir;
        if (!string.IsNullOrEmpty(service.ContainerName)) config["Hostname"] = service.ContainerName;
        if (service.Networks.Count > 0)
            config["NetworkingConfig"] = new Dictionary<string, object?>
            {
                ["EndpointsConfig"] = service.Networks.ToDictionary(net => net, net =>
                    (object)new Dictionary<string, object?>())
            };

        return JsonSerializer.Serialize(config);
    }

    private static (string HostIp, string HostPort, string ContainerPort, string Protocol) ParsePort(string port)
    {
        var protocol = "tcp";
        var body = port;
        var slash = port.LastIndexOf('/');
        if (slash > 0)
        {
            protocol = port[(slash + 1)..];
            body = port[..slash];
        }
        var segments = body.Split(':');
        string hostIp = "", hostPort = "", containerPort;
        switch (segments.Length)
        {
            case 1:
                containerPort = segments[0];
                break;
            case 2:
                hostPort = segments[0];
                containerPort = segments[1];
                break;
            default:
                hostIp = segments[0];
                hostPort = segments[1];
                containerPort = segments[2];
                break;
        }
        var range = containerPort.Split('-');
        containerPort = range[0];
        if (hostPort.Contains('-')) hostPort = hostPort.Split('-')[0];
        return (hostIp, hostPort, containerPort, protocol);
    }

    public static List<string> ShellSplit(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inSingle = false, inDouble = false;
        foreach (var c in command)
        {
            if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
            if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }
            if (char.IsWhiteSpace(c) && !inSingle && !inDouble)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    // ── Stack lifecycle ───────────────────────────────────────────────────

    public async Task StartStackAsync(string name, ActorKind actor, CancellationToken ct = default)
    {
        var doc = await LoadAsync(name, ct).ConfigureAwait(false);
        var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
        foreach (var service in doc.Services.Values)
        {
            var containerName = service.ContainerName ?? $"{name}-{service.Name}";
            try { await client.StartContainerAsync(containerName, ct).ConfigureAwait(false); }
            catch (DockerApiException ex) { _log.LogWarning(ex, "Could not start {Container}.", containerName); }
        }
        await UpdateStateAsync(name, StackState.Running, ct).ConfigureAwait(false);
    }

    public async Task StopStackAsync(string name, ActorKind actor, CancellationToken ct = default)
    {
        var doc = await LoadAsync(name, ct).ConfigureAwait(false);
        var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
        foreach (var service in doc.Services.Values)
        {
            var containerName = service.ContainerName ?? $"{name}-{service.Name}";
            try { await client.StopContainerAsync(containerName, 15, ct).ConfigureAwait(false); }
            catch (DockerApiException ex) { _log.LogWarning(ex, "Could not stop {Container}.", containerName); }
        }
        await UpdateStateAsync(name, StackState.Stopped, ct).ConfigureAwait(false);
    }

    public async Task RestartStackAsync(string name, ActorKind actor, CancellationToken ct = default)
    {
        var doc = await LoadAsync(name, ct).ConfigureAwait(false);
        var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
        foreach (var service in doc.Services.Values)
        {
            var containerName = service.ContainerName ?? $"{name}-{service.Name}";
            try { await client.RestartContainerAsync(containerName, 15, ct).ConfigureAwait(false); }
            catch (DockerApiException ex) { _log.LogWarning(ex, "Could not restart {Container}.", containerName); }
        }
        await UpdateStateAsync(name, StackState.Running, ct).ConfigureAwait(false);
    }

    public async Task DownStackAsync(string name, ActorKind actor, CancellationToken ct = default)
    {
        var doc = await LoadAsync(name, ct).ConfigureAwait(false);
        var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
        foreach (var service in doc.Services.Values)
        {
            var containerName = service.ContainerName ?? $"{name}-{service.Name}";
            try { await client.RemoveContainerAsync(containerName, true, true, ct).ConfigureAwait(false); }
            catch (DockerApiException ex) { _log.LogWarning(ex, "Could not remove {Container}.", containerName); }
        }
        await UpdateStateAsync(name, StackState.Stopped, ct).ConfigureAwait(false);
    }

    private async Task UpdateStateAsync(string name, StackState state, CancellationToken ct)
    {
        var meta = await _store.GetStackAsync(name, ct).ConfigureAwait(false) ?? new StackMeta { Name = name };
        meta.State = state;
        await _store.UpsertStackAsync(meta, ct).ConfigureAwait(false);
    }

    /// <summary>Pulls the latest images, recreates changed containers, verifies health for 25s, and rolls back on failure.</summary>
    public async Task<StackDeployResult> AutoUpdateAsync(string name, CancellationToken ct = default)
    {
        var meta = await _store.GetStackAsync(name, ct).ConfigureAwait(false) ?? new StackMeta { Name = name };
        var doc = await LoadAsync(name, ct).ConfigureAwait(false);
        var containerNames = doc.Services.Values
            .Select(s => s.ContainerName ?? $"{name}-{s.Name}").ToList();

        var result = await DeployAsync(name, ActorKind.Updater, "auto-update", forceRecreate: false, ct: ct)
            .ConfigureAwait(false);

        meta = await _store.GetStackAsync(name, ct).ConfigureAwait(false) ?? meta;
        meta.LastUpdateCheckAt = DateTimeOffset.UtcNow;
        if (!result.Success)
        {
            meta.LastError = result.Error;
            await _store.UpsertStackAsync(meta, ct).ConfigureAwait(false);
            return result;
        }

        if (result.Recreated.Count == 0)
        {
            await _store.UpsertStackAsync(meta, ct).ConfigureAwait(false);
            return result;
        }

        var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
        var healthy = await VerifyHealthyAsync(client, result.Recreated, TimeSpan.FromSeconds(25), ct)
            .ConfigureAwait(false);
        if (healthy)
        {
            meta.State = StackState.Running;
            meta.LastUpdatedAt = DateTimeOffset.UtcNow;
            meta.UpdateCount++;
            meta.LastError = null;
            await _store.UpsertStackAsync(meta, ct).ConfigureAwait(false);

            if (meta.PruneOldImages)
            {
                try { await client.PruneImagesAsync(danglingOnly: false, ct).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogWarning(ex, "Image prune after update failed."); }
            }
            return result;
        }

        if (meta.RollbackOnFailure)
        {
            _log.LogWarning("Stack {Stack} failed health check after update; rolling back.", name);
            var rolledBack = await RollbackAsync(name, ct).ConfigureAwait(false);
            meta.RollbackCount++;
            meta.State = rolledBack ? StackState.Running : StackState.Degraded;
            meta.LastError = rolledBack ? "Auto-update rolled back after failed health check." : "Auto-update and rollback both failed.";
            await _store.UpsertStackAsync(meta, ct).ConfigureAwait(false);
            return result with { Success = rolledBack, Error = meta.LastError };
        }

        meta.State = StackState.Degraded;
        meta.LastError = "Containers unhealthy after auto-update.";
        await _store.UpsertStackAsync(meta, ct).ConfigureAwait(false);
        return result with { Success = false, Error = meta.LastError };
    }

    /// <summary>Recreates containers from the most recent pre-update image snapshots.</summary>
    public async Task<bool> RollbackAsync(string name, CancellationToken ct = default)
    {
        var snapshots = await _store.GetSnapshotsAsync(name, ct).ConfigureAwait(false);
        if (snapshots.Count == 0) return false;
        var doc = await LoadAsync(name, ct).ConfigureAwait(false);
        var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);

        try
        {
            foreach (var snapshot in snapshots.Where(s => !string.IsNullOrEmpty(s.PreviousImageId)))
            {
                var service = doc.Services.Values.FirstOrDefault(s =>
                    (s.ContainerName ?? $"{name}-{s.Name}") == snapshot.ContainerName);
                if (service is null) continue;

                service.Image = snapshot.PreviousImageId;
                var environment = await ResolveEnvironmentAsync(service, ct).ConfigureAwait(false);
                await client.RemoveContainerAsync(snapshot.ContainerName, true, false, ct).ConfigureAwait(false);
                var config = BuildCreateConfig(name, service, environment,
                    doc.ComputeHash() + "-rollback", ActorKind.Updater, "rollback");
                await client.CreateContainerAsync(snapshot.ContainerName, config, ct).ConfigureAwait(false);
                await client.StartContainerAsync(snapshot.ContainerName, ct).ConfigureAwait(false);
            }
            await VerifyHealthyAsync(client, snapshots.Select(s => s.ContainerName),
                TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rollback of stack {Stack} failed.", name);
            return false;
        }
    }

    private async Task<bool> VerifyHealthyAsync(DockerSocketClient client, IEnumerable<string> containerNames,
        TimeSpan window, CancellationToken ct)
    {
        var names = containerNames.ToList();
        if (names.Count == 0) return true;
        var deadline = DateTimeOffset.UtcNow + window;
        while (DateTimeOffset.UtcNow < deadline)
        {
            bool anyBad = false, allStarted = true;
            foreach (var name in names)
            {
                using var inspect = await client.InspectContainerAsync(name, ct).ConfigureAwait(false);
                if (inspect.RootElement.ValueKind != JsonValueKind.Object) { anyBad = true; continue; }
                if (!inspect.RootElement.TryGetProperty("State", out var state)) { allStarted = false; continue; }
                var running = state.TryGetProperty("Running", out var r) && r.GetBoolean();
                if (!running) { allStarted = false; continue; }
                if (state.TryGetProperty("Health", out var health)
                    && health.ValueKind == JsonValueKind.Object
                    && health.TryGetProperty("Status", out var status))
                {
                    if (status.GetString() == "unhealthy") anyBad = true;
                    else if (status.GetString() == "starting") allStarted = false;
                }
            }
            if (anyBad) return false;
            if (allStarted) return true;
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        return false;
    }
}

/// <summary>Converts a <c>docker run</c> command line into a compose document.</summary>
public sealed class DockerRunConverter
{
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.Ordinal)
    {
        "-d", "--detach", "-i", "--interactive", "-t", "--tty", "-it", "-ti", "--rm",
        "--privileged", "--init", "--read-only"
    };

    public ComposeDocument Convert(string commandLine)
    {
        var tokens = ComposeStackService.ShellSplit(commandLine);
        if (tokens.Count == 0) throw new ArgumentException("Empty docker command.");

        int start = 0;
        if (tokens[0].EndsWith("docker", StringComparison.OrdinalIgnoreCase)) start = 1;
        if (start < tokens.Count && tokens[start] == "run") start++;
        else if (start < tokens.Count && tokens[start] == "container" && start + 1 < tokens.Count && tokens[start + 1] == "run")
            start += 2;

        var service = new ComposeService { Name = "app" };
        string? image = null;
        var command = new List<string>();

        for (int i = start; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (image is not null) { command.Add(token); continue; }
            if (!token.StartsWith('-')) { image = token; continue; }
            if (BooleanFlags.Contains(token)) continue;

            string NextValue()
            {
                var inline = token.Contains('=') ? token[(token.IndexOf('=') + 1)..] : null;
                if (inline is not null) return inline;
                return i + 1 < tokens.Count ? tokens[++i] : string.Empty;
            }

            var flag = token.Contains('=') ? token[..token.IndexOf('=')] : token;
            switch (flag)
            {
                case "-p": case "--publish": service.Ports.Add(NextValue()); break;
                case "-e": case "--env":
                    var env = NextValue();
                    var eq = env.IndexOf('=');
                    if (eq > 0) service.Environment[env[..eq]] = env[(eq + 1)..];
                    else service.Environment[env] = "";
                    break;
                case "-v": case "--volume": service.Volumes.Add(NextValue()); break;
                case "--name": service.ContainerName = NextValue(); break;
                case "--restart": service.Restart = NextValue(); break;
                case "--network": case "--net": service.NetworkMode = NextValue(); break;
                case "-l": case "--label":
                    var label = NextValue();
                    var lEq = label.IndexOf('=');
                    if (lEq > 0) service.Labels[label[..lEq]] = label[(lEq + 1)..];
                    break;
                case "--env-file": service.EnvFiles.Add(NextValue()); break;
                case "-w": case "--workdir": service.WorkingDir = NextValue(); break;
                case "-u": case "--user": service.User = NextValue(); break;
                case "--cap-add": service.CapAdd.Add(NextValue()); break;
                case "--cap-drop": service.CapDrop.Add(NextValue()); break;
                case "--platform": service.Platform = NextValue(); break;
                case "-h": case "--hostname": _ = NextValue(); break;
                case "--entrypoint": service.Entrypoint = NextValue(); break;
                case "--add-host": case "--dns": case "--health-cmd": case "--memory": case "-m":
                case "--cpus": case "--gpus": case "--device": case "--mount": case "--expose":
                case "--tmpfs": case "--ulimit": case "--sysctl": case "-a": case "--attach":
                    _ = NextValue();
                    break;
                default:
                    // Unknown flag: best-effort skip of a possible value.
                    if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-')) i++;
                    break;
            }
        }

        if (image is null) throw new ArgumentException("No image specified in docker run command.");
        service.Image = image;
        if (command.Count > 0) service.Command = string.Join(' ', command.Select(QuoteIfNeeded));
        if (string.IsNullOrEmpty(service.ContainerName))
            service.Name = image.Split('/', ':')[^1].Replace('.', '-').Replace('_', '-');
        else
            service.Name = service.ContainerName;

        var doc = new ComposeDocument { Name = service.Name };
        doc.Services[service.Name] = service;
        return doc;
    }

    private static string QuoteIfNeeded(string value) =>
        value.Contains(' ') ? "\"" + value + "\"" : value;
}

/// <summary>Converts a compose document into Kubernetes Deployment + Service + Gateway API HTTPRoute manifests.</summary>
public sealed class K8sConverter
{
    public string ToYaml(ComposeDocument doc, string @namespace = "default")
    {
        var manifests = new List<Dictionary<string, object>>();
        foreach (var service in doc.Services.Values)
        {
            if (string.IsNullOrEmpty(service.Image)) continue;
            var name = Sanitize(service.Name);
            manifests.Add(Deployment(doc.Name, name, service, @namespace));
            var ports = ExtractPorts(service);
            if (ports.Count > 0)
            {
                manifests.Add(ServiceManifest(name, ports, @namespace));
                manifests.Add(HttpRoute(name, ports[0], @namespace));
            }
        }

        var serializer = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build();
        return string.Join("---\n", manifests.Select(m => serializer.Serialize(m)));
    }

    private static Dictionary<string, object> Deployment(string stack, string name, ComposeService service,
        string ns)
    {
        var ports = ExtractPorts(service);
        var container = new Dictionary<string, object>
        {
            ["name"] = name,
            ["image"] = service.Image!
        };
        if (ports.Count > 0)
            container["ports"] = ports.Select(p => new Dictionary<string, object> { ["containerPort"] = p }).ToList();
        var env = service.Environment.Select(kv => new Dictionary<string, object>
        {
            ["name"] = kv.Key,
            ["value"] = kv.Value
        }).ToList();
        if (env.Count > 0) container["env"] = env;
        if (!string.IsNullOrEmpty(service.Command))
            container["command"] = ComposeStackService.ShellSplit(service.Command);
        if (service.Volumes.Count > 0)
            container["volumeMounts"] = service.Volumes
                .Select((v, i) => new Dictionary<string, object>
                {
                    ["name"] = $"vol-{i}",
                    ["mountPath"] = v.Contains(':') ? v.Split(':')[1] : v
                }).ToList();

        var podSpec = new Dictionary<string, object>
        {
            ["containers"] = new List<object> { container }
        };
        if (service.Volumes.Count > 0)
            podSpec["volumes"] = service.Volumes.Select((v, i) => new Dictionary<string, object>
            {
                ["name"] = $"vol-{i}",
                ["emptyDir"] = new Dictionary<string, object>()
            }).ToList();

        return new Dictionary<string, object>
        {
            ["apiVersion"] = "apps/v1",
            ["kind"] = "Deployment",
            ["metadata"] = new Dictionary<string, object>
            {
                ["name"] = name,
                ["namespace"] = ns,
                ["labels"] = new Dictionary<string, object>
                {
                    ["app"] = name,
                    ["app.kubernetes.io/part-of"] = stack
                }
            },
            ["spec"] = new Dictionary<string, object>
            {
                ["replicas"] = 1,
                ["selector"] = new Dictionary<string, object>
                {
                    ["matchLabels"] = new Dictionary<string, object> { ["app"] = name }
                },
                ["template"] = new Dictionary<string, object>
                {
                    ["metadata"] = new Dictionary<string, object>
                    {
                        ["labels"] = new Dictionary<string, object> { ["app"] = name }
                    },
                    ["spec"] = podSpec
                }
            }
        };
    }

    private static Dictionary<string, object> ServiceManifest(string name, List<int> ports, string ns)
        => new()
        {
            ["apiVersion"] = "v1",
            ["kind"] = "Service",
            ["metadata"] = new Dictionary<string, object> { ["name"] = name, ["namespace"] = ns },
            ["spec"] = new Dictionary<string, object>
            {
                ["selector"] = new Dictionary<string, object> { ["app"] = name },
                ["ports"] = ports.Select(p => new Dictionary<string, object>
                {
                    ["port"] = p,
                    ["targetPort"] = p,
                    ["protocol"] = "TCP"
                }).ToList()
            }
        };

    private static Dictionary<string, object> HttpRoute(string name, int port, string ns)
        => new()
        {
            ["apiVersion"] = "gateway.networking.k8s.io/v1",
            ["kind"] = "HTTPRoute",
            ["metadata"] = new Dictionary<string, object> { ["name"] = name, ["namespace"] = ns },
            ["spec"] = new Dictionary<string, object>
            {
                ["parentRefs"] = new List<object>
                {
                    new Dictionary<string, object> { ["name"] = "eh-gateway" }
                },
                ["rules"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["matches"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["path"] = new Dictionary<string, object>
                                {
                                    ["type"] = "PathPrefix",
                                    ["value"] = "/"
                                }
                            }
                        },
                        ["backendRefs"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["name"] = name,
                                ["port"] = port
                            }
                        }
                    }
                }
            }
        };

    private static List<int> ExtractPorts(ComposeService service)
    {
        var ports = new List<int>();
        foreach (var port in service.Ports)
        {
            var body = port.Contains('/') ? port[..port.LastIndexOf('/')] : port;
            var segments = body.Split(':');
            var containerPort = segments[^1].Split('-')[0];
            if (int.TryParse(containerPort, out var parsed)) ports.Add(parsed);
        }
        return ports.Distinct().ToList();
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
    }
}

/// <summary>Exports a running container's configuration back into a compose document.</summary>
public sealed class ContainerToComposeExporter
{
    public ComposeService ToService(JsonElement container, string? stackName, string? serviceName)
    {
        var config = container.GetProperty("Config");
        var hostConfig = container.TryGetProperty("HostConfig", out var hc) ? hc : default;
        var name = container.GetProperty("Name").GetString()?.TrimStart('/') ?? "app";

        var service = new ComposeService
        {
            Name = serviceName ?? name,
            ContainerName = name,
            Image = config.TryGetProperty("Image", out var img) ? img.GetString() : null
        };

        if (config.TryGetProperty("Cmd", out var cmd) && cmd.ValueKind == JsonValueKind.Array)
            service.Command = string.Join(' ', cmd.EnumerateArray()
                .Select(c => c.GetString()).Where(x => x is not null));

        if (config.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in env.EnumerateArray())
            {
                var value = entry.GetString();
                if (value is null) continue;
                int idx = value.IndexOf('=');
                if (idx > 0) service.Environment[value[..idx]] = value[(idx + 1)..];
            }
        }

        if (config.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labels.EnumerateObject())
            {
                if (label.Name.StartsWith("com.docker.compose", StringComparison.Ordinal) ||
                    label.Name.StartsWith("eh.dockmanager", StringComparison.Ordinal)) continue;
                service.Labels[label.Name] = label.Value.GetString() ?? "";
            }
        }

        if (hostConfig.ValueKind == JsonValueKind.Object)
        {
            if (hostConfig.TryGetProperty("PortBindings", out var pb) && pb.ValueKind == JsonValueKind.Object)
            {
                foreach (var binding in pb.EnumerateObject())
                {
                    if (binding.Value.ValueKind != JsonValueKind.Array) continue;
                    var first = binding.Value.EnumerateArray().FirstOrDefault();
                    var hostPort = first.ValueKind == JsonValueKind.Object
                        && first.TryGetProperty("HostPort", out var hp) ? hp.GetString() : null;
                    service.Ports.Add(string.IsNullOrEmpty(hostPort)
                        ? binding.Name
                        : hostPort + ":" + binding.Name);
                }
            }
            if (hostConfig.TryGetProperty("Binds", out var binds) && binds.ValueKind == JsonValueKind.Array)
                service.Volumes.AddRange(binds.EnumerateArray()
                    .Select(b => b.GetString()).Where(x => x is not null).Select(x => x!));
            if (hostConfig.TryGetProperty("RestartPolicy", out var rp) && rp.ValueKind == JsonValueKind.Object
                && rp.TryGetProperty("Name", out var rpn))
                service.Restart = rpn.GetString();
        }

        return service;
    }

    public ComposeDocument ToDocument(JsonElement container, string? stackName, string? serviceName)
    {
        var service = ToService(container, stackName, serviceName);
        var doc = new ComposeDocument { Name = stackName ?? service.Name };
        doc.Services[service.Name] = service;
        return doc;
    }
}

/// <summary>
/// Background worker that periodically pulls newer images for stacks with auto-update enabled,
/// recreates only the containers whose image digest changed, health-checks them for 25 seconds,
/// and rolls back automatically on failure. Emits webhook notifications throughout.
/// </summary>
public sealed class StackAutoUpdateWorker : BackgroundService
{
    private readonly SqliteWalStore _store;
    private readonly ComposeStackService _compose;
    private readonly IEventNotifier _notifier;
    private readonly ILogger<StackAutoUpdateWorker> _log;

    public StackAutoUpdateWorker(SqliteWalStore store, ComposeStackService compose,
        IEventNotifier notifier, ILogger<StackAutoUpdateWorker> log)
    {
        _store = store;
        _compose = compose;
        _notifier = notifier;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Auto-update cycle failed.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var stacks = await _store.GetStacksAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        foreach (var meta in stacks.Where(s => s.AutoUpdateEnabled))
        {
            var due = meta.LastUpdateCheckAt is null
                      || (now - meta.LastUpdateCheckAt.Value).TotalMinutes >= Math.Max(1, meta.AutoUpdateIntervalMinutes);
            if (!due) continue;

            _log.LogInformation("Auto-update check for stack {Stack}.", meta.Name);
            var result = await _compose.AutoUpdateAsync(meta.Name, ct).ConfigureAwait(false);
            var refreshed = await _store.GetStackAsync(meta.Name, ct).ConfigureAwait(false) ?? meta;

            if (result.Recreated.Count > 0)
            {
                await _notifier.NotifyAsync("stack.update",
                    $"Stack '{meta.Name}' updated",
                    $"Recreated {result.Recreated.Count} container(s); update #{refreshed.UpdateCount}.",
                    new { stack = meta.Name, recreated = result.Recreated, success = result.Success }, ct)
                    .ConfigureAwait(false);
            }

            if (refreshed.RollbackCount > meta.RollbackCount)
            {
                await _notifier.NotifyAsync("stack.rollback",
                    $"Stack '{meta.Name}' rolled back",
                    refreshed.LastError ?? "Rolled back after failed health check.",
                    new { stack = meta.Name }, ct).ConfigureAwait(false);
            }
        }
    }
}

