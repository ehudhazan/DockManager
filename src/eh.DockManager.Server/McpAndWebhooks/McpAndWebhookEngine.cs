// File: src/eh.DockManager.Server/McpAndWebhooks/McpAndWebhookEngine.cs
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using eh.DockManager.Server.Data;
using eh.DockManager.Server.Docker;
using eh.DockManager.Server.Kubernetes;
using eh.DockManager.Server.Models;
using eh.DockManager.Server.Portal;
using eh.DockManager.Server.Security;
using eh.DockManager.Server.Stacks;

namespace eh.DockManager.Server.McpAndWebhooks;

/// <summary>Creates and validates scoped MCP bearer tokens (<c>ehdm_…</c>).</summary>
public sealed class McpApiKeyService
{
    private readonly SqliteWalStore _store;

    public McpApiKeyService(SqliteWalStore store) => _store = store;

    public async Task<(McpApiKey Key, string Plaintext)> CreateAsync(string name, string scopes,
        CancellationToken ct = default)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var plaintext = "ehdm_" + secret;
        var key = new McpApiKey
        {
            Name = name,
            Prefix = plaintext[..Math.Min(12, plaintext.Length)],
            TokenHash = Hash(plaintext),
            Scopes = scopes,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _store.AddMcpApiKeyAsync(key, ct).ConfigureAwait(false);
        return (key, plaintext);
    }

    /// <summary>Issues a fresh secret for an existing key (same id/name/scopes) and returns it once.</summary>
    public async Task<(McpApiKey Key, string Plaintext)?> RotateAsync(long id, CancellationToken ct = default)
    {
        var key = await _store.GetMcpApiKeyByIdAsync(id, ct).ConfigureAwait(false);
        if (key is null) return null;
        var plaintext = "ehdm_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        key.Prefix = plaintext[..Math.Min(12, plaintext.Length)];
        key.TokenHash = Hash(plaintext);
        key.Enabled = true;
        key.LastUsedAt = null;
        await _store.UpsertMcpApiKeyAsync(key, ct).ConfigureAwait(false);
        return (key, plaintext);
    }

    public async Task<McpApiKey?> ValidateAsync(string bearer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearer) || !bearer.StartsWith("ehdm_", StringComparison.Ordinal)) return null;
        var key = await _store.FindMcpApiKeyByHashAsync(Hash(bearer), ct).ConfigureAwait(false);
        // A disabled/revoked key must not authenticate.
        if (key is null || !key.Enabled) return null;
        await _store.TouchMcpApiKeyAsync(key.Id, ct).ConfigureAwait(false);
        return key;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
        .ToLowerInvariant();
}

/// <summary>JSON-RPC 2.0 Streamable HTTP endpoint implementing the Model Context Protocol tools surface.</summary>
/// <summary>
/// Parsed MCP permission scopes. Grants are separated by <c>;</c> and actions within a grant by <c>+</c>:
/// <code>
///   *:*                         everything
///   *:read                      read (and list) on every resource
///   stacks:list+read+write      named actions on one resource
///   containers:read; images:delete
///   stacks:write:nextcloud      limit a grant to a named resource instance (glob allowed)
/// </code>
/// Legacy single tokens (<c>read</c>, <c>write</c>, <c>read,write</c>) are accepted and mean
/// <c>*:read</c> / <c>*:write</c>. Action hierarchy: <c>write</c> implies <c>read</c> implies
/// <c>list</c>; <c>delete</c> is independent and must be granted explicitly.
/// </summary>
public sealed class McpScopes
{
    private readonly record struct Grant(string Resource, string Action, string Target);

    private readonly List<Grant> _grants = new();

    public string Raw { get; }

    private McpScopes(string raw) => Raw = raw;

    public IReadOnlyList<string> Describe()
        => _grants.Select(g => g.Target == "*" ? $"{g.Resource}:{g.Action}" : $"{g.Resource}:{g.Action}:{g.Target}")
            .Distinct().ToList();

