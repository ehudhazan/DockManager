// File: src/eh.DockManager.Server/Security/AcmeClient.cs
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using eh.DockManager.Server.Data;
using eh.DockManager.Server.Models;

namespace eh.DockManager.Server.Security;

/// <summary>
/// Holds the in-flight HTTP-01 key authorizations, keyed by challenge token. The
/// <c>/.well-known/acme-challenge/{token}</c> endpoint reads from here while the CA validates.
/// </summary>
public sealed class AcmeChallengeStore
{
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public void Set(string token, string keyAuthorization) => _tokens[token] = keyAuthorization;

    public void Remove(string token) => _tokens.TryRemove(token, out _);

    public bool TryGet(string token, out string keyAuthorization) => _tokens.TryGetValue(token, out keyAuthorization!);
}

/// <summary>Outcome of one ACME order.</summary>
public sealed record AcmeIssueResult(bool Success, X509Certificate2? Certificate, string? ChainPem,
    List<string> Domains, DateTimeOffset? NotAfter, string? Error);

/// <summary>
/// Minimal ACME v2 (RFC 8555) client: an ECDSA P-256 account key, ES256 JWS, HTTP-01 challenges
/// and a PKCS#10 CSR, all built on <see cref="ECDsa"/>, <see cref="CertificateRequest"/> and
/// <see cref="HttpClient"/>. No external package is used (the repository's NuGet allowlist has no
/// ACME library). It needs the domain to resolve publicly and inbound TCP/80 to reach this server.
/// </summary>
public sealed class AcmeClient
{
    public const string ProductionDirectory = "https://acme-v02.api.letsencrypt.org/directory";
    public const string StagingDirectory = "https://acme-staging-v02.api.letsencrypt.org/directory";
    private const string Http01Prefix = "/.well-known/acme-challenge/";

    private readonly AcmeChallengeStore _challenges;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AcmeClient> _log;

    public AcmeClient(AcmeChallengeStore challenges, IHttpClientFactory http, ILogger<AcmeClient> log)
    {
        _challenges = challenges;
        _http = http;
        _log = log;
    }

    public static string DirectoryFor(AcmeConfig config)
        => config.UseStaging ? StagingDirectory
            : string.IsNullOrWhiteSpace(config.DirectoryUrl) ? ProductionDirectory : config.DirectoryUrl.Trim();

    public async Task<AcmeIssueResult> IssueAsync(AcmeConfig config, IEnumerable<string> domains,
        string accountKeyPath, CancellationToken ct)
    {
        var dnsNames = domains.Select(d => (d ?? string.Empty).Trim().Trim('.').ToLowerInvariant())
            .Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dnsNames.Count == 0)
            return new AcmeIssueResult(false, null, null, dnsNames, null, "Enter the domain to certify.");
        var primary = dnsNames[0];

