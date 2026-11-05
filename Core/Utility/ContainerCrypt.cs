using System.Security.Cryptography;
using BattleNGDP.CASC;
using BattleNGDP.TACT.Data;
using BattleNGDP.TACT.Manifest;

namespace BattleNGDP.Core;

public static class ContainerCrypt
{
    public static void DecryptContainerlessInPlace(ref Span<byte> data, int dropBytes = 0, int? nonceIter = null)
    {
        if (data.IsEmpty || data[0] != (byte) 'E') {
            throw new InvalidDataException("[Crypt] Expected an encrypted 'E' envelope.");
        }

        //E | keyNameLength | keyName | ivLength | IV | 'S'/'A' | ciphertext
        var cursor = 1;
        if (data.Length < 1 + 1 + 8 + 1 + 4 + 1) {
            throw new InvalidDataException("[Crypt] truncated encryption header.");
        }

        int keyNameLength = data[cursor++];
        if (keyNameLength != 8 || data.Length < cursor + keyNameLength + 1) {
            throw new InvalidDataException($"[Crypt] invalid key name length {keyNameLength}.");
        }

        var keyName = data.Slice(cursor, keyNameLength);
        cursor += keyNameLength;

        int ivLength = data[cursor++];
        if (ivLength is not (4 or 8) || data.Length < cursor + ivLength + 1) {
            throw new InvalidDataException($"[Crypt] invalid IV length {ivLength}.");
        }

        var iv = data.Slice(cursor, ivLength);
        cursor += ivLength;

        var cipher = (BLTEEncryptionType) data[cursor++];
        data = data[cursor..];

        Span<byte> keyValue = stackalloc byte[16];
        Span<byte> nonce = stackalloc byte[8];
        nonce.Clear();
        iv.CopyTo(nonce);

        if (nonceIter != null) {
            var index = checked((uint) nonceIter.Value);
            for (var i = 0; i < 4; i++) {
                nonce[i] ^= (byte) (index >> (i * 8));
            }
        }

        try {
            if (!TACTKeyringService.TryGetUniqueByNameBytes(keyName, keyValue)) {
                throw new KeyNotFoundException($"[Crypt] key unavailable/ambiguous: {Convert.ToHexString(keyName)}.");
            }

            switch (cipher) {
                case BLTEEncryptionType.Salsa20:
                    Salsa20.TransformInPlace(data, keyValue, nonce, 0);
                    break;

                case BLTEEncryptionType.Arc4:
                    Arc4.TransformInPlace(data, keyValue, dropBytes);
                    break;

                default:
                    throw new NotSupportedException($"[Crypt] unsupported encryption cipher 0x{cipher:X2}.");
            }
        } finally {
            CryptographicOperations.ZeroMemory(keyValue);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }
}