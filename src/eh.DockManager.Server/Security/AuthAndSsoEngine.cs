// File: src/eh.DockManager.Server/Security/AuthAndSsoEngine.cs
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using eh.DockManager.Server.Data;
using eh.DockManager.Server.Models;
using Fido2NetLib;
using Fido2NetLib.Objects;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Http;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using Microsoft.IdentityModel.Tokens;

namespace eh.DockManager.Server.Security;

/// <summary>Central claim keys persisted in the session cookie.</summary>
public static class EhClaims
{
    public const string UserId = "eh:uid";
    public const string Username = "eh:username";
    public const string Email = "eh:email";
    public const string Role = "eh:role";
    public const string Provider = "eh:provider";
    public const string Amr = "eh:amr";
    public const string Name = ClaimTypes.Name;
    public const string RoleClaim = ClaimTypes.Role;
}

/// <summary>PBKDF2-HMAC-SHA512 password hashing with constant-time verification.</summary>
public static class PasswordHasher
{
    public const int Iterations = 600_000;
    private const int SaltSize = 32;
    private const int HashSize = 64;

    public static (string Hash, string Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA512, HashSize);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string? hashBase64, string? saltBase64)
    {
        if (string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64)) return false;
        try
        {
            var expected = Convert.FromBase64String(hashBase64);
            var salt = Convert.FromBase64String(saltBase64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>RFC 4648 Base32 used by authenticator applications for TOTP secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0) sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string input)
    {
        input = input.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in input)
        {
            int value = Alphabet.IndexOf(c);
            if (value < 0) continue;
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return output.ToArray();
    }
}

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30 second period) plus otpauth:// URIs.</summary>
public static class TotpService
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    public static string GenerateSecret(int byteLength = 20) => Base32.Encode(RandomNumberGenerator.GetBytes(byteLength));

    public static string ComputeCode(string base32Secret, DateTimeOffset at)
    {
        var key = Base32.Decode(base32Secret);
        long counter = at.ToUnixTimeSeconds() / PeriodSeconds;
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   | (hash[offset + 3] & 0xFF);
        int otp = binary % 1_000_000;
        return otp.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool ValidateCode(string base32Secret, string? code, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != Digits) return false;
        foreach (var c in code) if (!char.IsDigit(c)) return false;
        var now = DateTimeOffset.UtcNow;
        var supplied = Encoding.ASCII.GetBytes(code);
        for (int i = -window; i <= window; i++)
        {
            var candidate = Encoding.ASCII.GetBytes(ComputeCode(base32Secret, now.AddSeconds(i * PeriodSeconds)));
            if (CryptographicOperations.FixedTimeEquals(candidate, supplied)) return true;
        }
        return false;
    }

    public static string BuildOtpAuthUri(string issuer, string account, string secret) =>
        "otpauth://totp/" + Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account) +
        "?secret=" + secret +
        "&issuer=" + Uri.EscapeDataString(issuer) +
        "&algorithm=SHA1&digits=" + Digits + "&period=" + PeriodSeconds;

    public static string BuildQrSvg(string otpAuthUri) => QrCodeGenerator.ToSvg(otpAuthUri);
}

/// <summary>
/// SAML assertion replay protector. Combines a process-local <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// with durable <c>INSERT OR IGNORE</c> rows in the <c>SamlSeenAssertions</c> table, implementing
/// <see cref="ITokenReplayCache"/> for the ITfoxtec validation pipeline.
/// </summary>
public sealed class SamlAssertionReplayProtector : ITokenReplayCache
{
    private readonly SqliteWalStore _store;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private readonly ILogger<SamlAssertionReplayProtector> _log;

    public SamlAssertionReplayProtector(SqliteWalStore store, ILogger<SamlAssertionReplayProtector> log)
    {
        _store = store;
        _log = log;
    }

