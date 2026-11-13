// File: src/eh.DockManager.Server/Docker/NetworkMetricsTracker.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using eh.DockManager.Server.Data;

namespace eh.DockManager.Server.Docker;

/// <summary>Per-interface counters reported by Docker's container stats (or /proc/net/dev on the host).</summary>
public sealed record NetworkInterfaceMetric(
    string Name,
    long RxBytes,
    long TxBytes,
    long RxPackets,
    long TxPackets,
    long RxErrors,
    long TxErrors,
    long RxDropped,
    long TxDropped);

/// <summary>A full parsed container stats sample including throttle, pids, block-IO and network detail.</summary>
public sealed record ExtendedContainerMetric(
    string ContainerId,
    double CpuPercent,
    int CpuCores,
    long CpuThrottledPeriods,
    double CpuThrottledMs,
    double MemoryMb,
    double MemoryLimitMb,
    double MemoryPeakMb,
    long MemoryFailCount,
    long PidsCurrent,
    long PidsLimit,
    long BlockReadBytes,
    long BlockWriteBytes,
    long NetworkRxBytes,
    long NetworkTxBytes,
    long NetworkRxPackets,
    long NetworkTxPackets,
    long NetworkRxErrors,
    long NetworkTxErrors,
    long NetworkRxDropped,
    long NetworkTxDropped,
    IReadOnlyList<NetworkInterfaceMetric> Interfaces);

/// <summary>One point on the traffic timeline.</summary>
public sealed record NetworkHistoryPoint(
    DateTimeOffset Timestamp,
    long RxBytes,
    long TxBytes,
    double RxBytesPerSec,
    double TxBytesPerSec,
    long RxErrors,
    long TxErrors,
    long RxDropped,
    long TxDropped);

/// <summary>TCP counters from the host's <c>/proc/net/snmp</c> (connection resets, retransmits, resets sent).</summary>
public sealed record HostTcpCounters(
    long CurrEstab,
    long ActiveOpens,
    long PassiveOpens,
    long AttemptFails,
    long EstabResets,
    long RetransSegs,
    long InErrs,
    long OutRsts);

/// <summary>Host-level network summary: 1h/24h totals, counters, interfaces, TCP health and the timeline.</summary>
public sealed record NetworkSummaryResponse(
    long RxLast1hBytes,
    long TxLast1hBytes,
    long RxLast24hBytes,
    long TxLast24hBytes,
    int TrackingMinutes,
    DateTimeOffset? TrackingSince,
    long RxPackets,
    long TxPackets,
    long RxErrors,
    long TxErrors,
    long RxDropped,
    long TxDropped,
    HostTcpCounters? HostTcp,
    IReadOnlyList<NetworkInterfaceMetric> HostInterfaces,
    IReadOnlyList<NetworkHistoryPoint> History);

/// <summary>Container-level network summary.</summary>
public sealed record ContainerNetworkHistoryResponse(
    string ContainerId,
    long RxLast1hBytes,
    long TxLast1hBytes,
    long RxLast24hBytes,
    long TxLast24hBytes,
    int TrackingMinutes,
    DateTimeOffset? TrackingSince,
    IReadOnlyList<NetworkHistoryPoint> History);

/// <summary>
/// Periodically samples running container network stats (plus <c>/proc/net/*</c> host counters when
/// accessible) and retains a 24-hour rolling ring buffer so the UI can chart In/Out traffic rates,
/// 1h/24h totals, packet drops and errors, and TCP connection resets.
/// </summary>
public sealed class NetworkMetricsTracker : BackgroundService
{
    private readonly DockerClientFactory _docker;
    private readonly SqliteWalStore _store;
    private readonly ILogger<NetworkMetricsTracker> _log;
    private readonly ConcurrentDictionary<string, LinkedList<Sample>> _hostHistory = new();
    private readonly ConcurrentDictionary<string, LinkedList<Sample>> _containerHistory = new();
    private readonly object _lock = new();
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly SemaphoreSlim Gate = new(6, 6);

    private readonly record struct Sample(
        DateTimeOffset At,
        long RxBytes,
        long TxBytes,
        long RxPackets,
        long TxPackets,
        long RxErrors,
        long TxErrors,
        long RxDropped,
        long TxDropped);