    public static McpScopes Parse(string? scopes)
    {
        var parsed = new McpScopes(scopes ?? string.Empty);
        var raw = (scopes ?? string.Empty).Trim();
        if (raw.Length == 0) return parsed;

        var entries = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length <= 1 && !raw.Contains(';'))
            entries = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in entries)
        {
            var parts = entry.Split(':', 3);
            if (parts.Length == 1)
            {
                parsed._grants.Add(new Grant("*", Normalize(parts[0]), "*"));
                continue;
            }
            var resource = parts[0].Length > 0 ? parts[0].ToLowerInvariant() : "*";
            var target = parts.Length > 2 && parts[2].Length > 0 ? parts[2].ToLowerInvariant() : "*";
            foreach (var action in parts[1].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                parsed._grants.Add(new Grant(resource, Normalize(action), target));
        }
        return parsed;
    }

    private static string Normalize(string action)
    {
        var value = action.Trim().ToLowerInvariant();
        return value.Length == 0 ? "*" : value;
    }

    /// <summary>True when the key may perform <paramref name="action"/> on <paramref name="resource"/>.</summary>
    public bool Allows(string resource, string action, string? target)
    {
        var normalized = resource.ToLowerInvariant();
        return _grants.Any(g => MatchesResource(g, normalized) && MatchesAction(g.Action, action)
                                && MatchesTarget(g.Target, target));
    }

    /// <summary>True when the key has any grant for the resource+action, ignoring the target (used for tools/list).</summary>
    public bool AllowsResourceAction(string resource, string action)
    {
        var normalized = resource.ToLowerInvariant();
        return _grants.Any(g => MatchesResource(g, normalized) && MatchesAction(g.Action, action));
    }

    /// <summary>True when every grant covering the resource is restricted to named targets.</summary>
    public bool TargetLimited(string resource)
    {
        var normalized = resource.ToLowerInvariant();
        var relevant = _grants.Where(g => MatchesResource(g, normalized)).ToList();
        return relevant.Count > 0 && relevant.TrueForAll(g => g.Target != "*");
    }

    public bool AllowsTarget(string resource, string target)
        => _grants.Any(g => MatchesResource(g, resource.ToLowerInvariant()) && MatchesTarget(g.Target, target));

    private static bool MatchesResource(Grant grant, string resource)
        => grant.Resource == "*" || grant.Resource == resource;

    private static bool MatchesAction(string granted, string requested)
    {
        if (granted == "*" || granted == requested) return true;
        return requested switch
        {
            "list" => granted is "read" or "write",
            "read" => granted is "write",
            _ => false
        };
    }

    private static bool MatchesTarget(string granted, string? target)
    {
        if (granted == "*") return true;
        if (string.IsNullOrEmpty(target)) return false;
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(granted).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(target.ToLowerInvariant(), pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

/// <summary>JSON-RPC 2.0 Streamable HTTP endpoint implementing the Model Context Protocol tools surface.</summary>
public sealed class McpServer
{
    private const string ProtocolVersion = "2025-06-18";

    private readonly ComposeStackService _compose;
    private readonly ComposeParser _parser;
    private readonly ComposeSchemaValidator _validator;
    private readonly DockerClientFactory _docker;
    private readonly KubeClusterService _kube;
    private readonly SqliteWalStore _store;
    private readonly Portal.PortalProxyConfigurator _portal;
    private readonly Portal.PortalDiscoveryService _discovery;
    private readonly Security.CertificateManager _certs;
    private readonly Security.CryptoVault _vault;
    private readonly WebhookNotifier _notifier;
    private readonly WakeOnLanService _wol;
    private readonly ILogger<McpServer> _log;

    public McpServer(ComposeStackService compose, ComposeParser parser, ComposeSchemaValidator validator,
        DockerClientFactory docker,
        KubeClusterService kube, SqliteWalStore store, Portal.PortalProxyConfigurator portal,
        Portal.PortalDiscoveryService discovery, Security.CertificateManager certs, Security.CryptoVault vault,
        WebhookNotifier notifier, WakeOnLanService wol, ILogger<McpServer> log)
    {
        _compose = compose;
        _parser = parser;
        _validator = validator;
        _docker = docker;
        _kube = kube;
        _store = store;
        _portal = portal;
        _discovery = discovery;
        _certs = certs;
        _vault = vault;
        _notifier = notifier;
        _wol = wol;
        _log = log;
    }

    /// <summary>Handles one JSON-RPC request. Returns null for notifications.</summary>
    public async Task<JsonNode?> HandleAsync(JsonElement request, McpApiKey key, CancellationToken ct)
    {
        string? id = null;
        JsonNode? idNode = null;
        if (request.TryGetProperty("id", out var idElem) && idElem.ValueKind is not JsonValueKind.Null)
        {
            idNode = JsonNode.Parse(idElem.GetRawText());
            id = idElem.ToString();
        }

        var method = request.TryGetProperty("method", out var m) ? m.GetString() : null;
        var scopes = McpScopes.Parse(key.Scopes);

        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(idNode, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "eh.DockManager", ["version"] = "1.0.0" },
                        ["_meta"] = new JsonObject
                        {
                            ["key"] = key.Name,
                            ["scopes"] = new JsonArray(scopes.Describe().Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
                        }
                    });

                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;

                case "ping":
                    return Result(idNode, new JsonObject());

                case "tools/list":
                    return Result(idNode, new JsonObject
                    {
                        ["tools"] = ToolDefinitions(scopes),
                        ["_meta"] = new JsonObject { ["key"] = key.Name, ["scopes"] = scopes.Raw }
                    });

                case "tools/call":
                    return await CallToolAsync(request, idNode, key, scopes, ct).ConfigureAwait(false);

                default:
                    return Error(idNode, -32601, $"Method '{method}' not found.");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MCP tool execution failed for {Method}.", method);
            return Error(idNode, -32603, ex.Message);
        }
    }

    private async Task<JsonNode?> CallToolAsync(JsonElement request, JsonNode? idNode, McpApiKey key,
        McpScopes scopes, CancellationToken ct)
    {
        if (!request.TryGetProperty("params", out var p)) return Error(idNode, -32602, "Missing params.");

        var toolName = p.TryGetProperty("name", out var n) ? n.GetString() : null;
        var args = p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;

        if (string.IsNullOrEmpty(toolName)) return Error(idNode, -32602, "Missing tool name.");
        if (!Access.TryGetValue(toolName, out var access))
            return Error(idNode, -32602, $"Unknown tool '{toolName}'.");

        string Arg(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";
        int ArgInt(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        bool ArgBool(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            && (v.ValueKind == JsonValueKind.True
                || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));
        string[] ArgArray(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToArray()
                : Array.Empty<string>();

        var resource = access.Resource;
        var action = access.Action;
        var target = access.TargetArg is null ? null : Arg(access.TargetArg);
        // container_action can also remove a container; that needs the independent `delete` grant.
        if (toolName == "container_action" && string.Equals(Arg("action"), "remove", StringComparison.OrdinalIgnoreCase))
            action = "delete";

        if (!scopes.Allows(resource, action, target))
        {
            var scopeHint = $"{resource}:{action}" + (access.TargetArg is null ? "" : ":<name>");
            return Error(idNode, -32003,
                $"Key '{key.Name}' is not permitted to {action} {resource}" +
                (access.TargetArg is null ? "." : $" '{target}'.") +
                $" Grant '{scopeHint}' (or '*:*') on the key to allow it.");
        }

        async Task Audit(string actionName, string? targetName, string? detail, bool success = true)
            => await _store.AddAuditAsync(new AuditLog
            {
                Actor = ActorKind.Mcp,
                ActorDetail = key.Name,
                Action = actionName,
                Target = targetName,
                Detail = detail,
                Success = success,
                Timestamp = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);

        switch (toolName)
        {
            // ── Stacks ───────────────────────────────────────────────────────
            case "list_stacks":
            {
                var stacks = await _store.GetStacksAsync(ct).ConfigureAwait(false);
                var names = _compose.ListStackNames();
                var array = new JsonArray();
                foreach (var name in names)
                {
                    var meta = stacks.FirstOrDefault(s => s.Name == name);
                    array.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["state"] = (meta?.State ?? StackState.Unknown).ToString(),
                        ["autoUpdate"] = meta?.AutoUpdateEnabled ?? false,
                        ["updateCount"] = meta?.UpdateCount ?? 0
                    });
                }
                return TextResult(idNode, FilterTargets(scopes, "stack", array, "name").ToJsonString());
            }

            case "get_stack":
            {
                var name = Arg("name");
                var path = _compose.ComposePath(name);
                var yaml = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : "";
                var meta = await _store.GetStackAsync(name, ct).ConfigureAwait(false);
                return TextResult(idNode, new JsonObject { ["name"] = name, ["yaml"] = yaml, ["meta"] = meta?.ToString() }.ToJsonString());
            }

            case "validate_stack":
            {
                var yaml = Arg("yaml");
                var name = Arg("name");
                if (string.IsNullOrWhiteSpace(yaml) && !string.IsNullOrWhiteSpace(name))
                {
                    var path = _compose.ComposePath(name);
                    if (!File.Exists(path)) return Error(idNode, -32602, $"Stack '{name}' not found.");
                    yaml = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                }
                if (string.IsNullOrWhiteSpace(yaml))
                    return Error(idNode, -32602, "Provide 'yaml' to validate, or a saved stack 'name'.");

                var validation = _validator.Validate(yaml);
                var issues = new JsonArray(validation.Issues.Select(issue => (JsonNode)new JsonObject
                {
                    ["line"] = issue.Line,
                    ["column"] = issue.Column,
                    ["message"] = issue.Message,
                    ["severity"] = issue.Severity,
                    ["path"] = issue.Path
                }).ToArray());
                return TextResult(idNode, new JsonObject
                {
                    ["valid"] = validation.Valid,
                    ["name"] = validation.Name is null ? null : JsonValue.Create(validation.Name),
                    ["serviceCount"] = validation.ServiceCount,
                    ["issues"] = issues
                }.ToJsonString(), !validation.Valid);
            }

            case "deploy_stack":
            {
                var name = Arg("name");
                var result = await _compose.DeployAsync(name, ActorKind.Mcp, key.Name, ct: ct).ConfigureAwait(false);
                await _notifier.NotifyAsync("stack.deploy", $"Stack '{name}' deployed",
                    result.Success ? "Deploy succeeded." : result.Error!, result, ct).ConfigureAwait(false);
                return TextResult(idNode, JsonSerializer.Serialize(result), !result.Success);
            }

            case "save_stack":
            {
                var name = Arg("name");
                var yaml = Arg("yaml");
                if (string.IsNullOrWhiteSpace(yaml)) return Error(idNode, -32602, "yaml is required.");
                var doc = await _compose.SaveAsync(name, yaml, ct).ConfigureAwait(false);
                await Audit("stack.save", name, null);
                return TextResult(idNode, new JsonObject
                {
                    ["saved"] = true,
                    ["services"] = new JsonArray(doc.Services.Keys.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
                }.ToJsonString());
            }

            case "delete_stack":
            {
                var name = Arg("name");
                await _compose.DeleteAsync(name, ct).ConfigureAwait(false);
                await Audit("stack.delete", name, null);
                return TextResult(idNode, $"Stack '{name}' deleted.");
            }

            case "stack_action":
            {
                var name = Arg("name");
                var actionName = Arg("action");
                switch (actionName)
                {
                    case "start": await _compose.StartStackAsync(name, ActorKind.Mcp, ct).ConfigureAwait(false); break;
                    case "stop": await _compose.StopStackAsync(name, ActorKind.Mcp, ct).ConfigureAwait(false); break;
                    case "restart": await _compose.RestartStackAsync(name, ActorKind.Mcp, ct).ConfigureAwait(false); break;
                    case "down": await _compose.DownStackAsync(name, ActorKind.Mcp, ct).ConfigureAwait(false); break;
                    case "update":
                        var updated = await _compose.AutoUpdateAsync(name, ct).ConfigureAwait(false);
                        await Audit("stack.update", name, null, updated.Success);
                        return TextResult(idNode, JsonSerializer.Serialize(updated), !updated.Success);
                    default: return Error(idNode, -32602, $"Unknown stack action '{actionName}'.");
                }
                await Audit("stack." + actionName, name, null);
                return TextResult(idNode, $"Stack '{name}' {actionName} completed.");
            }

            case "set_stack_autoupdate":
            {
                var name = Arg("name");
                var meta = await _store.GetStackAsync(name, ct).ConfigureAwait(false) ?? new StackMeta { Name = name };
                meta.AutoUpdateEnabled = ArgBool("enabled");
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("intervalMinutes", out _))
                    meta.AutoUpdateIntervalMinutes = ArgInt("intervalMinutes");
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("pruneOldImages", out _))
                    meta.PruneOldImages = ArgBool("pruneOldImages");
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("rollbackOnFailure", out _))
                    meta.RollbackOnFailure = ArgBool("rollbackOnFailure");
                await _store.UpsertStackAsync(meta, ct).ConfigureAwait(false);
                await Audit("stack.autoupdate", name, meta.AutoUpdateEnabled ? "enabled" : "disabled");
                return TextResult(idNode, $"Auto-update for '{name}' set to {meta.AutoUpdateEnabled}.");
            }

            // ── Converters ───────────────────────────────────────────────────
            case "convert_docker_run":
            {
                var doc = new DockerRunConverter().Convert(Arg("command"));
                return TextResult(idNode, _parser.Serialize(doc));
            }

            case "convert_compose_to_k8s":
            {
                var yaml = Arg("yaml");
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    var doc = await _compose.LoadAsync(Arg("stack"), ct).ConfigureAwait(false);
                    return TextResult(idNode, new K8sConverter().ToYaml(doc));
                }
                return TextResult(idNode, new K8sConverter().ToYaml(_parser.Parse(yaml, "converted")));
            }

            // ── Containers ───────────────────────────────────────────────────
            case "list_containers":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                using var doc = await client.ListContainersAsync(true, ct).ConfigureAwait(false);
                var array = new JsonArray(doc.RootElement.EnumerateArray()
                    .Select(e => (JsonNode)JsonNode.Parse(e.GetRawText())!).ToArray());
                return TextResult(idNode, FilterTargets(scopes, "container", array, "Names", "Id").ToJsonString());
            }

            case "get_container":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                using var doc = await client.InspectContainerAsync(Arg("id"), ct).ConfigureAwait(false);
                return TextResult(idNode, doc.RootElement.GetRawText());
            }

            case "container_action":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                var id = Arg("id");
                switch (Arg("action"))
                {
                    case "start": await client.StartContainerAsync(id, ct).ConfigureAwait(false); break;
                    case "stop": await client.StopContainerAsync(id, 10, ct).ConfigureAwait(false); break;
                    case "restart": await client.RestartContainerAsync(id, 10, ct).ConfigureAwait(false); break;
                    case "remove": await client.RemoveContainerAsync(id, true, true, ct).ConfigureAwait(false); break;
                    case "pause": await client.PauseContainerAsync(id, ct).ConfigureAwait(false); break;
                    case "unpause": await client.UnpauseContainerAsync(id, ct).ConfigureAwait(false); break;
                    default: return Error(idNode, -32602, "Unknown container action.");
                }
                await Audit("container." + Arg("action"), id, null);
                return TextResult(idNode, $"Container '{id}' {Arg("action")} completed.");
            }

            case "get_container_logs":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                var id = Arg("id");
                var tail = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("tail", out _) ? ArgInt("tail") : 200;
                await using var stream = await client.GetLogsStreamAsync(id, false, tail, true, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                await foreach (var (_, data) in DockerStreamDemuxer.DemuxAsync(stream, ct).ConfigureAwait(false))
                    sb.Append(Encoding.UTF8.GetString(data));
                var text = sb.ToString();
                if (text.Length == 0)
                {
                    await using var raw = await client.GetLogsStreamAsync(id, false, tail, true, ct).ConfigureAwait(false);
                    using var reader = new StreamReader(raw);
                    text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                }
                return TextResult(idNode, text);
            }

            case "exec_command":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                var id = Arg("id");
                var command = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("command", out var cmd)
                    && cmd.ValueKind == JsonValueKind.Array
                        ? cmd.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                        : new List<string> { "sh", "-c", Arg("command") };
                var (exitCode, output) = await client.RunCommandAsync(id, command, ct).ConfigureAwait(false);
                await Audit("container.exec", id, string.Join(' ', command), exitCode == 0);
                return TextResult(idNode, new JsonObject { ["exitCode"] = exitCode, ["output"] = output }.ToJsonString(),
                    exitCode != 0);
            }

            // ── Images & registries ──────────────────────────────────────────
            case "list_images":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                using var doc = await client.ListImagesAsync(ct).ConfigureAwait(false);
                var array = new JsonArray(doc.RootElement.EnumerateArray()
                    .Select(e => (JsonNode)JsonNode.Parse(e.GetRawText())!).ToArray());
                return TextResult(idNode, FilterTargets(scopes, "image", array, "RepoTags", "Id").ToJsonString());
            }

            case "pull_image":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                var image = Arg("image");
                var tag = string.IsNullOrEmpty(Arg("tag")) ? "latest" : Arg("tag");
                Registry? registry = null;
                var registryId = ArgInt("registryId");
                if (registryId > 0) registry = await _store.GetRegistryByIdAsync(registryId, ct).ConfigureAwait(false);
                await using var stream = await client.PullImageAsync(image, tag, registry, ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var output = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                await Audit("image.pull", image + ":" + tag, null);
                return TextResult(idNode, output);
            }

            case "remove_image":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                var id = Arg("id");
                await client.RemoveImageAsync(id, true, ct).ConfigureAwait(false);
                await Audit("image.remove", id, null);
                return TextResult(idNode, $"Image '{id}' removed.");
            }

            case "prune_unused_images":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                using var doc = await client.PruneImagesAsync(danglingOnly: false, ct).ConfigureAwait(false);
                await Audit("image.prune", null, null);
                return TextResult(idNode, doc.RootElement.GetRawText());
            }

            case "list_registries":
            {
                var list = await _store.GetRegistriesAsync(ct).ConfigureAwait(false);
                var array = new JsonArray(list.Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = r.Id,
                    ["name"] = r.Name,
                    ["url"] = r.Url,
                    ["username"] = r.Username,
                    ["ignoreSslErrors"] = r.IgnoreSslErrors,
                    ["isDefault"] = r.IsDefault,
                    ["hasPassword"] = !string.IsNullOrEmpty(r.EncryptedPassword)
                }).ToArray());
                return TextResult(idNode, FilterTargets(scopes, "registry", array, "name").ToJsonString());
            }

            case "save_registry":
            {
                var id = ArgInt("id");
                var registry = new Registry
                {
                    Id = id,
                    Name = Arg("name"),
                    Url = string.IsNullOrEmpty(Arg("url")) ? "https://index.docker.io/v1/" : Arg("url"),
                    Username = Arg("username"),
                    IgnoreSslErrors = ArgBool("ignoreSslErrors"),
                    IsDefault = ArgBool("isDefault")
                };
                var password = Arg("password");
                if (!string.IsNullOrEmpty(password)) registry.EncryptedPassword = _vault.EncryptString(password);
                else if (id > 0)
                {
                    var existing = await _store.GetRegistryByIdAsync(id, ct).ConfigureAwait(false);
                    registry.EncryptedPassword = existing?.EncryptedPassword;
                }
                await _store.UpsertRegistryAsync(registry, ct).ConfigureAwait(false);
                await Audit("registry.save", registry.Name, null);
                return TextResult(idNode, $"Registry '{registry.Name}' saved (id {registry.Id}).");
            }

            case "delete_registry":
            {
                var id = ArgInt("id");
                await _store.DeleteRegistryAsync(id, ct).ConfigureAwait(false);
                await Audit("registry.delete", id.ToString(), null);
                return TextResult(idNode, $"Registry {id} deleted.");
            }

            // ── Docker hosts ─────────────────────────────────────────────────
            case "list_hosts":
            {
                var list = await _store.GetDockerHostsAsync(ct).ConfigureAwait(false);
                var array = new JsonArray(list.Select(h => (JsonNode)new JsonObject
                {
                    ["id"] = h.Id,
                    ["name"] = h.Name,
                    ["kind"] = h.Kind.ToString(),
                    ["endpoint"] = h.Endpoint,
                    ["enabled"] = h.Enabled,
                    ["lastSeenAt"] = h.LastSeenAt?.ToString("o"),
                    ["lastError"] = h.LastError
                }).ToArray());
                return TextResult(idNode, FilterTargets(scopes, "host", array, "name").ToJsonString());
            }

            case "test_host":
            {
                var client = await _docker.GetAsync(ArgInt("id"), ct).ConfigureAwait(false);
                var online = await client.PingAsync(ct).ConfigureAwait(false);
                return TextResult(idNode, new JsonObject { ["online"] = online }.ToJsonString());
            }

            case "wake_host":
            {
                var host = await _store.GetDockerHostByIdAsync(ArgInt("id"), ct).ConfigureAwait(false);
                if (host?.WakeOnLanMac is null) return Error(idNode, -32602, "No Wake-on-LAN MAC configured for that host.");
                var sent = await _wol.WakeAsync(host.WakeOnLanMac, host.WakeOnLanBroadcast ?? "255.255.255.255",
                    host.WakeOnLanPort, ct).ConfigureAwait(false);
                await Audit("host.wake", host.Name, null, sent);
                return TextResult(idNode, new JsonObject { ["sent"] = sent }.ToJsonString());
            }

            // ── Kubernetes ───────────────────────────────────────────────────
            case "list_clusters":
            {
                var list = await _store.GetKubeClustersAsync(ct).ConfigureAwait(false);
                var array = new JsonArray(list.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["name"] = c.Name,
                    ["enabled"] = c.Enabled,
                    ["serverUrl"] = c.ServerUrl,
                    ["lastSeenAt"] = c.LastSeenAt?.ToString("o"),
                    ["lastError"] = c.LastError
                }).ToArray());
                return TextResult(idNode, FilterTargets(scopes, "cluster", array, "name").ToJsonString());
            }

            case "list_k8s_namespaces":
            {
                var cluster = await ResolveClusterAsync(Arg("cluster"), ct).ConfigureAwait(false);
                if (cluster is null) return Error(idNode, -32602, "No Kubernetes cluster available.");
                var ns = await _kube.ListNamespacesAsync(cluster, ct).ConfigureAwait(false);
                return TextResult(idNode, ns.ToJsonString());
            }

            case "list_k8s_pods":
            {
                var cluster = await ResolveClusterAsync(Arg("cluster"), ct).ConfigureAwait(false);
                if (cluster is null) return Error(idNode, -32602, "No Kubernetes cluster available.");
                var ns = Arg("namespace");
                var pods = await _kube.ListPodsAsync(cluster, string.IsNullOrEmpty(ns) ? "*" : ns, ct).ConfigureAwait(false);
                return TextResult(idNode, pods.ToJsonString());
            }

            case "list_k8s_deployments":
            {
                var cluster = await ResolveClusterAsync(Arg("cluster"), ct).ConfigureAwait(false);
                if (cluster is null) return Error(idNode, -32602, "No Kubernetes cluster available.");
                var ns = Arg("namespace");
                var deployments = await _kube.ListDeploymentsAsync(cluster, string.IsNullOrEmpty(ns) ? "*" : ns, ct)
                    .ConfigureAwait(false);
                return TextResult(idNode, deployments.ToJsonString());
            }

            case "list_k8s_services":
            {
                var cluster = await ResolveClusterAsync(Arg("cluster"), ct).ConfigureAwait(false);
                if (cluster is null) return Error(idNode, -32602, "No Kubernetes cluster available.");
                var ns = Arg("namespace");
                var services = await _kube.ListServicesAsync(cluster, string.IsNullOrEmpty(ns) ? "*" : ns, ct)
                    .ConfigureAwait(false);
                return TextResult(idNode, services.ToJsonString());
            }

            case "restart_k8s_deployment":
            {
                var cluster = await ResolveClusterAsync(Arg("cluster"), ct).ConfigureAwait(false);
                if (cluster is null) return Error(idNode, -32602, "No Kubernetes cluster available.");
                var ns = string.IsNullOrEmpty(Arg("namespace")) ? "default" : Arg("namespace");
                var ok = await _kube.RestartDeploymentAsync(cluster, Arg("name"), ns, ct).ConfigureAwait(false);
                await Audit("cluster.deployment.restart", $"{cluster.Name}/{ns}/{Arg("name")}", null, ok);
                return TextResult(idNode, new JsonObject { ["ok"] = ok }.ToJsonString(), !ok);
            }

            case "scale_k8s_deployment":
            {
                var cluster = await ResolveClusterAsync(Arg("cluster"), ct).ConfigureAwait(false);
                if (cluster is null) return Error(idNode, -32602, "No Kubernetes cluster available.");
                var ns = string.IsNullOrEmpty(Arg("namespace")) ? "default" : Arg("namespace");
                var ok = await _kube.ScaleDeploymentAsync(cluster, Arg("name"), ns, ArgInt("replicas"), ct)
                    .ConfigureAwait(false);
                await Audit("cluster.deployment.scale", $"{cluster.Name}/{ns}/{Arg("name")}", ArgInt("replicas").ToString(), ok);
                return TextResult(idNode, new JsonObject { ["ok"] = ok }.ToJsonString(), !ok);
            }

            case "apply_k8s_manifest":
            {
                var cluster = await ResolveClusterAsync(Arg("cluster"), ct).ConfigureAwait(false);
                if (cluster is null) return Error(idNode, -32602, "No Kubernetes cluster available.");
                var yaml = Arg("yaml");
                if (string.IsNullOrWhiteSpace(yaml)) return Error(idNode, -32602, "yaml is required.");
                var ns = string.IsNullOrEmpty(Arg("namespace")) ? "default" : Arg("namespace");
                var applied = await _kube.ApplyManifestAsync(cluster, yaml, ns, ct).ConfigureAwait(false);
                await Audit("cluster.apply", cluster.Name, string.Join(',', applied));
                return TextResult(idNode, new JsonObject
                {
                    ["applied"] = new JsonArray(applied.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
                }.ToJsonString());
            }

            // ── Webhooks ─────────────────────────────────────────────────────
            case "list_webhooks":
            {
                var list = await _store.GetWebhooksAsync(ct).ConfigureAwait(false);
                var array = new JsonArray(list.Select(w => (JsonNode)new JsonObject
                {
                    ["id"] = w.Id,
                    ["name"] = w.Name,
                    ["kind"] = w.Kind.ToString(),
                    ["url"] = w.Url,
                    ["events"] = w.Events,
                    ["enabled"] = w.Enabled,
                    ["lastStatus"] = w.LastStatus
                }).ToArray());
                return TextResult(idNode, FilterTargets(scopes, "webhook", array, "name").ToJsonString());
            }

            case "save_webhook":
            {
                var id = ArgInt("id");
                var existingHooks = id > 0 ? await _store.GetWebhooksAsync(ct).ConfigureAwait(false) : new List<Webhook>();
                var hook = existingHooks.FirstOrDefault(w => w.Id == id) ?? new Webhook();
                hook.Id = id;
                hook.Name = Arg("name");
                hook.Kind = Enum.TryParse<WebhookKind>(Arg("kind"), true, out var kind) ? kind : WebhookKind.GenericHmac;
                hook.Url = Arg("url");
                hook.ChatId = Arg("chatId");
                hook.Events = string.IsNullOrEmpty(Arg("events")) ? hook.Events : Arg("events");
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("enabled", out _)) hook.Enabled = ArgBool("enabled");
                var secret = Arg("secret");
                if (!string.IsNullOrEmpty(secret)) hook.Secret = secret;
                await _store.UpsertWebhookAsync(hook, ct).ConfigureAwait(false);
                await Audit("webhook.save", hook.Name, null);
                return TextResult(idNode, $"Webhook '{hook.Name}' saved (id {hook.Id}).");
            }

            case "delete_webhook":
            {
                var id = ArgInt("id");
                await _store.DeleteWebhookAsync(id, ct).ConfigureAwait(false);
                await Audit("webhook.delete", id.ToString(), null);
                return TextResult(idNode, $"Webhook {id} deleted.");
            }

            case "test_webhook":
            {
                var id = ArgInt("id");
                var hooks = await _store.GetWebhooksAsync(ct).ConfigureAwait(false);
                var hook = hooks.FirstOrDefault(w => w.Id == id);
                if (hook is null) return Error(idNode, -32602, $"Webhook {id} not found.");
                var tested = await _notifier.TestAsync(hook, ct).ConfigureAwait(false);
                await _store.UpsertWebhookAsync(tested, ct).ConfigureAwait(false);
                return TextResult(idNode, new JsonObject { ["lastStatus"] = tested.LastStatus }.ToJsonString(),
                    tested.LastStatus != "ok");
            }

            // ── Users (all non-admin users managed; Admin accounts are protected) ──
            case "list_users":
            {
                var list = await _store.GetUsersAsync(ct).ConfigureAwait(false);
                var array = new JsonArray(list.Select(u => (JsonNode)new JsonObject
                {
                    ["id"] = u.Id,
                    ["username"] = u.Username,
                    ["email"] = u.Email,
                    ["role"] = u.Role.ToString(),
                    ["provider"] = u.Provider.ToString(),
                    ["approved"] = u.Approved,
                    ["disabled"] = u.Disabled,
                    ["totpEnabled"] = u.TotpEnabled,
                    ["protected"] = u.Role == UserRole.Admin,
                    ["createdAt"] = u.CreatedAt.ToString("o"),
                    ["lastLoginAt"] = u.LastLoginAt?.ToString("o")
                }).ToArray());
                return TextResult(idNode, FilterTargets(scopes, "user", array, "username").ToJsonString());
            }

            case "get_user":
            {
                var username = Arg("username");
                var uid = ArgInt("id");
                var user = !string.IsNullOrWhiteSpace(username)
                    ? await _store.GetUserByUsernameAsync(username.Trim(), ct).ConfigureAwait(false)
                    : uid > 0 ? await _store.GetUserByIdAsync(uid, ct).ConfigureAwait(false) : null;
                if (user is null) return Error(idNode, -32602, "User not found.");
                if (user.Role == UserRole.Admin)
                    return Error(idNode, -32003, "Admin accounts cannot be accessed or modified via MCP.");
                return TextResult(idNode, new JsonObject
                {
                    ["id"] = user.Id,
                    ["username"] = user.Username,
                    ["email"] = user.Email,
                    ["role"] = user.Role.ToString(),
                    ["provider"] = user.Provider.ToString(),
                    ["approved"] = user.Approved,
                    ["disabled"] = user.Disabled,
                    ["totpEnabled"] = user.TotpEnabled,
                    ["createdAt"] = user.CreatedAt.ToString("o"),
                    ["lastLoginAt"] = user.LastLoginAt?.ToString("o")
                }.ToJsonString());
            }

            case "create_user":
            {
                var username = Arg("username").Trim();
                var password = Arg("password");
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                    return Error(idNode, -32602, "username and password are required.");
                if (password.Length < 8)
                    return Error(idNode, -32602, "password must be at least 8 characters.");
                var roleArg = Arg("role");
                var role = string.IsNullOrWhiteSpace(roleArg)
                    ? UserRole.Viewer
                    : Enum.TryParse<UserRole>(roleArg, true, out var parsed)
                        ? parsed
                        : throw new InvalidOperationException($"Invalid role '{roleArg}'.");
                if (role == UserRole.Admin)
                    return Error(idNode, -32003, "MCP cannot create Admin users. Only Viewer or Pending roles are permitted.");
                if (await _store.GetUserByUsernameAsync(username, ct).ConfigureAwait(false) is not null)
                    return Error(idNode, -32602, $"User '{username}' already exists.");

                var (hash, salt) = PasswordHasher.Hash(password);
                var user = new User
                {
                    Username = username,
                    Email = Arg("email").Trim(),
                    Role = role,
                    Provider = IdentityProvider.Local,
                    PasswordHash = hash,
                    PasswordSalt = salt,
                    Approved = role != UserRole.Pending,
                    Disabled = false
                };
                await _store.UpsertUserAsync(user, ct).ConfigureAwait(false);
                await Audit("user.create", user.Username, $"role={user.Role}");
                return TextResult(idNode, new JsonObject
                {
                    ["id"] = user.Id,
                    ["username"] = user.Username,
                    ["role"] = user.Role.ToString(),
                    ["approved"] = user.Approved
                }.ToJsonString());
            }

            case "update_user":
            {
                var username = Arg("username").Trim();
                var uid = ArgInt("id");
                var user = !string.IsNullOrWhiteSpace(username)
                    ? await _store.GetUserByUsernameAsync(username, ct).ConfigureAwait(false)
                    : uid > 0 ? await _store.GetUserByIdAsync(uid, ct).ConfigureAwait(false) : null;
                if (user is null) return Error(idNode, -32602, "User not found.");
                if (user.Role == UserRole.Admin)
                    return Error(idNode, -32003, "MCP cannot modify Admin users.");

                var roleArg = Arg("role");
                if (!string.IsNullOrWhiteSpace(roleArg))
                {
                    if (!Enum.TryParse<UserRole>(roleArg, true, out var parsed))
                        return Error(idNode, -32602, $"Invalid role '{roleArg}'.");
                    if (parsed == UserRole.Admin)
                        return Error(idNode, -32003, "MCP cannot promote users to Admin.");
                    user.Role = parsed;
                    if (parsed != UserRole.Pending) user.Approved = true;
                }
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("disabled", out _))
                    user.Disabled = ArgBool("disabled");
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("approved", out _))
                    user.Approved = ArgBool("approved");
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("email", out _))
                    user.Email = Arg("email").Trim();
                var newPassword = Arg("password");
                if (!string.IsNullOrWhiteSpace(newPassword))
                {
                    if (newPassword.Length < 8)
                        return Error(idNode, -32602, "password must be at least 8 characters.");
                    var (hash, salt) = PasswordHasher.Hash(newPassword);
                    user.PasswordHash = hash;
                    user.PasswordSalt = salt;
                }
                await _store.UpsertUserAsync(user, ct).ConfigureAwait(false);
                await Audit("user.update", user.Username, $"role={user.Role}, disabled={user.Disabled}");
                return TextResult(idNode, new JsonObject
                {
                    ["updated"] = true,
                    ["id"] = user.Id,
                    ["username"] = user.Username,
                    ["role"] = user.Role.ToString(),
                    ["disabled"] = user.Disabled,
                    ["approved"] = user.Approved
                }.ToJsonString());
            }

            case "delete_user":
            {
                var username = Arg("username").Trim();
                var uid = ArgInt("id");
                var user = !string.IsNullOrWhiteSpace(username)
                    ? await _store.GetUserByUsernameAsync(username, ct).ConfigureAwait(false)
                    : uid > 0 ? await _store.GetUserByIdAsync(uid, ct).ConfigureAwait(false) : null;
                if (user is null) return Error(idNode, -32602, "User not found.");
                if (user.Role == UserRole.Admin)
                    return Error(idNode, -32003, "MCP cannot delete Admin users.");
                await _store.DeleteUserAsync(user.Id, ct).ConfigureAwait(false);
                await Audit("user.delete", user.Username, null);
                return TextResult(idNode, $"User '{user.Username}' deleted.");
            }

            // ── Portal ───────────────────────────────────────────────────────
            case "list_portal_apps":
            {
                var list = await _store.GetPortalAppsAsync(ct).ConfigureAwait(false);
                var array = new JsonArray(list.Select(a => (JsonNode)new JsonObject
                {
                    ["id"] = a.Id,
                    ["subdomain"] = a.Subdomain,
                    ["name"] = a.Name,
                    ["category"] = a.Category,
                    ["auth"] = a.Auth.ToString(),
                    ["autoscale"] = a.Autoscale.ToString(),
                    ["hidden"] = a.Hidden,
                    ["manual"] = a.Manual,
                    ["externalUrl"] = a.ExternalUrl
                }).ToArray());
                return TextResult(idNode, FilterTargets(scopes, "portal", array, "subdomain", "name").ToJsonString());
            }

            case "save_portal_app":
            {
                var subdomain = Arg("subdomain");
                if (string.IsNullOrWhiteSpace(subdomain)) return Error(idNode, -32602, "subdomain is required.");
                var existing = await _store.GetPortalAppBySubdomainAsync(subdomain, ct).ConfigureAwait(false);
                var app = existing ?? new PortalApp { Subdomain = subdomain };
                app.Name = string.IsNullOrEmpty(Arg("name")) ? app.Name : Arg("name");
                if (!string.IsNullOrEmpty(Arg("category"))) app.Category = Arg("category");
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("icon", out _)) app.Icon = Arg("icon");
                if (Enum.TryParse<PortalAuthMode>(Arg("auth"), true, out var auth)) app.Auth = auth;
                if (Enum.TryParse<AutoscaleMode>(Arg("autoscale"), true, out var autoscale)) app.Autoscale = autoscale;
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("targetPort", out _))
                    app.TargetPort = ArgInt("targetPort");
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("externalUrl", out _))
                    app.ExternalUrl = Arg("externalUrl");
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("hidden", out _)) app.Hidden = ArgBool("hidden");
                app.Manual = true;
                await _store.UpsertPortalAppAsync(app, ct).ConfigureAwait(false);
                await _portal.RebuildAsync(ct).ConfigureAwait(false);
                await Audit("portal.save", subdomain, null);
                return TextResult(idNode, $"Portal app '{subdomain}' saved.");
            }

            case "delete_portal_app":
            {
                var subdomain = Arg("subdomain");
                await _store.DeletePortalAppAsync(subdomain, ct).ConfigureAwait(false);
                await _portal.RebuildAsync(ct).ConfigureAwait(false);
                await Audit("portal.delete", subdomain, null);
                return TextResult(idNode, $"Portal app '{subdomain}' deleted.");
            }

            case "refresh_portal":
            {
                await _discovery.ReconcileAsync(ct).ConfigureAwait(false);
                await Audit("portal.refresh", null, null);
                return TextResult(idNode, "Portal discovery refreshed.");
            }

            // ── System settings & audit ──────────────────────────────────────
            case "get_settings":
            {
                var domain = _portal.PortalDomain ?? "";
                var lanIp = await _store.GetConfigAsync("portal.lanIp", ct).ConfigureAwait(false) ?? "";
                var cert = _certs.GetServerCertificateInfo();
                return TextResult(idNode, new JsonObject
                {
                    ["portalDomain"] = domain,
                    ["lanIp"] = lanIp,
                    ["dataDirectory"] = EhConstants.DataDir,
                    ["certificateSubject"] = cert.Subject,
                    ["certificateThumbprint"] = cert.Thumbprint,
                    ["certificateDnsNames"] = new JsonArray(cert.DnsNames.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                    ["certificateIpAddresses"] = new JsonArray(cert.IpAddresses.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
                }.ToJsonString());
            }

            case "update_settings":
            {
                var domain = Arg("portalDomain").Trim().Trim('.').ToLowerInvariant();
                if (domain.Length > 0)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(domain,
                            "^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$"))
                        return Error(idNode, -32602, "Enter a valid DNS name such as dock.lan or home.example.com.");
                    await _portal.SetPortalDomainAsync(domain, ct).ConfigureAwait(false);
                    _certs.SetPortalDomain(domain);
                }
                var lanIp = Arg("lanIp").Trim();
                if (lanIp.Length > 0) await _store.SetConfigAsync("portal.lanIp", lanIp, ct).ConfigureAwait(false);
                if (ArgBool("regenerateCertificate") && domain.Length > 0)
                {
                    var dns = new List<string> { domain, "*." + domain, "localhost" };
                    var ips = new List<string> { "127.0.0.1", "::1" };
                    if (lanIp.Length > 0) ips.Add(lanIp);
                    _certs.RegenerateServerCertificate(dns, ips);
                }
                await Audit("settings.update", domain.Length > 0 ? domain : null,
                    ArgBool("regenerateCertificate") ? "domain+certificate" : "domain");
                return TextResult(idNode, "Settings updated.");
            }

            case "list_audit":
            {
                var limit = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("limit", out _) ? Math.Clamp(ArgInt("limit"), 1, 200) : 50;
                var logs = await _store.GetAuditLogsAsync(limit, ct).ConfigureAwait(false);
                var array = new JsonArray(logs.Select(l => (JsonNode)new JsonObject
                {
                    ["id"] = l.Id,
                    ["actor"] = l.Actor.ToString(),
                    ["actorDetail"] = l.ActorDetail,
                    ["action"] = l.Action,
                    ["target"] = l.Target,
                    ["detail"] = l.Detail,
                    ["success"] = l.Success,
                    ["timestamp"] = l.Timestamp.ToString("o")
                }).ToArray());
                return TextResult(idNode, array.ToJsonString());
            }

            // ── Containers (create / files) ─────────────────────────────────
            case "create_container":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                var image = Arg("image");
                if (string.IsNullOrWhiteSpace(image)) return Error(idNode, -32602, "image is required.");
                var name = Arg("name");
                if (string.IsNullOrWhiteSpace(name)) name = "app-" + Guid.NewGuid().ToString("n")[..8];

                var exposed = new Dictionary<string, object>();
                var bindings = new Dictionary<string, object>();
                foreach (var spec in ArgArray("ports"))
                {
                    var raw = spec.Trim();
                    if (raw.Length == 0) continue;
                    var protocol = "tcp";
                    var slash = raw.LastIndexOf('/');
                    if (slash >= 0) { protocol = raw[(slash + 1)..].ToLowerInvariant(); raw = raw[..slash]; }
                    string hostPort, containerPort;
                    var colon = raw.IndexOf(':');
                    if (colon >= 0) { hostPort = raw[..colon].Trim(); containerPort = raw[(colon + 1)..].Trim(); }
                    else { hostPort = string.Empty; containerPort = raw; }
                    if (!int.TryParse(containerPort, out var parsedContainer) || parsedContainer <= 0)
                        return Error(idNode, -32602, $"Invalid port mapping: {spec}");
                    if (hostPort.Length > 0 && !int.TryParse(hostPort, out _))
                        return Error(idNode, -32602, $"Invalid host port: {spec}");
                    var portKey = $"{parsedContainer}/{protocol}";
                    exposed[portKey] = new { };
                    bindings[portKey] = new[] { new { HostIp = string.Empty, HostPort = hostPort } };
                }

                var environment = ArgArray("env").Select(e => e.Trim()).Where(e => e.Length > 0).ToArray();
                if (environment.Any(e => !e.Contains('=')))
                    return Error(idNode, -32602, "Environment entries must be KEY=VALUE.");
                var restart = Arg("restartPolicy");
                if (restart is not ("no" or "always" or "unless-stopped" or "on-failure")) restart = "unless-stopped";
                var start = !(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("start", out var startElem)
                    && startElem.ValueKind == JsonValueKind.False);

                var config = JsonSerializer.Serialize(new
                {
                    Image = image,
                    Cmd = string.IsNullOrWhiteSpace(Arg("command"))
                        ? null : ComposeStackService.ShellSplit(Arg("command")).ToArray(),
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
                try
                {
                    var createdId = await client.CreateContainerAsync(name, config, ct).ConfigureAwait(false);
                    if (start) await client.StartContainerAsync(createdId, ct).ConfigureAwait(false);
                    await Audit("container.create", name, image);
                    return TextResult(idNode, new JsonObject { ["id"] = createdId, ["name"] = name }.ToJsonString());
                }
                catch (DockerApiException ex)
                {
                    return Error(idNode, -32603, ex.Message);
                }
            }

            case "list_container_files":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                var id = Arg("id");
                var path = Arg("path");
                if (path.Length == 0) path = "/";
                var entries = await client.ListArchiveAsync(id, path, ct).ConfigureAwait(false);
                var array = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
                {
                    ["name"] = e.Name,
                    ["isDirectory"] = e.IsDirectory,
                    ["size"] = e.Size,
                    ["modified"] = e.Modified.ToString("o"),
                    ["mode"] = e.Mode
                }).ToArray());
                return TextResult(idNode, array.ToJsonString());
            }

            case "read_container_file":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                var id = Arg("id");
                var path = Arg("path");
                if (string.IsNullOrWhiteSpace(path)) return Error(idNode, -32602, "path is required.");
                var bytes = await client.ReadArchiveFileAsync(id, path, ct).ConfigureAwait(false);
                if (bytes.Length > 128 * 1024)
                    return Error(idNode, -32602, $"File is {bytes.Length} bytes; the 128 KB MCP limit was exceeded.");
                var binary = Array.IndexOf(bytes, (byte)0) >= 0;
                await Audit("container.file.read", id, path);
                return TextResult(idNode, new JsonObject
                {
                    ["path"] = path,
                    ["encoding"] = binary ? "base64" : "utf8",
                    ["size"] = bytes.Length,
                    ["content"] = binary ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes)
                }.ToJsonString());
            }

            case "get_image":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                using var doc = await client.InspectImageAsync(Arg("id"), ct).ConfigureAwait(false);
                return TextResult(idNode, doc.RootElement.GetRawText());
            }

            // ── Hosts (save / delete) ───────────────────────────────────────
            case "save_host":
            {
                var id = ArgInt("id");
                var host = id > 0 ? await _store.GetDockerHostByIdAsync(id, ct).ConfigureAwait(false) : null;
                host ??= new DockerHost();
                host.Id = id;
                host.Name = Arg("name");
                if (Arg("endpoint").Length > 0) host.Endpoint = Arg("endpoint");
                if (Enum.TryParse<DockerHostKind>(Arg("kind"), true, out var kind)) host.Kind = kind;
                if (host.Kind != DockerHostKind.UnixSocket)
                    host.Endpoint = DockerSocketClient.NormalizeEndpoint(host.Endpoint);
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("enabled", out _)) host.Enabled = ArgBool("enabled");
                if (Arg("wakeOnLanMac").Length > 0) host.WakeOnLanMac = Arg("wakeOnLanMac");
                if (Arg("wakeOnLanBroadcast").Length > 0) host.WakeOnLanBroadcast = Arg("wakeOnLanBroadcast");
                if (Arg("clientCertPem").Length > 0) host.ClientCertPem = Arg("clientCertPem");
                if (Arg("clientKeyPem").Length > 0) host.ClientKeyPem = Arg("clientKeyPem");
                if (Arg("caCertPem").Length > 0) host.CaCertPem = Arg("caCertPem");
                await _store.UpsertDockerHostAsync(host, ct).ConfigureAwait(false);
                _docker.Invalidate(host.Id);
                await Audit("host.save", host.Name, null);
                return TextResult(idNode, new JsonObject { ["id"] = host.Id, ["name"] = host.Name }.ToJsonString());
            }

            case "delete_host":
            {
                var id = ArgInt("id");
                if (id <= 0) return Error(idNode, -32602, "id is required.");
                await _store.DeleteDockerHostAsync(id, ct).ConfigureAwait(false);
                _docker.Invalidate(id);
                await Audit("host.delete", id.ToString(), null);
                return TextResult(idNode, $"Host {id} deleted.");
            }

            // ── Kubernetes (cluster CRUD, HTTPRoutes, pod logs) ─────────────
            case "save_cluster":
            {
                var id = ArgInt("id");
                var cluster = id > 0 ? await _store.GetKubeClusterByIdAsync(id, ct).ConfigureAwait(false) : null;
                cluster ??= new KubeCluster();
                cluster.Id = id;
                cluster.Name = Arg("name");
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("enabled", out _)) cluster.Enabled = ArgBool("enabled");
                if (Arg("kubeConfigYaml").Length > 0) cluster.KubeConfigYaml = Arg("kubeConfigYaml");
                if (Arg("token").Length > 0) cluster.Token = Arg("token");
                if (Arg("clientCertPem").Length > 0) cluster.ClientCertPem = Arg("clientCertPem");
                if (Arg("clientKeyPem").Length > 0) cluster.ClientKeyPem = Arg("clientKeyPem");
                if (Arg("caCertPem").Length > 0) cluster.CaCertPem = Arg("caCertPem");
                if (Arg("serverUrl").Length > 0) cluster.ServerUrl = Arg("serverUrl");
                if (Arg("context").Length > 0) cluster.Context = Arg("context");
                await _store.UpsertKubeClusterAsync(cluster, ct).ConfigureAwait(false);
                await Audit("cluster.save", cluster.Name, null);
                return TextResult(idNode, new JsonObject { ["id"] = cluster.Id, ["name"] = cluster.Name }.ToJsonString());
            }

            case "delete_cluster":
            {
                var id = ArgInt("id");
                if (id <= 0) return Error(idNode, -32602, "id is required.");
                await _store.DeleteKubeClusterAsync(id, ct).ConfigureAwait(false);
                await Audit("cluster.delete", id.ToString(), null);
                return TextResult(idNode, $"Cluster {id} deleted.");
            }

            case "list_k8s_httproutes":
            {
                var cluster = await ResolveClusterAsync(Arg("cluster"), ct).ConfigureAwait(false);
                if (cluster is null) return Error(idNode, -32602, "No Kubernetes cluster available.");
                var routes = await _kube.ListHttpRoutesAsync(cluster, Arg("namespace"), ct).ConfigureAwait(false);
                return TextResult(idNode, routes.ToJsonString());
            }

            case "get_k8s_pod_logs":
            {
                var cluster = await ResolveClusterAsync(Arg("cluster"), ct).ConfigureAwait(false);
                if (cluster is null) return Error(idNode, -32602, "No Kubernetes cluster available.");
                var pod = Arg("pod");
                if (pod.Length == 0) return Error(idNode, -32602, "pod is required.");
                var ns = Arg("namespace");
                if (ns.Length == 0) ns = "default";
                var tail = ArgInt("tail");
                if (tail <= 0) tail = 200;
                var logs = await _kube.GetPodLogsAsync(cluster, pod, ns, Arg("container"), tail, ct).ConfigureAwait(false);
                return TextResult(idNode, logs);
            }

            // ── Stack secrets ───────────────────────────────────────────────
            case "save_stack_secret":
            {
                var name = Arg("name");
                var secretName = Arg("secretName");
                if (name.Length == 0 || secretName.Length == 0)
                    return Error(idNode, -32602, "name and secretName are required.");
                await _compose.SetSecretAsync(secretName, Arg("value"), ct).ConfigureAwait(false);
                await Audit("stack.secret", name, secretName);
                return TextResult(idNode, $"Secret '{secretName}' stored.");
            }

            default:
                return Error(idNode, -32602, $"Unknown tool '{toolName}'.");
        }
    }

    private async Task<KubeCluster?> ResolveClusterAsync(string? name, CancellationToken ct)
    {
        var clusters = await _store.GetKubeClustersAsync(ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(name)
            ? clusters.FirstOrDefault(c => c.Enabled)
            : clusters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonArray FilterTargets(McpScopes scopes, string resource, JsonArray items, params string[] nameKeys)
    {
        if (!scopes.TargetLimited(resource)) return items;
        var filtered = new JsonArray();
        foreach (var item in items)
        {
            var name = nameKeys
                .Select(key => item?[key] switch
                {
                    JsonArray array when array.Count > 0 => array[0]?.GetValue<string>()?.TrimStart('/'),
                    JsonValue value => value.GetValue<string>(),
                    _ => null
                })
                .FirstOrDefault(value => !string.IsNullOrEmpty(value));
            if (name is not null && scopes.AllowsTarget(resource, name))
                filtered.Add(item?.DeepClone());
        }
        return filtered;
    }

    private static JsonNode TextResult(JsonNode? id, string text, bool isError = false)
        => Result(id, new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = isError
        });

    private static JsonNode Result(JsonNode? id, JsonNode result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    private static JsonNode Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    /// <summary>Resource + required action for each tool, and which argument names the resource instance.</summary>
    private sealed record ToolAccess(string Resource, string Action, string? TargetArg);

    private static readonly Dictionary<string, ToolAccess> Access = new(StringComparer.Ordinal)
    {
        ["list_stacks"] = new("stack", "list", null),
        ["get_stack"] = new("stack", "read", "name"),
        ["validate_stack"] = new("stack", "read", null),
        ["deploy_stack"] = new("stack", "write", "name"),
        ["save_stack"] = new("stack", "write", "name"),
        ["delete_stack"] = new("stack", "delete", "name"),
        ["stack_action"] = new("stack", "write", "name"),
        ["set_stack_autoupdate"] = new("stack", "write", "name"),
        ["convert_docker_run"] = new("convert", "read", null),
        ["convert_compose_to_k8s"] = new("convert", "read", null),
        ["list_containers"] = new("container", "list", null),
        ["get_container"] = new("container", "read", "id"),
        ["container_action"] = new("container", "write", "id"),
        ["get_container_logs"] = new("container", "read", "id"),
        ["exec_command"] = new("exec", "write", "id"),
        ["list_images"] = new("image", "list", null),
        ["pull_image"] = new("image", "write", "image"),
        ["remove_image"] = new("image", "delete", "id"),
        ["prune_unused_images"] = new("image", "delete", null),
        ["list_registries"] = new("registry", "list", null),
        ["save_registry"] = new("registry", "write", "name"),
        ["delete_registry"] = new("registry", "delete", "id"),
        ["list_hosts"] = new("host", "list", null),
        ["test_host"] = new("host", "read", "id"),
        ["wake_host"] = new("host", "write", "id"),
        ["list_clusters"] = new("cluster", "list", null),
        ["list_k8s_namespaces"] = new("cluster", "read", "cluster"),
        ["list_k8s_pods"] = new("cluster", "read", "cluster"),
        ["list_k8s_deployments"] = new("cluster", "read", "cluster"),
        ["list_k8s_services"] = new("cluster", "read", "cluster"),
        ["restart_k8s_deployment"] = new("cluster", "write", "cluster"),
        ["scale_k8s_deployment"] = new("cluster", "write", "cluster"),
        ["apply_k8s_manifest"] = new("cluster", "write", "cluster"),
        ["list_webhooks"] = new("webhook", "list", null),
        ["save_webhook"] = new("webhook", "write", "name"),
        ["delete_webhook"] = new("webhook", "delete", "id"),
        ["test_webhook"] = new("webhook", "write", "id"),
        ["list_users"] = new("user", "list", null),
        ["get_user"] = new("user", "read", "username"),
        ["create_user"] = new("user", "write", "username"),
        ["update_user"] = new("user", "write", "username"),
        ["delete_user"] = new("user", "delete", "username"),
        ["list_portal_apps"] = new("portal", "list", null),
        ["save_portal_app"] = new("portal", "write", "subdomain"),
        ["delete_portal_app"] = new("portal", "delete", "subdomain"),
        ["refresh_portal"] = new("portal", "write", null),
        ["get_settings"] = new("system", "read", null),
        ["update_settings"] = new("system", "write", null),
        ["list_audit"] = new("audit", "list", null),
        ["create_container"] = new("container", "write", "name"),
        ["list_container_files"] = new("container", "read", "id"),
        ["read_container_file"] = new("container", "read", "id"),
        ["get_image"] = new("image", "read", "id"),
        ["save_host"] = new("host", "write", "name"),
        ["delete_host"] = new("host", "delete", "id"),
        ["save_cluster"] = new("cluster", "write", "name"),
        ["delete_cluster"] = new("cluster", "delete", "id"),
        ["list_k8s_httproutes"] = new("cluster", "read", "cluster"),
        ["get_k8s_pod_logs"] = new("cluster", "read", "cluster"),
        ["save_stack_secret"] = new("stack", "write", "name")
    };

    private static JsonArray ToolDefinitions(McpScopes scopes)
    {
        static JsonObject Tool(string name, string description, JsonObject properties, JsonArray required)
            => new()
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required
                }
            };

        static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
        static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };
        static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };
        static JsonObject Arr(string description) => new() { ["type"] = "array", ["items"] = Str("Value"), ["description"] = description };
        static JsonArray Req(params string[] names) => new(names.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());

        var tools = new List<(string Name, JsonNode Definition)>
        {
            ("list_stacks", Tool("list_stacks", "List all compose stacks with their state and auto-update counters.", new JsonObject(), Req())),
            ("get_stack", Tool("get_stack", "Return a stack's compose YAML and metadata.", new JsonObject { ["name"] = Str("Stack name") }, Req("name"))),
            ("validate_stack", Tool("validate_stack", "Validate compose YAML against the schema: parse errors with line/column, unknown keys, invalid enum values and bad port mappings. Pass 'yaml', or a saved stack 'name'.", new JsonObject { ["yaml"] = Str("Compose YAML to validate"), ["name"] = Str("Existing stack name (alternative to yaml)") }, Req())),
            ("deploy_stack", Tool("deploy_stack", "Deploy (or reconcile) a compose stack by name.", new JsonObject { ["name"] = Str("Stack name") }, Req("name"))),
            ("save_stack", Tool("save_stack", "Save a compose stack's YAML.", new JsonObject { ["name"] = Str("Stack name"), ["yaml"] = Str("Compose YAML") }, Req("name", "yaml"))),
            ("delete_stack", Tool("delete_stack", "Delete a compose stack and its files.", new JsonObject { ["name"] = Str("Stack name") }, Req("name"))),
            ("stack_action", Tool("stack_action", "Run a lifecycle action on a stack.", new JsonObject { ["name"] = Str("Stack name"), ["action"] = Str("start | stop | restart | down | update") }, Req("name", "action"))),
            ("set_stack_autoupdate", Tool("set_stack_autoupdate", "Configure per-stack auto-update.", new JsonObject { ["name"] = Str("Stack name"), ["enabled"] = Bool("Enabled"), ["intervalMinutes"] = Num("Check interval"), ["pruneOldImages"] = Bool("Prune old images"), ["rollbackOnFailure"] = Bool("Roll back on failure") }, Req("name"))),
            ("convert_docker_run", Tool("convert_docker_run", "Convert a 'docker run' command line into a compose document.", new JsonObject { ["command"] = Str("The docker run command") }, Req("command"))),
            ("convert_compose_to_k8s", Tool("convert_compose_to_k8s", "Convert compose YAML (or a saved stack) into Kubernetes manifests.", new JsonObject { ["yaml"] = Str("Compose YAML"), ["stack"] = Str("Existing stack name (alternative to yaml)") }, Req())),
            ("list_containers", Tool("list_containers", "List containers on the default Docker host.", new JsonObject(), Req())),
            ("get_container", Tool("get_container", "Inspect one container.", new JsonObject { ["id"] = Str("Container id or name") }, Req("id"))),
            ("container_action", Tool("container_action", "Start, stop, restart, pause, unpause, or remove a container.", new JsonObject { ["id"] = Str("Container id or name"), ["action"] = Str("start | stop | restart | remove | pause | unpause") }, Req("id", "action"))),
            ("get_container_logs", Tool("get_container_logs", "Fetch recent logs from a container.", new JsonObject { ["id"] = Str("Container id or name"), ["tail"] = Num("Number of lines") }, Req("id"))),
            ("exec_command", Tool("exec_command", "Execute a command inside a container and return its output.", new JsonObject { ["id"] = Str("Container id or name"), ["command"] = new JsonObject { ["type"] = "array", ["items"] = Str("Argument") } }, Req("id"))),
            ("list_images", Tool("list_images", "List local images.", new JsonObject(), Req())),
            ("pull_image", Tool("pull_image", "Pull an image from a registry.", new JsonObject { ["image"] = Str("Repository"), ["tag"] = Str("Tag"), ["registryId"] = Num("Registry id") }, Req("image"))),
            ("remove_image", Tool("remove_image", "Remove a local image.", new JsonObject { ["id"] = Str("Image id") }, Req("id"))),
            ("prune_unused_images", Tool("prune_unused_images", "Remove all unused images from the default Docker host.", new JsonObject(), Req())),
            ("list_registries", Tool("list_registries", "List configured image registries.", new JsonObject(), Req())),
            ("save_registry", Tool("save_registry", "Create or update a private registry.", new JsonObject { ["id"] = Num("Existing id"), ["name"] = Str("Name"), ["url"] = Str("Registry URL"), ["username"] = Str("Username"), ["password"] = Str("Password / token"), ["ignoreSslErrors"] = Bool("Ignore SSL"), ["isDefault"] = Bool("Default registry") }, Req("name"))),
            ("delete_registry", Tool("delete_registry", "Delete a registry.", new JsonObject { ["id"] = Num("Registry id") }, Req("id"))),
            ("list_hosts", Tool("list_hosts", "List Docker hosts.", new JsonObject(), Req())),
            ("test_host", Tool("test_host", "Check that a Docker host is reachable.", new JsonObject { ["id"] = Num("Host id") }, Req("id"))),
            ("wake_host", Tool("wake_host", "Send a Wake-on-LAN packet to a host.", new JsonObject { ["id"] = Num("Host id") }, Req("id"))),
            ("list_clusters", Tool("list_clusters", "List Kubernetes clusters.", new JsonObject(), Req())),
            ("list_k8s_namespaces", Tool("list_k8s_namespaces", "List namespaces in a cluster.", new JsonObject { ["cluster"] = Str("Cluster name (optional)") }, Req())),
            ("list_k8s_pods", Tool("list_k8s_pods", "List pods across a cluster.", new JsonObject { ["cluster"] = Str("Cluster name (optional)"), ["namespace"] = Str("Namespace, or '*' for all") }, Req())),
            ("list_k8s_deployments", Tool("list_k8s_deployments", "List deployments in a cluster.", new JsonObject { ["cluster"] = Str("Cluster name (optional)"), ["namespace"] = Str("Namespace, or '*' for all") }, Req())),
            ("list_k8s_services", Tool("list_k8s_services", "List services in a cluster.", new JsonObject { ["cluster"] = Str("Cluster name (optional)"), ["namespace"] = Str("Namespace, or '*' for all") }, Req())),
            ("restart_k8s_deployment", Tool("restart_k8s_deployment", "Roll out (restart) a deployment.", new JsonObject { ["cluster"] = Str("Cluster name (optional)"), ["namespace"] = Str("Namespace"), ["name"] = Str("Deployment name") }, Req("name"))),
            ("scale_k8s_deployment", Tool("scale_k8s_deployment", "Scale a deployment.", new JsonObject { ["cluster"] = Str("Cluster name (optional)"), ["namespace"] = Str("Namespace"), ["name"] = Str("Deployment name"), ["replicas"] = Num("Replica count") }, Req("name", "replicas"))),
            ("apply_k8s_manifest", Tool("apply_k8s_manifest", "Apply a multi-document Kubernetes manifest.", new JsonObject { ["cluster"] = Str("Cluster name (optional)"), ["yaml"] = Str("Manifest YAML"), ["namespace"] = Str("Default namespace") }, Req("yaml"))),
            ("list_webhooks", Tool("list_webhooks", "List webhook notifications.", new JsonObject(), Req())),
            ("save_webhook", Tool("save_webhook", "Create or update a webhook.", new JsonObject { ["id"] = Num("Existing id"), ["name"] = Str("Name"), ["kind"] = Str("Discord | Telegram | Slack | Ntfy | Gotify | GenericHmac"), ["url"] = Str("Webhook URL"), ["chatId"] = Str("Telegram chat id"), ["secret"] = Str("Secret / token"), ["events"] = Str("Comma-separated events"), ["enabled"] = Bool("Enabled") }, Req("name", "url"))),
            ("delete_webhook", Tool("delete_webhook", "Delete a webhook.", new JsonObject { ["id"] = Num("Webhook id") }, Req("id"))),
            ("test_webhook", Tool("test_webhook", "Send a test notification through a webhook.", new JsonObject { ["id"] = Num("Webhook id") }, Req("id"))),
            ("list_users", Tool("list_users", "List user accounts (Admin accounts are marked protected and cannot be modified via MCP).", new JsonObject(), Req())),
            ("get_user", Tool("get_user", "Inspect a non-admin user account by username or id.", new JsonObject { ["username"] = Str("Username"), ["id"] = Num("User id (optional)") }, Req())),
            ("create_user", Tool("create_user", "Create a non-admin user account (Viewer or Pending only; Admin is forbidden).", new JsonObject { ["username"] = Str("Username"), ["password"] = Str("Password (>= 8 chars)"), ["email"] = Str("Email (optional)"), ["role"] = Str("Viewer | Pending") }, Req("username", "password"))),
            ("update_user", Tool("update_user", "Update or approve a non-admin user (Admin accounts and promotion to Admin are forbidden).", new JsonObject { ["username"] = Str("Target username"), ["id"] = Num("Target user id (alternative)"), ["role"] = Str("Viewer | Pending"), ["disabled"] = Bool("Disable account"), ["approved"] = Bool("Approve account"), ["email"] = Str("Email"), ["password"] = Str("New password (>= 8 chars)") }, Req())),
            ("delete_user", Tool("delete_user", "Delete a non-admin user account (Admin accounts cannot be deleted).", new JsonObject { ["username"] = Str("Target username"), ["id"] = Num("Target user id (alternative)") }, Req())),
            ("list_portal_apps", Tool("list_portal_apps", "List portal applications.", new JsonObject(), Req())),
            ("save_portal_app", Tool("save_portal_app", "Create or update a portal app.", new JsonObject { ["subdomain"] = Str("Subdomain"), ["name"] = Str("Display name"), ["category"] = Str("Category"), ["icon"] = Str("Icon URL"), ["auth"] = Str("Public | Sso | Admin"), ["autoscale"] = Str("None | Idle15m | Idle1h"), ["targetPort"] = Num("Container port"), ["externalUrl"] = Str("External URL"), ["hidden"] = Bool("Hide from the portal") }, Req("subdomain"))),
            ("delete_portal_app", Tool("delete_portal_app", "Delete a portal app.", new JsonObject { ["subdomain"] = Str("Subdomain") }, Req("subdomain"))),
            ("refresh_portal", Tool("refresh_portal", "Re-run portal auto-discovery.", new JsonObject(), Req())),
            ("get_settings", Tool("get_settings", "Read portal domain, LAN IP and certificate details.", new JsonObject(), Req())),
            ("update_settings", Tool("update_settings", "Update portal domain / LAN IP (optionally regenerate the certificate).", new JsonObject { ["portalDomain"] = Str("Portal domain"), ["lanIp"] = Str("LAN IP"), ["regenerateCertificate"] = Bool("Regenerate certificate") }, Req())),
            ("list_audit", Tool("list_audit", "Read the recent audit log (actor, action, target).", new JsonObject { ["limit"] = Num("Max entries (<=200)") }, Req())),
            ("create_container", Tool("create_container", "Create and start a container from an image.", new JsonObject { ["image"] = Str("Image reference"), ["name"] = Str("Container name (optional)"), ["command"] = Str("Command line (optional)"), ["env"] = Arr("KEY=VALUE entries"), ["ports"] = Arr("host:container[/proto] mappings"), ["restartPolicy"] = Str("no | always | unless-stopped | on-failure"), ["start"] = Bool("Start immediately (default true)") }, Req("image"))),
            ("list_container_files", Tool("list_container_files", "List a directory inside a container.", new JsonObject { ["id"] = Str("Container id or name"), ["path"] = Str("Directory path") }, Req("id"))),
            ("read_container_file", Tool("read_container_file", "Read a file from a container (text or base64, capped at 128 KB).", new JsonObject { ["id"] = Str("Container id or name"), ["path"] = Str("File path") }, Req("id", "path"))),
            ("get_image", Tool("get_image", "Inspect a local image.", new JsonObject { ["id"] = Str("Image id or reference") }, Req("id"))),
            ("save_host", Tool("save_host", "Create or update a Docker host.", new JsonObject { ["id"] = Num("Existing id"), ["name"] = Str("Name"), ["endpoint"] = Str("Endpoint"), ["kind"] = Str("UnixSocket | HttpsMutualTls | AgentMtls"), ["enabled"] = Bool("Enabled"), ["wakeOnLanMac"] = Str("Wake-on-LAN MAC"), ["wakeOnLanBroadcast"] = Str("Broadcast address"), ["clientCertPem"] = Str("mTLS client certificate PEM"), ["clientKeyPem"] = Str("mTLS client key PEM"), ["caCertPem"] = Str("CA certificate PEM") }, Req("name", "endpoint"))),
            ("delete_host", Tool("delete_host", "Delete a Docker host.", new JsonObject { ["id"] = Num("Host id") }, Req("id"))),
            ("save_cluster", Tool("save_cluster", "Import or update a Kubernetes cluster.", new JsonObject { ["id"] = Num("Existing id"), ["name"] = Str("Name"), ["enabled"] = Bool("Enabled"), ["kubeConfigYaml"] = Str("kubeconfig YAML"), ["token"] = Str("Bearer token"), ["clientCertPem"] = Str("Client certificate PEM"), ["clientKeyPem"] = Str("Client key PEM"), ["caCertPem"] = Str("CA certificate PEM"), ["serverUrl"] = Str("API server URL"), ["context"] = Str("kubeconfig context") }, Req("name"))),
            ("delete_cluster", Tool("delete_cluster", "Delete a Kubernetes cluster.", new JsonObject { ["id"] = Num("Cluster id") }, Req("id"))),
            ("list_k8s_httproutes", Tool("list_k8s_httproutes", "List Gateway API HTTPRoutes in a namespace.", new JsonObject { ["cluster"] = Str("Cluster name (optional)"), ["namespace"] = Str("Namespace") }, Req())),
            ("get_k8s_pod_logs", Tool("get_k8s_pod_logs", "Fetch recent logs from a pod.", new JsonObject { ["cluster"] = Str("Cluster name (optional)"), ["namespace"] = Str("Namespace"), ["pod"] = Str("Pod name"), ["container"] = Str("Container (optional)"), ["tail"] = Num("Lines") }, Req("pod"))),
            ("save_stack_secret", Tool("save_stack_secret", "Store an encrypted secret for stacks.", new JsonObject { ["name"] = Str("Stack name"), ["secretName"] = Str("Secret name"), ["value"] = Str("Secret value") }, Req("name", "secretName", "value")))
        };

        var permitted = new JsonArray();
        foreach (var (name, definition) in tools)
        {
            if (Access.TryGetValue(name, out var access) && scopes.AllowsResourceAction(access.Resource, access.Action))
                permitted.Add(definition);
        }
        return permitted;
    }
}