    public bool TryAdd(string key, DateTime expiresOn)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var expiresOnOffset = new DateTimeOffset(DateTime.SpecifyKind(expiresOn, DateTimeKind.Utc));
        if (_seen.TryGetValue(key, out var existing) && existing > DateTimeOffset.UtcNow)
        {
            _log.LogWarning("Rejected replayed SAML token {Key}.", key);
            return false;
        }
        _seen[key] = expiresOnOffset;
        try
        {
            return _store.TryInsertSamlAssertionAsync(new SamlSeenAssertion
            {
                Id = key,
                Issuer = "replay-cache",
                NotOnOrAfter = expiresOnOffset
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Replay cache persistence failed for {Key}; using in-memory only.", key);
            return true;
        }
    }

    public bool TryFind(string key)
        => !string.IsNullOrEmpty(key) && _seen.TryGetValue(key, out var exp) && exp > DateTimeOffset.UtcNow;

    /// <summary>Explicitly records a ResponseId so a replayed top-level response is rejected.</summary>
    public bool RecordResponseId(string responseId, string issuer, DateTimeOffset notOnOrAfter)
    {
        var key = "resp:" + responseId;
        _seen[key] = notOnOrAfter;
        try
        {
            return _store.TryInsertSamlAssertionAsync(new SamlSeenAssertion
            {
                Id = key,
                ResponseId = responseId,
                Issuer = issuer,
                NotOnOrAfter = notOnOrAfter
            }).GetAwaiter().GetResult();
        }
        catch { return true; }
    }

    public void Prune() => _store.PruneSamlAssertionsAsync().GetAwaiter().GetResult();
}

/// <summary>Hardened SAML 2.0 Service Provider built on ITfoxtec.Identity.Saml2.</summary>
public sealed class SamlSpService
{
    private readonly SqliteWalStore _store;
    private readonly SamlAssertionReplayProtector _replay;
    private readonly IHttpClientFactory _http;
    private readonly CertificateManager _certs;
    private readonly ILogger<SamlSpService> _log;

    private Saml2Configuration? _configuration;
    private SamlSpConfig _spConfig = new();
    private DateTimeOffset _builtAt = DateTimeOffset.MinValue;

    public SamlSpService(SqliteWalStore store, SamlAssertionReplayProtector replay,
        IHttpClientFactory http, CertificateManager certs, ILogger<SamlSpService> log)
    {
        _store = store;
        _replay = replay;
        _http = http;
        _certs = certs;
        _log = log;
    }

    public SamlSpConfig Config => _spConfig;
    public bool IsEnabled => _spConfig.Enabled;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        _spConfig = await _store.GetConfigObjectAsync("saml.sp", new SamlSpConfig(), ct).ConfigureAwait(false);
        if (!_spConfig.Enabled)
        {
            _configuration = null;
            return;
        }

        var config = new Saml2Configuration
        {
            Issuer = _spConfig.EntityId,
            AllowedIssuer = _spConfig.IdpEntityId,
            SignatureAlgorithm = Saml2SecurityAlgorithms.RsaSha256Signature,
            CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.ChainTrust,
            RevocationMode = X509RevocationMode.Online,
            DetectReplayedTokens = true,
            TokenReplayCache = _replay
        };

        if (!string.IsNullOrEmpty(_spConfig.IdpSsoUrl))
            config.SingleSignOnDestination = new Uri(_spConfig.IdpSsoUrl);

        var signing = LoadSpSigningCertificate();
        if (signing is not null) config.SigningCertificate = signing;

        // Resolve IdP metadata (cards, SSO URL, validation certificates).
        try
        {
            string? metadataXml = _spConfig.IdpMetadataXml;
            if (string.IsNullOrWhiteSpace(metadataXml) && !string.IsNullOrWhiteSpace(_spConfig.IdpMetadataUrl))
                metadataXml = await _http.CreateClient("saml").GetStringAsync(_spConfig.IdpMetadataUrl, ct)
                    .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(metadataXml))
            {
                var entity = new EntityDescriptor().ReadIdPSsoDescriptor(metadataXml);
                config.AllowedIssuer ??= entity.EntityId ?? _spConfig.IdpEntityId;
                var idp = entity.IdPSsoDescriptor;
                if (idp is not null)
                {
                    var sso = idp.SingleSignOnServices?.FirstOrDefault();
                    if (sso?.Location is not null && string.IsNullOrEmpty(_spConfig.IdpSsoUrl))
                        config.SingleSignOnDestination = sso.Location;
                    foreach (var cert in idp.SigningCertificates ?? Enumerable.Empty<X509Certificate2>())
                        config.SignatureValidationCertificates.Add(cert);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to resolve SAML IdP metadata; SP login may fail until corrected.");
        }

        _configuration = config;
        _builtAt = DateTimeOffset.UtcNow;
        _log.LogInformation("SAML SP configuration loaded (IdP SSO: {Sso}).", config.SingleSignOnDestination);
    }

