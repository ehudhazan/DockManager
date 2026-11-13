// File: src/eh.DockManager.Server/Stacks/ComposeSchemaValidator.cs
using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace eh.DockManager.Server.Stacks;

/// <summary>One validation finding for a compose document, with a 1-based line/column.</summary>
public sealed record ValidationIssue(int Line, int Column, string Message, string Severity, string? Path = null);

/// <summary>The outcome of validating a compose document.</summary>
public sealed record ComposeValidationResult(bool Valid, string? Name, int ServiceCount, List<ValidationIssue> Issues);

/// <summary>
/// Validates a docker-compose document against the compose specification subset DockManager supports:
/// unknown keys are warned about, enum-valued properties (restart, network_mode, depends_on conditions,
/// logging drivers, healthcheck tests, volume types, …) and port syntax are checked, and a service must
/// declare an <c>image</c> or a <c>build</c>. Parse errors are reported with their YAML line/column so the
/// client can point at them. Warnings never block a save; errors do.
/// </summary>
public sealed class ComposeSchemaValidator
{
    private const int MaxCharacters = 1_048_576;

    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.Ordinal)
    {
        "name", "version", "services", "networks", "volumes", "configs", "secrets", "include"
    };

    private static readonly HashSet<string> ServiceKeys = new(StringComparer.Ordinal)
    {
        "image", "build", "container_name", "command", "entrypoint", "environment", "env_file", "expose",
        "ports", "volumes", "networks", "depends_on", "restart", "network_mode", "hostname", "domainname",
        "user", "working_dir", "labels", "cap_add", "cap_drop", "privileged", "platform", "healthcheck",
        "logging", "ulimits", "sysctls", "tmpfs", "devices", "dns", "dns_search", "extra_hosts", "init",
        "ipc", "pid", "read_only", "security_opt", "shm_size", "stop_grace_period", "stop_signal", "tty",
        "stdin_open", "secrets", "configs", "deploy", "profiles", "pull_policy", "mem_limit", "cpus",
        "cpu_shares", "cpu_quota", "cpu_period", "runtime", "gpus", "group_add", "isolation", "mac_address",
        "oom_kill_disable", "oom_score_adj", "pids_limit", "scale", "userns_mode", "volumes_from",
        "external_links", "links", "storage_opt", "cgroup_parent", "cgroup", "annotations", "attach",
        "credential_spec", "device_cgroup_rules", "cpuset", "mem_reservation", "mem_swappiness",
        "memswap_limit", "sysctl", "volumes_from", "network", "depends_on"
    };

    private static readonly HashSet<string> NetworkKeys = new(StringComparer.Ordinal)
    {
        "driver", "driver_opts", "attachable", "enable_ipv6", "external", "internal", "ipam", "labels",
        "name", "ipam", "enable_ipv4"
    };

    private static readonly HashSet<string> VolumeKeys = new(StringComparer.Ordinal)
    {
        "driver", "driver_opts", "external", "labels", "name"
    };

    private static readonly Regex PortSpec = new(
        @"^(?:(?<ip>[0-9a-fA-F:.]+):)?(?<host>\d{1,5}(?:-\d{1,5})?):(?<container>\d{1,5}(?:-\d{1,5})?)$|^(?<exposed>\d{1,5}(?:-\d{1,5})?)(?:/(?<proto>tcp|udp|sctp))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string RestartPattern = @"^(no|always|unless-stopped|on-failure(?::\d+)?)$";

    private static readonly Dictionary<string, string[]> EnumValues = new(StringComparer.Ordinal)
    {
        ["restart"] = new[] { "no", "always", "unless-stopped", "on-failure" },
        ["network_mode"] = new[] { "none", "host", "bridge", "service:<name>", "container:<name>" },
        ["pull_policy"] = new[] { "always", "never", "missing", "build", "if_not_present", "daily", "weekly" },
        ["volumes.type"] = new[] { "bind", "volume", "tmpfs", "image", "npipe", "cluster" },
        ["volumes.consistency"] = new[] { "consistent", "cached", "delegated" },
        ["logging.driver"] = new[]
        {
            "json-file", "local", "syslog", "journald", "gelf", "fluentd", "awslogs", "splunk", "etwlogs",
            "gcplogs", "none"
        },
        ["healthcheck.test"] = new[] { "CMD", "CMD-SHELL", "NONE" },
        ["networks.driver"] = new[] { "bridge", "host", "overlay", "ipvlan", "macvlan", "none" },
        ["depends_on.condition"] = new[]
        {
            "service_started", "service_healthy", "service_completed_successfully"
        },
        ["deploy.restart_policy.condition"] = new[] { "none", "on-failure", "any" },
        ["deploy.mode"] = new[] { "replicated", "global", "replicated-job", "global-job" }
    };

    /// <summary>Trailing tokens that are freeform: a value of one of these enum keys is not a closed set.</summary>
    private static readonly Dictionary<string, string> KeyPaths = new(StringComparer.Ordinal)
    {
        ["services"] = "services",
        ["services.*"] = "service",
        ["networks"] = "networks",
        ["networks.*"] = "network",
        ["volumes"] = "volumes",
        ["volumes.*"] = "volume",
    };

    public ComposeValidationResult Validate(string yaml)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(yaml))
            return new ComposeValidationResult(false, null, 0,
                new List<ValidationIssue> { new(1, 1, "The compose file is empty.", "error") });

        if (yaml.Length > MaxCharacters)
            return new ComposeValidationResult(false, null, 0,
                new List<ValidationIssue> { new(1, 1, "The compose file is too large to validate.", "error") });

        // ── Parse (a syntax error is the only hard failure we return early on) ──
        Dictionary<object, object>? root;
        try
        {
            root = new YamlDotNet.Serialization.DeserializerBuilder().Build()
                .Deserialize<Dictionary<object, object>>(yaml);
        }
        catch (YamlException ex)
        {
            var line = ex.Start.Line > 0 ? (int)ex.Start.Line : 1;
            var column = ex.Start.Column > 0 ? (int)ex.Start.Column : 1;
            issues.Add(new ValidationIssue(line, column, ex.Message, "error"));
            return new ComposeValidationResult(false, null, 0, issues);
        }

        if (root is null)
            return new ComposeValidationResult(false, null, 0,
                new List<ValidationIssue> { new(1, 1, "The compose file does not contain a mapping.", "error") });

        string? name = root.TryGetValue("name", out var nameValue) ? nameValue?.ToString() : null;

        // ── Top level ──
        foreach (var key in root.Keys)
        {
            var text = key?.ToString() ?? "";
            if (text.Length == 0 || text.StartsWith("x-", StringComparison.Ordinal)) continue;
            if (!TopLevelKeys.Contains(text))
                Warn(issues, $"Unknown top-level key '{text}'.", text);
        }

        if (root.TryGetValue("version", out var version) && version is not null)
            Warn(issues, "The top-level 'version' key is obsolete in the Compose Specification and is ignored.",
                "version");

        if (!root.TryGetValue("services", out var servicesValue) || servicesValue is not IDictionary<object, object> services)
        {
            issues.Add(new ValidationIssue(1, 1, "A compose file must define 'services' as a mapping.", "error", "services"));
            return new ComposeValidationResult(false, name, 0, issues);
        }

        if (services.Count == 0)
            issues.Add(new ValidationIssue(1, 1, "'services' is empty; add at least one service.", "error", "services"));

        foreach (var entry in services)
        {
            var serviceName = entry.Key?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(serviceName)) continue;
            var path = $"services.{serviceName}";
            if (entry.Value is not IDictionary<object, object> service)
            {
                issues.Add(new ValidationIssue(1, 1,
                    $"Service '{serviceName}' must be a mapping of properties.", "error", path));
                continue;
            }

            ValidateService(issues, serviceName, service, path);
        }

        foreach (var (section, known) in new (string, HashSet<string>)[]
                 {
                     ("networks", NetworkKeys), ("volumes", VolumeKeys)
                 })
        {
            if (!root.TryGetValue(section, out var value) || value is not IDictionary<object, object> map) continue;
            foreach (var item in map)
            {
                var itemName = item.Key?.ToString() ?? "";
                var itemPath = $"{section}.{itemName}";
                if (item.Value is not IDictionary<object, object> itemMap)
                {
                    issues.Add(new ValidationIssue(1, 1,
                        $"'{itemPath}' must be a mapping (or null for default settings).", "warning", itemPath));
                    continue;
                }
                foreach (var property in itemMap.Keys)
                {
                    var key = property?.ToString() ?? "";
                    if (key.Length == 0 || key.StartsWith("x-", StringComparison.Ordinal)) continue;
                    if (!known.Contains(key))
                        Warn(issues, $"Unknown key '{key}' under '{itemPath}'.", $"{itemPath}.{key}");
                }

                if (section == "networks" && itemMap.TryGetValue("driver", out var driver) && driver is not null)
                    CheckEnum(issues, $"{itemPath}.driver", "networks.driver", driver.ToString());

                if (itemMap.TryGetValue("external", out var external) && external is not null
                    && external is not bool and not string and not IDictionary<object, object>)
                    issues.Add(new ValidationIssue(1, 1, $"'{itemPath}.external' must be true/false or a mapping.",
                        "error", $"{itemPath}.external"));
            }
        }

        // The YAML object model loses source positions, so re-attach a useful line for findings that
        // name a property or a literal: the editor uses it to jump to the offending line.
        var sourceLines = yaml.Replace("\r\n", "\n").Split('\n');
        var located = issues.Select(issue => issue.Path is null ? issue : Locate(issue, sourceLines)).ToList();

        var sorted = located
            .OrderBy(i => i.Line)
            .ThenBy(i => i.Column)
            .ToList();
        var valid = sorted.All(i => i.Severity != "error");
        return new ComposeValidationResult(valid, name, services.Count, sorted);
    }

    private static void ValidateService(List<ValidationIssue> issues, string serviceName,
        IDictionary<object, object> service, string path)
    {
        foreach (var property in service.Keys)
        {
            var key = property?.ToString() ?? "";
            if (key.Length == 0 || key.StartsWith("x-", StringComparison.Ordinal)) continue;
            if (!ServiceKeys.Contains(key))
                Warn(issues, $"Unknown service key '{key}' in '{serviceName}'.", $"{path}.{key}");
        }

        var hasImage = service.TryGetValue("image", out var image) && !string.IsNullOrWhiteSpace(image?.ToString());
        var hasBuild = service.ContainsKey("build");
        if (!hasImage && !hasBuild)
            issues.Add(new ValidationIssue(1, 1,
                $"Service '{serviceName}' must declare an 'image' or a 'build'.", "warning", path));

        if (service.TryGetValue("restart", out var restart) && restart is not null)
        {
            var text = restart.ToString() ?? "";
            if (!Regex.IsMatch(text, RestartPattern, RegexOptions.IgnoreCase))
                Error(issues, $"'{text}' is not valid for 'restart'. Use no, always, unless-stopped or on-failure[:max-retries].",
                    $"{path}.restart");
        }

        if (service.TryGetValue("network_mode", out var networkMode) && networkMode is not null)
            CheckEnum(issues, $"{path}.network_mode", "network_mode", networkMode.ToString());

        if (service.TryGetValue("pull_policy", out var pullPolicy) && pullPolicy is not null)
        {
            var text = pullPolicy.ToString() ?? "";
            if (!text.StartsWith("every_", StringComparison.OrdinalIgnoreCase))
                CheckEnum(issues, $"{path}.pull_policy", "pull_policy", text);
        }

        if (service.TryGetValue("ports", out var ports))
            ValidatePorts(issues, serviceName, ports, $"{path}.ports");

        if (service.TryGetValue("depends_on", out var dependsOn)
            && dependsOn is IDictionary<object, object> dependsMap)
        {
            foreach (var dependency in dependsMap)
            {
                if (dependency.Value is IDictionary<object, object> conditionMap
                    && conditionMap.TryGetValue("condition", out var condition) && condition is not null)
                    CheckEnum(issues, $"{path}.depends_on.{dependency.Key}.condition", "depends_on.condition",
                        condition.ToString());
            }
        }

        if (service.TryGetValue("logging", out var logging) && logging is IDictionary<object, object> loggingMap
            && loggingMap.TryGetValue("driver", out var driverValue) && driverValue is not null)
            CheckEnum(issues, $"{path}.logging.driver", "logging.driver", driverValue.ToString());

        if (service.TryGetValue("healthcheck", out var healthcheck)
            && healthcheck is IDictionary<object, object> healthMap
            && healthMap.TryGetValue("test", out var test) && test is IEnumerable<object> testList)
        {
            var first = testList.FirstOrDefault()?.ToString();
            if (!string.IsNullOrEmpty(first))
                CheckEnum(issues, $"{path}.healthcheck.test", "healthcheck.test", first);
        }

        if (service.TryGetValue("volumes", out var volumes) && volumes is IEnumerable<object> volumeList)
        {
            var index = 0;
            foreach (var volume in volumeList)
            {
                index++;
                if (volume is IDictionary<object, object> volumeMap)
                {
                    if (volumeMap.TryGetValue("type", out var type) && type is not null)
                        CheckEnum(issues, $"{path}.volumes[{index}].type", "volumes.type", type.ToString());
                    if (volumeMap.TryGetValue("consistency", out var consistency) && consistency is not null)
                        CheckEnum(issues, $"{path}.volumes[{index}].consistency", "volumes.consistency",
                            consistency.ToString());
                    if (!volumeMap.ContainsKey("target") && !volumeMap.ContainsKey("source"))
                        Warn(issues, $"Volume entry #{index} should set 'source' and 'target'.",
                            $"{path}.volumes[{index}]");
                }
            }
        }
    }

    private static void ValidatePorts(List<ValidationIssue> issues, string serviceName, object ports, string path)
    {
        if (ports is string scalar)
        {
            issues.Add(new ValidationIssue(1, 1,
                $"'ports' in '{serviceName}' should be a list, not the scalar '{scalar}'.", "warning", path));
            return;
        }

        if (ports is not IEnumerable<object> list) return;

        foreach (var entry in list)
        {
            var text = entry is IDictionary<object, object> map
                ? (map.TryGetValue("published", out var published) ? published?.ToString() ?? "" : "")
                : entry?.ToString() ?? "";
            if (text.Length == 0) continue;
            if (map_hasTarget(entry)) continue; // long syntax validated loosely
            if (!PortSpec.IsMatch(text))
                Error(issues,
                    $"'{text}' is not a valid port mapping. Use [host:]container[/tcp|udp|sctp], e.g. \"8080:80\".",
                    path);
        }

        static bool map_hasTarget(object? entry)
            => entry is IDictionary<object, object> map && map.ContainsKey("target");
    }

    private static void CheckEnum(List<ValidationIssue> issues, string path, string enumKey, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (!EnumValues.TryGetValue(enumKey, out var allowed)) return;
        var normalized = value.Trim();
        if (allowed.Any(a => string.Equals(a, normalized, StringComparison.OrdinalIgnoreCase))) return;
        if (allowed.Any(a => a.Contains('<') && normalized.StartsWith(a.Split('<')[0], StringComparison.OrdinalIgnoreCase)))
            return;
        Error(issues, $"'{normalized}' is not valid for '{path}'. Expected one of: {string.Join(", ", allowed)}.", path);
    }

    /// <summary>Finds the source line for an issue by its property name and, when present, its literal value.</summary>
    private static ValidationIssue Locate(ValidationIssue issue, string[] lines)
    {
        var key = issue.Path!.Split('.').Last();
        var literal = FirstQuoted(issue.Message);
        var fallback = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var body = lines[i].TrimStart();
            if (body.StartsWith("- ", StringComparison.Ordinal)) body = body[2..].TrimStart();
            if (!body.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            if (fallback == 0) fallback = i + 1;
            if (string.IsNullOrEmpty(literal)) return issue with { Line = i + 1, Column = lines[i].Length - body.Length + 1 };
            var rest = body[(key.Length + 1)..].Trim().Trim('"', '\'');
            if (string.Equals(rest, literal, StringComparison.OrdinalIgnoreCase))
                return issue with { Line = i + 1, Column = lines[i].IndexOf(literal, StringComparison.Ordinal) + 1 };
        }
        if (fallback == 0) return issue;

        // A literal that lives on a list item under the key (ports, volumes, …): find that item line.
        if (!string.IsNullOrEmpty(literal))
        {
            for (var i = fallback - 1; i < lines.Length; i++)
            {
                var body = lines[i].TrimStart();
                if (body.StartsWith("- ", StringComparison.Ordinal)) body = body[2..].TrimStart();
                if (string.Equals(body.Trim().Trim('"', '\''), literal, StringComparison.OrdinalIgnoreCase))
                    return issue with { Line = i + 1, Column = lines[i].IndexOf(literal, StringComparison.Ordinal) + 1 };
            }
        }

        return issue with { Line = fallback, Column = 1 };
    }

    private static string? FirstQuoted(string message)
    {
        var start = message.IndexOf('\'');
        if (start < 0) return null;
        var end = message.IndexOf('\'', start + 1);
        return end <= start ? null : message[(start + 1)..end];
    }

    private static void Error(List<ValidationIssue> issues, string message, string? path)
        => issues.Add(new ValidationIssue(1, 1, message, "error", path));

    private static void Warn(List<ValidationIssue> issues, string message, string? path)
        => issues.Add(new ValidationIssue(1, 1, message, "warning", path));
}
