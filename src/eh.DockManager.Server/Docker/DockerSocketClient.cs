// File: src/eh.DockManager.Server/Docker/DockerSocketClient.cs
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using eh.DockManager.Server.Data;
using eh.DockManager.Server.Models;
using eh.DockManager.Server.Security;

namespace eh.DockManager.Server.Docker;

/// <summary>An in-flight interactive exec session created on a Docker host.</summary>
public sealed record DockerExecSession(string Id, bool Tty);

/// <summary>
/// A raw, duplex connection to the Docker API obtained by hijacking the HTTP connection
/// (used for <c>exec</c> and <c>attach</c> where the Docker daemon takes over the socket).
/// </summary>
public sealed class DockerHijackStream : Stream
{
    private readonly Stream _inner;
    public DockerHijackStream(Stream inner, string statusLine, IReadOnlyDictionary<string, string> headers)
    {
        _inner = inner;
        StatusLine = statusLine;
        Headers = headers;
    }

    public string StatusLine { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public Stream Inner => _inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Pure managed Docker Engine API client supporting the local Unix socket and remote mutual-TLS hosts.
/// No <c>Docker.DotNet</c> dependency is used.
/// </summary>
public sealed class DockerSocketClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SocketsHttpHandler _handler;
    private readonly ILogger _log;
    private readonly CryptoVault _vault;
    private readonly CertificateManager _certs;
    private readonly string _apiVersion;

    public DockerHost Host { get; }

    public DockerSocketClient(DockerHost host, CryptoVault vault, CertificateManager certs, ILogger log,
        string apiVersion = "v1.43")
    {
        Host = host;
        _vault = vault;
        _certs = certs;
        _log = log;
        _apiVersion = apiVersion;

        _handler = BuildHandler(host);
        var baseAddress = host.Kind == DockerHostKind.UnixSocket
            ? new Uri("http://docker")
            : new Uri(host.Endpoint);
        _http = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "eh.DockManager/1.0");
    }