/// <summary>
/// Fan-out notifier with native payload formats for Discord, Telegram, Slack, Ntfy, Gotify and a
/// generic HMAC-SHA256 webhook (<c>X-Hub-Signature-256</c>).
/// </summary>
public sealed class WebhookNotifier : IEventNotifier
{
    private readonly SqliteWalStore _store;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<WebhookNotifier> _log;

    public WebhookNotifier(SqliteWalStore store, IHttpClientFactory http, ILogger<WebhookNotifier> log)
    {
        _store = store;
        _http = http;
        _log = log;
    }

    public Task NotifyAsync(string eventType, string title, string message, CancellationToken ct = default)
        => NotifyAsync(eventType, title, message, null, ct);

    public async Task NotifyAsync(string eventType, string title, string message, object? payload,
        CancellationToken ct = default)
    {
        var webhooks = await _store.GetWebhooksAsync(ct).ConfigureAwait(false);
        var targets = webhooks.Where(w => w.Enabled && EventMatches(w.Events, eventType)).ToList();
        if (targets.Count == 0) return;

        var client = _http.CreateClient("webhook");
        foreach (var hook in targets)
        {
            try
            {
                await SendAsync(client, hook, eventType, title, message, payload, ct).ConfigureAwait(false);
                hook.LastFiredAt = DateTimeOffset.UtcNow;
                hook.LastStatus = "ok";
            }
            catch (Exception ex)
            {
                hook.LastStatus = "error: " + ex.Message;
                _log.LogWarning(ex, "Webhook '{Name}' ({Kind}) failed.", hook.Name, hook.Kind);
            }
            await _store.UpsertWebhookAsync(hook, ct).ConfigureAwait(false);
        }
    }

