// File: src/eh.DockManager.Server/Models/DomainModels.cs
using System.Text.Json.Serialization;

namespace eh.DockManager.Server.Models;

/// <summary>
/// Global constants shared across the control plane.
/// </summary>
public static class EhConstants
{
    /// <summary>Root data directory. Override with the <c>EHDM_DATA_DIR</c> environment variable.</summary>
    public static readonly string DataDir =
        Environment.GetEnvironmentVariable("EHDM_DATA_DIR") is { Length: > 0 } dir ? dir : "/data";

    public static readonly string DbPath = Path.Combine(DataDir, "dockmanager.db");
    public static readonly string StacksDir = Path.Combine(DataDir, "stacks");
    public static readonly string CertsDir = Path.Combine(DataDir, "certs");
    public static readonly string MasterKeyPath = Path.Combine(DataDir, ".master.key");
    public static readonly string KeysDir = Path.Combine(DataDir, "keys");
    public const string ActorLabel = "eh.dockmanager.actor";
    public const string ActorDetailLabel = "eh.dockmanager.actor_detail";
    public const string ConfigHashLabel = "eh.dockmanager.config_hash";
    public const string ComposeProjectLabel = "com.docker.compose.project";
    public const string ComposeServiceLabel = "com.docker.compose.service";
    public const string DefaultPortalDomain = "dock.lan";
    public const string SamlReplayCacheKey = "__Host-eh_saml_req";
    public const string AdminSessionCookie = "__Host-eh_session";
    public const string MtlsHeader = "X-EHDM-Client-Cert";

    /// <summary>Ports probed by the auto-discovery engine.</summary>
    public static readonly int[] DiscoverablePorts =
    {
        80, 443, 3000, 5000, 8000, 8080, 8096, 8123, 9000, 32400
    };
}

/// <summary>Role hierarchy for access control.</summary>
public enum UserRole
{
    Pending = 0,
    Viewer = 1,
    Admin = 2
}

/// <summary>How a user account was created.</summary>
public enum IdentityProvider
{
    Local = 0,
    Google = 1,
    Microsoft = 2,
    Saml = 3
}

/// <summary>Authentication mode enforced on a portal application route.</summary>
public enum PortalAuthMode
{
    Public = 0,
    Sso = 1,
    Admin = 2
}

/// <summary>Scale-to-zero autoscale behaviour for a portal app.</summary>
public enum AutoscaleMode
{
    None = 0,
    Idle15m = 1,
    Idle1h = 2
}

/// <summary>Transport used to reach a Docker host.</summary>
public enum DockerHostKind
{
    UnixSocket = 0,
    HttpsMutualTls = 1,
    AgentMtls = 2
}

/// <summary>Provenance actor that performed an operation.</summary>
public enum ActorKind
{
    WebUi = 0,
    Mcp = 1,
    Updater = 2,
    Cli = 3,
    System = 4
}

/// <summary>Webhook destination flavour.</summary>
public enum WebhookKind
{
    Discord = 0,
    Telegram = 1,
    Slack = 2,
    Ntfy = 3,
    Gotify = 4,
    GenericHmac = 5
}

/// <summary>Lifecycle state of a compose stack.</summary>
public enum StackState
{
    Unknown = 0,
    Running = 1,
    Partial = 2,
    Stopped = 3,
    Degraded = 4
}

