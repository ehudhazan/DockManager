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

    public async Task<McpApiKey?> ValidateAsync(string bearer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearer) || !bearer.StartsWith("ehdm_", StringComparison.Ordinal)) return null;
        var key = await _store.FindMcpApiKeyByHashAsync(Hash(bearer), ct).ConfigureAwait(false);
        if (key is not null) await _store.TouchMcpApiKeyAsync(key.Id, ct).ConfigureAwait(false);
        return key;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
        .ToLowerInvariant();
}

/// <summary>JSON-RPC 2.0 Streamable HTTP endpoint implementing the Model Context Protocol tools surface.</summary>
public sealed class McpServer
{
    private const string ProtocolVersion = "2025-06-18";

    private readonly ComposeStackService _compose;
    private readonly ComposeParser _parser;
    private readonly DockerClientFactory _docker;
    private readonly KubeClusterService _kube;
    private readonly SqliteWalStore _store;
    private readonly ILogger<McpServer> _log;

    public McpServer(ComposeStackService compose, ComposeParser parser, DockerClientFactory docker,
        KubeClusterService kube, SqliteWalStore store, ILogger<McpServer> log)
    {
        _compose = compose;
        _parser = parser;
        _docker = docker;
        _kube = kube;
        _store = store;
        _log = log;
    }