    public async Task<Webhook> TestAsync(Webhook hook, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(_http.CreateClient("webhook"), hook, "test", "eh.DockManager test",
                "This is a test notification.", null, ct).ConfigureAwait(false);
            hook.LastStatus = "ok";
            hook.LastFiredAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            hook.LastStatus = "error: " + ex.Message;
        }
        return hook;
    }

    private static bool EventMatches(string configured, string eventType)
    {
        if (string.IsNullOrWhiteSpace(configured)) return true;
        if (configured.Contains('*', StringComparison.Ordinal)) return true;
        return configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(e => string.Equals(e, eventType, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SendAsync(HttpClient client, Webhook hook, string eventType, string title, string message,
        object? payload, CancellationToken ct)
    {
        var text = $"{title}\n{message}";
        switch (hook.Kind)
        {
            case WebhookKind.Discord:
            {
                var body = new
                {
                    content = $"**{title}**\n{message}",
                    embeds = new object[]
                    {
                        new { title, description = message, footer = new { text = "eh.DockManager • " + eventType } }
                    }
                };
                await PostJsonAsync(client, hook.Url, body, ct).ConfigureAwait(false);
                break;
            }
            case WebhookKind.Telegram:
            {
                var url = hook.Url.TrimEnd('/') + "/sendMessage";
                await PostJsonAsync(client, url, new
                {
                    chat_id = hook.ChatId,
                    text = $"*{title}*\n{message}",
                    parse_mode = "Markdown"
                }, ct).ConfigureAwait(false);
                break;
            }
            case WebhookKind.Slack:
                await PostJsonAsync(client, hook.Url, new
                {
                    text = text,
                    blocks = new object[]
                    {
                        new { type = "section", text = new { type = "mrkdwn", text = $"*{title}*\n{message}" } }
                    }
                }, ct).ConfigureAwait(false);
                break;
            case WebhookKind.Ntfy:
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new StringContent(text) };
                request.Headers.TryAddWithoutValidation("Title", title);
                request.Headers.TryAddWithoutValidation("Tags", "container,server");
                if (!string.IsNullOrEmpty(hook.Secret))
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + hook.Secret);
                await SendAsync(client, request, ct).ConfigureAwait(false);
                break;
            }
            case WebhookKind.Gotify:
            {
                var url = hook.Url.TrimEnd('/') + "/message";
                if (!string.IsNullOrEmpty(hook.Secret)) url += "?token=" + Uri.EscapeDataString(hook.Secret);
                await PostJsonAsync(client, url, new { title, message, priority = 5 }, ct).ConfigureAwait(false);
                break;
            }
            case WebhookKind.GenericHmac:
            default:
            {
                var json = JsonSerializer.Serialize(new
                {
                    @event = eventType,
                    title,
                    message,
                    timestamp = DateTimeOffset.UtcNow,
                    payload
                });
                using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("X-EHDM-Event", eventType);
                if (!string.IsNullOrEmpty(hook.Secret))
                {
                    var mac = new HMACSHA256(Encoding.UTF8.GetBytes(hook.Secret));
                    var signature = Convert.ToHexString(mac.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
                    request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", "sha256=" + signature);
                }
                await SendAsync(client, request, ct).ConfigureAwait(false);
                break;
            }
        }
    }

    private static async Task PostJsonAsync(HttpClient client, string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        await SendAsync(client, request, ct).ConfigureAwait(false);
    }

    private static async Task SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Webhook returned {(int)response.StatusCode}: {body}");
        }
    }
}