        try
        {
            var client = _http.CreateClient("acme");
            var directory = await GetJsonAsync(client, DirectoryFor(config), ct).ConfigureAwait(false);
            var newNonce = directory["newNonce"]!.GetValue<string>();
            var newAccount = directory["newAccount"]!.GetValue<string>();
            var newOrder = directory["newOrder"]!.GetValue<string>();

            using var accountKey = LoadOrCreateAccountKey(accountKeyPath);
            var (jwk, canonical) = BuildJwk(accountKey);
            var thumbprint = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

            string? nonce = null;
            async Task<string> NextNonceAsync()
            {
                if (nonce is not null) { var n = nonce; nonce = null; return n; }
                using var head = new HttpRequestMessage(HttpMethod.Head, newNonce);
                using var resp = await client.SendAsync(head, ct).ConfigureAwait(false);
                return ReadNonce(resp) ?? throw new InvalidOperationException("ACME server did not return a nonce.");
            }

            async Task<(JsonNode? Json, string Text, HttpResponseMessage Response)> PostAsync(
                string url, string? kid, JsonNode? payload, bool postAsGet = false)
            {
                var attempt = 0;
                while (true)
                {
                    var currentNonce = await NextNonceAsync().ConfigureAwait(false);
                    var header = new JsonObject { ["alg"] = "ES256", ["nonce"] = currentNonce, ["url"] = url };
                    if (kid is null) header["jwk"] = jwk.DeepClone();
                    else header["kid"] = kid;

                    var protectedB64 = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()));
                    var payloadB64 = postAsGet ? string.Empty
                        : Base64Url(Encoding.UTF8.GetBytes((payload ?? new JsonObject()).ToJsonString()));
                    var signingInput = Encoding.ASCII.GetBytes(protectedB64 + "." + payloadB64);
                    var signature = accountKey.SignData(signingInput, HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

                    var body = new JsonObject
                    {
                        ["protected"] = protectedB64,
                        ["payload"] = payloadB64,
                        ["signature"] = Base64Url(signature)
                    };
                    using var content = new StringContent(body.ToJsonString());
                    // The CA rejects "application/jose+json; charset=utf-8": the media type must be exact.
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/jose+json");
                    var response = await client.PostAsync(url, content, ct).ConfigureAwait(false);
                    nonce = ReadNonce(response);
                    var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        var parsed = IsJson(text) ? JsonNode.Parse(text) : null;
                        return (parsed, text, response);
                    }

                    // ACME rotates nonces aggressively; retry once when the server rejects ours.
                    if (attempt++ == 0 && text.Contains("badNonce", StringComparison.OrdinalIgnoreCase))
                    {
                        response.Dispose();
                        continue;
                    }

                    var detail = TryErrorDetail(text) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                    response.Dispose();
                    throw new InvalidOperationException($"ACME request to {url} failed: {detail}");
                }
            }

            // ── Account ─────────────────────────────────────────────────────
            var accountPayload = new JsonObject { ["termsOfServiceAgreed"] = true };
            if (!string.IsNullOrWhiteSpace(config.Email))
                accountPayload["contact"] = new JsonArray { "mailto:" + config.Email.Trim() };
            var accountResponse = (await PostAsync(newAccount, null, accountPayload).ConfigureAwait(false)).Response;
            var accountUrl = accountResponse.Headers.Location?.ToString();
            accountResponse.Dispose();
            if (string.IsNullOrWhiteSpace(accountUrl))
                throw new InvalidOperationException("ACME server did not return an account URL.");

            // ── Order ───────────────────────────────────────────────────────
            var identifiers = new JsonArray(dnsNames
                .Select(d => (JsonNode)new JsonObject { ["type"] = "dns", ["value"] = d }).ToArray());
            var orderPost = await PostAsync(newOrder, accountUrl,
                new JsonObject { ["identifiers"] = identifiers }).ConfigureAwait(false);
            var orderUrl = orderPost.Response.Headers.Location?.ToString()
                ?? throw new InvalidOperationException("ACME server did not return an order URL.");
            orderPost.Response.Dispose();
            var order = orderPost.Json!;

            // ── HTTP-01 challenges ──────────────────────────────────────────
            foreach (var authorizationUrl in order["authorizations"]!.AsArray())
            {
                var authzUrl = authorizationUrl!.GetValue<string>();
                var authz = (await PostAsync(authzUrl, accountUrl, null, postAsGet: true).ConfigureAwait(false)).Json!;
                var challenge = authz["challenges"]!.AsArray()
                    .FirstOrDefault(c => c!["type"]?.GetValue<string>() == "http-01")
                    ?? throw new InvalidOperationException("The CA offered no http-01 challenge.");
                var token = challenge["token"]!.GetValue<string>();
                _challenges.Set(token, token + "." + thumbprint);
                _log.LogInformation("ACME http-01 challenge ready at {Prefix}{Token}.", Http01Prefix, token);
                try
                {
                    await PostAsync(challenge["url"]!.GetValue<string>(), accountUrl, new JsonObject())
                        .ConfigureAwait(false);
                    var status = await WaitForAuthorizationAsync(PostAsync, authzUrl, accountUrl, ct).ConfigureAwait(false);
                    if (status != "valid")
                        return new AcmeIssueResult(false, null, null, dnsNames, null,
                            $"Authorization for {authz["identifier"]?["value"]?.GetValue<string>()} ended '{status}'. " +
                            "Check that the domain resolves publicly and port 80 reaches this server.");
                }
                finally
                {
                    _challenges.Remove(token);
                }
            }

            // ── Finalize ────────────────────────────────────────────────────
            using var certKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csr = new CertificateRequest("CN=" + primary, certKey, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            foreach (var d in dnsNames) san.AddDnsName(d);
            csr.CertificateExtensions.Add(san.Build());
            var csrDer = csr.CreateSigningRequest();

            var finalized = (await PostAsync(order["finalize"]!.GetValue<string>(), accountUrl,
                new JsonObject { ["csr"] = Base64Url(csrDer) }).ConfigureAwait(false)).Json!;
            var status2 = finalized["status"]?.GetValue<string>();
            for (var attempt = 0; attempt < 30 && status2 is "processing" or "ready" or null; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                finalized = (await PostAsync(orderUrl, accountUrl, null, postAsGet: true).ConfigureAwait(false)).Json!;
                status2 = finalized["status"]?.GetValue<string>();
            }
            if (status2 != "valid")
                return new AcmeIssueResult(false, null, null, dnsNames, null,
                    $"The order ended '{status2 ?? "unknown"}' after finalization.");

            var certificateUrl = finalized["certificate"]?.GetValue<string>()
                ?? throw new InvalidOperationException("The order is valid but carries no certificate URL.");
            var chainPem = (await PostAsync(certificateUrl, accountUrl, null, postAsGet: true).ConfigureAwait(false)).Text;
            var leafPem = FirstCertificatePem(chainPem);
            var keyPem = certKey.ExportECPrivateKeyPem();

            using var combined = X509Certificate2.CreateFromPem(leafPem, keyPem);
            var pfx = combined.Export(X509ContentType.Pfx);
            var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
            _log.LogInformation("ACME issued {Subject} (expires {Expiry:u}).", certificate.Subject, certificate.NotAfter);
            return new AcmeIssueResult(true, certificate, chainPem, dnsNames, certificate.NotAfter, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ACME issuance for {Domain} failed.", primary);
            return new AcmeIssueResult(false, null, null, dnsNames, null, ex.Message);
        }
    }

    private static async Task<string> WaitForAuthorizationAsync(
        Func<string, string?, JsonNode?, bool, Task<(JsonNode? Json, string Text, HttpResponseMessage Response)>> post,
        string authzUrl, string accountUrl, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var authz = (await post(authzUrl, accountUrl, null, true).ConfigureAwait(false)).Json;
            var status = authz?["status"]?.GetValue<string>();
            if (status is "valid" or "invalid") return status;
        }
        return "pending";
    }

    private static ECDsa LoadOrCreateAccountKey(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }
        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(path, created.ExportECPrivateKeyPem());
        CryptoVault.TryRestrict(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return created;
    }

    private static (JsonObject Jwk, string Canonical) BuildJwk(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        var x = Base64Url(parameters.Q.X!);
        var y = Base64Url(parameters.Q.Y!);
        // RFC 7638 thumbprint input: the required members in lexicographic order.
        var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}";
        return (new JsonObject { ["crv"] = "P-256", ["kty"] = "EC", ["x"] = x, ["y"] = y }, canonical);
    }

    private static string? ReadNonce(HttpResponseMessage response)
        => response.Headers.TryGetValues("Replay-Nonce", out var values) ? values.FirstOrDefault() : null;

    private static string? TryErrorDetail(string body)
    {
        try
        {
            var json = JsonNode.Parse(body);
            var detail = json?["detail"]?.GetValue<string>();
            var type = json?["type"]?.GetValue<string>();
            if (detail is null) return null;
            return type is null ? detail : $"{type}: {detail}";
        }
        catch
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }

    private static bool IsJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException($"Empty ACME directory at {url}.");
    }

    private static string FirstCertificatePem(string chainPem)
    {
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        var start = chainPem.IndexOf(begin, StringComparison.Ordinal);
        var stop = chainPem.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || stop < 0) throw new InvalidOperationException("The ACME response contained no certificate.");
        return chainPem[start..(stop + end.Length)];
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

}

