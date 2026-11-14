// File: src/eh.DockManager.Server/Security/CryptoAndCertManager.cs
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using eh.DockManager.Server.Models;

namespace eh.DockManager.Server.Security;

/// <summary>
/// AES-256-GCM secret vault. The 256-bit master key lives at <c>/data/.master.key</c>
/// with POSIX mode 0600 and is loaded exactly once per process.
/// Ciphertext layout: <c>base64( nonce[12] || tag[16] || ciphertext )</c>.
/// </summary>
public sealed class CryptoVault
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _masterKey;

    public CryptoVault(string? keyPath = null)
    {
        var path = keyPath ?? EhConstants.MasterKeyPath;
        _masterKey = LoadOrCreateKey(path);
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32) return existing;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, key);
        TryRestrict(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    internal static void TryRestrict(string path, UnixFileMode mode)
    {
        try { File.SetUnixFileMode(path, mode); }
        catch { /* Best effort on non-POSIX hosts. */ }
    }

    public string EncryptString(string plaintext)
    {
        if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_masterKey, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        var blob = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(blob);
    }

    public string DecryptString(string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return string.Empty;
        var blob = Convert.FromBase64String(encoded);
        if (blob.Length < NonceSize + TagSize) throw new CryptographicException("Vault payload too short.");
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_masterKey, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Encrypts a UTF-8 secret, returning null when the input is null/empty.</summary>
    public string? EncryptOrNull(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? null : EncryptString(plaintext);

    public string? DecryptOrNull(string? encoded)
        => string.IsNullOrEmpty(encoded) ? null : DecryptString(encoded);
}

/// <summary>
/// Owns the internal PKI: an ECDSA P-384 root CA, the ECDSA P-256 leaf server certificate
/// with dynamic SANs, and on-demand mutual-TLS client certificates.
/// The active server certificate is swapped atomically with <see cref="Interlocked.Exchange{T}"/>.
/// </summary>
public sealed class CertificateManager
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _certsDir;
    private readonly string _caPfxPath;
    private readonly string _caCrtPath;
    private readonly string _tlsPfxPath;
    private readonly string _clientsDir;
    private readonly string _sanManifestPath;
    private readonly string _acmeStatePath;
    private readonly string _acmeChainPath;
    private readonly ILogger<CertificateManager> _log;
    private volatile string _portalDomain;

    private X509Certificate2 _caCertificate;
    private X509Certificate2? _serverCertificate;
    private readonly object _mutex = new();
    private readonly ConcurrentDictionary<string, X509Certificate2> _clientCache = new();

    private const string CaCertPassword = "eh-dockmanager-ca";
    private const string TlsCertPassword = "eh-dockmanager-tls";

    public CertificateManager(ILogger<CertificateManager> log, string? certsDir = null, string? portalDomain = null)
    {
        _log = log;
        _portalDomain = string.IsNullOrWhiteSpace(portalDomain)
            ? (Environment.GetEnvironmentVariable("EHDM_PORTAL_DOMAIN") is { Length: > 0 } envDomain
                ? envDomain.Trim()
                : EhConstants.DefaultPortalDomain)
            : portalDomain.Trim();
        _certsDir = certsDir ?? EhConstants.CertsDir;
        _caPfxPath = Path.Combine(_certsDir, "ca.pfx");
        _caCrtPath = Path.Combine(_certsDir, "ca.crt");
        _tlsPfxPath = Path.Combine(_certsDir, "tls.pfx");
        _clientsDir = Path.Combine(_certsDir, "clients");
        _sanManifestPath = Path.Combine(_certsDir, "san-manifest.json");
        _acmeStatePath = Path.Combine(_certsDir, "acme-state.json");
        _acmeChainPath = Path.Combine(_certsDir, "acme-fullchain.pem");

        Directory.CreateDirectory(_certsDir);
        Directory.CreateDirectory(_clientsDir);

        _caCertificate = LoadOrCreateRootCa();
        _serverCertificate = LoadOrCreateServerCertificate(ReadSanManifest());
    }

    public X509Certificate2 CaCertificate => _caCertificate;

    /// <summary>The certificate currently presented by Kestrel (hot-swappable).</summary>
    public X509Certificate2? ServerCertificate => Volatile.Read(ref _serverCertificate);

    public string CaCertificatePem => _caCertificate.ExportCertificatePem();

    /// <summary>
    /// Updates the domain used for the certificate CN and default SANs. Does not re-issue the
    /// certificate by itself; call <see cref="RegenerateServerCertificate"/> to apply it.
    /// </summary>
    public void SetPortalDomain(string domain)
    {
        if (!string.IsNullOrWhiteSpace(domain)) _portalDomain = domain.Trim().Trim('.').ToLowerInvariant();
    }

    /// <summary>Describes the leaf certificate Kestrel currently presents (subject, SANs, expiry).</summary>
    public ServerCertificateInfo GetServerCertificateInfo()
    {
        var cert = ServerCertificate;
        var dns = new List<string>();
        var ips = new List<string>();
        if (cert is not null)
        {
            foreach (var ext in cert.Extensions)
            {
                if (ext is not X509SubjectAlternativeNameExtension san) continue;
                dns.AddRange(san.EnumerateDnsNames());
                ips.AddRange(san.EnumerateIPAddresses().Select(address => address.ToString()));
            }
        }
        return new ServerCertificateInfo(
            cert?.Subject ?? string.Empty,
            cert?.Thumbprint ?? string.Empty,
            cert?.NotAfter ?? DateTimeOffset.MinValue,
            dns,
            ips);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Root CA
    // ──────────────────────────────────────────────────────────────────────

    private X509Certificate2 LoadOrCreateRootCa()
    {
        if (File.Exists(_caPfxPath))
        {
            try
            {
                var ca = X509CertificateLoader.LoadPkcs12FromFile(_caPfxPath, CaCertPassword,
                    X509KeyStorageFlags.Exportable);
                _log.LogInformation("Loaded internal root CA {Subject} (expires {Expiry:u}).", ca.Subject, ca.NotAfter);
                return ca;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load existing root CA; regenerating.");
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var req = new CertificateRequest(
            "CN=eh.DockManager Internal Root CA, O=eh.DockManager, OU=PKI",
            key, HashAlgorithmName.SHA384);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var now = DateTimeOffset.UtcNow.AddDays(-1);
        using var selfSigned = req.CreateSelfSigned(now, now.AddYears(10));
        var caCert = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx, CaCertPassword),
            CaCertPassword, X509KeyStorageFlags.Exportable);

        File.WriteAllBytes(_caPfxPath, caCert.Export(X509ContentType.Pfx, CaCertPassword));
        CryptoVault.TryRestrict(_caPfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(_caCrtPath, caCert.ExportCertificatePem());
        CryptoVault.TryRestrict(_caCrtPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        _log.LogInformation("Generated new ECDSA P-384 internal root CA (expires {Expiry:u}).", caCert.NotAfter);
        return caCert;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Server certificate
    // ──────────────────────────────────────────────────────────────────────

    public (List<string> Dns, List<string> Ips) DefaultSans() => (
        new List<string> { "localhost", "*." + _portalDomain, _portalDomain },
        new List<string> { "127.0.0.1", "::1" });

    private (List<string> Dns, List<string> Ips) ReadSanManifest()
    {
        if (File.Exists(_sanManifestPath))
        {
            try
            {
                var m = JsonSerializer.Deserialize<SanManifest>(File.ReadAllText(_sanManifestPath), JsonOpts);
                if (m is not null && (m.Dns.Count > 0 || m.Ips.Count > 0)) return (m.Dns, m.Ips);
            }
            catch { /* fall through to defaults */ }
        }
        return DefaultSans();
    }

    private X509Certificate2 LoadOrCreateServerCertificate((List<string> Dns, List<string> Ips) sans)
    {
        if (File.Exists(_tlsPfxPath))
        {
            try
            {
                var tls = X509CertificateLoader.LoadPkcs12FromFile(_tlsPfxPath, TlsCertPassword,
                    X509KeyStorageFlags.Exportable);
                if (tls.NotAfter > DateTimeOffset.UtcNow.AddDays(7)
                    && SanMatches(tls, sans))
                {
                    _log.LogInformation("Loaded server certificate {Subject} (expires {Expiry:u}).", tls.Subject, tls.NotAfter);
                    return tls;
                }
                _log.LogInformation("Regenerating server certificate (expired or SAN change detected).");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load existing server certificate; regenerating.");
            }
        }
        return GenerateServerCertificate(sans);
    }

    private static bool SanMatches(X509Certificate2 cert, (List<string> Dns, List<string> Ips) sans)
    {
        try
        {
            var wanted = new HashSet<string>(sans.Dns, StringComparer.OrdinalIgnoreCase);
            foreach (var ip in sans.Ips) wanted.Add(ip);
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in cert.Extensions)
            {
                if (ext is not X509SubjectAlternativeNameExtension san) continue;
                foreach (var dns in san.EnumerateDnsNames()) present.Add(dns);
                foreach (var ip in san.EnumerateIPAddresses()) present.Add(ip.ToString());
            }
            return wanted.All(present.Contains);
        }
        catch { return true; }
    }

    private X509Certificate2 GenerateServerCertificate((List<string> Dns, List<string> Ips) sans)
    {
        lock (_mutex)
        {
            var ca = _caCertificate;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=" + _portalDomain,
                key, HashAlgorithmName.SHA256);

            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1") // serverAuth
            }, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            var san = new SubjectAlternativeNameBuilder();
            foreach (var dns in sans.Dns.Distinct(StringComparer.OrdinalIgnoreCase)) san.AddDnsName(dns);
            foreach (var ip in sans.Ips.Distinct())
                if (IPAddress.TryParse(ip, out var parsed)) san.AddIpAddress(parsed);
            req.CertificateExtensions.Add(san.Build());

            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = notBefore.AddYears(2);
            var serial = RandomNumberGenerator.GetBytes(16);
            using var issued = req.Create(ca, notBefore, notAfter, serial);
            using var withKey = issued.CopyWithPrivateKey(key);

            var pfx = withKey.Export(X509ContentType.Pfx, TlsCertPassword);
            File.WriteAllBytes(_tlsPfxPath, pfx);
            CryptoVault.TryRestrict(_tlsPfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            File.WriteAllText(_sanManifestPath, JsonSerializer.Serialize(
                new SanManifest { Dns = sans.Dns, Ips = sans.Ips }, JsonOpts));

            var result = X509CertificateLoader.LoadPkcs12(pfx, TlsCertPassword, X509KeyStorageFlags.Exportable);
            _log.LogInformation("Generated ECDSA P-256 server certificate with SANs: {Sans}. Expires {Expiry:u}.",
                string.Join(", ", sans.Dns.Concat(sans.Ips)), result.NotAfter);
            return result;
        }
    }

    /// <summary>Regenerates the leaf certificate with new SANs and hot-swaps it into Kestrel.</summary>
    public X509Certificate2 RegenerateServerCertificate(IEnumerable<string>? dnsNames, IEnumerable<string>? ipAddresses)
    {
        var (defaultDns, defaultIps) = DefaultSans();
        var dns = (dnsNames ?? Enumerable.Empty<string>())
            .Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim())
            .Concat(defaultDns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ips = (ipAddresses ?? Enumerable.Empty<string>())
            .Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim())
            .Concat(defaultIps).Distinct().ToList();

        var fresh = GenerateServerCertificate((dns, ips));
        var previous = Interlocked.Exchange(ref _serverCertificate, fresh);
        _log.LogInformation("Hot-swapped server certificate (previous thumbprint {Old}, new {New}).",
            previous?.Thumbprint, fresh.Thumbprint);
        return fresh;
    }

    // ──────────────────────────────────────────────────────────────────────
    // ACME / Let's Encrypt certificates
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the active leaf was issued by ACME, so the startup routine must not replace it
    /// with an internally signed certificate.
    /// </summary>
    public bool IsAcmeActive
    {
        get
        {
            try
            {
                if (!File.Exists(_acmeStatePath)) return false;
                using var doc = JsonDocument.Parse(File.ReadAllText(_acmeStatePath));
                return doc.RootElement.TryGetProperty("active", out var active) && active.GetBoolean();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Installs an externally issued (ACME) leaf with its chain, persists it as the active leaf and
    /// hot-swaps it into Kestrel. The SAN manifest is updated so a restart keeps the external
    /// certificate instead of regenerating an internal one.
    /// </summary>
    public X509Certificate2 InstallExternalServerCertificate(X509Certificate2 certificate,
        IEnumerable<string> dnsNames, string? chainPem = null)
    {
        lock (_mutex)
        {
            var dns = dnsNames.Select(d => d.Trim())
                .Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var pfx = certificate.Export(X509ContentType.Pfx, TlsCertPassword);
            File.WriteAllBytes(_tlsPfxPath, pfx);
            CryptoVault.TryRestrict(_tlsPfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            File.WriteAllText(_sanManifestPath, JsonSerializer.Serialize(
                new SanManifest { Dns = dns, Ips = new List<string>() }, JsonOpts));

            if (!string.IsNullOrWhiteSpace(chainPem))
            {
                File.WriteAllText(_acmeChainPath, chainPem);
                CryptoVault.TryRestrict(_acmeChainPath, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            File.WriteAllText(_acmeStatePath, JsonSerializer.Serialize(new
            {
                active = true,
                domains = dns,
                notAfter = certificate.NotAfter,
                installedAt = DateTimeOffset.UtcNow
            }, JsonOpts));

            var stable = X509CertificateLoader.LoadPkcs12(pfx, TlsCertPassword, X509KeyStorageFlags.Exportable);
            var previous = Interlocked.Exchange(ref _serverCertificate, stable);
            _log.LogInformation("Installed external (ACME) certificate {Subject}, expiring {Expiry:u}; hot-swapped {Old}.",
                stable.Subject, stable.NotAfter, previous?.Thumbprint);
            return stable;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // mTLS client certificates
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>Issues (or loads) a client certificate for an agent/host, signed by the internal CA.</summary>
    public (X509Certificate2 Certificate, string CertPem, string KeyPem) IssueClientCertificate(string commonName, int validDays = 825)
    {
        commonName = Sanitize(commonName);
        var cacheKey = commonName;
        if (_clientCache.TryGetValue(cacheKey, out var cached)
            && cached.NotAfter > DateTimeOffset.UtcNow.AddDays(7))
        {
            return (cached, cached.ExportCertificatePem(),
                cached.GetECDsaPrivateKey()?.ExportECPrivateKeyPem() ?? string.Empty);
        }

        var pfxPath = Path.Combine(_clientsDir, commonName + ".pfx");
        X509Certificate2 cert;
        if (File.Exists(pfxPath))
        {
            cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, CaCertPassword, X509KeyStorageFlags.Exportable);
            if (cert.NotAfter <= DateTimeOffset.UtcNow.AddDays(7))
                cert = CreateClientCertificate(commonName, validDays, pfxPath);
        }
        else
        {
            cert = CreateClientCertificate(commonName, validDays, pfxPath);
        }

        _clientCache[cacheKey] = cert;
        var keyPem = cert.GetECDsaPrivateKey()?.ExportECPrivateKeyPem() ?? string.Empty;
        return (cert, cert.ExportCertificatePem(), keyPem);
    }

    private X509Certificate2 CreateClientCertificate(string commonName, int validDays, string pfxPath)
    {
        lock (_mutex)
        {
            var ca = _caCertificate;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN={commonName}, OU=Docker Agent, O=eh.DockManager",
                key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.2") // clientAuth
            }, false));

            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = notBefore.AddDays(validDays);
            using var issued = req.Create(ca, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
            using var withKey = issued.CopyWithPrivateKey(key);
            var pfx = withKey.Export(X509ContentType.Pfx, CaCertPassword);
            File.WriteAllBytes(pfxPath, pfx);
            CryptoVault.TryRestrict(pfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return X509CertificateLoader.LoadPkcs12(pfx, CaCertPassword, X509KeyStorageFlags.Exportable);
        }
    }

    /// <summary>
    /// Issues a server certificate for the single-binary agent (<c>--agent</c>), signed by the
    /// internal root CA, so DockManager's Docker client can validate the agent's TLS endpoint
    /// through the same CA chain it already trusts. The remote host name/IP are added as SANs.
    /// </summary>
    public (X509Certificate2 Certificate, string CertPem, string KeyPem) IssueAgentServerCertificate(
        string commonName, IEnumerable<string>? dnsNames = null, IEnumerable<string>? ipAddresses = null,
        int validDays = 825)
    {
        commonName = Sanitize(commonName);
        lock (_mutex)
        {
            var ca = _caCertificate;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN={commonName}, OU=Docker Agent, O=eh.DockManager",
                key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1") // serverAuth
            }, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            var dns = (dnsNames ?? Enumerable.Empty<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim())
                .Append("localhost").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in dns) san.AddDnsName(name);
            foreach (var ip in (ipAddresses ?? Enumerable.Empty<string>())
                         .Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct())
                if (IPAddress.TryParse(ip, out var parsed)) san.AddIpAddress(parsed);
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());

            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = notBefore.AddDays(validDays);
            using var issued = req.Create(ca, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
            using var withKey = issued.CopyWithPrivateKey(key);
            var certPem = issued.ExportCertificatePem();
            var keyPem = key.ExportECPrivateKeyPem();
            var pfx = withKey.Export(X509ContentType.Pfx, CaCertPassword);
            _log.LogInformation(
                "Issued agent server certificate CN={CommonName} (SANs: {Sans}; expires {Expiry:u}).",
                commonName, string.Join(", ", dns), notAfter);
            return (X509CertificateLoader.LoadPkcs12(pfx, CaCertPassword, X509KeyStorageFlags.Exportable),
                certPem, keyPem);
        }
    }

    /// <summary>Validates a presented client certificate against the internal root CA chain.</summary>
    public bool ValidateClientCertificate(X509Certificate2? client)
    {
        if (client is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_caCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(client);
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        return sb.ToString();
    }

    private sealed class SanManifest
    {
        public List<string> Dns { get; set; } = new();
        public List<string> Ips { get; set; } = new();
    }
}

/// <summary>Immutable snapshot of the active leaf certificate used by the settings UI.</summary>
public sealed record ServerCertificateInfo(
    string Subject,
    string Thumbprint,
    DateTimeOffset NotAfter,
    IReadOnlyList<string> DnsNames,
    IReadOnlyList<string> IpAddresses);