    private SocketsHttpHandler BuildHandler(DockerHost host)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };

        if (host.Kind == DockerHostKind.UnixSocket)
        {
            var socketPath = host.Endpoint.Replace("unix://", "", StringComparison.OrdinalIgnoreCase);
            handler.ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            };
        }
        else
        {
            var clientCert = LoadClientCertificate(host);
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = clientCert is null ? null : new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                {
                    if (errors == SslPolicyErrors.None) return true;
                    if (cert is X509Certificate2 c2)
                    {
                        if (_certs.ValidateClientCertificate(c2)) return true;
                        if (host.CaCertPem is { Length: > 0 })
                        {
                            using var pin = X509Certificate2.CreateFromPem(host.CaCertPem);
                            return c2.Thumbprint == pin.Thumbprint;
                        }
                    }
                    _log.LogWarning("Docker host {Host} TLS validation failed: {Errors}", host.Name, errors);
                    return false;
                }
            };
        }
        return handler;
    }

    private X509Certificate2? LoadClientCertificate(DockerHost host)
    {
        if (string.IsNullOrEmpty(host.ClientCertPem) || string.IsNullOrEmpty(host.ClientKeyPem)) return null;
        try
        {
            return X509Certificate2.CreateFromPem(host.ClientCertPem, host.ClientKeyPem);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load client certificate for Docker host {Host}.", host.Name);
            return null;
        }
    }

    // ── Low-level helpers ─────────────────────────────────────────────────

    private string Url(string path) => "/" + _apiVersion + path;

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/_ping", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Docker host {Host} ping failed.", Host.Name);
            return false;
        }
    }

    public async Task<JsonDocument> GetVersionAsync(CancellationToken ct = default)
        => await GetJsonAsync("/version", ct).ConfigureAwait(false);

    public async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(Url(path), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new DockerApiException(resp.StatusCode, path, body);
        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument> GetJsonOrNullAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(Url(path), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return JsonDocument.Parse("null");
        if (!resp.IsSuccessStatusCode) throw new DockerApiException(resp.StatusCode, path, body);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "null" : body);
    }

    private async Task<string> PostAsync(string path, string? jsonBody, CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = content };
        if (headers is not null)
            foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotModified)
            throw new DockerApiException(resp.StatusCode, path, body);
        return body;
    }

    private async Task<string> DeleteAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.DeleteAsync(Url(path), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new DockerApiException(resp.StatusCode, path, body);
        return body;
    }

    // ── Containers ────────────────────────────────────────────────────────

    public async Task<JsonDocument> ListContainersAsync(bool all = true, CancellationToken ct = default)
        => await GetJsonAsync($"/containers/json?all={(all ? "1" : "0")}", ct).ConfigureAwait(false);

    public async Task<JsonDocument> InspectContainerAsync(string id, CancellationToken ct = default)
        => await GetJsonOrNullAsync($"/containers/{Uri.EscapeDataString(id)}/json", ct).ConfigureAwait(false);

    public Task StartContainerAsync(string id, CancellationToken ct = default)
        => PostAsync($"/containers/{Uri.EscapeDataString(id)}/start", null, ct);

    public Task StopContainerAsync(string id, int timeoutSeconds = 10, CancellationToken ct = default)
        => PostAsync($"/containers/{Uri.EscapeDataString(id)}/stop?t={timeoutSeconds}", null, ct);

    public Task RestartContainerAsync(string id, int timeoutSeconds = 10, CancellationToken ct = default)
        => PostAsync($"/containers/{Uri.EscapeDataString(id)}/restart?t={timeoutSeconds}", null, ct);

    public Task KillContainerAsync(string id, string signal = "SIGKILL", CancellationToken ct = default)
        => PostAsync($"/containers/{Uri.EscapeDataString(id)}/kill?signal={Uri.EscapeDataString(signal)}", null, ct);

    public Task PauseContainerAsync(string id, CancellationToken ct = default)
        => PostAsync($"/containers/{Uri.EscapeDataString(id)}/pause", null, ct);

    public Task UnpauseContainerAsync(string id, CancellationToken ct = default)
        => PostAsync($"/containers/{Uri.EscapeDataString(id)}/unpause", null, ct);

    public Task RenameContainerAsync(string id, string newName, CancellationToken ct = default)
        => PostAsync($"/containers/{Uri.EscapeDataString(id)}/rename?name={Uri.EscapeDataString(newName)}", null, ct);

    public Task RemoveContainerAsync(string id, bool force = true, bool volumes = true, CancellationToken ct = default)
        => DeleteAsync($"/containers/{Uri.EscapeDataString(id)}?force={(force ? 1 : 0)}&v={(volumes ? 1 : 0)}", ct);

    /// <summary>Creates a container from an image with the supplied configuration JSON.</summary>
    public async Task<string> CreateContainerAsync(string name, string createConfigJson, CancellationToken ct = default)
    {
        var body = await PostAsync($"/containers/create?name={Uri.EscapeDataString(name)}", createConfigJson, ct)
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("Id").GetString() ?? string.Empty;
    }

    public async Task<bool> ContainerExistsAsync(string name, CancellationToken ct = default)
    {
        using var doc = await InspectContainerAsync(name, ct).ConfigureAwait(false);
        return doc.RootElement.ValueKind != JsonValueKind.Null;
    }

    public async Task<Stream> GetLogsStreamAsync(string id, bool follow, int tail, bool timestamps,
        CancellationToken ct = default)
        => await GetStreamAsync(
            $"/containers/{Uri.EscapeDataString(id)}/logs?stdout=1&stderr=1&follow={(follow ? 1 : 0)}" +
            $"&tail={tail}&timestamps={(timestamps ? 1 : 0)}", ct).ConfigureAwait(false);

    public async Task<Stream> GetStatsStreamAsync(string id, bool stream = true, CancellationToken ct = default)
        => await GetStreamAsync($"/containers/{Uri.EscapeDataString(id)}/stats?stream={(stream ? 1 : 0)}", ct)
            .ConfigureAwait(false);

    private async Task<Stream> GetStreamAsync(string path, CancellationToken ct)
    {
        var resp = await _http.GetAsync(Url(path), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            resp.Dispose();
            throw new DockerApiException(resp.StatusCode, path, body);
        }
        return await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    // ── Images ────────────────────────────────────────────────────────────

    public async Task<JsonDocument> ListImagesAsync(CancellationToken ct = default)
        => await GetJsonAsync("/images/json?all=1", ct).ConfigureAwait(false);

    public async Task<JsonDocument> SystemDfAsync(CancellationToken ct = default)
        => await GetJsonAsync("/system/df", ct).ConfigureAwait(false);

    /// <summary>Pulls an image, streaming progress frames from the daemon.</summary>
    public async Task<Stream> PullImageAsync(string fromImage, string tag, Registry? registry,
        CancellationToken ct = default)
    {
        var reference = string.IsNullOrEmpty(tag) ? fromImage : fromImage + ":" + tag;
        var auth = EncodeRegistryAuth(registry);
        var request = new HttpRequestMessage(HttpMethod.Post,
            Url($"/images/create?fromImage={Uri.EscapeDataString(fromImage)}&tag={Uri.EscapeDataString(tag)}"));
        request.Content = new StringContent(string.Empty);
        if (auth is not null) request.Headers.TryAddWithoutValidation("X-Registry-Auth", auth);
        var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            resp.Dispose();
            throw new DockerApiException(resp.StatusCode, $"images/create:{reference}", body);
        }
        return await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    public async Task<JsonDocument> PruneImagesAsync(bool danglingOnly, CancellationToken ct = default)
    {
        var filters = Uri.EscapeDataString("{\"dangling\":[\"" + (danglingOnly ? "true" : "false") + "\"]}");
        var body = await PostAsync($"/images/prune?filters={filters}", null, ct).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    public Task RemoveImageAsync(string id, bool force = true, CancellationToken ct = default)
        => DeleteAsync($"/images/{Uri.EscapeDataString(id)}?force={(force ? 1 : 0)}", ct);

    public async Task<JsonDocument> InspectImageAsync(string id, CancellationToken ct = default)
        => await GetJsonOrNullAsync($"/images/{Uri.EscapeDataString(id)}/json", ct).ConfigureAwait(false);

    public async Task<JsonDocument> PruneContainersAsync(CancellationToken ct = default)
    {
        var body = await PostAsync("/containers/prune", null, ct).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    // ── Events ────────────────────────────────────────────────────────────

    public async Task<Stream> StreamEventsAsync(string? filtersJson, CancellationToken ct = default)
    {
        var path = "/events";
        if (!string.IsNullOrEmpty(filtersJson))
            path += "?filters=" + Uri.EscapeDataString(filtersJson);
        return await GetStreamAsync(path, ct).ConfigureAwait(false);
    }

    // ── Archive (Tar file browser) ────────────────────────────────────────

    /// <summary>Streams a tar archive of the given path. Works even on stopped/distroless containers.</summary>
    public async Task<Stream> GetArchiveAsync(string containerId, string path, CancellationToken ct = default)
        => await GetStreamAsync(
            $"/containers/{Uri.EscapeDataString(containerId)}/archive?path={Uri.EscapeDataString(path)}", ct)
            .ConfigureAwait(false);

    /// <summary>Uploads a single file into a container at the destination directory.</summary>
    public async Task PutArchiveAsync(string containerId, string destinationDir, string fileName, Stream content,
        CancellationToken ct = default)
    {
        using var tar = new MemoryStream();
        await using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, fileName) { DataStream = content };
            await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
        }
        tar.Position = 0;
        using var streamContent = new StreamContent(tar);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        using var request = new HttpRequestMessage(HttpMethod.Put,
            Url($"/containers/{Uri.EscapeDataString(containerId)}/archive?path={Uri.EscapeDataString(destinationDir)}"));
        request.Content = streamContent;
        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new DockerApiException(resp.StatusCode, "archive:put", body);
        }
    }

    /// <summary>Lists directory entries inside a container by reading the returned tar stream.</summary>
    public async Task<List<TarEntryInfo>> ListArchiveAsync(string containerId, string path, CancellationToken ct = default)
    {
        await using var stream = await GetArchiveAsync(containerId, path, ct).ConfigureAwait(false);
        var entries = new List<TarEntryInfo>();
        await using var reader = new TarReader(stream, leaveOpen: false);
        while (await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            entries.Add(new TarEntryInfo(
                entry.Name.TrimEnd('/'),
                entry.EntryType is TarEntryType.Directory,
                entry.Length,
                entry.ModificationTime,
                entry.Mode.ToString()));
            if (entry.DataStream is not null)
                await entry.DataStream.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
        }
        return entries;
    }

    /// <summary>Reads a single file from inside a container (works on stopped containers).</summary>
    public async Task<byte[]> ReadArchiveFileAsync(string containerId, string filePath, CancellationToken ct = default)
    {
        await using var stream = await GetArchiveAsync(containerId, filePath, ct).ConfigureAwait(false);
        await using var reader = new TarReader(stream, leaveOpen: false);
        if (await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry
            && entry.DataStream is not null)
        {
            using var ms = new MemoryStream();
            await entry.DataStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        return Array.Empty<byte>();
    }

    // ── Exec (hijacked duplex stream) ─────────────────────────────────────

    public async Task<DockerExecSession> CreateExecAsync(string containerId, IEnumerable<string> command,
        bool tty = true, string? workingDir = null, IDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Tty = tty,
            Cmd = command.ToArray(),
            WorkingDir = workingDir,
            Env = environment?.Select(kv => kv.Key + "=" + kv.Value).ToArray()
        });
        var body = await PostAsync($"/containers/{Uri.EscapeDataString(containerId)}/exec", payload, ct)
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("Id").GetString()
                 ?? throw new DockerApiException(HttpStatusCode.InternalServerError, "exec/create", body);
        return new DockerExecSession(id, tty);
    }

    /// <summary>Starts an exec session and returns a raw duplex stream (hijacked connection).</summary>
    public async Task<DockerHijackStream> StartExecAsync(DockerExecSession session, CancellationToken ct = default)
    {
        var body = session.Tty ? "{\"Detach\":false,\"Tty\":true}" : "{\"Detach\":false,\"Tty\":false}";
        return await HijackAsync("POST", Url($"/exec/{session.Id}/start"), body, ct).ConfigureAwait(false);
    }

    /// <summary>Performs a one-shot command, returning combined output. Convenient for the MCP tool.</summary>
    public async Task<(int ExitCode, string Output)> RunCommandAsync(string containerId, IEnumerable<string> command,
        CancellationToken ct = default)
    {
        var session = await CreateExecAsync(containerId, command, tty: false, ct: ct).ConfigureAwait(false);
        var hijack = await StartExecAsync(session, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        await foreach (var (streamType, data) in DockerStreamDemuxer.DemuxAsync(hijack, ct).ConfigureAwait(false))
        {
            if (streamType is DockerStreamType.Stdout or DockerStreamType.Stderr)
                sb.Append(Encoding.UTF8.GetString(data));
        }
        var inspect = await GetJsonOrNullAsync($"/exec/{session.Id}/json", ct).ConfigureAwait(false);
        int exitCode = 0;
        if (inspect.RootElement.ValueKind == JsonValueKind.Object
            && inspect.RootElement.TryGetProperty("ExitCode", out var ec) && ec.ValueKind == JsonValueKind.Number)
            exitCode = ec.GetInt32();
        return (exitCode, sb.ToString());
    }

    // ── Raw hijack transport ──────────────────────────────────────────────

    private async Task<DockerHijackStream> HijackAsync(string method, string path, string body,
        CancellationToken ct)
    {
        Stream stream = await OpenRawConnectionAsync(ct).ConfigureAwait(false);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var request = new StringBuilder();
        request.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
        request.Append("Host: docker\r\n");
        request.Append("Content-Type: application/json\r\n");
        request.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        request.Append("Connection: Upgrade\r\n");
        request.Append("Upgrade: tcp\r\n");
        request.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(request.ToString());
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var (statusLine, headers) = await ReadResponseHeadAsync(stream, ct).ConfigureAwait(false);
        return new DockerHijackStream(stream, statusLine, headers);
    }

    private async Task<Stream> OpenRawConnectionAsync(CancellationToken ct)
    {
        if (Host.Kind == DockerHostKind.UnixSocket)
        {
            var socketPath = Host.Endpoint.Replace("unix://", "", StringComparison.OrdinalIgnoreCase);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }

        var uri = new Uri(Host.Endpoint);
        var tcp = new TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port, ct).ConfigureAwait(false);
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, cert, _, errors) =>
        {
            if (errors == SslPolicyErrors.None) return true;
            return cert is X509Certificate2 c2 && _certs.ValidateClientCertificate(c2);
        });
        var clientCert = LoadClientCertificate(Host);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = uri.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificates = clientCert is null ? null : new X509CertificateCollection { clientCert }
        };
        await ssl.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
        return ssl;
    }

    private static async Task<(string StatusLine, Dictionary<string, string> Headers)> ReadResponseHeadAsync(
        Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var sb = new StringBuilder();
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            sb.Append((char)buffer[0]);
            if (sb.Length >= 4 && sb[^4] == '\r' && sb[^3] == '\n' && sb[^2] == '\r' && sb[^1] == '\n') break;
            if (sb.Length > 65536) throw new IOException("Docker response headers too large.");
        }

        var lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var statusLine = lines.Length > 0 ? lines[0] : string.Empty;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int idx = lines[i].IndexOf(':');
            if (idx > 0) headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
        }
        return (statusLine, headers);
    }

    // ── Registry auth ─────────────────────────────────────────────────────

    public string? EncodeRegistryAuth(Registry? registry)
    {
        if (registry is null || (string.IsNullOrEmpty(registry.Username) && string.IsNullOrEmpty(registry.EncryptedPassword)))
            return null;
        var password = string.IsNullOrEmpty(registry.EncryptedPassword) ? null : _vault.DecryptOrNull(registry.EncryptedPassword);
        var payload = JsonSerializer.Serialize(new
        {
            username = registry.Username,
            password,
            serveraddress = registry.Url
        });
        return Base64Url(Encoding.UTF8.GetBytes(payload));
    }

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}