/// <summary>
/// Renews the ACME certificate in the background. Every six hours it checks the active leaf and,
/// when ACME is enabled, re-issues it once it is within 30 days of expiry (or when ACME is enabled
/// but no ACME certificate is installed yet).
/// </summary>
public sealed class AcmeRenewalWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private readonly SqliteWalStore _store;
    private readonly AcmeClient _acme;
    private readonly CertificateManager _certs;
    private readonly ILogger<AcmeRenewalWorker> _log;

    public AcmeRenewalWorker(SqliteWalStore store, AcmeClient acme, CertificateManager certs,
        ILogger<AcmeRenewalWorker> log)
    {
        _store = store;
        _acme = acme;
        _certs = certs;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "ACME renewal check failed."); }
            try { await Task.Delay(Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var config = await _store.GetConfigObjectAsync("acme", new AcmeConfig(), ct).ConfigureAwait(false);
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.Domain)) return;

        var info = _certs.GetServerCertificateInfo();
        if (_certs.IsAcmeActive && info.NotAfter > DateTimeOffset.UtcNow.Add(RenewBefore)) return;

        var domains = config.Domain.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _log.LogInformation("Requesting ACME certificate for {Domains}.", string.Join(", ", domains));
        var result = await _acme.IssueAsync(config, domains,
            Path.Combine(EhConstants.CertsDir, "acme-account.pem"), ct).ConfigureAwait(false);
        config.LastError = result.Success ? null : result.Error;
        if (result.Success && result.Certificate is not null)
        {
            _certs.InstallExternalServerCertificate(result.Certificate, result.Domains, result.ChainPem);
            config.LastIssuedAt = DateTimeOffset.UtcNow;
        }
        await _store.SetConfigObjectAsync("acme", config, ct).ConfigureAwait(false);
    }
}