    private X509Certificate2? LoadSpSigningCertificate()
    {
        if (string.IsNullOrEmpty(_spConfig.SpSigningCertPfxBase64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(_spConfig.SpSigningCertPfxBase64);
            return X509CertificateLoader.LoadPkcs12(bytes, _spConfig.SpSigningCertPassword,
                X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invalid SP signing certificate; continuing without request signing.");
            return null;
        }
    }

    public Saml2Configuration GetLiveConfiguration()
    {
        if (_configuration is null) throw new InvalidOperationException("SAML SP is not configured or enabled.");
        return _configuration;
    }

    public string BuildMetadataXml()
    {
        var config = GetLiveConfiguration();
        var acsUrl = new Uri(new Uri(config.Issuer), "/api/auth/saml2/acs");
        var sp = new SPSsoDescriptor
        {
            AuthnRequestsSigned = config.SigningCertificate is not null,
            WantAssertionsSigned = _spConfig.WantAssertionsSigned,
            AssertionConsumerServices = new List<AssertionConsumerService>
            {
                new() { Binding = ProtocolBindings.HttpPost, Location = acsUrl, Index = 0, IsDefault = true }
            }
        };
        if (config.SigningCertificate is not null)
            sp.SigningCertificates = new List<X509Certificate2> { config.SigningCertificate };

        var entity = new EntityDescriptor(config)
        {
            SPSsoDescriptor = sp
        };
        return new Saml2Metadata(entity).CreateMetadata().ToXml();
    }

    /// <summary>Builds a redirect-bound AuthnRequest and returns the redirect URL plus request id.</summary>
    public (string Url, string RequestId) BuildLoginRedirect(string returnUrl, string acsUrl)
    {
        var config = GetLiveConfiguration();
        var request = new Saml2AuthnRequest(config)
        {
            AssertionConsumerServiceUrl = new Uri(acsUrl),
            ForceAuthn = false,
            NameIdPolicy = new NameIdPolicy { AllowCreate = true },
            Destination = config.SingleSignOnDestination
        };

        var binding = new Saml2RedirectBinding();
        binding.SetRelayStateQuery(new Dictionary<string, string>
        {
            ["returnUrl"] = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl,
            ["reqId"] = request.IdAsString
        });
        binding.Bind(request);
        return (binding.RedirectLocation.ToString(), request.IdAsString);
    }

    /// <summary>
    /// Hardened ACS handler. Performs XXE/XML-bomb validation on the raw message before delegating to
    /// ITfoxtec for signature and condition validation.
    /// </summary>
    public SamlAssertionResult HandleAcs(bool isPost, NameValueCollection query, NameValueCollection form,
        string rawQueryString)
    {
        var encoded = isPost ? form["SAMLResponse"] : query["SAMLResponse"];
        if (string.IsNullOrEmpty(encoded))
            throw new InvalidOperationException("Missing SAMLResponse payload.");

        var xml = isPost ? DecodePost(encoded) : DecodeRedirect(encoded);
        GuardXml(xml);

        var httpRequest = new ITfoxtec.Identity.Saml2.Http.HttpRequest
        {
            Method = isPost ? "POST" : "GET",
            Query = query,
            Form = form,
            QueryString = rawQueryString
        };

        Saml2Binding binding = isPost ? new Saml2PostBinding() : new Saml2RedirectBinding();
        var response = new Saml2AuthnResponse(GetLiveConfiguration());
        binding.Unbind(httpRequest, response);

        if (response.Status != Saml2StatusCodes.Success)
            throw new InvalidOperationException($"SAML authentication failed with status {response.Status}.");

        var identity = response.ClaimsIdentity;
        var nameId = identity.Get(ClaimTypes.NameIdentifier)
                     ?? identity.Get("http://schemas.itfoxtec.com/ws/2014/02/identity/claims/saml2sessionnameid")
                     ?? identity.Get(ClaimTypes.Name);
        var sessionIndex = identity.Get("http://schemas.itfoxtec.com/ws/2014/02/identity/claims/saml2sessionindex");

        var responseId = response.InResponseToAsString;
        var notOnOrAfter = DateTimeOffset.UtcNow.AddMinutes(10);
        if (!string.IsNullOrEmpty(responseId))
            _replay.RecordResponseId(responseId, GetLiveConfiguration().Issuer ?? "sp", notOnOrAfter);

        return new SamlAssertionResult(identity, nameId, sessionIndex, responseId);
    }

    private static string DecodePost(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static string DecodeRedirect(string value)
    {
        var raw = Convert.FromBase64String(Uri.UnescapeDataString(value));
        using var input = new MemoryStream(raw);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Rejects DTDs, external entity resolution, and oversized documents before unbinding.</summary>
    private static void GuardXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 524_288,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            ConformanceLevel = ConformanceLevel.Document
        };
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);
        while (reader.Read()) { }
    }
}

public sealed record SamlAssertionResult(ClaimsIdentity Identity, string? NameId, string? SessionIndex, string? ResponseId);

/// <summary>Minimal-but-correct SAML 2.0 Identity Provider that lets self-hosted apps trust this server.</summary>
public sealed class SamlIdpService
{
    private readonly SqliteWalStore _store;
    private readonly CertificateManager _certs;
    private readonly ILogger<SamlIdpService> _log;
    private SamlIdpConfig _config = new();
    private X509Certificate2? _signingCertificate;

