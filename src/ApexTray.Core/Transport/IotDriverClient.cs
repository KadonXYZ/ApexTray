using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace ApexTray.Core.Transport;

public enum ChecksumType { Bit7 = 0, Bit8 = 1, None = 2 }

public enum DangleDevType { None = 0, Keyboard = 1, Mouse = 2 }

public sealed record DongleState(string Path, uint MouseId, bool MouseOnline, uint MouseBattery);

// gRPC-web client for the vendor's iot_driver service (driver.DriverGrpc on 127.0.0.1:3814).
// iot_driver owns the checksum and the dongle-to-mouse radio relay, so we only hand it payloads.
public sealed class IotDriverClient : IDisposable
{
    public const int Port = 3814;
    private const string ServicePath = "/driver.DriverGrpc/";

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{Port}"),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var res = await UnaryAsync("getVersion", [], ct);
        return ProtoReader.Parse(res).FirstOrDefault(f => f.Number == 1).AsString();
    }

    public Task SendMsgAsync(string path, byte[] msg, ChecksumType checksum, DangleDevType devType, CancellationToken ct = default)
        => SendAsync("sendMsg", path, msg, checksum, devType, ct);

    public Task SendRawFeatureAsync(string path, byte[] msg, ChecksumType checksum, DangleDevType devType, CancellationToken ct = default)
        => SendAsync("sendRawFeature", path, msg, checksum, devType, ct);

    public Task<byte[]> ReadMsgAsync(string path, CancellationToken ct = default) => ReadAsync("readMsg", path, ct);

    public Task<byte[]> ReadRawFeatureAsync(string path, CancellationToken ct = default) => ReadAsync("readRawFeature", path, ct);

    // Pauses iot_driver's own wireless polling so it doesn't interleave with bulk uploads.
    public Task SetWirelessLoopLockedAsync(bool locked, CancellationToken ct = default)
        => UnaryAsync("changeWirelessLoopStatus", new ProtoWriter().Bool(1, locked).ToArray(), ct);

    // Streams the AJAZZ 2.4G dongle's state each time iot_driver reports a device-list change.
    public async IAsyncEnumerable<DongleState?> WatchDongleAsync(ushort vid, ushort pid, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var msg in StreamAsync("watchDevList", [], ct))
        {
            DongleState? found = null;
            foreach (var dj in ProtoReader.Parse(msg).Where(f => f.Number == 1))
            {
                var common = ProtoReader.Parse(dj.Data).FirstOrDefault(f => f.Number == 2);
                if (common.Data.IsEmpty) continue;
                var c = ProtoReader.Parse(common.Data);
                if (Uint(c, 7) != vid || Uint(c, 8) != pid) continue;

                bool online = false;
                uint battery = 0;
                var mouse = c.FirstOrDefault(f => f.Number == 2);
                if (!mouse.Data.IsEmpty)
                {
                    var status = ProtoReader.Parse(mouse.Data).FirstOrDefault(f => f.Number == 2);
                    if (!status.Data.IsEmpty)
                    {
                        var s = ProtoReader.Parse(status.Data);
                        battery = Uint(s, 1);
                        online = Uint(s, 2) != 0;
                    }
                }
                found = new DongleState(c.FirstOrDefault(f => f.Number == 3).AsString(), Uint(c, 6), online, battery);
            }

            var changeType = ProtoReader.Parse(msg).FirstOrDefault(f => f.Number == 2).Value;
            const ulong removed = 2;
            yield return changeType == removed ? null : found;
        }
    }

    // Notifications devices push on their own, e.g. the mouse's DPI button. Raw bytes, report id first.
    public async IAsyncEnumerable<byte[]> WatchVenderAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var msg in StreamAsync("watchVender", [], ct))
            yield return ProtoReader.Parse(msg).FirstOrDefault(f => f.Number == 1).Data.ToArray();
    }

    public void Dispose() => _http.Dispose();

    private static uint Uint(List<ProtoField> fields, int number) => (uint)fields.FirstOrDefault(f => f.Number == number).Value;

    private async Task SendAsync(string method, string path, byte[] msg, ChecksumType checksum, DangleDevType devType, CancellationToken ct)
    {
        var req = new ProtoWriter()
            .String(1, path)
            .Bytes(2, msg)
            .UInt(3, (ulong)checksum)
            .UInt(4, (ulong)devType)
            .ToArray();
        var res = await UnaryAsync(method, req, ct);
        var err = ProtoReader.Parse(res).FirstOrDefault(f => f.Number == 1);
        if (!err.Data.IsEmpty) throw new IOException($"iot_driver {method}: {err.AsString()}");
    }

    private async Task<byte[]> ReadAsync(string method, string path, CancellationToken ct)
    {
        var res = await UnaryAsync(method, new ProtoWriter().String(1, path).ToArray(), ct);
        var fields = ProtoReader.Parse(res);
        var err = fields.FirstOrDefault(f => f.Number == 1);
        if (!err.Data.IsEmpty) throw new IOException($"iot_driver {method}: {err.AsString()}");
        return fields.FirstOrDefault(f => f.Number == 2).Data.ToArray();
    }

    private async Task<byte[]> UnaryAsync(string method, byte[] request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (var msg in StreamAsync(method, request, timeout.Token))
            return msg;
        return [];
    }

    private async IAsyncEnumerable<byte[]> StreamAsync(string method, byte[] request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ServicePath + method)
        {
            Content = new ByteArrayContent(Frame(request)),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
        req.Headers.Add("x-grpc-web", "1");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        ThrowOnGrpcStatus(method, resp.Headers.TryGetValues("grpc-status", out var s) ? s.FirstOrDefault() : null, resp);

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        var header = new byte[5];
        while (await ReadExactlyOrEndAsync(body, header, ct))
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            var payload = new byte[len];
            if (len > 0 && !await ReadExactlyOrEndAsync(body, payload, ct))
                yield break;

            const byte trailerFlag = 0x80;
            if ((header[0] & trailerFlag) != 0)
            {
                var trailers = Encoding.ASCII.GetString(payload);
                var status = trailers.Split("\r\n").FirstOrDefault(l => l.StartsWith("grpc-status:", StringComparison.OrdinalIgnoreCase));
                ThrowOnGrpcStatus(method, status?["grpc-status:".Length..].Trim(), null);
                yield break;
            }
            yield return payload;
        }
    }

    private static void ThrowOnGrpcStatus(string method, string? status, HttpResponseMessage? resp)
    {
        if (string.IsNullOrEmpty(status) || status == "0") return;
        var message = resp != null && resp.Headers.TryGetValues("grpc-message", out var m) ? m.FirstOrDefault() : null;
        throw new IOException($"iot_driver {method} failed (grpc-status {status}) {message}");
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static byte[] Frame(byte[] msg)
    {
        var f = new byte[5 + msg.Length];
        BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(1), msg.Length);
        msg.CopyTo(f, 5);
        return f;
    }
}
