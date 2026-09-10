using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using ProgramManager.Core;

namespace ProgramManager;

internal static class ConnectionSettings
{
    public static string[] LocalAddresses()
    {
        var addresses = new List<(bool Gateway, string Address)>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return []; }
        foreach (var adapter in interfaces)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                var gateway = properties.GatewayAddresses.Any(g => IsLanAddress(g.Address));
                addresses.AddRange(properties.UnicastAddresses.Where(a => IsLanAddress(a.Address)).Select(a => (gateway, a.Address.ToString())));
            }
            catch (NetworkInformationException) { /* An adapter may disconnect while the settings window opens. */ }
        }
        return addresses.OrderByDescending(a => a.Gateway).ThenBy(a => a.Address, StringComparer.Ordinal).Select(a => a.Address).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool IsLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    public static async Task<Catalog> CheckAsync(string code, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PairingInfo pairing;
        try { pairing = PairingInfo.Parse(code); }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("호스트의 ‘연결 코드’에서 복사한 PM1: 연결 코드 전체를 붙여 넣으세요. IP 주소만 입력해서는 연결할 수 없습니다.", ex);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var client = new CatalogClient(pairing);
        try { return await client.FetchCatalogAsync(deadline.Token).ConfigureAwait(false); }
        catch (Exception ex) when (token.IsCancellationRequested) { throw new OperationCanceledException("연결 확인을 취소했습니다.", ex, token); }
        catch (Exception ex) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("8초 안에 호스트에 연결하지 못했습니다. 호스트 프로그램 실행 상태, 같은 내부망 연결, 방화벽과 포트를 확인하세요.", ex);
        }
        catch (AuthenticationException ex)
        {
            throw new InvalidOperationException("호스트 인증서를 확인하지 못했습니다. 호스트에서 새 연결 코드를 복사한 뒤 다시 확인하세요.", ex);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            var socket = FindSocketError(ex);
            if (socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
                throw new InvalidOperationException("호스트 PC 이름을 찾을 수 없습니다. 호스트 설정에서 현재 내부망 IP로 연결 코드를 다시 만들어 사용하세요.", ex);
            if (socket?.SocketErrorCode == SocketError.TimedOut)
                throw new TimeoutException("호스트 응답 시간이 초과되었습니다. 호스트 실행 상태와 내부망 연결, 방화벽을 확인하세요.", ex);
            throw new IOException("호스트에 연결하거나 목록을 받지 못했습니다. 호스트가 실행 중인지, IP·포트가 맞는지, 방화벽에서 연결을 허용했는지 확인하세요.", ex);
        }
        // The core's authentication and catalog-validation messages remain unchanged.
    }

    private static SocketException? FindSocketError(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is SocketException socket) return socket;
        return null;
    }
}
