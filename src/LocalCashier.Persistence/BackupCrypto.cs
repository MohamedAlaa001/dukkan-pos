using System.Security.Cryptography;
using LocalCashier.Domain;

namespace LocalCashier.Persistence;

/// <summary>
/// Passphrase-encrypted file format "LCBK1": PBKDF2-SHA256 key, chunked AES-256-GCM.
/// The key depends only on the recovery secret (never on the machine), so a package restores on any PC.
/// Chunks are authenticated individually; the final chunk is flagged in the AAD so truncation is detected.
/// </summary>
public static class BackupCrypto
{
    static readonly byte[] Magic = "LCBK1"u8.ToArray();
    const int ChunkSize = 1 << 20;
    const int Iterations = 600_000;
    const int TagSize = 16;

    static byte[] Nonce(byte[] nonceBase, uint index)
    {
        var n = new byte[12];
        nonceBase.CopyTo(n, 0);
        BitConverter.GetBytes(index).CopyTo(n, 8);
        return n;
    }

    static byte[] Aad(bool last) => [.. Magic, last ? (byte)1 : (byte)0];

    static int ReadFull(Stream s, byte[] buf)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = s.Read(buf, total, buf.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public static void Encrypt(string inputPath, string outputPath, string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonceBase = RandomNumberGenerator.GetBytes(8);
        var key = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, TagSize);
        using var input = File.OpenRead(inputPath);
        using var output = File.Create(outputPath);
        output.Write(Magic);
        output.Write(salt);
        output.Write(nonceBase);

        var buf = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var n = ReadFull(input, buf);
        uint index = 0;
        while (true)
        {
            var m = ReadFull(input, next);
            var last = m == 0;
            var cipher = new byte[n];
            var tag = new byte[TagSize];
            aes.Encrypt(Nonce(nonceBase, index), buf.AsSpan(0, n), cipher, tag, Aad(last));
            output.Write(BitConverter.GetBytes(n));
            output.Write(cipher);
            output.Write(tag);
            if (last) break;
            (buf, next) = (next, buf);
            n = m;
            index++;
        }

        output.Flush(true);
    }

    /// <summary>Decrypts to <paramref name="outputPath"/>; a wrong secret, tampering or truncation throws <see cref="PosException"/>.</summary>
    public static void Decrypt(string inputPath, string outputPath, string secret)
    {
        try
        {
            using var input = File.OpenRead(inputPath);
            var header = new byte[Magic.Length + 16 + 8];
            if (ReadFull(input, header) != header.Length || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new PosException(PosError.BackupInvalid, "This is not a Local Cashier backup package.");
            var salt = header.AsSpan(Magic.Length, 16).ToArray();
            var nonceBase = header.AsSpan(Magic.Length + 16, 8).ToArray();
            var key = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32);
            using var aes = new AesGcm(key, TagSize);
            using var output = File.Create(outputPath);

            uint index = 0;
            var lenBuf = new byte[4];
            while (true)
            {
                if (ReadFull(input, lenBuf) != 4) throw new PosException(PosError.BackupInvalid, "The backup package is truncated.");
                var len = BitConverter.ToInt32(lenBuf);
                if (len < 0 || len > ChunkSize) throw new PosException(PosError.BackupInvalid, "The backup package is corrupt.");
                var cipher = new byte[len];
                var tag = new byte[TagSize];
                if (ReadFull(input, cipher) != len || ReadFull(input, tag) != TagSize)
                    throw new PosException(PosError.BackupInvalid, "The backup package is truncated.");
                var last = input.Position == input.Length;
                var plain = new byte[len];
                aes.Decrypt(Nonce(nonceBase, index), cipher, tag, plain, Aad(last));
                output.Write(plain);
                if (last) break;
                index++;
            }
        }
        catch (CryptographicException)
        {
            throw new PosException(PosError.BackupInvalid, "The recovery secret is wrong or the backup package is damaged.");
        }
    }
}
