using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProgramManager.Core;

public static class Secrets
{
    public static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}

public sealed class PairingInfo
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 45672;
    public string Fingerprint { get; set; } = "";
    public string Token { get; set; } = "";

    public string Export()
    {
        Validate();
        return "PM1:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));
    }

    public static PairingInfo Parse(string text)
    {
        if (text is null || text.Length > 8192 || !text.Trim().StartsWith("PM1:", StringComparison.Ordinal)) throw new InvalidDataException("Program Manager 연결 코드가 아닙니다.");
        try
        {
            var value = JsonSerializer.Deserialize<PairingInfo>(Convert.FromBase64String(text.Trim().Substring(4))) ?? throw new InvalidDataException("연결 코드가 비어 있습니다.");
            value.Validate();
            return value;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw new InvalidDataException("연결 코드 형식이 올바르지 않습니다.", ex); }
    }

    public void Validate()
    {
        if (Host is null || Host.Length > 253 || Host.Length == 0 || Host.Any(c => char.IsWhiteSpace(c) || "/\\@?#%".IndexOf(c) >= 0) || Uri.CheckHostName(Host) == UriHostNameType.Unknown || Port is < 1024 or > 65535)
            throw new InvalidDataException("호스트 이름 또는 포트가 올바르지 않습니다.");
        if (Fingerprint is null || Token is null || !Regex.IsMatch(Fingerprint, "\\A[a-fA-F0-9]{64}\\z") || !Regex.IsMatch(Token, "\\A[a-fA-F0-9]{64}\\z"))
            throw new InvalidDataException("호스트 인증 정보가 올바르지 않습니다.");
    }
}

public sealed class HostIdentity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string Fingerprint => Compat.Hash(Certificate.RawData);
    public string Token { get; }

    private HostIdentity(X509Certificate2 certificate, string token) { Certificate = certificate; Token = token; }

    public static HostIdentity LoadOrCreate(string root)
    {
        Directory.CreateDirectory(root);
        using var identityLock = new FileStream(Path.Combine(root, ".identity.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = Path.Combine(root, "host-identity.json");
        if (File.Exists(path))
        {
            var protectedValue = JsonFiles.Read(path, "");
            var saved = JsonSerializer.Deserialize<SavedIdentity>(Secrets.Unprotect(protectedValue)) ?? throw new InvalidDataException("호스트 인증 파일이 올바르지 않습니다.");
            var cert = new X509Certificate2(Convert.FromBase64String(saved.Pfx), "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            if (!cert.HasPrivateKey || saved.Token is null || !Regex.IsMatch(saved.Token, "\\A[A-Fa-f0-9]{64}\\z")) { cert.Dispose(); throw new InvalidDataException("호스트 개인 키 또는 토큰이 올바르지 않습니다."); }
            return new HostIdentity(cert, saved.Token);
        }
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=Program Manager", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        var pfx = generated.Export(X509ContentType.Pfx, "");
        var token = Compat.RandomHex();
        JsonFiles.Write(path, Secrets.Protect(JsonSerializer.Serialize(new SavedIdentity { Pfx = Convert.ToBase64String(pfx), Token = token })));
        // Schannel needs a user key container; EphemeralKeySet cannot authenticate on legacy Windows.
        return new HostIdentity(new X509Certificate2(pfx, "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable), token);
    }

    public PairingInfo CreatePairing(string host, int port = 45672)
    {
        var info = new PairingInfo { Host = host, Port = port, Fingerprint = Fingerprint, Token = Token }; info.Validate(); return info;
    }
    public void Dispose() => Certificate.Dispose();
    private sealed class SavedIdentity { public string Pfx { get; set; } = ""; public string Token { get; set; } = ""; }
}