    public NetworkMetricsTracker(DockerClientFactory docker, SqliteWalStore store, ILogger<NetworkMetricsTracker> log)
    {
        _docker = docker;
        _store = store;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Network metrics background sample failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SampleAllAsync(CancellationToken ct)
    {
        var hosts = await _store.GetDockerHostsAsync(ct).ConfigureAwait(false);
        var hostIds = hosts.Where(h => h.Enabled).Select(h => (long?)h.Id).ToList();
        if (hostIds.Count == 0) hostIds.Add(null);

        foreach (var hostId in hostIds)
        {
            try
            {
                var client = hostId.HasValue
                    ? await _docker.GetAsync(hostId.Value, ct).ConfigureAwait(false)
                    : await _docker.GetDefaultAsync(ct).ConfigureAwait(false);
                using var list = await client.GetJsonAsync("/containers/json?all=0", ct).ConfigureAwait(false);
                var ids = new List<string>();
                foreach (var item in list.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("Id", out var idProp) && idProp.GetString() is { Length: > 0 } id)
                        ids.Add(id);
                }

                var metrics = new ConcurrentBag<ExtendedContainerMetric>();
                await Task.WhenAll(ids.Select(async id =>
                {
                    await Gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await using var stream = await client.GetStatsStreamAsync(id, stream: false, ct).ConfigureAwait(false);
                        using var reader = new StreamReader(stream);
                        var payload = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                        if (ParseExtendedMetric(id, payload) is { } m) metrics.Add(m);
                    }
                    catch
                    {
                        /* skip an individual container's stats error */
                    }
                    finally
                    {
                        Gate.Release();
                    }
                })).ConfigureAwait(false);

                RecordHostMetrics(hostId, metrics.ToList());
            }
            catch
            {
                /* host offline: keep the previous history */
            }
        }
    }

    /// <summary>Records one sampling round for a host (also usable from the dashboard without waiting).</summary>
    public void RecordHostMetrics(long? hostId, IReadOnlyCollection<ExtendedContainerMetric> metrics)
    {
        var now = DateTimeOffset.UtcNow;
        var hostKey = hostId?.ToString(CultureInfo.InvariantCulture) ?? "default";
        long rx = 0, tx = 0, rxPkt = 0, txPkt = 0, rxErr = 0, txErr = 0, rxDrop = 0, txDrop = 0;

        foreach (var m in metrics)
        {
            rx += m.NetworkRxBytes;
            tx += m.NetworkTxBytes;
            rxPkt += m.NetworkRxPackets;
            txPkt += m.NetworkTxPackets;
            rxErr += m.NetworkRxErrors;
            txErr += m.NetworkTxErrors;
            rxDrop += m.NetworkRxDropped;
            txDrop += m.NetworkTxDropped;

            AppendSample(_containerHistory, m.ContainerId, new Sample(
                now, m.NetworkRxBytes, m.NetworkTxBytes,
                m.NetworkRxPackets, m.NetworkTxPackets,
                m.NetworkRxErrors, m.NetworkTxErrors,
                m.NetworkRxDropped, m.NetworkTxDropped));
        }

        AppendSample(_hostHistory, hostKey, new Sample(
            now, rx, tx, rxPkt, txPkt, rxErr, txErr, rxDrop, txDrop));
    }

    private void AppendSample(ConcurrentDictionary<string, LinkedList<Sample>> dict, string key, Sample sample)
    {
        var list = dict.GetOrAdd(key, _ => new LinkedList<Sample>());
        lock (_lock)
        {
            if (list.Last is { Value: var last } && (sample.At - last.At).TotalSeconds < 2)
                return;
            list.AddLast(sample);
            var cutoff = sample.At - Retention;
            while (list.First is { Value: var first } && first.At < cutoff)
                list.RemoveFirst();
        }
    }

    /// <summary>Host-level summary for the dashboard and the network view.</summary>
    public NetworkSummaryResponse GetHostNetworkSummary(long? hostId)
    {
        var samples = ReadHistory(_hostHistory, hostId?.ToString(CultureInfo.InvariantCulture) ?? "default", hostId is null);
        var last = samples.Count > 0 ? samples[^1] : default;
        var (rx1h, tx1h, rx24h, tx24h, trackingMinutes, trackingSince, history) = ComputeSeries(samples, 120);
        return new NetworkSummaryResponse(
            rx1h, tx1h, rx24h, tx24h,
            trackingMinutes, trackingSince,
            last.RxPackets, last.TxPackets,
            last.RxErrors, last.TxErrors,
            last.RxDropped, last.TxDropped,
            ReadHostTcpCounters(), ReadHostInterfaces(), history);
    }

    /// <summary>Container-level summary (accepts a short id or a name prefix).</summary>
    public ContainerNetworkHistoryResponse GetContainerNetworkHistory(string containerId)
    {
        var samples = ReadHistory(_containerHistory, containerId, allowAnyFallback: false, matchByPrefix: true);
        var (rx1h, tx1h, rx24h, tx24h, trackingMinutes, trackingSince, history) = ComputeSeries(samples, 90);
        return new ContainerNetworkHistoryResponse(
            containerId, rx1h, tx1h, rx24h, tx24h, trackingMinutes, trackingSince, history);
    }

    private List<Sample> ReadHistory(ConcurrentDictionary<string, LinkedList<Sample>> dict, string key,
        bool allowAnyFallback, bool matchByPrefix = false)
    {
        lock (_lock)
        {
            if (dict.TryGetValue(key, out var list)) return list.ToList();
            if (matchByPrefix && key.Length >= 6)
            {
                var match = dict.Keys.FirstOrDefault(k =>
                    k.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                if (match is not null && dict.TryGetValue(match, out var mList)) return mList.ToList();
            }
            if (allowAnyFallback)
            {
                var first = dict.Values.FirstOrDefault();
                if (first is not null) return first.ToList();
            }
            return new List<Sample>();
        }
    }

    private static (long Rx1h, long Tx1h, long Rx24h, long Tx24h, int TrackingMinutes, DateTimeOffset? TrackingSince,
        IReadOnlyList<NetworkHistoryPoint> History) ComputeSeries(List<Sample> samples, int maxPoints)
    {
        if (samples.Count == 0)
            return (0, 0, 0, 0, 0, null, Array.Empty<NetworkHistoryPoint>());

        var now = DateTimeOffset.UtcNow;
        var earliest = samples[0];
        var trackingMinutes = Math.Max(0, (int)Math.Round((now - earliest.At).TotalMinutes));

        long SumDelta(Func<Sample, long> selector, DateTimeOffset since)
        {
            long total = 0;
            Sample? prev = null;
            foreach (var s in samples)
            {
                if (s.At < since)
                {
                    prev = s;
                    continue;
                }
                if (prev.HasValue)
                {
                    var d = selector(s) - selector(prev.Value);
                    total += d >= 0 ? d : selector(s);
                }
                prev = s;
            }
            return total;
        }

        var rx1h = SumDelta(s => s.RxBytes, now.AddHours(-1));
        var tx1h = SumDelta(s => s.TxBytes, now.AddHours(-1));
        var rx24h = SumDelta(s => s.RxBytes, now.AddHours(-24));
        var tx24h = SumDelta(s => s.TxBytes, now.AddHours(-24));

        var points = new List<NetworkHistoryPoint>(samples.Count);
        for (int i = 0; i < samples.Count; i++)
        {
            var cur = samples[i];
            double rxRate = 0, txRate = 0;
            if (i > 0)
            {
                var prev = samples[i - 1];
                var dt = Math.Max(1.0, (cur.At - prev.At).TotalSeconds);
                var dRx = cur.RxBytes - prev.RxBytes;
                var dTx = cur.TxBytes - prev.TxBytes;
                if (dRx >= 0) rxRate = Math.Round(dRx / dt, 1);
                if (dTx >= 0) txRate = Math.Round(dTx / dt, 1);
            }
            points.Add(new NetworkHistoryPoint(
                cur.At, cur.RxBytes, cur.TxBytes, rxRate, txRate,
                cur.RxErrors, cur.TxErrors, cur.RxDropped, cur.TxDropped));
        }

        if (points.Count > maxPoints)
            points = points.Skip(points.Count - maxPoints).ToList();

        return (rx1h, tx1h, rx24h, tx24h, trackingMinutes, earliest.At, points);
    }

    private static HostTcpCounters? ReadHostTcpCounters()
    {
        try
        {
            if (!File.Exists("/proc/net/snmp")) return null;
            var lines = File.ReadAllLines("/proc/net/snmp");
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (!lines[i].StartsWith("Tcp:", StringComparison.Ordinal) ||
                    !lines[i + 1].StartsWith("Tcp:", StringComparison.Ordinal))
                    continue;
                var headers = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var values = lines[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var map = new Dictionary<string, long>(StringComparer.Ordinal);
                for (int j = 1; j < Math.Min(headers.Length, values.Length); j++)
                {
                    if (long.TryParse(values[j], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        map[headers[j]] = v;
                }
                return new HostTcpCounters(
                    map.GetValueOrDefault("CurrEstab"),
                    map.GetValueOrDefault("ActiveOpens"),
                    map.GetValueOrDefault("PassiveOpens"),
                    map.GetValueOrDefault("AttemptFails"),
                    map.GetValueOrDefault("EstabResets"),
                    map.GetValueOrDefault("RetransSegs"),
                    map.GetValueOrDefault("InErrs"),
                    map.GetValueOrDefault("OutRsts"));
            }
        }
        catch
        {
            /* /proc/net/snmp unavailable in the sandbox/container */
        }
        return null;
    }

    private static IReadOnlyList<NetworkInterfaceMetric> ReadHostInterfaces()
    {
        var result = new List<NetworkInterfaceMetric>();
        try
        {
            if (!File.Exists("/proc/net/dev")) return result;
            var lines = File.ReadAllLines("/proc/net/dev");
            foreach (var raw in lines.Skip(2))
            {
                var colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                var name = raw[..colon].Trim();
                if (name == "lo") continue;
                var parts = raw[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 12) continue;
                long Parse(int idx) => long.TryParse(parts[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
                result.Add(new NetworkInterfaceMetric(
                    name,
                    Parse(0),   // rx bytes
                    Parse(8),   // tx bytes
                    Parse(1),   // rx packets
                    Parse(9),   // tx packets
                    Parse(2),   // rx errors
                    Parse(10),  // tx errors
                    Parse(3),   // rx dropped
                    Parse(11))); // tx dropped
            }
        }
        catch
        {
            /* ignore */
        }
        return result;
    }

    /// <summary>Parses one Docker <c>/stats</c> payload into the full metric set.</summary>
    public static ExtendedContainerMetric? ParseExtendedMetric(string id, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            double cpuPercent = 0;
            int cores = 1;
            long throttledPeriods = 0;
            double throttledMs = 0;
            if (root.TryGetProperty("cpu_stats", out var cpu) && root.TryGetProperty("precpu_stats", out var pre)
                && cpu.TryGetProperty("cpu_usage", out var cpuUsage)
                && pre.TryGetProperty("cpu_usage", out var preUsage))
            {
                double cpuDelta = (cpuUsage.TryGetProperty("total_usage", out var cu) ? cu.GetDouble() : 0)
                                  - (preUsage.TryGetProperty("total_usage", out var pu) ? pu.GetDouble() : 0);
                double systemDelta = (cpu.TryGetProperty("system_cpu_usage", out var cs) ? cs.GetDouble() : 0)
                                     - (pre.TryGetProperty("system_cpu_usage", out var ps) ? ps.GetDouble() : 0);
                cores = cpu.TryGetProperty("online_cpus", out var oc) && oc.ValueKind == JsonValueKind.Number
                    ? oc.GetInt32()
                    : (cpuUsage.TryGetProperty("percpu_usage", out var per) && per.ValueKind == JsonValueKind.Array
                        ? Math.Max(1, per.GetArrayLength()) : 1);
                if (systemDelta > 0 && cpuDelta >= 0) cpuPercent = cpuDelta / systemDelta * cores * 100.0;
                if (cpu.TryGetProperty("throttling_data", out var td) && td.ValueKind == JsonValueKind.Object)
                {
                    if (td.TryGetProperty("throttled_periods", out var tp) && tp.ValueKind == JsonValueKind.Number)
                        throttledPeriods = tp.GetInt64();
                    if (td.TryGetProperty("throttled_time", out var tt) && tt.ValueKind == JsonValueKind.Number)
                        throttledMs = Math.Round(tt.GetDouble() / 1_000_000.0, 1);
                }
            }

            double memMb = 0, limitMb = 0, peakMb = 0;
            long failCount = 0;
            if (root.TryGetProperty("memory_stats", out var mem) && mem.ValueKind == JsonValueKind.Object)
            {
                if (mem.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Number)
                {
                    var rawBytes = usage.GetDouble();
                    if (mem.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
                    {
                        if (stats.TryGetProperty("inactive_file", out var inact) && inact.ValueKind == JsonValueKind.Number)
                            rawBytes = Math.Max(0, rawBytes - inact.GetDouble());
                        else if (stats.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Number)
                            rawBytes = Math.Max(0, rawBytes - cache.GetDouble());
                    }
                    memMb = rawBytes / 1048576.0;
                }
                if (mem.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Number)
                    limitMb = limit.GetDouble() / 1048576.0;
                if (mem.TryGetProperty("max_usage", out var maxU) && maxU.ValueKind == JsonValueKind.Number)
                    peakMb = maxU.GetDouble() / 1048576.0;
                if (mem.TryGetProperty("failcnt", out var fc) && fc.ValueKind == JsonValueKind.Number)
                    failCount = fc.GetInt64();
            }

            long pidsCurrent = 0, pidsLimit = 0;
            if (root.TryGetProperty("pids_stats", out var pids) && pids.ValueKind == JsonValueKind.Object)
            {
                if (pids.TryGetProperty("current", out var pc) && pc.ValueKind == JsonValueKind.Number)
                    pidsCurrent = pc.GetInt64();
                if (pids.TryGetProperty("limit", out var pl) && pl.ValueKind == JsonValueKind.Number)
                    pidsLimit = pl.GetInt64();
            }

            long blkRead = 0, blkWrite = 0;
            if (root.TryGetProperty("blkio_stats", out var blk) && blk.ValueKind == JsonValueKind.Object
                && blk.TryGetProperty("io_service_bytes_recursive", out var ioArr)
                && ioArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in ioArr.EnumerateArray())
                {
                    var op = entry.TryGetProperty("op", out var opProp) ? opProp.GetString() : null;
                    var val = entry.TryGetProperty("value", out var vProp) && vProp.ValueKind == JsonValueKind.Number
                        ? vProp.GetInt64() : 0;
                    if (string.Equals(op, "read", StringComparison.OrdinalIgnoreCase)) blkRead += val;
                    else if (string.Equals(op, "write", StringComparison.OrdinalIgnoreCase)) blkWrite += val;
                }
            }

            long rx = 0, tx = 0, rxPkt = 0, txPkt = 0, rxErr = 0, txErr = 0, rxDrop = 0, txDrop = 0;
            var interfaces = new List<NetworkInterfaceMetric>();
            if (root.TryGetProperty("networks", out var networks) && networks.ValueKind == JsonValueKind.Object)
            {
                foreach (var net in networks.EnumerateObject())
                {
                    long irx = GetL(net.Value, "rx_bytes"), itx = GetL(net.Value, "tx_bytes");
                    long irxp = GetL(net.Value, "rx_packets"), itxp = GetL(net.Value, "tx_packets");
                    long irxe = GetL(net.Value, "rx_errors"), itxe = GetL(net.Value, "tx_errors");
                    long irxd = GetL(net.Value, "rx_dropped"), itxd = GetL(net.Value, "tx_dropped");
                    rx += irx; tx += itx;
                    rxPkt += irxp; txPkt += itxp;
                    rxErr += irxe; txErr += itxe;
                    rxDrop += irxd; txDrop += itxd;
                    interfaces.Add(new NetworkInterfaceMetric(net.Name, irx, itx, irxp, itxp, irxe, itxe, irxd, itxd));
                }
            }

            return new ExtendedContainerMetric(
                id,
                Math.Round(cpuPercent, 2),
                cores,
                throttledPeriods,
                throttledMs,
                Math.Round(memMb, 1),
                Math.Round(limitMb, 1),
                Math.Round(peakMb, 1),
                failCount,
                pidsCurrent,
                pidsLimit,
                blkRead,
                blkWrite,
                rx, tx, rxPkt, txPkt, rxErr, txErr, rxDrop, txDrop,
                interfaces);
        }
        catch
        {
            return null;
        }
    }

    private static long GetL(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