    public SamlIdpService(SqliteWalStore store, CertificateManager certs, ILogger<SamlIdpService> log)
    {
        _store = store;
        _certs = certs;
        _log = log;
    }

    public SamlIdpConfig Config => _config;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        _config = await _store.GetConfigObjectAsync("saml.idp", new SamlIdpConfig(), ct).ConfigureAwait(false);
        _signingCertificate = LoadOrCreateSigningCertificate();
    }

    private X509Certificate2 LoadOrCreateSigningCertificate()
    {
        var path = Path.Combine(EhConstants.CertsDir, "idp-signing.pfx");
        const string password = "eh-dockmanager-idp";
        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTimeOffset.UtcNow.AddDays(7)) return existing;
            }
            catch (Exception ex) { _log.LogWarning(ex, "Reloading SAML IdP signing certificate."); }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=eh.DockManager SAML IdP, O=eh.DockManager", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        using var self = req.CreateSelfSigned(now, now.AddYears(5));
        File.WriteAllBytes(path, self.Export(X509ContentType.Pfx, password));
        CryptoVault.TryRestrict(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx, password), password,
            X509KeyStorageFlags.Exportable);
    }

    public string GetMetadataXml()
    {
        var certB64 = Convert.ToBase64String(_signingCertificate?.RawData ?? Array.Empty<byte>());
        var sso = _config.SsoUrl;
        var xml =
            "<md:EntityDescriptor xmlns:md=\"urn:oasis:names:tc:SAML:2.0:metadata\" " +
            "xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\" " +
            $"entityID=\"{XmlEscape(_config.EntityId)}\">" +
            "<md:IDPSSODescriptor protocolSupportEnumeration=\"urn:oasis:names:tc:SAML:2.0:protocol\">" +
            "<md:KeyDescriptor use=\"signing\"><ds:KeyInfo><ds:X509Data><ds:X509Certificate>" + certB64 +
            "</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>" +
            "<md:NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</md:NameIDFormat>" +
            "<md:SingleSignOnService Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect\" " +
            $"Location=\"{XmlEscape(sso)}\"/>" +
            "<md:SingleSignOnService Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST\" " +
            $"Location=\"{XmlEscape(sso)}\"/>" +
            "</md:IDPSSODescriptor>" +
            "</md:EntityDescriptor>";
        return xml;
    }

    /// <summary>Builds and signs a SAMLResponse for the given user; returns base64 for the POST binding.</summary>
    public string BuildSignedResponse(string acsUrl, string? audience, string? inResponseTo,
        string subjectNameId, IDictionary<string, string> attributes)
    {
        if (_signingCertificate is null) throw new InvalidOperationException("SAML IdP signing certificate unavailable.");
        var now = DateTimeOffset.UtcNow;
        var notOnOrAfter = now.AddMinutes(_config.AssertionLifetimeMinutes);
        var responseId = "_" + Guid.NewGuid().ToString("N");
        var assertionId = "_" + Guid.NewGuid().ToString("N");

        XNamespace samlp = "urn:oasis:names:tc:SAML:2.0:protocol";
        XNamespace saml = "urn:oasis:names:tc:SAML:2.0:assertion";

        var attributeElements = attributes.Select(kv =>
            new XElement(saml + "Attribute",
                new XAttribute("Name", kv.Key),
                new XElement(saml + "AttributeValue", kv.Value))).ToArray();

        var assertion = new XElement(saml + "Assertion",
            new XAttribute("ID", assertionId),
            new XAttribute("Version", "2.0"),
            new XAttribute("IssueInstant", now.ToString("O")),
            new XElement(saml + "Issuer", _config.EntityId),
            new XElement(saml + "Subject",
                new XElement(saml + "NameID",
                    new XAttribute("Format", "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress"),
                    subjectNameId),
                new XElement(saml + "SubjectConfirmation",
                    new XAttribute("Method", "urn:oasis:names:tc:SAML:2.0:cm:bearer"),
                    new XElement(saml + "SubjectConfirmationData",
                        new XAttribute("NotOnOrAfter", notOnOrAfter.ToString("O")),
                        new XAttribute("Recipient", acsUrl),
                        inResponseTo is null ? new XAttribute("InResponseTo", "") : new XAttribute("InResponseTo", inResponseTo)))),
            new XElement(saml + "Conditions",
                new XAttribute("NotBefore", now.AddMinutes(-1).ToString("O")),
                new XAttribute("NotOnOrAfter", notOnOrAfter.ToString("O")),
                audience is null ? null : new XElement(saml + "AudienceRestriction",
                    new XElement(saml + "Audience", audience))),
            new XElement(saml + "AuthnStatement",
                new XAttribute("AuthnInstant", now.ToString("O")),
                new XAttribute("SessionIndex", "_" + Guid.NewGuid().ToString("N")),
                new XElement(saml + "AuthnContext",
                    new XElement(saml + "AuthnContextClassRef",
                        "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport"))),
            new XElement(saml + "AttributeStatement", attributeElements));

        var response = new XElement(samlp + "Response",
            new XAttribute("ID", responseId),
            new XAttribute("Version", "2.0"),
            new XAttribute("IssueInstant", now.ToString("O")),
            new XAttribute("Destination", acsUrl),
            inResponseTo is null ? null : new XAttribute("InResponseTo", inResponseTo),
            new XElement(saml + "Issuer", _config.EntityId),
            new XElement(samlp + "Status", new XElement(samlp + "StatusCode",
                new XAttribute("Value", "urn:oasis:names:tc:SAML:2.0:status:Success"))),
            assertion);

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(response.ToString(SaveOptions.DisableFormatting));

        var signed = SignAssertion(doc, assertionId);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(signed.OuterXml));
    }

    private XmlDocument SignAssertion(XmlDocument doc, string assertionId)
    {
        var assertionNode = doc.GetElementsByTagName("Assertion", "urn:oasis:names:tc:SAML:2.0:assertion")
            .Cast<XmlElement>().FirstOrDefault(e => e.GetAttribute("ID") == assertionId)
            ?? throw new InvalidOperationException("Unable to locate assertion for signing.");

        var signedXml = new SignedXmlWithId(doc)
        {
            SigningKey = _signingCertificate!.GetRSAPrivateKey()
        };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        var reference = new Reference("#" + assertionId)
        {
            DigestMethod = SignedXml.XmlDsigSHA256Url
        };
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(_signingCertificate));
        signedXml.KeyInfo = keyInfo;
        signedXml.ComputeSignature();
        var signature = signedXml.GetXml();
        assertionNode.ParentNode!.InsertAfter(doc.ImportNode(signature, true), assertionNode);
        return doc;
    }

    private static string XmlEscape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    /// <summary>SignedXml subclass that resolves ID attributes so references like #assertionId work.</summary>
    private sealed class SignedXmlWithId : SignedXml
    {
        public SignedXmlWithId(XmlDocument document) : base(document) { }

        public override XmlElement? GetIdElement(XmlDocument? document, string idValue)
            => base.GetIdElement(document, idValue)
               ?? document?.GetElementsByTagName("*").Cast<XmlElement>()
                   .FirstOrDefault(e => e.GetAttribute("ID") == idValue);
    }
}

