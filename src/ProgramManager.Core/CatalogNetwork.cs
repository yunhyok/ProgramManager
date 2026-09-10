using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace ProgramManager.Core;

// One bounded JSON request/response per TLS 1.2 connection, followed by package bytes.
// TLS 1.2 and Stream byte[] APIs keep the exact protocol usable on Windows 7 / .NET 4.8.
internal sealed class WireMessage
{
    public int Protocol { get; set; } = 1;
    public string Operation { get; set; } = "";
    public string Token { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Error { get; set; } = "";
    public long Size { get; set; }
    public Catalog? Catalog { get; set; }
}

internal static class Wire
{
    public static async Task WriteAsync(Stream stream, WireMessage message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > CatalogRules.MaxCatalogBytes) throw new InvalidDataException("네트워크 메시지가 너무 큽니다.");
        var length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length));
        await stream.WriteAsync(length, 0, length.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<WireMessage> ReadAsync(Stream stream, int maximum, CancellationToken token)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
        if (length < 2 || length > maximum) throw new InvalidDataException("네트워크 메시지 길이가 올바르지 않습니다.");
        var bytes = new byte[length];
        await ReadExactlyAsync(stream, bytes, token).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize<WireMessage>(bytes, JsonFiles.Options) ?? throw new InvalidDataException("네트워크 메시지가 비어 있습니다.");
        if (value.Protocol != 1) throw new InvalidDataException("지원하지 않는 통신 버전입니다.");
        return value;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var position = 0;
        while (position < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, position, buffer.Length - position, token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("전송이 완료되기 전에 연결이 닫혔습니다.");
            position += read;
        }
    }
}

public sealed class CatalogServer : IDisposable
{
    private readonly CatalogStore store;
    private readonly HostIdentity identity;
    private readonly SemaphoreSlim slots = new(8);
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private readonly List<Task> handlers = [];
    private CancellationTokenSource? lifetime;
    private TcpListener? listener;
    private Task? accepting;

    public CatalogServer(CatalogStore store, HostIdentity identity) { this.store = store; this.identity = identity; }

