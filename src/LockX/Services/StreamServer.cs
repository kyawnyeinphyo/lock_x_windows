using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LockX.Core.Crypto;

namespace LockX.Services;

/// <summary>
/// Serves one encrypted item to the video player over loopback HTTP with byte-range
/// support, decrypting only the chunks each range touches. No plaintext copy of the
/// video is ever written to disk.
///
/// The URL contains an unguessable 128-bit token and the listener only accepts
/// connections from 127.0.0.1, and it lives only while the viewer is open.
/// </summary>
public sealed class StreamServer : IDisposable
{
    readonly BlobReader _reader;
    readonly string _contentType;
    readonly string _token = Convert.ToHexString(Kdf.RandomBytes(16)).ToLowerInvariant();
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();

    public Uri Url { get; }

    public StreamServer(BlobReader reader, string fileName)
    {
        _reader = reader;
        _contentType = MimeFor(Path.GetExtension(fileName));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // Keep the extension: the media pipeline sniffs container type from it.
        var ext = Path.GetExtension(fileName);
        Url = new Uri($"http://127.0.0.1:{port}/{_token}/video{ext}");
        _ = AcceptLoop();
    }

    async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => Handle(client));
            }
        }
        catch { /* shutting down */ }
    }

    async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            var request = await ReadRequestHead(stream);
            if (request == null) return;

            var lines = request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var parts = lines[0].Split(' ');
            if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD")
                || !parts[1].StartsWith("/" + _token + "/", StringComparison.Ordinal))
            {
                await Send(stream, "404 Not Found", "Content-Length: 0\r\n");
                return;
            }

            long total = _reader.PlainSize;
            long start = 0, end = total - 1;
            bool partial = false;
            foreach (var line in lines.Skip(1))
            {
                if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                var spec = line[6..].Trim();
                if (!spec.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) break;
                var range = spec[6..].Split(',')[0].Split('-');
                if (range.Length == 2)
                {
                    if (range[0].Length == 0 && long.TryParse(range[1], out var suffix))
                        start = Math.Max(0, total - suffix);
                    else if (long.TryParse(range[0], out var s))
                    {
                        start = s;
                        if (long.TryParse(range[1], out var e)) end = Math.Min(e, total - 1);
                    }
                    partial = true;
                }
                break;
            }

            if (total == 0 || start >= total || start > end)
            {
                await Send(stream, "416 Range Not Satisfiable", $"Content-Range: bytes */{total}\r\nContent-Length: 0\r\n");
                return;
            }

            long length = end - start + 1;
            var head = new StringBuilder()
                .Append($"Content-Type: {_contentType}\r\n")
                .Append("Accept-Ranges: bytes\r\n")
                .Append($"Content-Length: {length}\r\n")
                .Append("Cache-Control: no-store\r\n");
            if (partial) head.Append($"Content-Range: bytes {start}-{end}/{total}\r\n");
            await Send(stream, partial ? "206 Partial Content" : "200 OK", head.ToString());

            if (parts[0] == "HEAD") return;

            long offset = start;
            while (offset <= end && !_cts.IsCancellationRequested)
            {
                int n = (int)Math.Min(1 << 20, end - offset + 1);
                var slice = await Task.Run(() => _reader.Read(offset, n));
                if (slice.Length == 0) break;
                await stream.WriteAsync(slice, _cts.Token);
                offset += slice.Length;
            }
        }
        catch { /* the player closes connections whenever it seeks */ }
    }

    static async Task<string?> ReadRequestHead(NetworkStream stream)
    {
        var buf = new byte[8192];
        int len = 0;
        while (len < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(len));
            if (n <= 0) return null;
            len += n;
            var text = Encoding.ASCII.GetString(buf, 0, len);
            if (text.Contains("\r\n\r\n")) return text;
        }
        return null;
    }

    static async Task Send(NetworkStream stream, string status, string headers)
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{headers}Connection: close\r\n\r\n");
        await stream.WriteAsync(bytes);
    }

    static string MimeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".wmv" => "video/x-ms-wmv",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mpg" or ".mpeg" => "video/mpeg",
        ".3gp" => "video/3gpp",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _reader.Dispose(); } catch { }
    }
}
