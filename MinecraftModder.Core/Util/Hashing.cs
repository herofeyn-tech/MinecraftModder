using System.Security.Cryptography;

namespace MinecraftModder.Core.Util
{
    public static class Hashing
    {
        public static string Sha1OfFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        }

        public static string Sha512OfFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
        }

        public static Task<string> Sha1OfFileAsync(string path, CancellationToken ct = default)
            => Task.Run(() => Sha1OfFile(path), ct);

        // Dosya bilinen hash'lerden biriyle eşleşiyor mu? En güçlü olanı (sha512) varsa onu kullanır.
        // Hiç hash bilinmiyorsa boyut karşılaştırmasıyla yetinir.
        public static bool Matches(string path, string? sha1, string? sha512, long expectedSize)
        {
            if (!File.Exists(path)) return false;
            if (expectedSize > 0 && new FileInfo(path).Length != expectedSize) return false;

            if (!string.IsNullOrEmpty(sha512)) return string.Equals(Sha512OfFile(path), sha512, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(sha1)) return string.Equals(Sha1OfFile(path), sha1, StringComparison.OrdinalIgnoreCase);
            return expectedSize > 0;
        }
    }
}