/// <summary>Raised when the Docker daemon returns a non-success status.</summary>
public sealed class DockerApiException : Exception
{
    public DockerApiException(HttpStatusCode statusCode, string path, string body)
        : base($"Docker API {path} failed with {(int)statusCode} {statusCode}: {Truncate(body)}")
    {
        StatusCode = statusCode;
        Path = path;
        ResponseBody = body;
    }

    public HttpStatusCode StatusCode { get; }
    public string Path { get; }
    public string ResponseBody { get; }

    private static string Truncate(string body) => body.Length <= 400 ? body : body[..400] + "…";
}

/// <summary>Creates and caches one <see cref="DockerSocketClient"/> per configured Docker host.</summary>
public sealed class DockerClientFactory : IDisposable
{
    private readonly SqliteWalStore _store;
    private readonly CryptoVault _vault;
    private readonly CertificateManager _certs;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DockerClientFactory> _log;
    private readonly ConcurrentDictionary<long, DockerSocketClient> _cache = new();

    public DockerClientFactory(SqliteWalStore store, CryptoVault vault, CertificateManager certs,
        ILoggerFactory loggerFactory, ILogger<DockerClientFactory> log)
    {
        _store = store;
        _vault = vault;
        _certs = certs;
        _loggerFactory = loggerFactory;
        _log = log;
    }

