using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
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
    public string ErrorCode { get; set; } = "";
    public int Capabilities { get; set; }
    public bool ForceManagerRefresh { get; set; }
    public bool RefreshCatalog { get; set; }
    public string RefreshWarning { get; set; } = "";
    public string GitHubRepository { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public Catalog? Catalog { get; set; }
    public AppRelease? Release { get; set; }
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
    public CatalogStore? ManagerUpdates { get; set; }
    public Func<bool, CancellationToken, Task>? RefreshManagerUpdatesAsync { get; set; }
    public Func<CancellationToken, Task<string>>? RefreshCatalogAsync { get; set; }
    private readonly object refreshLock = new();
    private Task<string>? catalogRefresh;

    private Task<string> RefreshPublishedCatalogAsync(CancellationToken token)
    {
        // Concurrent clients share the current refresh; the next request starts a fresh one.
        lock (refreshLock)
            return catalogRefresh is { IsCompleted: false } ? catalogRefresh : catalogRefresh = RefreshPublishedCatalogCoreAsync(token);
    }

    private async Task<string> RefreshPublishedCatalogCoreAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(150));
        try { return RefreshCatalogAsync is { } refresh ? await refresh(timeout.Token).ConfigureAwait(false) : ""; }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        { return "호스트에서 GitHub 최신 목록을 확인하지 못했습니다. 보관된 배포 목록을 표시합니다. 호스트의 인터넷 연결과 GitHub 설정을 확인하세요."; }
    }

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
                // Report preparation failures before sending a body. A partial body always closes the connection.
                var responseStarted = false;
                try
                {
                    var requestedStore = store;
                    if (request.Operation is "manager-catalog" or "manager-package")
                    {
                        var managerStore = ManagerUpdates;
                        if (managerStore is null)
                        {
                            await Wire.WriteAsync(tls, new WireMessage { ErrorCode = "manager_updates_unavailable", Error = "호스트에서 Program Manager 업데이트 배포가 설정되지 않았습니다. 호스트의 업데이트 설정을 확인하세요." }, deadline.Token).ConfigureAwait(false);
                            return;
                        }
                        requestedStore = managerStore;
                        if (request.Operation == "manager-catalog")
                        {
                            deadline.CancelAfter(TimeSpan.FromMinutes(3));
                            var refresh = RefreshManagerUpdatesAsync;
                            if (refresh != null) await refresh(request.ForceManagerRefresh, deadline.Token).ConfigureAwait(false);
                        }
                    }
                    if (request.Operation is "catalog" or "manager-catalog")
                    {
                        var warning = request.Operation == "catalog" && request.RefreshCatalog
                            ? await RefreshPublishedCatalogAsync(token).ConfigureAwait(false) : "";
                        var catalog = requestedStore.Read();
                        if (request.Capabilities < 1 && catalog.Apps.Any(a => !string.IsNullOrEmpty(a.GitHubRepository)))
                            await Wire.WriteAsync(tls, new WireMessage { ErrorCode = "client_update_required", Error = "GitHub 배포 목록을 사용하려면 클라이언트 Program Manager를 0.2.0 이상으로 업데이트하세요." }, deadline.Token).ConfigureAwait(false);
                        else await Wire.WriteAsync(tls, new WireMessage { Capabilities = 1, Catalog = catalog, RefreshWarning = warning }, deadline.Token).ConfigureAwait(false);
                    }
                    else if (request.Operation is "package" or "manager-package")
                    {
                        var app = requestedStore.Read().Apps.SingleOrDefault(a => a.Id == request.Id) ?? throw new FileNotFoundException();
                        if (request.Capabilities < 1 && !string.IsNullOrEmpty(app.GitHubRepository))
                        {
                            await Wire.WriteAsync(tls, new WireMessage { ErrorCode = "client_update_required", Error = "GitHub 설치 파일을 받으려면 클라이언트 Program Manager를 0.2.0 이상으로 업데이트하세요." }, deadline.Token).ConfigureAwait(false);
                            return;
                        }
                        using var package = await requestedStore.PreparePackageAsync(request.Id, request.Version, request.Platform, deadline.Token).ConfigureAwait(false);
                        var release = package.Release;
                        var header = new WireMessage
                        {
                            Id = app.Id, GitHubRepository = app.GitHubRepository, Size = release.Size,
                            Release = new AppRelease
                            {
                                Version = release.Version, Platform = release.Platform, Size = release.Size,
                                FileName = release.FileName, Sha256 = release.Sha256, PublishedUtc = release.PublishedUtc,
                                GitHubAssetId = release.GitHubAssetId, GitHubTag = release.GitHubTag
                            }
                        };
                        responseStarted = true;
                        await Wire.WriteAsync(tls, header, deadline.Token).ConfigureAwait(false);
                        await package.Content.CopyToAsync(tls, 81920, deadline.Token).ConfigureAwait(false);
                        await tls.FlushAsync(deadline.Token).ConfigureAwait(false);
                    }
                    else if (request.Operation == "documentation")
                    {
                        CatalogRules.Id(request.Id);
                        var app = store.Read().Apps.SingleOrDefault(a => a.Id == request.Id) ?? throw new FileNotFoundException();
                        var html = await store.FetchDocumentationAsync(app.Id, deadline.Token).ConfigureAwait(false);
                        if (html.Length is < 1 or > CatalogRules.MaxDocumentationBytes) throw new InvalidDataException();
                        responseStarted = true;
                        await Wire.WriteAsync(tls, new WireMessage { Operation = "documentation", Id = app.Id, GitHubRepository = app.GitHubRepository, Size = html.Length, Sha256 = Compat.Hash(html) }, deadline.Token).ConfigureAwait(false);
                        await tls.WriteAsync(html, 0, html.Length, deadline.Token).ConfigureAwait(false);
                        await tls.FlushAsync(deadline.Token).ConfigureAwait(false);
                    }
                    else await Wire.WriteAsync(tls, new WireMessage { ErrorCode = "unsupported_request", Error = "지원하지 않는 요청입니다." }, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!responseStarted && !deadline.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
                {
                    var response = ex switch
                    {
                        HttpRequestException => new WireMessage { ErrorCode = "upstream_unavailable", Error = "호스트에서 GitHub에 연결하지 못했습니다. 호스트의 인터넷 연결과 GitHub 접근 권한을 확인하세요." },
                        OperationCanceledException => new WireMessage { ErrorCode = "upstream_timeout", Error = "호스트에서 GitHub 응답을 기다리다 시간이 초과되었습니다. 잠시 후 다시 시도하세요." },
                        FileNotFoundException => new WireMessage { ErrorCode = "not_found", Error = "요청한 프로그램이나 설치 파일을 찾을 수 없습니다. 배포 목록을 새로고침하세요." },
                        InvalidDataException or ArgumentException => new WireMessage { ErrorCode = "invalid_content", Error = "요청 정보 또는 원본 파일을 검증하지 못했습니다. 호스트에서 GitHub 목록을 새로고침하세요." },
                        _ => new WireMessage { ErrorCode = "host_unavailable", Error = "호스트가 파일을 준비하지 못했습니다. 호스트의 GitHub 연결 설정과 저장 공간을 확인한 뒤 다시 시도하세요." }
                    };
                    await Wire.WriteAsync(tls, response, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or System.ComponentModel.Win32Exception or OperationCanceledException or ObjectDisposedException or InvalidDataException or JsonException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException or ArgumentException)
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
    private readonly bool managerUpdates;
    private readonly string operationPrefix;
    private readonly CancellationTokenSource lifetime = new();
    private Catalog? snapshot;
    public string RefreshWarning { get; private set; } = "";

    public CatalogClient(PairingInfo info, bool managerUpdates = false)
    {
        this.info = PairingInfo.Parse(info.Export());
        this.managerUpdates = managerUpdates;
        operationPrefix = managerUpdates ? "manager-" : "";
    }

    public async Task<Catalog> FetchCatalogAsync(CancellationToken token = default, bool forceManagerRefresh = false, bool refreshCatalog = false)
    {
        RefreshWarning = "";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(managerUpdates || refreshCatalog ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(30));
        using var client = new TcpClient();
        using var close = deadline.Token.Register(client.Close);
        using var tls = await ConnectAsync(client, deadline.Token).ConfigureAwait(false);
        await Wire.WriteAsync(tls, new WireMessage { Operation = operationPrefix + "catalog", Token = info.Token, Capabilities = 1, ForceManagerRefresh = managerUpdates && forceManagerRefresh, RefreshCatalog = !managerUpdates && refreshCatalog }, deadline.Token).ConfigureAwait(false);
        var response = await Wire.ReadAsync(tls, CatalogRules.MaxCatalogBytes, deadline.Token).ConfigureAwait(false);
        CheckResponse(response);
        var catalog = CatalogRules.Validate(response.Catalog!);
        snapshot = JsonSerializer.Deserialize<Catalog>(JsonSerializer.SerializeToUtf8Bytes(catalog))!;
        RefreshWarning = response.RefreshWarning ?? "";
        return catalog;
    }

    public async Task<string> DownloadAsync(CatalogApp app, AppRelease release, string destination, IProgress<int>? progress = null, CancellationToken token = default)
    {
        CatalogRules.Id(app.Id);
        var version = CatalogRules.NormalizeVersion(release.Version);
        CatalogRules.Platform(release.Platform);
        var trustedApp = TrustedApp(app.Id);
        var trusted = trustedApp.Releases.SingleOrDefault(r => CatalogRules.NormalizeVersion(r.Version) == version && r.Platform == release.Platform)
            ?? throw new InvalidOperationException("먼저 같은 연결에서 카탈로그를 새로고침하세요.");
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        var temporary = Path.Combine(destination, app.Id + "." + Guid.NewGuid().ToString("N") + ".part");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        using var client = new TcpClient();
        using var close = deadline.Token.Register(client.Close);
        try
        {
            using var tls = await ConnectAsync(client, deadline.Token).ConfigureAwait(false);
            await Wire.WriteAsync(tls, new WireMessage { Operation = operationPrefix + "package", Token = info.Token, Capabilities = 1, Id = app.Id, Version = version, Platform = trusted.Platform }, deadline.Token).ConfigureAwait(false);
            var response = await Wire.ReadAsync(tls, 65536, deadline.Token).ConfigureAwait(false);
            CheckResponse(response);
            var actual = response.Release;
            if (actual is null)
            {
                // A pre-GitHub host is still valid for a catalog with a known digest.
                if (managerUpdates || !string.IsNullOrEmpty(trustedApp.GitHubRepository) || !IsSha256(trusted.Sha256))
                    throw new InvalidDataException("호스트를 업데이트한 뒤 배포 목록을 새로고침하세요.");
                actual = trusted;
            }
            else if (response.Id != trustedApp.Id || response.GitHubRepository != trustedApp.GitHubRepository
                || CatalogRules.NormalizeVersion(actual.Version) != version || actual.Platform != trusted.Platform
                || actual.FileName != trusted.FileName || actual.Size != trusted.Size || actual.PublishedUtc != trusted.PublishedUtc
                || actual.GitHubAssetId != trusted.GitHubAssetId || actual.GitHubTag != trusted.GitHubTag
                || !IsSha256(actual.Sha256)
                || (!string.IsNullOrEmpty(trusted.Sha256) && !Compat.Equal(actual.Sha256.ToUpperInvariant(), trusted.Sha256.ToUpperInvariant())))
                throw new InvalidDataException("설치 파일 정보가 카탈로그와 다릅니다. 배포 목록을 새로고침하세요.");
            if (response.Size != trusted.Size) throw new InvalidDataException("설치 파일 크기가 카탈로그와 다릅니다.");
            await ReceiveFileAsync(tls, temporary, actual.Size, actual.Sha256, progress, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var final = Path.Combine(destination, trustedApp.Id + "-" + version + "-" + trusted.Platform + "-" + actual.Sha256.Substring(0, 12).ToUpperInvariant() + Path.GetExtension(trusted.FileName).ToLowerInvariant());
            Compat.Replace(temporary, final);
            return final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<string> DownloadDocumentationAsync(CatalogApp app, string destination, CancellationToken token = default)
    {
        if (managerUpdates) throw new InvalidOperationException("Program Manager 업데이트 연결에서는 설명 페이지를 받을 수 없습니다. 프로그램의 도움말을 이용하세요.");
        CatalogRules.Id(app.Id);
        var trusted = TrustedApp(app.Id);
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        var temporary = Path.Combine(destination, trusted.Id + "." + Guid.NewGuid().ToString("N") + ".part");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var client = new TcpClient();
        using var close = deadline.Token.Register(client.Close);
        try
        {
            using var tls = await ConnectAsync(client, deadline.Token).ConfigureAwait(false);
            await Wire.WriteAsync(tls, new WireMessage { Operation = "documentation", Token = info.Token, Capabilities = 1, Id = trusted.Id }, deadline.Token).ConfigureAwait(false);
            var response = await Wire.ReadAsync(tls, 8192, deadline.Token).ConfigureAwait(false);
            CheckResponse(response);
            if (response.Operation != "documentation" || response.Id != trusted.Id || response.GitHubRepository != trusted.GitHubRepository
                || response.Size is < 1 or > CatalogRules.MaxDocumentationBytes || !IsSha256(response.Sha256))
                throw new InvalidDataException("호스트가 보낸 설명 페이지 정보가 올바르지 않습니다.");
            await ReceiveFileAsync(tls, temporary, response.Size, response.Sha256, null, deadline.Token).ConfigureAwait(false);
            // The host creates a self-contained page; reject malformed UTF-8 before opening it locally.
            _ = new System.Text.UTF8Encoding(false, true).GetString(File.ReadAllBytes(temporary));
            deadline.Token.ThrowIfCancellationRequested();
            var final = Path.Combine(destination, trusted.Id + "-" + response.Sha256.Substring(0, 12).ToUpperInvariant() + ".html");
            Compat.Replace(temporary, final);
            return final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private CatalogApp TrustedApp(string id) => snapshot?.Apps.SingleOrDefault(a => a.Id == id)
        ?? throw new InvalidOperationException("먼저 같은 연결에서 카탈로그를 새로고침하세요.");

    private static bool IsSha256(string value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static async Task ReceiveFileAsync(Stream source, string temporary, long size, string digest, IProgress<int>? progress, CancellationToken token)
    {
        using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
        using var sha = SHA256.Create();
        var buffer = new byte[81920];
        long received = 0;
        var lastPercent = -1;
        while (received < size)
        {
            var read = await source.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, size - received), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("파일 다운로드가 중단되었습니다.");
            await file.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, read, null, 0);
            received += read;
            var percent = (int)(received * 100 / size);
            if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        if (!Compat.Equal(Compat.Hex(sha.Hash!), digest.ToUpperInvariant())) throw new InvalidDataException("파일 SHA-256 검증에 실패했습니다.");
        await file.FlushAsync(token).ConfigureAwait(false);
        file.Flush(true);
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

    private void CheckResponse(WireMessage response)
    {
        if (managerUpdates && (response.ErrorCode == "unsupported_request" || (response.ErrorCode == "" && response.Error == "지원하지 않는 요청입니다.")))
            throw new InvalidOperationException("호스트 Program Manager를 0.3.0 이상으로 업데이트한 뒤 다시 확인하세요.");
        if (!string.IsNullOrEmpty(response.Error))
        {
            CatalogRules.Text(response.Error, 2000);
            throw new InvalidOperationException(response.Error);
        }
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
