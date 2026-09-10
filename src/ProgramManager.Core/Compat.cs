using System.Security.Cryptography;

#if NET8_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif

namespace ProgramManager.Core;

internal static class Compat
{
    public static string Hex(byte[] data) => BitConverter.ToString(data).Replace("-", "");
    public static string Hash(byte[] data) { using var sha = SHA256.Create(); return Hex(sha.ComputeHash(data)); }
    public static string Hash(Stream stream, CancellationToken token = default)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Hex(sha.Hash!);
    }
    public static string RandomHex() { using var random = RandomNumberGenerator.Create(); var bytes = new byte[32]; random.GetBytes(bytes); return Hex(bytes); }
    public static bool Equal(string left, string right)
    {
        if (left.Length != right.Length) return false;
        var difference = 0;
        for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
        return difference == 0;
    }
    public static void Replace(string source, string target)
    {
        if (File.Exists(target)) File.Replace(source, target, null);
        else File.Move(source, target);
    }
}