    /// <summary>Handles one JSON-RPC request. Returns null for notifications.</summary>
    public async Task<JsonNode?> HandleAsync(JsonElement request, string scope, CancellationToken ct)
    {
        string? id = null;
        JsonNode? idNode = null;
        if (request.TryGetProperty("id", out var idElem) && idElem.ValueKind is not JsonValueKind.Null)
        {
            idNode = JsonNode.Parse(idElem.GetRawText());
            id = idElem.ToString();
        }

        var method = request.TryGetProperty("method", out var m) ? m.GetString() : null;
        var requireWrite = scope != "read";

        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(idNode, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "eh.DockManager",
                            ["version"] = "1.0.0"
                        }
                    });

                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;

                case "ping":
                    return Result(idNode, new JsonObject());

                case "tools/list":
                    return Result(idNode, new JsonObject { ["tools"] = ToolDefinitions() });

                case "tools/call":
                    return await CallToolAsync(request, idNode, requireWrite, ct).ConfigureAwait(false);

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

    private async Task<JsonNode?> CallToolAsync(JsonElement request, JsonNode? idNode, bool requireWrite,
        CancellationToken ct)
    {
        if (!request.TryGetProperty("params", out var p))
            return Error(idNode, -32602, "Missing params.");

        var toolName = p.TryGetProperty("name", out var n) ? n.GetString() : null;
        var args = p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a : default;

        if (string.IsNullOrEmpty(toolName))
            return Error(idNode, -32602, "Missing tool name.");

        if (requireWrite && MutatingTools.Contains(toolName))
            return Error(idNode, -32001, $"Token scope 'read' cannot invoke mutating tool '{toolName}'.");

        string Text()
        {
            if (args.ValueKind != JsonValueKind.Object) return "{}";
            return args.GetRawText();
        }
        string Arg(string name) => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

        switch (toolName)
        {
            case "list_stacks":
            {
                var names = _compose.ListStackNames();
                var stacks = new JsonArray();
                foreach (var name in names)
                {
                    var meta = await _store.GetStackAsync(name, ct).ConfigureAwait(false);
                    stacks.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["state"] = (meta?.State ?? StackState.Unknown).ToString(),
                        ["autoUpdate"] = meta?.AutoUpdateEnabled ?? false,
                        ["updateCount"] = meta?.UpdateCount ?? 0
                    });
                }
                return TextResult(idNode, new JsonObject { ["stacks"] = stacks }.ToJsonString());
            }

            case "deploy_stack":
            {
                var name = Arg("name");
                var result = await _compose.DeployAsync(name, ActorKind.Mcp, "mcp:deploy_stack", ct: ct)
                    .ConfigureAwait(false);
                return TextResult(idNode, JsonSerializer.Serialize(result), !result.Success);
            }

            case "stack_action":
            {
                var name = Arg("name");
                var action = Arg("action");
                switch (action)
                {
                    case "start": await _compose.StartStackAsync(name, ActorKind.Mcp, ct).ConfigureAwait(false); break;
                    case "stop": await _compose.StopStackAsync(name, ActorKind.Mcp, ct).ConfigureAwait(false); break;
                    case "restart": await _compose.RestartStackAsync(name, ActorKind.Mcp, ct).ConfigureAwait(false); break;
                    case "down": await _compose.DownStackAsync(name, ActorKind.Mcp, ct).ConfigureAwait(false); break;
                    case "update":
                        var upd = await _compose.AutoUpdateAsync(name, ct).ConfigureAwait(false);
                        return TextResult(idNode, JsonSerializer.Serialize(upd), !upd.Success);
                    default: return Error(idNode, -32602, $"Unknown stack action '{action}'.");
                }
                return TextResult(idNode, $"Stack '{name}' {action} completed.");
            }

            case "convert_docker_run":
            {
                var command = Arg("command");
                var doc = new DockerRunConverter().Convert(command);
                var yaml = _parser.Serialize(doc);
                return TextResult(idNode, yaml);
            }

            case "convert_compose_to_k8s":
            {
                var yaml = Arg("yaml");
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    var path = Arg("stack");
                    var doc = await _compose.LoadAsync(path, ct).ConfigureAwait(false);
                    return TextResult(idNode, new K8sConverter().ToYaml(doc));
                }
                var parsed = _parser.Parse(yaml, "converted");
                return TextResult(idNode, new K8sConverter().ToYaml(parsed));
            }

            case "list_containers":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                using var doc = await client.ListContainersAsync(true, ct).ConfigureAwait(false);
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
                await _store.AddAuditAsync(new AuditLog
                {
                    Actor = ActorKind.Mcp,
                    ActorDetail = "mcp:container_action",
                    Action = "container." + Arg("action"),
                    Target = id,
                    Timestamp = DateTimeOffset.UtcNow
                }, ct).ConfigureAwait(false);
                return TextResult(idNode, $"Container '{id}' {Arg("action")} completed.");
            }

            case "get_container_logs":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                var id = Arg("id");
                var tail = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("tail", out var t)
                    && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 200;
                await using var stream = await client.GetLogsStreamAsync(id, false, tail, true, ct)
                    .ConfigureAwait(false);
                var sb = new StringBuilder();
                await foreach (var (_, data) in DockerStreamDemuxer.DemuxAsync(stream, ct).ConfigureAwait(false))
                    sb.Append(Encoding.UTF8.GetString(data));
                var text = sb.ToString();
                // TTY containers emit unframed logs; fall back to raw text when framing yielded nothing.
                if (text.Length == 0)
                {
                    await using var raw = await client.GetLogsStreamAsync(id, false, tail, true, ct)
                        .ConfigureAwait(false);
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
                await _store.AddAuditAsync(new AuditLog
                {
                    Actor = ActorKind.Mcp,
                    ActorDetail = "mcp:exec_command",
                    Action = "container.exec",
                    Target = id,
                    Detail = string.Join(' ', command),
                    Success = exitCode == 0,
                    Timestamp = DateTimeOffset.UtcNow
                }, ct).ConfigureAwait(false);
                return TextResult(idNode, new JsonObject { ["exitCode"] = exitCode, ["output"] = output }.ToJsonString(),
                    exitCode != 0);
            }

            case "prune_unused_images":
            {
                var client = await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                using var doc = await client.PruneImagesAsync(danglingOnly: false, ct).ConfigureAwait(false);
                return TextResult(idNode, doc.RootElement.GetRawText());
            }

            case "list_k8s_pods":
            {
                var clusters = await _store.GetKubeClustersAsync(ct).ConfigureAwait(false);
                var clusterName = Arg("cluster");
                var cluster = string.IsNullOrEmpty(clusterName)
                    ? clusters.FirstOrDefault(c => c.Enabled)
                    : clusters.FirstOrDefault(c => c.Name == clusterName);
                if (cluster is null) return Error(idNode, -32602, "No Kubernetes cluster available.");
                var ns = Arg("namespace");
                var pods = await _kube.ListPodsAsync(cluster, string.IsNullOrEmpty(ns) ? "*" : ns, ct)
                    .ConfigureAwait(false);
                return TextResult(idNode, pods.ToJsonString());
            }

            default:
                return Error(idNode, -32602, $"Unknown tool '{toolName}'.");
        }
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

    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal)
    {
        "deploy_stack", "stack_action", "container_action", "exec_command", "prune_unused_images"
    };

    private static JsonArray ToolDefinitions()
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

        return new JsonArray
        {
            Tool("list_stacks", "List all compose stacks with their state and auto-update counters.",
                new JsonObject(), new JsonArray()),
            Tool("deploy_stack", "Deploy (or reconcile) a compose stack by name.",
                new JsonObject { ["name"] = Str("Stack name") }, new JsonArray { "name" }),
            Tool("stack_action", "Run a lifecycle action on a stack.",
                new JsonObject
                {
                    ["name"] = Str("Stack name"),
                    ["action"] = Str("start | stop | restart | down | update")
                }, new JsonArray { "name", "action" }),
            Tool("convert_docker_run", "Convert a 'docker run' command line into a compose document.",
                new JsonObject { ["command"] = Str("The docker run command") }, new JsonArray { "command" }),
            Tool("convert_compose_to_k8s", "Convert compose YAML (or a saved stack) into Kubernetes manifests.",
                new JsonObject
                {
                    ["yaml"] = Str("Compose YAML"),
                    ["stack"] = Str("Existing stack name (alternative to yaml)")
                }, new JsonArray()),
            Tool("list_containers", "List containers on the default Docker host.",
                new JsonObject(), new JsonArray()),
            Tool("container_action", "Start, stop, restart, pause, unpause, or remove a container.",
                new JsonObject
                {
                    ["id"] = Str("Container id or name"),
                    ["action"] = Str("start | stop | restart | remove | pause | unpause")
                }, new JsonArray { "id", "action" }),
            Tool("get_container_logs", "Fetch recent logs from a container.",
                new JsonObject { ["id"] = Str("Container id or name"), ["tail"] = Str("Number of lines") },
                new JsonArray { "id" }),
            Tool("exec_command", "Execute a command inside a container and return its output.",
                new JsonObject
                {
                    ["id"] = Str("Container id or name"),
                    ["command"] = new JsonObject { ["type"] = "array", ["items"] = Str("Argument") }
                }, new JsonArray { "id" }),
            Tool("prune_unused_images", "Remove all unused images from the default Docker host.",
                new JsonObject(), new JsonArray()),
            Tool("list_k8s_pods", "List pods across a Kubernetes cluster.",
                new JsonObject
                {
                    ["cluster"] = Str("Cluster name (optional)"),
                    ["namespace"] = Str("Namespace, or '*' for all")
                }, new JsonArray())
        };
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