    public Task StartAsync(int port, CancellationToken token = default)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (listener is not null) throw new InvalidOperationException("호스트가 이미 실행 중입니다.");
        token.ThrowIfCancellationRequested();
        var next = new TcpListener(IPAddress.Any, port);
        next.Server.ExclusiveAddressUse = true;
        next.Start(16);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        listener = next;
        accepting = AcceptAsync(next, lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptAsync(TcpListener server, CancellationToken token)
    {
        using var stop = token.Register(server.Stop);
        var windowStart = DateTime.UtcNow;
        var requests = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await server.AcceptTcpClientAsync().ConfigureAwait(false);
                if ((DateTime.UtcNow - windowStart).TotalMinutes >= 1) { windowStart = DateTime.UtcNow; requests = 0; }
                // ponytail: global LAN cap, per-peer rate limits if larger fleets exceed 120 connections/minute.
                if (++requests > 120 || !slots.Wait(0)) { client.Dispose(); continue; }
                clients.TryAdd(client, 0);
                var task = ServeAsync(client, token);
                lock (handlers) { handlers.RemoveAll(t => t.IsCompleted); handlers.Add(task); }
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested && (ex is SocketException or ObjectDisposedException or InvalidOperationException)) { }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var close = deadline.Token.Register(client.Close);
        try
        {
            using (client)
            using (var tls = new SslStream(client.GetStream(), false))
            {
                await tls.AuthenticateAsServerAsync(identity.Certificate, false, SslProtocols.Tls12, false).ConfigureAwait(false);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var request = await Wire.ReadAsync(tls, 8192, deadline.Token).ConfigureAwait(false);
                if (request.Token is null || !Compat.Equal(identity.Token, request.Token))
                { await Wire.WriteAsync(tls, new WireMessage { Error = "호스트 인증에 실패했습니다. 연결 코드를 다시 확인하세요." }, deadline.Token).ConfigureAwait(false); return; }
                deadline.CancelAfter(TimeSpan.FromMinutes(30));
                if (request.Operation == "catalog")
                    await Wire.WriteAsync(tls, new WireMessage { Catalog = store.Read() }, deadline.Token).ConfigureAwait(false);
                else if (request.Operation == "package")
                {
                    var path = store.GetPackagePath(request.Id, request.Version, request.Platform);
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await Wire.WriteAsync(tls, new WireMessage { Size = input.Length }, deadline.Token).ConfigureAwait(false);
                    await input.CopyToAsync(tls, 81920, deadline.Token).ConfigureAwait(false);
                    await tls.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                else await Wire.WriteAsync(tls, new WireMessage { Error = "지원하지 않는 요청입니다." }, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or System.ComponentModel.Win32Exception or OperationCanceledException or ObjectDisposedException or InvalidDataException or JsonException or InvalidOperationException)
        { /* Rejected/aborted connection: no untrusted input or secrets enter logs. */ }
        finally { clients.TryRemove(client, out _); slots.Release(); }
    }

    public async Task StopAsync()
    {
        lifetime?.Cancel();
        listener?.Stop();
        foreach (var client in clients.Keys) client.Close();
        if (accepting is not null) await accepting.ConfigureAwait(false);
        Task[] active;
        lock (handlers) active = handlers.ToArray();
        await Task.WhenAll(active).ConfigureAwait(false);
        lifetime?.Dispose(); lifetime = null; listener = null; accepting = null;
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}

public sealed class CatalogClient : IDisposable
{
    private readonly PairingInfo info;
    private readonly CancellationTokenSource lifetime = new();
    private Catalog? snapshot;

    public CatalogClient(PairingInfo info) => this.info = PairingInfo.Parse(info.Export());

    public async Task<Catalog> FetchCatalogAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = new TcpClient();
        using var close = deadline.Token.Register(client.Close);
        using var tls = await ConnectAsync(client, deadline.Token).ConfigureAwait(false);
        await Wire.WriteAsync(tls, new WireMessage { Operation = "catalog", Token = info.Token }, deadline.Token).ConfigureAwait(false);
        var response = await Wire.ReadAsync(tls, CatalogRules.MaxCatalogBytes, deadline.Token).ConfigureAwait(false);
        CheckResponse(response);
        var catalog = CatalogRules.Validate(response.Catalog!);
        snapshot = JsonSerializer.Deserialize<Catalog>(JsonSerializer.SerializeToUtf8Bytes(catalog))!;
        return catalog;
    }

    public async Task<string> DownloadAsync(CatalogApp app, AppRelease release, string destination, IProgress<int>? progress = null, CancellationToken token = default)
    {
        CatalogRules.Id(app.Id);
        var version = CatalogRules.NormalizeVersion(release.Version);
        CatalogRules.Platform(release.Platform);
        var trusted = snapshot?.Apps.SingleOrDefault(a => a.Id == app.Id)?.Releases.SingleOrDefault(r => CatalogRules.NormalizeVersion(r.Version) == version && r.Platform == release.Platform)
            ?? throw new InvalidOperationException("먼저 같은 연결에서 카탈로그를 새로고침하세요.");
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        var final = Path.Combine(destination, app.Id + "-" + version + "-" + trusted.Platform + "-" + trusted.Sha256.Substring(0, 12) + Path.GetExtension(trusted.FileName).ToLowerInvariant());
        var temporary = final + "." + Guid.NewGuid().ToString("N") + ".part";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        using var client = new TcpClient();
        using var close = deadline.Token.Register(client.Close);
        try
        {
            using var tls = await ConnectAsync(client, deadline.Token).ConfigureAwait(false);
            await Wire.WriteAsync(tls, new WireMessage { Operation = "package", Token = info.Token, Id = app.Id, Version = version, Platform = trusted.Platform }, deadline.Token).ConfigureAwait(false);
            var response = await Wire.ReadAsync(tls, 8192, deadline.Token).ConfigureAwait(false);
            CheckResponse(response);
            if (response.Size != trusted.Size) throw new InvalidDataException("설치 파일 크기가 카탈로그와 다릅니다.");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                long received = 0;
                var lastPercent = -1;
                while (received < trusted.Size)
                {
                    var read = await tls.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, trusted.Size - received), deadline.Token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("설치 파일 다운로드가 중단되었습니다.");
                    await file.WriteAsync(buffer, 0, read, deadline.Token).ConfigureAwait(false);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    received += read;
                    var percent = (int)(received * 100 / trusted.Size);
                    if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                if (!Compat.Equal(Compat.Hex(sha.Hash!), trusted.Sha256.ToUpperInvariant())) throw new InvalidDataException("설치 파일 SHA-256 검증에 실패했습니다.");
                await file.FlushAsync(deadline.Token).ConfigureAwait(false);
                file.Flush(true);
            }
            deadline.Token.ThrowIfCancellationRequested();
            Compat.Replace(temporary, final);
            return final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<SslStream> ConnectAsync(TcpClient client, CancellationToken token)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(10));
        using var close = handshake.Token.Register(client.Close);
        await client.ConnectAsync(info.Host, info.Port).ConfigureAwait(false);
        var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var cert = new X509Certificate2(certificate);
            var now = DateTime.UtcNow;
            return now >= cert.NotBefore.ToUniversalTime() && now <= cert.NotAfter.ToUniversalTime()
                && Compat.Equal(Compat.Hash(cert.RawData), info.Fingerprint.ToUpperInvariant());
        });
        try { await tls.AuthenticateAsClientAsync(info.Host, new X509CertificateCollection(), SslProtocols.Tls12, false).ConfigureAwait(false); return tls; }
        catch { tls.Dispose(); throw; }
    }

    private static void CheckResponse(WireMessage response)
    {
        if (!string.IsNullOrEmpty(response.Error)) throw new InvalidOperationException(response.Error);
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