/// <summary>Registers/looks up users from external identity providers under the strict pending policy.</summary>
public sealed class ExternalIdentityService
{
    private readonly SqliteWalStore _store;
    private readonly ILogger<ExternalIdentityService> _log;

    public ExternalIdentityService(SqliteWalStore store, ILogger<ExternalIdentityService> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Returns the local user for the given external identity, creating a <see cref="UserRole.Pending"/>
    /// account when it is unknown. The caller is responsible for firing the pending-user webhook.
    /// </summary>
    public async Task<(User User, bool IsNew)> ResolveAsync(IdentityProvider provider, string email,
        string? displayName, string? subject = null, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var user = await _store.GetUserByEmailAsync(normalized, ct).ConfigureAwait(false)
                   ?? await _store.GetUserByUsernameAsync(normalized, ct).ConfigureAwait(false);
        if (user is not null)
        {
            if (user.Provider == IdentityProvider.Local)
            {
                // Link an existing local account rather than creating a duplicate.
                user.LastLoginAt = DateTimeOffset.UtcNow;
                await _store.UpsertUserAsync(user, ct).ConfigureAwait(false);
            }
            return (user, false);
        }

        var created = new User
        {
            Username = normalized,
            Email = normalized,
            DisplayName = displayName,
            Provider = provider,
            Role = UserRole.Pending,
            Approved = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _store.UpsertUserAsync(created, ct).ConfigureAwait(false);
        await _store.AddAuditAsync(new AuditLog
        {
            Actor = ActorKind.System,
            Action = "user.pending",
            Target = normalized,
            Detail = $"New {provider} identity awaiting approval.",
            Timestamp = DateTimeOffset.UtcNow
        }, ct).ConfigureAwait(false);
        _log.LogInformation("Created pending {Provider} user for {Email}.", provider, normalized);
        return (created, true);
    }
}

/// <summary>Persisted WebAuthn / passkey ceremonies backed by Fido2.AspNet.</summary>
public sealed class PasskeyService
{
    private readonly Fido2 _fido2;
    private readonly SqliteWalStore _store;
    private readonly ILogger<PasskeyService> _log;
    private readonly ConcurrentDictionary<string, Ceremony> _ceremonies = new();

    private sealed record Ceremony(string OptionsJson, long? UserId, DateTimeOffset Expires);

    public PasskeyService(SqliteWalStore store, IConfiguration configuration,
        Portal.PortalProxyConfigurator portal, ILogger<PasskeyService> log)
    {
        _store = store;
        _log = log;
        // Follow the *configured* portal domain (persisted value wins over the environment
        // variable) so the WebAuthn RP ID matches the host the browser actually visits.
        var domain = string.IsNullOrWhiteSpace(portal.PortalDomain)
            ? EhConstants.DefaultPortalDomain
            : portal.PortalDomain;
        var origins = (configuration["EHDM_ORIGINS"] ?? $"https://{domain}")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = domain,
            ServerName = "eh.DockManager",
            ServerIcon = $"https://{domain}/favicon.ico",
            Origins = origins.ToHashSet(StringComparer.OrdinalIgnoreCase),
            TimestampDriftTolerance = 300,
            ChallengeSize = 32
        });
    }