    public async Task<DockerSocketClient> GetAsync(long hostId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(hostId, out var cached)) return cached;
        var host = await _store.GetDockerHostByIdAsync(hostId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Unknown Docker host {hostId}.");
        return Cache(host);
    }

    public async Task<DockerSocketClient> GetDefaultAsync(CancellationToken ct = default)
    {
        var hosts = await _store.GetDockerHostsAsync(ct).ConfigureAwait(false);
        var host = hosts.FirstOrDefault(h => h.Enabled)
                   ?? throw new InvalidOperationException("No Docker host is configured.");
        return Cache(host);
    }

    private DockerSocketClient Cache(DockerHost host)
    {
        return _cache.GetOrAdd(host.Id, _ => new DockerSocketClient(host, _vault, _certs,
            _loggerFactory.CreateLogger<DockerSocketClient>()));
    }

    public void Invalidate(long hostId)
    {
        if (_cache.TryRemove(hostId, out var client)) client.Dispose();
    }

    public void Dispose()
    {
        foreach (var client in _cache.Values) client.Dispose();
        _cache.Clear();
    }
}

/// <summary>
/// Agentless vulnerability scanner: reads the package inventory from inside a container
/// (Debian <c>dpkg</c> or Alpine <c>apk</c>) and batch-queries the OSV.dev API.
/// </summary>
public sealed class DockerCveScanner
{
    private readonly DockerClientFactory _factory;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<DockerCveScanner> _log;

