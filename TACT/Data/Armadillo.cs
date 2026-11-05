using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BattleNGDP.Core;
using System.Security.Cryptography;

namespace BattleNGDP.TACT.Data;

[InlineArray(16)]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ArmadilloDataKey
{
    public byte Key;

    public ReadOnlySpan<byte> AsSpan =>
        MemoryMarshal.CreateReadOnlySpan(ref Key, 16);
}

[InlineArray(4)]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ArmadilloDataHash
{
    public byte Hash;

    public ReadOnlySpan<byte> AsSpan =>
        MemoryMarshal.CreateReadOnlySpan(ref Hash, 4);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ArmadilloData
{
    public ArmadilloDataKey Key;
    public ArmadilloDataHash Hash;
}

public static class Armadillo
{
    private static readonly Dictionary<string, ArmadilloData> s_keys;

    static Armadillo()
    {
        s_keys = new Dictionary<string, ArmadilloData>();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) {
            throw new PlatformNotSupportedException("No native application-data directory is available.");
        }

        var armadilloPath = Path.Combine(appData, "Battle.net", "Armadillo");
        if (!Directory.Exists(armadilloPath)) {
            return;
        }

        var files = Directory.GetFiles(armadilloPath, "*.ak");
        Span<byte> keyMD5 = stackalloc byte[16];
        foreach (var file in files) {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var data = File.ReadAllBytes(file);
            if (data.Length != 20) {
                Logger.Log($"Invalid Armadillo key file: {fileName}.ak");
                continue;
            }

            var armadillo = MemoryMarshal.Read<ArmadilloData>(data);
            MD5.HashData(armadillo.Key.AsSpan, keyMD5);
            if (armadillo.Hash.AsSpan.SequenceEqual(keyMD5[..4])) {
                s_keys[fileName] = armadillo;
            } else {
                Logger.Log($"Invalid Armadillo key file: {fileName}.ak (hash mismatch)");
            }
        }
    }

    public static bool Decrypt(in PooledBuffer<byte> data, string keyName, string hash, ulong offset = 0)
    {
        if (!s_keys.TryGetValue(keyName, out var armadilloData)) {
            Logger.Log($"Armadillo key not found: {keyName}");
            return false;
        }

        if (hash.Length < 32) {
            throw new ArgumentException("Asset hash is too small.. Must be at least 32 bytes.", nameof(hash));
        }

        Span<byte> nonce = stackalloc byte[8];
        hash.AsSpan(16, 16).DecodeHex(nonce);
        Salsa20.TransformInPlace(data.Span, armadilloData.Key.AsSpan, nonce, offset);
        return true;
    }
}