    public (string FlowId, string OptionsJson) BeginRegistration(User user)
    {
        var existing = _store.GetPasskeysAsync(user.Id).GetAwaiter().GetResult();
        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Id = Encoding.UTF8.GetBytes(user.Id.ToString()),
                Name = user.Username,
                DisplayName = user.DisplayName ?? user.Username
            },
            ExcludeCredentials = existing
                .Select(p => new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, p.CredentialId))
                .ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                UserVerification = UserVerificationRequirement.Preferred,
                ResidentKey = ResidentKeyRequirement.Preferred
            },
            AttestationPreference = AttestationConveyancePreference.None,
            PubKeyCredParams = DefaultPubKeyCredParams()
        });

        return StoreCeremony(options.ToJson(), user.Id, options);
    }

    public async Task<Passkey> CompleteRegistrationAsync(string flowId, string attestationJson,
        string passkeyName, CancellationToken ct = default)
    {
        var ceremony = TakeCeremony(flowId, out var userId);
        if (userId is null) throw new InvalidOperationException("Unknown passkey registration ceremony.");
        var options = CredentialCreateOptions.FromJson(ceremony.OptionsJson);
        var raw = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationJson,
            Fido2Json.Options) ?? throw new InvalidOperationException("Invalid attestation payload.");

        var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = raw,
            OriginalOptions = options,
            IsCredentialIdUniqueToUserCallback = async (p, token) =>
                await _store.GetPasskeyByCredentialIdAsync(p.CredentialId, token).ConfigureAwait(false) is null
        }, ct).ConfigureAwait(false);

        var passkey = new Passkey
        {
            UserId = userId.Value,
            Name = string.IsNullOrWhiteSpace(passkeyName) ? "Passkey" : passkeyName,
            CredentialId = result.Id,
            PublicKey = result.PublicKey,
            SignCount = result.SignCount,
            AaGuid = result.AaGuid,
            Transports = (result.Transports ?? Array.Empty<AuthenticatorTransport>()).Select(t => (uint)t).ToArray(),
            BackupEligible = result.IsBackupEligible,
            BackupState = result.IsBackedUp,
            UserVerified = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _store.AddPasskeyAsync(passkey, ct).ConfigureAwait(false);
        return passkey;
    }

    public (string FlowId, string OptionsJson) BeginLogin(User? user)
    {
        IReadOnlyList<PublicKeyCredentialDescriptor> allowed = Array.Empty<PublicKeyCredentialDescriptor>();
        if (user is not null)
        {
            var keys = _store.GetPasskeysAsync(user.Id).GetAwaiter().GetResult();
            allowed = keys
                .Select(p => new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, p.CredentialId))
                .ToList();
        }

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            UserVerification = UserVerificationRequirement.Preferred
        });
        return StoreCeremony(options.ToJson(), user?.Id, options);
    }

    public async Task<(User User, Passkey Passkey)> CompleteLoginAsync(string flowId, string assertionJson,
        CancellationToken ct = default)
    {
        var ceremony = TakeCeremony(flowId, out _);
        var options = AssertionOptions.FromJson(ceremony.OptionsJson);
        var raw = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson, Fido2Json.Options)
                  ?? throw new InvalidOperationException("Invalid assertion payload.");

        var stored = await _store.GetPasskeyByCredentialIdAsync(raw.RawId, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Unknown credential.");

        var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = raw,
            OriginalOptions = options,
            StoredPublicKey = stored.PublicKey,
            StoredSignatureCounter = (uint)stored.SignCount,
            IsUserHandleOwnerOfCredentialIdCallback = (p, token) =>
                Task.FromResult(p.CredentialId.SequenceEqual(stored.CredentialId))
        }, ct).ConfigureAwait(false);

        stored.SignCount = result.SignCount;
        stored.BackupState = result.IsBackedUp;
        stored.LastUsedAt = DateTimeOffset.UtcNow;
        await _store.UpdatePasskeyCounterAsync(stored.Id, result.SignCount, result.IsBackedUp, ct).ConfigureAwait(false);

        var user = await _store.GetUserByIdAsync(stored.UserId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Passkey owner no longer exists.");
        return (user, stored);
    }

    private (string FlowId, string OptionsJson) StoreCeremony(string optionsJson, long? userId, object options)
    {
        var flowId = Guid.NewGuid().ToString("N");
        _ceremonies[flowId] = new Ceremony(optionsJson, userId, DateTimeOffset.UtcNow.AddMinutes(5));
        PruneCeremonies();
        return (flowId, optionsJson);
    }

    private Ceremony TakeCeremony(string flowId, out long? userId)
    {
        if (!_ceremonies.TryRemove(flowId, out var ceremony) || ceremony.Expires < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Passkey ceremony expired or unknown.");
        userId = ceremony.UserId;
        return ceremony;
    }

    private void PruneCeremonies()
    {
        var cutoff = DateTimeOffset.UtcNow;
        foreach (var kv in _ceremonies)
            if (kv.Value.Expires < cutoff) _ceremonies.TryRemove(kv.Key, out _);
    }

    private static List<PubKeyCredParam> DefaultPubKeyCredParams() =>
        new()
        {
            new PubKeyCredParam(COSE.Algorithm.ES256, PublicKeyCredentialType.PublicKey),
            new PubKeyCredParam(COSE.Algorithm.RS256, PublicKeyCredentialType.PublicKey),
            new PubKeyCredParam(COSE.Algorithm.EdDSA, PublicKeyCredentialType.PublicKey)
        };
}