    public DockerCveScanner(DockerClientFactory factory, IHttpClientFactory http, ILogger<DockerCveScanner> log)
    {
        _factory = factory;
        _http = http;
        _log = log;
    }

    public async Task<List<CveFinding>> ScanAsync(string containerId, CancellationToken ct = default)
    {
        var client = await _factory.GetDefaultAsync(ct).ConfigureAwait(false);
        var packages = new List<(string Name, string Version, string Ecosystem)>();

        try
        {
            var dpkg = await client.ReadArchiveFileAsync(containerId, "/var/lib/dpkg/status", ct).ConfigureAwait(false);
            packages.AddRange(ParseDpkg(Encoding.UTF8.GetString(dpkg)));
        }
        catch (Exception ex) { _log.LogDebug(ex, "dpkg inventory unavailable for {Id}.", containerId); }

        if (packages.Count == 0)
        {
            try
            {
                var apk = await client.ReadArchiveFileAsync(containerId, "/lib/apk/db/installed", ct).ConfigureAwait(false);
                packages.AddRange(ParseApk(Encoding.UTF8.GetString(apk)));
            }
            catch (Exception ex) { _log.LogDebug(ex, "apk inventory unavailable for {Id}.", containerId); }
        }

        if (packages.Count == 0) return new List<CveFinding>();

        var queries = packages.Select(p => new
        {
            package = new { name = p.Name, ecosystem = p.Ecosystem },
            version = p.Version
        }).ToArray();

        var http = _http.CreateClient("osv");
        using var resp = await http.PostAsync("https://api.osv.dev/v1/querybatch",
            new StringContent(JsonSerializer.Serialize(new { queries }), Encoding.UTF8, "application/json"), ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("OSV.dev batch query returned {Status}.", resp.StatusCode);
            return new List<CveFinding>();
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var findings = new List<CveFinding>();
        if (doc.RootElement.TryGetProperty("results", out var results))
        {
            int i = 0;
            foreach (var result in results.EnumerateArray())
            {
                if (i >= packages.Count) break;
                var pkg = packages[i++];
                if (!result.TryGetProperty("vulns", out var vulns)) continue;
                foreach (var vuln in vulns.EnumerateArray())
                {
                    findings.Add(new CveFinding(
                        vuln.TryGetProperty("id", out var id) ? id.GetString() ?? "?" : "?",
                        pkg.Name,
                        pkg.Version,
                        vuln.TryGetProperty("summary", out var s) ? s.GetString() : null,
                        vuln.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.Array
                            ? sev.EnumerateArray().Select(x =>
                                x.TryGetProperty("score", out var sc) ? sc.GetString() : null)
                                .FirstOrDefault(x => x is not null)
                            : null));
                }
            }
        }
        return findings;
    }

    private static IEnumerable<(string Name, string Version, string Ecosystem)> ParseDpkg(string status)
    {
        string? name = null, version = null;
        foreach (var raw in status.Split('\n'))
        {
            if (raw.Length == 0)
            {
                if (name is not null && version is not null) yield return (name, version, "Debian");
                name = version = null;
                continue;
            }
            if (raw.StartsWith("Package:", StringComparison.Ordinal)) name = raw[8..].Trim();
            else if (raw.StartsWith("Version:", StringComparison.Ordinal)) version = raw[8..].Trim();
        }
        if (name is not null && version is not null) yield return (name, version, "Debian");
    }

    private static IEnumerable<(string Name, string Version, string Ecosystem)> ParseApk(string installed)
    {
        string? name = null, version = null;
        foreach (var raw in installed.Split('\n'))
        {
            if (raw.Length == 0)
            {
                if (name is not null && version is not null) yield return (name, version, "Alpine");
                name = version = null;
                continue;
            }
            if (raw.StartsWith("P:", StringComparison.Ordinal)) name = raw[2..].Trim();
            else if (raw.StartsWith("V:", StringComparison.Ordinal)) version = raw[2..].Trim();
        }
        if (name is not null && version is not null) yield return (name, version, "Alpine");
    }
}

public sealed record CveFinding(string Id, string Package, string Version, string? Summary, string? Severity);
