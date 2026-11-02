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

        // CurseForge'un dosya parmak izi: boşluk karakterleri (9, 10, 13, 32) atılmış içerik üzerinde
        // MurmurHash2 (seed 1). Elle atılmış bir jar'ın CurseForge'daki karşılığını bulmak için.
        public static uint CurseForgeFingerprint(string path) => CurseForgeFingerprint(File.ReadAllBytes(path));

        internal static uint CurseForgeFingerprint(byte[] raw)
        {
            var data = new byte[raw.Length];
            int length = 0;
            foreach (byte b in raw)
                if (b is not (9 or 10 or 13 or 32)) data[length++] = b;

            const uint m = 0x5bd1e995;
            uint h = 1 ^ (uint)length;
            int i = 0;
            for (; i + 4 <= length; i += 4)
            {
                uint k = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
                k *= m;
                k ^= k >> 24;
                k *= m;
                h *= m;
                h ^= k;
            }
            switch (length - i)
            {
                case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
                case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
                case 1: h ^= data[i]; h *= m; break;
            }
            h ^= h >> 13;
            h *= m;
            h ^= h >> 15;
            return h;
        }

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