/// <summary>Shared JSON options for deserializing Fido2 wire payloads.</summary>
internal static class Fido2Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>High-level orchestration: local login, session claims, TOTP step-up, and user approval.</summary>
public sealed class AuthService
{
    private readonly SqliteWalStore _store;
    private readonly ILogger<AuthService> _log;

    public AuthService(SqliteWalStore store, ILogger<AuthService> log)
    {
        _store = store;
        _log = log;
    }

    public async Task<User?> EnsureBootstrapAdminAsync(string username, string password, CancellationToken ct = default)
    {
        if (await _store.CountUsersAsync(ct).ConfigureAwait(false) > 0) return null;
        var (hash, salt) = PasswordHasher.Hash(password);
        var admin = new User
        {
            Username = username,
            Email = username.Contains('@') ? username : string.Empty,
            Role = UserRole.Admin,
            Provider = IdentityProvider.Local,
            PasswordHash = hash,
            PasswordSalt = salt,
            Approved = true,
            DisplayName = "Administrator"
        };
        await _store.UpsertUserAsync(admin, ct).ConfigureAwait(false);
        _log.LogWarning("Bootstrapped initial administrator '{User}'. Change the password immediately.", username);
        return admin;
    }

    public enum LoginOutcome { Success, InvalidCredentials, RequiresTotp, Disabled, Pending }

