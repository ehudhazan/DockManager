// File: src/eh.DockManager.Server/Data/SqliteWalStore.cs
using System.Data;
using System.Text.Json;
using eh.DockManager.Server.Models;
using Microsoft.Data.Sqlite;

namespace eh.DockManager.Server.Data;

/// <summary>
/// ACID persistence layer backed by a single SQLite database running in WAL mode.
/// A connection is opened per operation (connection pooling inside SQLite handles reuse)
/// which keeps the store thread-safe without a global lock.
/// </summary>
public sealed class SqliteWalStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public SqliteWalStore(string? dbPath = null)
    {
        _dbPath = dbPath ?? EhConstants.DbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath => _dbPath;

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static async Task<SqliteCommand> CmdAsync(SqliteConnection conn, string sql,
        CancellationToken ct, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await Task.CompletedTask.ConfigureAwait(false);
        return cmd;
    }

    /// <summary>Creates the schema, enables WAL, and applies durability pragmas.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var pragma = conn.CreateCommand();
        pragma.CommandText =
            "PRAGMA journal_mode = WAL;" +
            "PRAGMA synchronous = NORMAL;" +
            "PRAGMA foreign_keys = ON;" +
            "PRAGMA busy_timeout = 10000;" +
            "PRAGMA journal_size_limit = 67108864;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var ddl = conn.CreateCommand();
        ddl.CommandText = Schema;
        await ddl.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS Config (
            Key         TEXT PRIMARY KEY,
            Value       TEXT,
            UpdatedAt   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Users (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            Username      TEXT NOT NULL UNIQUE,
            Email         TEXT NOT NULL DEFAULT '',
            Role          INTEGER NOT NULL DEFAULT 0,
            Provider      INTEGER NOT NULL DEFAULT 0,
            PasswordHash  TEXT,
            PasswordSalt  TEXT,
            TotpSecret    TEXT,
            TotpEnabled   INTEGER NOT NULL DEFAULT 0,
            Disabled      INTEGER NOT NULL DEFAULT 0,
            Approved      INTEGER NOT NULL DEFAULT 0,
            DisplayName   TEXT,
            AvatarUrl     TEXT,
            CreatedAt     TEXT NOT NULL,
            LastLoginAt   TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_Users_Email ON Users(Email);

        CREATE TABLE IF NOT EXISTS Passkeys (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId        INTEGER NOT NULL,
            Name          TEXT NOT NULL,
            CredentialId  BLOB NOT NULL UNIQUE,
            PublicKey     BLOB NOT NULL,
            SignCount     INTEGER NOT NULL DEFAULT 0,
            AaGuid        TEXT NOT NULL,
            Transports    TEXT,
            UserVerified  INTEGER NOT NULL DEFAULT 0,
            BackupEligible INTEGER NOT NULL DEFAULT 0,
            BackupState   INTEGER NOT NULL DEFAULT 0,
            CreatedAt     TEXT NOT NULL,
            LastUsedAt    TEXT,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_Passkeys_User ON Passkeys(UserId);

        CREATE TABLE IF NOT EXISTS SamlSeenAssertions (
            Id          TEXT PRIMARY KEY,
            ResponseId  TEXT,
            Issuer      TEXT NOT NULL,
            NotOnOrAfter TEXT NOT NULL,
            SeenAt      TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_SamlSeen_Expiry ON SamlSeenAssertions(NotOnOrAfter);

        CREATE TABLE IF NOT EXISTS Registries (
            Id                INTEGER PRIMARY KEY AUTOINCREMENT,
            Name              TEXT NOT NULL,
            Url               TEXT NOT NULL,
            Username          TEXT,
            EncryptedPassword TEXT,
            IgnoreSslErrors   INTEGER NOT NULL DEFAULT 0,
            IsDefault         INTEGER NOT NULL DEFAULT 0,
            CreatedAt         TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS DockerHosts (
            Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            Name               TEXT NOT NULL UNIQUE,
            Kind               INTEGER NOT NULL DEFAULT 0,
            Endpoint           TEXT NOT NULL,
            Enabled            INTEGER NOT NULL DEFAULT 1,
            ClientCertPem      TEXT,
            ClientKeyPem       TEXT,
            CaCertPem          TEXT,
            WakeOnLanMac       TEXT,
            WakeOnLanBroadcast TEXT,
            WakeOnLanPort      INTEGER NOT NULL DEFAULT 9,
            CreatedAt          TEXT NOT NULL,
            LastSeenAt         TEXT,
            LastError          TEXT
        );

        CREATE TABLE IF NOT EXISTS KubeClusters (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            Name          TEXT NOT NULL UNIQUE,
            Enabled       INTEGER NOT NULL DEFAULT 1,
            KubeConfigYaml TEXT,
            Token         TEXT,
            ClientCertPem TEXT,
            ClientKeyPem  TEXT,
            CaCertPem     TEXT,
            ServerUrl     TEXT NOT NULL DEFAULT '',
            Context       TEXT,
            CreatedAt     TEXT NOT NULL,
            LastSeenAt    TEXT,
            LastError     TEXT
        );

        CREATE TABLE IF NOT EXISTS StacksMeta (
            Name                    TEXT PRIMARY KEY,
            State                   INTEGER NOT NULL DEFAULT 0,
            AutoUpdateEnabled       INTEGER NOT NULL DEFAULT 0,
            AutoUpdateIntervalMinutes INTEGER NOT NULL DEFAULT 1440,
            PruneOldImages          INTEGER NOT NULL DEFAULT 1,
            RollbackOnFailure       INTEGER NOT NULL DEFAULT 1,
            ConfigHash              TEXT,
            LastError               TEXT,
            CreatedAt               TEXT NOT NULL,
            LastDeployedAt          TEXT,
            LastUpdateCheckAt       TEXT,
            LastUpdatedAt           TEXT,
            UpdateCount             INTEGER NOT NULL DEFAULT 0,
            RollbackCount           INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS PortalApps (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            Subdomain      TEXT NOT NULL UNIQUE,
            Name           TEXT NOT NULL,
            Icon           TEXT,
            Category       TEXT NOT NULL DEFAULT 'Apps',
            Auth           INTEGER NOT NULL DEFAULT 1,
            Autoscale      INTEGER NOT NULL DEFAULT 0,
            Hidden         INTEGER NOT NULL DEFAULT 0,
            Manual         INTEGER NOT NULL DEFAULT 0,
            AutoDiscovered INTEGER NOT NULL DEFAULT 0,
            TargetPort     INTEGER,
            ContainerId    TEXT,
            ContainerName  TEXT,
            StackName      TEXT,
            HostId         INTEGER,
            ExternalUrl    TEXT,
            SortOrder      INTEGER NOT NULL DEFAULT 0,
            Notes          TEXT,
            CreatedAt      TEXT NOT NULL,
            UpdatedAt      TEXT
        );

        CREATE TABLE IF NOT EXISTS Webhooks (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            Name         TEXT NOT NULL,
            Kind         INTEGER NOT NULL DEFAULT 0,
            Url          TEXT NOT NULL,
            ChatId       TEXT,
            Secret       TEXT,
            Enabled      INTEGER NOT NULL DEFAULT 1,
            Events       TEXT NOT NULL DEFAULT '',
            CreatedAt    TEXT NOT NULL,
            LastFiredAt  TEXT,
            LastStatus   TEXT
        );

        CREATE TABLE IF NOT EXISTS McpApiKeys (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            Name       TEXT NOT NULL,
            Prefix     TEXT NOT NULL,
            TokenHash  TEXT NOT NULL UNIQUE,
            Scopes     TEXT NOT NULL DEFAULT 'read,write',
            Enabled    INTEGER NOT NULL DEFAULT 1,
            CreatedAt  TEXT NOT NULL,
            LastUsedAt TEXT
        );

        CREATE TABLE IF NOT EXISTS AuditLogs (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Actor       INTEGER NOT NULL DEFAULT 4,
            ActorDetail TEXT,
            Action      TEXT NOT NULL,
            Target      TEXT,
            Detail      TEXT,
            Success     INTEGER NOT NULL DEFAULT 1,
            Timestamp   TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Audit_Timestamp ON AuditLogs(Timestamp DESC);

        CREATE TABLE IF NOT EXISTS ContainerImageSnapshots (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            StackName       TEXT NOT NULL,
            ContainerName   TEXT NOT NULL,
            Image           TEXT NOT NULL,
            ImageId         TEXT NOT NULL,
            PreviousImageId TEXT,
            CapturedAt      TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Snapshot_Stack ON ContainerImageSnapshots(StackName, ContainerName);
        """;

    private static string Iso(DateTimeOffset v) => v.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseIso(string? s) =>
        string.IsNullOrWhiteSpace(s) ? DateTimeOffset.UtcNow : DateTimeOffset.Parse(s, null,
            System.Globalization.DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ParseIsoNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : DateTimeOffset.Parse(s, null,
            System.Globalization.DateTimeStyles.RoundtripKind);

    private static string? S(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static long I64(SqliteDataReader r, string name) => r.GetInt64(r.GetOrdinal(name));
    private static long? I64Null(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }
    private static int I32(SqliteDataReader r, string name) => r.GetInt32(r.GetOrdinal(name));
    private static bool B(SqliteDataReader r, string name) => r.GetInt64(r.GetOrdinal(name)) != 0;

    private async Task<int> ExecAsync(string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = await CmdAsync(conn, sql, ct, args).ConfigureAwait(false);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<T?> QuerySingleAsync<T>(string sql, Func<SqliteDataReader, T> map,
        CancellationToken ct, params (string, object?)[] args)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = await CmdAsync(conn, sql, ct, args).ConfigureAwait(false);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? map(reader) : default;
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> map,
        CancellationToken ct, params (string, object?)[] args)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = await CmdAsync(conn, sql, ct, args).ConfigureAwait(false);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<T>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(map(reader));
        return list;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Config
    // ──────────────────────────────────────────────────────────────────────

    public async Task<string?> GetConfigAsync(string key, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT Value FROM Config WHERE Key = $k", r => S(r, "Value"), ct, ("$k", key))
            .ConfigureAwait(false);

    public async Task SetConfigAsync(string key, string? value, CancellationToken ct = default)
        => await ExecAsync(
            "INSERT INTO Config(Key, Value, UpdatedAt) VALUES($k, $v, $t) " +
            "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt",
            ct, ("$k", key), ("$v", value), ("$t", Iso(DateTimeOffset.UtcNow))).ConfigureAwait(false);

    public async Task<T> GetConfigObjectAsync<T>(string key, T fallback, CancellationToken ct = default)
    {
        var raw = await GetConfigAsync(key, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        try { return JsonSerializer.Deserialize<T>(raw, JsonOpts) ?? fallback; }
        catch { return fallback; }
    }

    public Task SetConfigObjectAsync<T>(string key, T value, CancellationToken ct = default)
        => SetConfigAsync(key, JsonSerializer.Serialize(value, JsonOpts), ct);

    public async Task<List<StoredConfig>> GetAllConfigAsync(CancellationToken ct = default)
        => await QueryAsync("SELECT Key, Value, UpdatedAt FROM Config ORDER BY Key",
            r => new StoredConfig { Key = S(r, "Key")!, Value = S(r, "Value"), UpdatedAt = ParseIso(S(r, "UpdatedAt")) },
            ct).ConfigureAwait(false);

    // ──────────────────────────────────────────────────────────────────────
    // Users
    // ──────────────────────────────────────────────────────────────────────

    private static User MapUser(SqliteDataReader r) => new()
    {
        Id = I64(r, "Id"),
        Username = S(r, "Username") ?? "",
        Email = S(r, "Email") ?? "",
        Role = (UserRole)I32(r, "Role"),
        Provider = (IdentityProvider)I32(r, "Provider"),
        PasswordHash = S(r, "PasswordHash"),
        PasswordSalt = S(r, "PasswordSalt"),
        TotpSecret = S(r, "TotpSecret"),
        TotpEnabled = B(r, "TotpEnabled"),
        Disabled = B(r, "Disabled"),
        Approved = B(r, "Approved"),
        DisplayName = S(r, "DisplayName"),
        AvatarUrl = S(r, "AvatarUrl"),
        CreatedAt = ParseIso(S(r, "CreatedAt")),
        LastLoginAt = ParseIsoNull(S(r, "LastLoginAt"))
    };

    public async Task<List<User>> GetUsersAsync(CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM Users ORDER BY Id", MapUser, ct).ConfigureAwait(false);

    public async Task<User?> GetUserByIdAsync(long id, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM Users WHERE Id = $id", MapUser, ct, ("$id", id)).ConfigureAwait(false);

    public async Task<User?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM Users WHERE Username = $u COLLATE NOCASE", MapUser, ct,
            ("$u", username)).ConfigureAwait(false);

    public async Task<User?> GetUserByEmailAsync(string email, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM Users WHERE Email = $e COLLATE NOCASE", MapUser, ct,
            ("$e", email)).ConfigureAwait(false);

    public async Task<long> UpsertUserAsync(User u, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            if (u.Id == 0)
            {
                await using var cmd = await CmdAsync(conn,
                    "INSERT INTO Users(Username, Email, Role, Provider, PasswordHash, PasswordSalt, TotpSecret, " +
                    "TotpEnabled, Disabled, Approved, DisplayName, AvatarUrl, CreatedAt, LastLoginAt) " +
                    "VALUES($un,$em,$ro,$pr,$ph,$ps,$ts,$te,$di,$ap,$dn,$av,$ca,$la); SELECT last_insert_rowid();",
                    ct,
                    ("$un", u.Username), ("$em", u.Email), ("$ro", (int)u.Role), ("$pr", (int)u.Provider),
                    ("$ph", u.PasswordHash), ("$ps", u.PasswordSalt), ("$ts", u.TotpSecret),
                    ("$te", u.TotpEnabled ? 1 : 0), ("$di", u.Disabled ? 1 : 0), ("$ap", u.Approved ? 1 : 0),
                    ("$dn", u.DisplayName), ("$av", u.AvatarUrl), ("$ca", Iso(u.CreatedAt)),
                    ("$la", u.LastLoginAt.HasValue ? Iso(u.LastLoginAt.Value) : null)).ConfigureAwait(false);
                var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                u.Id = Convert.ToInt64(scalar);
                return u.Id;
            }

            await using var upd = await CmdAsync(conn,
                "UPDATE Users SET Username=$un, Email=$em, Role=$ro, Provider=$pr, PasswordHash=$ph, " +
                "PasswordSalt=$ps, TotpSecret=$ts, TotpEnabled=$te, Disabled=$di, Approved=$ap, " +
                "DisplayName=$dn, AvatarUrl=$av, LastLoginAt=$la WHERE Id=$id", ct,
                ("$un", u.Username), ("$em", u.Email), ("$ro", (int)u.Role), ("$pr", (int)u.Provider),
                ("$ph", u.PasswordHash), ("$ps", u.PasswordSalt), ("$ts", u.TotpSecret),
                ("$te", u.TotpEnabled ? 1 : 0), ("$di", u.Disabled ? 1 : 0), ("$ap", u.Approved ? 1 : 0),
                ("$dn", u.DisplayName), ("$av", u.AvatarUrl),
                ("$la", u.LastLoginAt.HasValue ? Iso(u.LastLoginAt.Value) : null), ("$id", u.Id)).ConfigureAwait(false);
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return u.Id;
        }
        finally { _writeGate.Release(); }
    }

    public Task DeleteUserAsync(long id, CancellationToken ct = default)
        => ExecAsync("DELETE FROM Users WHERE Id = $id", ct, ("$id", id));

    public async Task<int> CountUsersAsync(CancellationToken ct = default)
        => await QuerySingleAsync("SELECT COUNT(*) AS C FROM Users", r => I32(r, "C"), ct).ConfigureAwait(false);

    // ──────────────────────────────────────────────────────────────────────
    // Passkeys
    // ──────────────────────────────────────────────────────────────────────

    private static Passkey MapPasskey(SqliteDataReader r) => new()
    {
        Id = I64(r, "Id"),
        UserId = I64(r, "UserId"),
        Name = S(r, "Name") ?? "Passkey",
        CredentialId = (byte[])r["CredentialId"],
        PublicKey = (byte[])r["PublicKey"],
        SignCount = I64(r, "SignCount"),
        AaGuid = Guid.TryParse(S(r, "AaGuid"), out var g) ? g : Guid.Empty,
        Transports = ParseTransports(S(r, "Transports")),
        UserVerified = B(r, "UserVerified"),
        BackupEligible = B(r, "BackupEligible"),
        BackupState = B(r, "BackupState"),
        CreatedAt = ParseIso(S(r, "CreatedAt")),
        LastUsedAt = ParseIsoNull(S(r, "LastUsedAt"))
    };

    private static uint[] ParseTransports(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<uint>();
        try { return JsonSerializer.Deserialize<uint[]>(raw, JsonOpts) ?? Array.Empty<uint>(); }
        catch { return Array.Empty<uint>(); }
    }

    public async Task<List<Passkey>> GetPasskeysAsync(long userId, CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM Passkeys WHERE UserId = $u ORDER BY Id", MapPasskey, ct,
            ("$u", userId)).ConfigureAwait(false);

    public async Task<Passkey?> GetPasskeyByCredentialIdAsync(byte[] credentialId, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM Passkeys WHERE CredentialId = $c", MapPasskey, ct,
            ("$c", credentialId)).ConfigureAwait(false);

    public async Task<long> AddPasskeyAsync(Passkey p, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = await CmdAsync(conn,
            "INSERT INTO Passkeys(UserId, Name, CredentialId, PublicKey, SignCount, AaGuid, Transports, " +
            "UserVerified, BackupEligible, BackupState, CreatedAt, LastUsedAt) " +
            "VALUES($u,$n,$c,$p,$s,$a,$t,$uv,$be,$bs,$ca,$la); SELECT last_insert_rowid();", ct,
            ("$u", p.UserId), ("$n", p.Name), ("$c", p.CredentialId), ("$p", p.PublicKey),
            ("$s", p.SignCount), ("$a", p.AaGuid.ToString()), ("$t", JsonSerializer.Serialize(p.Transports, JsonOpts)),
            ("$uv", p.UserVerified ? 1 : 0), ("$be", p.BackupEligible ? 1 : 0), ("$bs", p.BackupState ? 1 : 0),
            ("$ca", Iso(p.CreatedAt)), ("$la", p.LastUsedAt.HasValue ? Iso(p.LastUsedAt.Value) : null)).ConfigureAwait(false);
        p.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return p.Id;
    }

    public Task UpdatePasskeyCounterAsync(long id, long signCount, bool backupState, CancellationToken ct = default)
        => ExecAsync("UPDATE Passkeys SET SignCount=$s, BackupState=$b, LastUsedAt=$l WHERE Id=$id", ct,
            ("$s", signCount), ("$b", backupState ? 1 : 0), ("$l", Iso(DateTimeOffset.UtcNow)), ("$id", id));

    public Task DeletePasskeyAsync(long id, CancellationToken ct = default)
        => ExecAsync("DELETE FROM Passkeys WHERE Id = $id", ct, ("$id", id));

    // ──────────────────────────────────────────────────────────────────────
    // SAML replay cache
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>Atomically records a seen SAML assertion id. Returns true when newly inserted.</summary>
    public async Task<bool> TryInsertSamlAssertionAsync(SamlSeenAssertion a, CancellationToken ct = default)
    {
        var rows = await ExecAsync(
            "INSERT OR IGNORE INTO SamlSeenAssertions(Id, ResponseId, Issuer, NotOnOrAfter, SeenAt) " +
            "VALUES($i,$r,$s,$n,$t)", ct,
            ("$i", a.Id), ("$r", a.ResponseId), ("$s", a.Issuer), ("$n", Iso(a.NotOnOrAfter)),
            ("$t", Iso(a.SeenAt))).ConfigureAwait(false);
        return rows > 0;
    }

    public Task PruneSamlAssertionsAsync(CancellationToken ct = default)
        => ExecAsync("DELETE FROM SamlSeenAssertions WHERE NotOnOrAfter < $n", ct, ("$n", Iso(DateTimeOffset.UtcNow)));

    // ──────────────────────────────────────────────────────────────────────
    // Registries
    // ──────────────────────────────────────────────────────────────────────

    private static Registry MapRegistry(SqliteDataReader r) => new()
    {
        Id = I64(r, "Id"),
        Name = S(r, "Name") ?? "",
        Url = S(r, "Url") ?? "",
        Username = S(r, "Username"),
        EncryptedPassword = S(r, "EncryptedPassword"),
        IgnoreSslErrors = B(r, "IgnoreSslErrors"),
        IsDefault = B(r, "IsDefault"),
        CreatedAt = ParseIso(S(r, "CreatedAt"))
    };

    public async Task<List<Registry>> GetRegistriesAsync(CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM Registries ORDER BY IsDefault DESC, Name", MapRegistry, ct).ConfigureAwait(false);

    public async Task<Registry?> GetRegistryByIdAsync(long id, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM Registries WHERE Id=$id", MapRegistry, ct, ("$id", id)).ConfigureAwait(false);

    public async Task<Registry?> GetRegistryForHostAsync(string host, CancellationToken ct = default)
    {
        var all = await GetRegistriesAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(r => r.Url.Contains(host, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(r => r.IsDefault)
            ?? all.FirstOrDefault();
    }

    public async Task<long> UpsertRegistryAsync(Registry r, CancellationToken ct = default)
    {
        if (r.Id == 0)
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = await CmdAsync(conn,
                "INSERT INTO Registries(Name, Url, Username, EncryptedPassword, IgnoreSslErrors, IsDefault, CreatedAt) " +
                "VALUES($n,$u,$un,$pw,$ig,$df,$c); SELECT last_insert_rowid();", ct,
                ("$n", r.Name), ("$u", r.Url), ("$un", r.Username), ("$pw", r.EncryptedPassword),
                ("$ig", r.IgnoreSslErrors ? 1 : 0), ("$df", r.IsDefault ? 1 : 0), ("$c", Iso(r.CreatedAt)))
                .ConfigureAwait(false);
            r.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        else
        {
            await ExecAsync("UPDATE Registries SET Name=$n, Url=$u, Username=$un, EncryptedPassword=$pw, " +
                "IgnoreSslErrors=$ig, IsDefault=$df WHERE Id=$id", ct,
                ("$n", r.Name), ("$u", r.Url), ("$un", r.Username), ("$pw", r.EncryptedPassword),
                ("$ig", r.IgnoreSslErrors ? 1 : 0), ("$df", r.IsDefault ? 1 : 0), ("$id", r.Id)).ConfigureAwait(false);
        }

        if (r.IsDefault)
            await ExecAsync("UPDATE Registries SET IsDefault=0 WHERE Id<>$id", ct, ("$id", r.Id)).ConfigureAwait(false);
        return r.Id;
    }

    public Task DeleteRegistryAsync(long id, CancellationToken ct = default)
        => ExecAsync("DELETE FROM Registries WHERE Id=$id", ct, ("$id", id));

    // ──────────────────────────────────────────────────────────────────────
    // DockerHosts
    // ──────────────────────────────────────────────────────────────────────

    private static DockerHost MapHost(SqliteDataReader r) => new()
    {
        Id = I64(r, "Id"),
        Name = S(r, "Name") ?? "",
        Kind = (DockerHostKind)I32(r, "Kind"),
        Endpoint = S(r, "Endpoint") ?? "",
        Enabled = B(r, "Enabled"),
        ClientCertPem = S(r, "ClientCertPem"),
        ClientKeyPem = S(r, "ClientKeyPem"),
        CaCertPem = S(r, "CaCertPem"),
        WakeOnLanMac = S(r, "WakeOnLanMac"),
        WakeOnLanBroadcast = S(r, "WakeOnLanBroadcast"),
        WakeOnLanPort = I32(r, "WakeOnLanPort"),
        CreatedAt = ParseIso(S(r, "CreatedAt")),
        LastSeenAt = ParseIsoNull(S(r, "LastSeenAt")),
        LastError = S(r, "LastError")
    };

    public async Task<List<DockerHost>> GetDockerHostsAsync(CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM DockerHosts ORDER BY Id", MapHost, ct).ConfigureAwait(false);

    public async Task<DockerHost?> GetDockerHostByIdAsync(long id, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM DockerHosts WHERE Id=$id", MapHost, ct, ("$id", id)).ConfigureAwait(false);

    public async Task<long> UpsertDockerHostAsync(DockerHost h, CancellationToken ct = default)
    {
        if (h.Id == 0)
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = await CmdAsync(conn,
                "INSERT INTO DockerHosts(Name, Kind, Endpoint, Enabled, ClientCertPem, ClientKeyPem, CaCertPem, " +
                "WakeOnLanMac, WakeOnLanBroadcast, WakeOnLanPort, CreatedAt, LastSeenAt, LastError) " +
                "VALUES($n,$k,$e,$en,$cc,$ck,$ca,$wm,$wb,$wp,$cr,$ls,$le); SELECT last_insert_rowid();", ct,
                ("$n", h.Name), ("$k", (int)h.Kind), ("$e", h.Endpoint), ("$en", h.Enabled ? 1 : 0),
                ("$cc", h.ClientCertPem), ("$ck", h.ClientKeyPem), ("$ca", h.CaCertPem),
                ("$wm", h.WakeOnLanMac), ("$wb", h.WakeOnLanBroadcast), ("$wp", h.WakeOnLanPort),
                ("$cr", Iso(h.CreatedAt)), ("$ls", h.LastSeenAt.HasValue ? Iso(h.LastSeenAt.Value) : null),
                ("$le", h.LastError)).ConfigureAwait(false);
            h.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        else
        {
            await ExecAsync("UPDATE DockerHosts SET Name=$n, Kind=$k, Endpoint=$e, Enabled=$en, ClientCertPem=$cc, " +
                "ClientKeyPem=$ck, CaCertPem=$ca, WakeOnLanMac=$wm, WakeOnLanBroadcast=$wb, " +
                "WakeOnLanPort=$wp, LastSeenAt=$ls, LastError=$le WHERE Id=$id", ct,
                ("$n", h.Name), ("$k", (int)h.Kind), ("$e", h.Endpoint), ("$en", h.Enabled ? 1 : 0),
                ("$cc", h.ClientCertPem), ("$ck", h.ClientKeyPem), ("$ca", h.CaCertPem),
                ("$wm", h.WakeOnLanMac), ("$wb", h.WakeOnLanBroadcast), ("$wp", h.WakeOnLanPort),
                ("$ls", h.LastSeenAt.HasValue ? Iso(h.LastSeenAt.Value) : null), ("$le", h.LastError),
                ("$id", h.Id)).ConfigureAwait(false);
        }
        return h.Id;
    }

    public Task DeleteDockerHostAsync(long id, CancellationToken ct = default)
        => ExecAsync("DELETE FROM DockerHosts WHERE Id=$id", ct, ("$id", id));

    // ──────────────────────────────────────────────────────────────────────
    // KubeClusters
    // ──────────────────────────────────────────────────────────────────────

    private static KubeCluster MapCluster(SqliteDataReader r) => new()
    {
        Id = I64(r, "Id"),
        Name = S(r, "Name") ?? "",
        Enabled = B(r, "Enabled"),
        KubeConfigYaml = S(r, "KubeConfigYaml"),
        Token = S(r, "Token"),
        ClientCertPem = S(r, "ClientCertPem"),
        ClientKeyPem = S(r, "ClientKeyPem"),
        CaCertPem = S(r, "CaCertPem"),
        ServerUrl = S(r, "ServerUrl") ?? "",
        Context = S(r, "Context"),
        CreatedAt = ParseIso(S(r, "CreatedAt")),
        LastSeenAt = ParseIsoNull(S(r, "LastSeenAt")),
        LastError = S(r, "LastError")
    };

    public async Task<List<KubeCluster>> GetKubeClustersAsync(CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM KubeClusters ORDER BY Id", MapCluster, ct).ConfigureAwait(false);

    public async Task<KubeCluster?> GetKubeClusterByIdAsync(long id, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM KubeClusters WHERE Id=$id", MapCluster, ct, ("$id", id)).ConfigureAwait(false);

    public async Task<long> UpsertKubeClusterAsync(KubeCluster c, CancellationToken ct = default)
    {
        if (c.Id == 0)
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = await CmdAsync(conn,
                "INSERT INTO KubeClusters(Name, Enabled, KubeConfigYaml, Token, ClientCertPem, ClientKeyPem, " +
                "CaCertPem, ServerUrl, Context, CreatedAt, LastSeenAt, LastError) " +
                "VALUES($n,$en,$kc,$tk,$cc,$ck,$ca,$su,$cx,$cr,$ls,$le); SELECT last_insert_rowid();", ct,
                ("$n", c.Name), ("$en", c.Enabled ? 1 : 0), ("$kc", c.KubeConfigYaml), ("$tk", c.Token),
                ("$cc", c.ClientCertPem), ("$ck", c.ClientKeyPem), ("$ca", c.CaCertPem), ("$su", c.ServerUrl),
                ("$cx", c.Context), ("$cr", Iso(c.CreatedAt)),
                ("$ls", c.LastSeenAt.HasValue ? Iso(c.LastSeenAt.Value) : null), ("$le", c.LastError)).ConfigureAwait(false);
            c.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        else
        {
            await ExecAsync("UPDATE KubeClusters SET Name=$n, Enabled=$en, KubeConfigYaml=$kc, Token=$tk, " +
                "ClientCertPem=$cc, ClientKeyPem=$ck, CaCertPem=$ca, ServerUrl=$su, Context=$cx, " +
                "LastSeenAt=$ls, LastError=$le WHERE Id=$id", ct,
                ("$n", c.Name), ("$en", c.Enabled ? 1 : 0), ("$kc", c.KubeConfigYaml), ("$tk", c.Token),
                ("$cc", c.ClientCertPem), ("$ck", c.ClientKeyPem), ("$ca", c.CaCertPem), ("$su", c.ServerUrl),
                ("$cx", c.Context), ("$ls", c.LastSeenAt.HasValue ? Iso(c.LastSeenAt.Value) : null),
                ("$le", c.LastError), ("$id", c.Id)).ConfigureAwait(false);
        }
        return c.Id;
    }

    public Task DeleteKubeClusterAsync(long id, CancellationToken ct = default)
        => ExecAsync("DELETE FROM KubeClusters WHERE Id=$id", ct, ("$id", id));

    // ──────────────────────────────────────────────────────────────────────
    // StacksMeta
    // ──────────────────────────────────────────────────────────────────────

    private static StackMeta MapStack(SqliteDataReader r) => new()
    {
        Name = S(r, "Name") ?? "",
        State = (StackState)I32(r, "State"),
        AutoUpdateEnabled = B(r, "AutoUpdateEnabled"),
        AutoUpdateIntervalMinutes = I32(r, "AutoUpdateIntervalMinutes"),
        PruneOldImages = B(r, "PruneOldImages"),
        RollbackOnFailure = B(r, "RollbackOnFailure"),
        ConfigHash = S(r, "ConfigHash"),
        LastError = S(r, "LastError"),
        CreatedAt = ParseIso(S(r, "CreatedAt")),
        LastDeployedAt = ParseIsoNull(S(r, "LastDeployedAt")),
        LastUpdateCheckAt = ParseIsoNull(S(r, "LastUpdateCheckAt")),
        LastUpdatedAt = ParseIsoNull(S(r, "LastUpdatedAt")),
        UpdateCount = I32(r, "UpdateCount"),
        RollbackCount = I32(r, "RollbackCount")
    };

    public async Task<List<StackMeta>> GetStacksAsync(CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM StacksMeta ORDER BY Name", MapStack, ct).ConfigureAwait(false);

    public async Task<StackMeta?> GetStackAsync(string name, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM StacksMeta WHERE Name=$n", MapStack, ct, ("$n", name)).ConfigureAwait(false);

    public async Task UpsertStackAsync(StackMeta s, CancellationToken ct = default)
        => await ExecAsync(
            "INSERT INTO StacksMeta(Name, State, AutoUpdateEnabled, AutoUpdateIntervalMinutes, PruneOldImages, " +
            "RollbackOnFailure, ConfigHash, LastError, CreatedAt, LastDeployedAt, LastUpdateCheckAt, LastUpdatedAt, " +
            "UpdateCount, RollbackCount) VALUES($n,$st,$ae,$ai,$po,$rb,$ch,$le,$cr,$ld,$lu,$lup,$uc,$rc) " +
            "ON CONFLICT(Name) DO UPDATE SET State=$st, AutoUpdateEnabled=$ae, AutoUpdateIntervalMinutes=$ai, " +
            "PruneOldImages=$po, RollbackOnFailure=$rb, ConfigHash=$ch, LastError=$le, LastDeployedAt=$ld, " +
            "LastUpdateCheckAt=$lu, LastUpdatedAt=$lup, UpdateCount=$uc, RollbackCount=$rc", ct,
            ("$n", s.Name), ("$st", (int)s.State), ("$ae", s.AutoUpdateEnabled ? 1 : 0),
            ("$ai", s.AutoUpdateIntervalMinutes), ("$po", s.PruneOldImages ? 1 : 0),
            ("$rb", s.RollbackOnFailure ? 1 : 0), ("$ch", s.ConfigHash), ("$le", s.LastError),
            ("$cr", Iso(s.CreatedAt)), ("$ld", s.LastDeployedAt.HasValue ? Iso(s.LastDeployedAt.Value) : null),
            ("$lu", s.LastUpdateCheckAt.HasValue ? Iso(s.LastUpdateCheckAt.Value) : null),
            ("$lup", s.LastUpdatedAt.HasValue ? Iso(s.LastUpdatedAt.Value) : null),
            ("$uc", s.UpdateCount), ("$rc", s.RollbackCount)).ConfigureAwait(false);

    public Task DeleteStackAsync(string name, CancellationToken ct = default)
        => ExecAsync("DELETE FROM StacksMeta WHERE Name=$n", ct, ("$n", name));

    // ──────────────────────────────────────────────────────────────────────
    // ContainerImageSnapshots
    // ──────────────────────────────────────────────────────────────────────

    private static ContainerImageSnapshot MapSnapshot(SqliteDataReader r) => new()
    {
        Id = I64(r, "Id"),
        StackName = S(r, "StackName") ?? "",
        ContainerName = S(r, "ContainerName") ?? "",
        Image = S(r, "Image") ?? "",
        ImageId = S(r, "ImageId") ?? "",
        PreviousImageId = S(r, "PreviousImageId"),
        CapturedAt = ParseIso(S(r, "CapturedAt"))
    };

    public async Task SaveSnapshotAsync(ContainerImageSnapshot snap, CancellationToken ct = default)
        => await ExecAsync(
            "INSERT INTO ContainerImageSnapshots(StackName, ContainerName, Image, ImageId, PreviousImageId, CapturedAt) " +
            "VALUES($s,$c,$i,$ii,$pi,$t)", ct,
            ("$s", snap.StackName), ("$c", snap.ContainerName), ("$i", snap.Image), ("$ii", snap.ImageId),
            ("$pi", snap.PreviousImageId), ("$t", Iso(snap.CapturedAt))).ConfigureAwait(false);

    public async Task<List<ContainerImageSnapshot>> GetSnapshotsAsync(string stackName, CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM ContainerImageSnapshots WHERE StackName=$s ORDER BY Id DESC", MapSnapshot, ct,
            ("$s", stackName)).ConfigureAwait(false);

    public Task ClearSnapshotsAsync(string stackName, CancellationToken ct = default)
        => ExecAsync("DELETE FROM ContainerImageSnapshots WHERE StackName=$s", ct, ("$s", stackName));

    // ──────────────────────────────────────────────────────────────────────
    // PortalApps
    // ──────────────────────────────────────────────────────────────────────

    private static PortalApp MapPortalApp(SqliteDataReader r) => new()
    {
        Id = I64(r, "Id"),
        Subdomain = S(r, "Subdomain") ?? "",
        Name = S(r, "Name") ?? "",
        Icon = S(r, "Icon"),
        Category = S(r, "Category") ?? "Apps",
        Auth = (PortalAuthMode)I32(r, "Auth"),
        Autoscale = (AutoscaleMode)I32(r, "Autoscale"),
        Hidden = B(r, "Hidden"),
        Manual = B(r, "Manual"),
        AutoDiscovered = B(r, "AutoDiscovered"),
        TargetPort = (int?)I64Null(r, "TargetPort"),
        ContainerId = S(r, "ContainerId"),
        ContainerName = S(r, "ContainerName"),
        StackName = S(r, "StackName"),
        HostId = I64Null(r, "HostId"),
        ExternalUrl = S(r, "ExternalUrl"),
        SortOrder = I32(r, "SortOrder"),
        Notes = S(r, "Notes"),
        CreatedAt = ParseIso(S(r, "CreatedAt")),
        UpdatedAt = ParseIsoNull(S(r, "UpdatedAt"))
    };

    public async Task<List<PortalApp>> GetPortalAppsAsync(CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM PortalApps ORDER BY SortOrder, Name", MapPortalApp, ct).ConfigureAwait(false);

    public async Task<PortalApp?> GetPortalAppBySubdomainAsync(string subdomain, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM PortalApps WHERE Subdomain=$s COLLATE NOCASE", MapPortalApp, ct,
            ("$s", subdomain)).ConfigureAwait(false);

    public async Task<long> UpsertPortalAppAsync(PortalApp a, CancellationToken ct = default)
    {
        a.UpdatedAt = DateTimeOffset.UtcNow;
        await ExecAsync(
            "INSERT INTO PortalApps(Subdomain, Name, Icon, Category, Auth, Autoscale, Hidden, Manual, " +
            "AutoDiscovered, TargetPort, ContainerId, ContainerName, StackName, HostId, ExternalUrl, SortOrder, " +
            "Notes, CreatedAt, UpdatedAt) " +
            "VALUES($sd,$nm,$ic,$cat,$au,$as,$hi,$ma,$ad,$tp,$ci,$cn,$sn,$ho,$eu,$so,$no,$cr,$up) " +
            "ON CONFLICT(Subdomain) DO UPDATE SET Name=$nm, Icon=$ic, Category=$cat, Auth=$au, Autoscale=$as, " +
            "Hidden=$hi, Manual=$ma, AutoDiscovered=$ad, TargetPort=$tp, ContainerId=$ci, ContainerName=$cn, " +
            "StackName=$sn, HostId=$ho, ExternalUrl=$eu, SortOrder=$so, Notes=$no, UpdatedAt=$up", ct,
            ("$sd", a.Subdomain), ("$nm", a.Name), ("$ic", a.Icon), ("$cat", a.Category), ("$au", (int)a.Auth),
            ("$as", (int)a.Autoscale), ("$hi", a.Hidden ? 1 : 0), ("$ma", a.Manual ? 1 : 0),
            ("$ad", a.AutoDiscovered ? 1 : 0), ("$tp", a.TargetPort.HasValue ? a.TargetPort.Value : null),
            ("$ci", a.ContainerId), ("$cn", a.ContainerName), ("$sn", a.StackName), ("$ho", a.HostId),
            ("$eu", a.ExternalUrl), ("$so", a.SortOrder), ("$no", a.Notes), ("$cr", Iso(a.CreatedAt)),
            ("$up", Iso(a.UpdatedAt.Value))).ConfigureAwait(false);

        var stored = await GetPortalAppBySubdomainAsync(a.Subdomain, ct).ConfigureAwait(false);
        a.Id = stored?.Id ?? 0;
        return a.Id;
    }

    public Task DeletePortalAppAsync(string subdomain, CancellationToken ct = default)
        => ExecAsync("DELETE FROM PortalApps WHERE Subdomain=$s", ct, ("$s", subdomain));

    public Task DeleteAutoDiscoveredPortalAppAsync(string containerId, CancellationToken ct = default)
        => ExecAsync("DELETE FROM PortalApps WHERE ContainerId=$c AND AutoDiscovered=1", ct, ("$c", containerId));

    // ──────────────────────────────────────────────────────────────────────
    // Webhooks
    // ──────────────────────────────────────────────────────────────────────

    private static Webhook MapWebhook(SqliteDataReader r) => new()
    {
        Id = I64(r, "Id"),
        Name = S(r, "Name") ?? "",
        Kind = (WebhookKind)I32(r, "Kind"),
        Url = S(r, "Url") ?? "",
        ChatId = S(r, "ChatId"),
        Secret = S(r, "Secret"),
        Enabled = B(r, "Enabled"),
        Events = S(r, "Events") ?? "",
        CreatedAt = ParseIso(S(r, "CreatedAt")),
        LastFiredAt = ParseIsoNull(S(r, "LastFiredAt")),
        LastStatus = S(r, "LastStatus")
    };

    public async Task<List<Webhook>> GetWebhooksAsync(CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM Webhooks ORDER BY Id", MapWebhook, ct).ConfigureAwait(false);

    public async Task<long> UpsertWebhookAsync(Webhook w, CancellationToken ct = default)
    {
        if (w.Id == 0)
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = await CmdAsync(conn,
                "INSERT INTO Webhooks(Name, Kind, Url, ChatId, Secret, Enabled, Events, CreatedAt, LastFiredAt, LastStatus) " +
                "VALUES($n,$k,$u,$c,$s,$e,$ev,$cr,$lf,$ls); SELECT last_insert_rowid();", ct,
                ("$n", w.Name), ("$k", (int)w.Kind), ("$u", w.Url), ("$c", w.ChatId), ("$s", w.Secret),
                ("$e", w.Enabled ? 1 : 0), ("$ev", w.Events), ("$cr", Iso(w.CreatedAt)),
                ("$lf", w.LastFiredAt.HasValue ? Iso(w.LastFiredAt.Value) : null), ("$ls", w.LastStatus))
                .ConfigureAwait(false);
            w.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        else
        {
            await ExecAsync("UPDATE Webhooks SET Name=$n, Kind=$k, Url=$u, ChatId=$c, Secret=$s, Enabled=$e, " +
                "Events=$ev, LastFiredAt=$lf, LastStatus=$ls WHERE Id=$id", ct,
                ("$n", w.Name), ("$k", (int)w.Kind), ("$u", w.Url), ("$c", w.ChatId), ("$s", w.Secret),
                ("$e", w.Enabled ? 1 : 0), ("$ev", w.Events),
                ("$lf", w.LastFiredAt.HasValue ? Iso(w.LastFiredAt.Value) : null), ("$ls", w.LastStatus),
                ("$id", w.Id)).ConfigureAwait(false);
        }
        return w.Id;
    }

    public Task DeleteWebhookAsync(long id, CancellationToken ct = default)
        => ExecAsync("DELETE FROM Webhooks WHERE Id=$id", ct, ("$id", id));

    // ──────────────────────────────────────────────────────────────────────
    // MCP API keys
    // ──────────────────────────────────────────────────────────────────────

    private static McpApiKey MapKey(SqliteDataReader r) => new()
    {
        Id = I64(r, "Id"),
        Name = S(r, "Name") ?? "",
        Prefix = S(r, "Prefix") ?? "",
        TokenHash = S(r, "TokenHash") ?? "",
        Scopes = S(r, "Scopes") ?? "",
        Enabled = B(r, "Enabled"),
        CreatedAt = ParseIso(S(r, "CreatedAt")),
        LastUsedAt = ParseIsoNull(S(r, "LastUsedAt"))
    };

    public async Task<List<McpApiKey>> GetMcpApiKeysAsync(CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM McpApiKeys ORDER BY Id", MapKey, ct).ConfigureAwait(false);

    public async Task<long> AddMcpApiKeyAsync(McpApiKey k, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = await CmdAsync(conn,
            "INSERT INTO McpApiKeys(Name, Prefix, TokenHash, Scopes, Enabled, CreatedAt, LastUsedAt) " +
            "VALUES($n,$p,$h,$s,$e,$c,$l); SELECT last_insert_rowid();", ct,
            ("$n", k.Name), ("$p", k.Prefix), ("$h", k.TokenHash), ("$s", k.Scopes), ("$e", k.Enabled ? 1 : 0),
            ("$c", Iso(k.CreatedAt)), ("$l", k.LastUsedAt.HasValue ? Iso(k.LastUsedAt.Value) : null)).ConfigureAwait(false);
        k.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return k.Id;
    }

    public async Task<McpApiKey?> FindMcpApiKeyByHashAsync(string tokenHash, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM McpApiKeys WHERE TokenHash=$h AND Enabled=1", MapKey, ct,
            ("$h", tokenHash)).ConfigureAwait(false);

    public async Task<McpApiKey?> GetMcpApiKeyByIdAsync(long id, CancellationToken ct = default)
        => await QuerySingleAsync("SELECT * FROM McpApiKeys WHERE Id=$id", MapKey, ct, ("$id", id)).ConfigureAwait(false);

    public Task UpsertMcpApiKeyAsync(McpApiKey k, CancellationToken ct = default)
        => ExecAsync(
            "UPDATE McpApiKeys SET Name=$n, Prefix=$p, TokenHash=$h, Scopes=$s, Enabled=$e, LastUsedAt=$l WHERE Id=$id",
            ct, ("$n", k.Name), ("$p", k.Prefix), ("$h", k.TokenHash), ("$s", k.Scopes),
            ("$e", k.Enabled ? 1 : 0), ("$l", k.LastUsedAt.HasValue ? Iso(k.LastUsedAt.Value) : null),
            ("$id", k.Id));

    public Task TouchMcpApiKeyAsync(long id, CancellationToken ct = default)
        => ExecAsync("UPDATE McpApiKeys SET LastUsedAt=$t WHERE Id=$id", ct, ("$t", Iso(DateTimeOffset.UtcNow)), ("$id", id));

    public Task DeleteMcpApiKeyAsync(long id, CancellationToken ct = default)
        => ExecAsync("DELETE FROM McpApiKeys WHERE Id=$id", ct, ("$id", id));

    // ──────────────────────────────────────────────────────────────────────
    // Audit logs
    // ──────────────────────────────────────────────────────────────────────

    private static AuditLog MapAudit(SqliteDataReader r) => new()
    {
        Id = I64(r, "Id"),
        Actor = (ActorKind)I32(r, "Actor"),
        ActorDetail = S(r, "ActorDetail"),
        Action = S(r, "Action") ?? "",
        Target = S(r, "Target"),
        Detail = S(r, "Detail"),
        Success = B(r, "Success"),
        Timestamp = ParseIso(S(r, "Timestamp"))
    };

    public async Task AddAuditAsync(AuditLog log, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = await CmdAsync(conn,
            "INSERT INTO AuditLogs(Actor, ActorDetail, Action, Target, Detail, Success, Timestamp) " +
            "VALUES($a,$ad,$ac,$t,$d,$s,$ts); SELECT last_insert_rowid();", ct,
            ("$a", (int)log.Actor), ("$ad", log.ActorDetail), ("$ac", log.Action), ("$t", log.Target),
            ("$d", log.Detail), ("$s", log.Success ? 1 : 0), ("$ts", Iso(log.Timestamp))).ConfigureAwait(false);
        log.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task<List<AuditLog>> GetAuditLogsAsync(int limit = 200, CancellationToken ct = default)
        => await QueryAsync("SELECT * FROM AuditLogs ORDER BY Id DESC LIMIT $l", MapAudit, ct, ("$l", limit))
            .ConfigureAwait(false);

    public Task PruneAuditLogsAsync(int keep, CancellationToken ct = default)
        => ExecAsync("DELETE FROM AuditLogs WHERE Id NOT IN (SELECT Id FROM AuditLogs ORDER BY Id DESC LIMIT $k)", ct,
            ("$k", keep));

    /// <summary>Runs a raw scalar query. Intended for diagnostics and health checks.</summary>
    public async Task<long> ScalarLongAsync(string sql, CancellationToken ct = default)
        => await QuerySingleAsync(sql, r => I64(r, r.GetName(0)), ct).ConfigureAwait(false);
}