public sealed class User
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Pending;
    public IdentityProvider Provider { get; set; } = IdentityProvider.Local;

    [JsonIgnore]
    public string? PasswordHash { get; set; }

    [JsonIgnore]
    public string? PasswordSalt { get; set; }

    [JsonIgnore]
    public string? TotpSecret { get; set; }

    public bool TotpEnabled { get; set; }
    public bool Disabled { get; set; }
    public bool Approved { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class Passkey
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "Passkey";
    public byte[] CredentialId { get; set; } = Array.Empty<byte>();
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public long SignCount { get; set; }
    public Guid AaGuid { get; set; }
    public uint[] Transports { get; set; } = Array.Empty<uint>();
    public bool UserVerified { get; set; }
    public bool BackupEligible { get; set; }
    public bool BackupState { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class SamlSeenAssertion
{
    public string Id { get; set; } = string.Empty;
    public string? ResponseId { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public DateTimeOffset NotOnOrAfter { get; set; }
    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Registry
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = "https://index.docker.io/v1/";
    public string? Username { get; set; }

    [JsonIgnore]
    public string? EncryptedPassword { get; set; }

    public bool IgnoreSslErrors { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DockerHost
{
    public long Id { get; set; }
    public string Name { get; set; } = "local";
    public DockerHostKind Kind { get; set; } = DockerHostKind.UnixSocket;
    public string Endpoint { get; set; } = "unix:///var/run/docker.sock";
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string? ClientCertPem { get; set; }

    [JsonIgnore]
    public string? ClientKeyPem { get; set; }

    public string? CaCertPem { get; set; }
    public string? WakeOnLanMac { get; set; }
    public string? WakeOnLanBroadcast { get; set; } = "255.255.255.255";
    public int WakeOnLanPort { get; set; } = 9;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class KubeCluster
{
    public long Id { get; set; }
    public string Name { get; set; } = "default";
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string? KubeConfigYaml { get; set; }

    [JsonIgnore]
    public string? Token { get; set; }

    [JsonIgnore]
    public string? ClientCertPem { get; set; }

    [JsonIgnore]
    public string? ClientKeyPem { get; set; }

    public string? CaCertPem { get; set; }
    public string ServerUrl { get; set; } = string.Empty;
    public string? Context { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class StackMeta
{
    public string Name { get; set; } = string.Empty;
    public StackState State { get; set; } = StackState.Unknown;
    public bool AutoUpdateEnabled { get; set; }
    public int AutoUpdateIntervalMinutes { get; set; } = 1440;
    public bool PruneOldImages { get; set; } = true;
    public bool RollbackOnFailure { get; set; } = true;
    public string? ConfigHash { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastDeployedAt { get; set; }
    public DateTimeOffset? LastUpdateCheckAt { get; set; }
    public DateTimeOffset? LastUpdatedAt { get; set; }
    public int UpdateCount { get; set; }
    public int RollbackCount { get; set; }
}

public sealed class PortalApp
{
    public long Id { get; set; }
    public string Subdomain { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string Category { get; set; } = "Apps";
    public PortalAuthMode Auth { get; set; } = PortalAuthMode.Sso;
    public AutoscaleMode Autoscale { get; set; } = AutoscaleMode.None;
    public bool Hidden { get; set; }
    public bool Manual { get; set; }
    public bool AutoDiscovered { get; set; }
    public int? TargetPort { get; set; }
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }
    public string? StackName { get; set; }
    public long? HostId { get; set; }
    public string? ExternalUrl { get; set; }
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class Webhook
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public WebhookKind Kind { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ChatId { get; set; }

    [JsonIgnore]
    public string? Secret { get; set; }

    public bool Enabled { get; set; } = true;
    public string Events { get; set; } = "stack.deploy,stack.update,stack.rollback,cve.alert,user.pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastFiredAt { get; set; }
    public string? LastStatus { get; set; }
}

public sealed class McpApiKey
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;

    [JsonIgnore]
    public string TokenHash { get; set; } = string.Empty;

    public string Scopes { get; set; } = "read,write";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public ActorKind Actor { get; set; } = ActorKind.System;
    public string? ActorDetail { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? Detail { get; set; }
    public bool Success { get; set; } = true;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OidcProviderConfig
{
    public string Provider { get; set; } = "google"; // google | microsoft
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;

    [JsonIgnore]
    public string? ClientSecret { get; set; }

    public string? Authority { get; set; }
    public string? DisplayName { get; set; }
}

public sealed class SamlSpConfig
{
    public bool Enabled { get; set; }
    public string EntityId { get; set; } = "https://dock.lan/saml2";
    public string IdpMetadataUrl { get; set; } = string.Empty;
    public string? IdpMetadataXml { get; set; }
    public string? IdpEntityId { get; set; }
    public string? IdpSsoUrl { get; set; }
    public bool WantAssertionsSigned { get; set; } = true;
    public string? SpSigningCertPfxBase64 { get; set; }
    public string? SpSigningCertPassword { get; set; }
    public string AttributeEmail { get; set; } = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress";
    public string AttributeName { get; set; } = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name";
    public string AttributeGroups { get; set; } = "http://schemas.xmlsoap.org/claims/Group";
    public string AdminGroup { get; set; } = "dockmanager-admins";
    public string ViewerGroup { get; set; } = "dockmanager-viewers";
}

public sealed class SamlIdpConfig
{
    public bool Enabled { get; set; }
    public string EntityId { get; set; } = "https://dock.lan/saml/idp";
    public string SsoUrl { get; set; } = "https://dock.lan/api/saml/idp/sso";
    public int AssertionLifetimeMinutes { get; set; } = 5;
}

public sealed class AcmeConfig
{
    public bool Enabled { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DirectoryUrl { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";
    /// <summary>Comma-separated domains; the first is the CN and the rest are SANs.</summary>
    public string Domain { get; set; } = string.Empty;
    public bool UseStaging { get; set; }

    /// <summary>Last issuance failure, surfaced in the UI (not a credential).</summary>
    public string? LastError { get; set; }
    public DateTimeOffset? LastIssuedAt { get; set; }
}

public sealed class StoredConfig
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Persisted auto-update snapshot for per-container rollback.</summary>
public sealed class ContainerImageSnapshot
{
    public long Id { get; set; }
    public string StackName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string ImageId { get; set; } = string.Empty;
    public string? PreviousImageId { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Result of a container/image API mutation.</summary>
public sealed record OperationResult(bool Success, string? Message = null, string? Actor = null)
{
    public static OperationResult Ok(string? message = null) => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

/// <summary>A live container metric sample.</summary>
public sealed record ContainerMetric(
    string ContainerId,
    double CpuPercent,
    double MemoryMb,
    double MemoryLimitMb,
    long NetworkRxBytes,
    long NetworkTxBytes,
    DateTimeOffset Timestamp);

/// <summary>SSE-friendly event envelope used by the provenance feed.</summary>
public sealed record ProvenanceEvent(
    string Type,
    string Actor,
    string? ActorDetail,
    string? Target,
    string Message,
    DateTimeOffset Timestamp);

/// <summary>Directory entry for the Tar file browser.</summary>
public sealed record TarEntryInfo(string Name, bool IsDirectory, long Size, DateTimeOffset Modified, string Mode);

/// <summary>Notifier abstraction so workers can emit events without depending on a concrete transport.</summary>
public interface IEventNotifier
{
    Task NotifyAsync(string eventType, string title, string message, CancellationToken ct = default);
    Task NotifyAsync(string eventType, string title, string message, object? payload, CancellationToken ct = default);
}