    public async Task<(LoginOutcome Outcome, User? User)> PasswordLoginAsync(string username, string password,
        string? totpCode, CancellationToken ct = default)
    {
        var user = await _store.GetUserByUsernameAsync(username, ct).ConfigureAwait(false);
        if (user is null)
        {
            // Perform a dummy verification to reduce user-enumeration timing signals.
            PasswordHasher.Verify(password, Convert.ToBase64String(new byte[64]), Convert.ToBase64String(new byte[32]));
            return (LoginOutcome.InvalidCredentials, null);
        }
        if (user.Disabled) return (LoginOutcome.Disabled, null);
        if (!PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt))
            return (LoginOutcome.InvalidCredentials, null);
        if (user.Role == UserRole.Pending || !user.Approved) return (LoginOutcome.Pending, user);
        if (user.TotpEnabled)
        {
            if (string.IsNullOrEmpty(totpCode)) return (LoginOutcome.RequiresTotp, user);
            if (string.IsNullOrEmpty(user.TotpSecret) || !TotpService.ValidateCode(user.TotpSecret, totpCode))
                return (LoginOutcome.InvalidCredentials, null);
        }
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _store.UpsertUserAsync(user, ct).ConfigureAwait(false);
        return (LoginOutcome.Success, user);
    }

    public ClaimsPrincipal BuildPrincipal(User user, string authenticationMethod)
    {
        var claims = new List<Claim>
        {
            new(EhClaims.UserId, user.Id.ToString()),
            new(EhClaims.Username, user.Username),
            new(EhClaims.Email, user.Email ?? string.Empty),
            new(EhClaims.Role, user.Role.ToString()),
            new(EhClaims.Provider, user.Provider.ToString()),
            new(EhClaims.Amr, authenticationMethod),
            new(ClaimTypes.Name, user.DisplayName ?? user.Username),
            new(ClaimTypes.Role, user.Role.ToString())
        };
        var identity = new ClaimsIdentity(claims, "eh.cookie");
        return new ClaimsPrincipal(identity);
    }

    public async Task<bool> ApproveUserAsync(long userId, UserRole role, CancellationToken ct = default)
    {
        var user = await _store.GetUserByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return false;
        user.Role = role;
        user.Approved = true;
        await _store.UpsertUserAsync(user, ct).ConfigureAwait(false);
        await _store.AddAuditAsync(new AuditLog
        {
            Actor = ActorKind.WebUi,
            Action = "user.approve",
            Target = user.Username,
            Detail = $"Approved as {role}.",
            Timestamp = DateTimeOffset.UtcNow
        }, ct).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Flattened helper for reading claims regardless of which SAML/OIDC dialect produced them.</summary>
public static class ClaimsExtensions
{
    public static string? Get(this ClaimsIdentity identity, string type) =>
        identity.FindFirst(type)?.Value;
}
