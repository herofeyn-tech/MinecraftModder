namespace MinecraftModder.Core.Util
{
    public sealed class UnsafePathException : Exception
    {
        public UnsafePathException(string relativePath)
            : base($"Paket, oyun klasörünün dışına dosya yazmaya çalıştı: '{relativePath}'") { }
    }

    // Modpack'lerin içindeki dosya yolları dışarıdan gelir ve güvenilmezdir.
    // "../../Windows/x.dll" gibi bir yol, profil klasörünün dışına yazmaya çalışabilir (zip-slip).
    // Bu sınıf her yolu profil klasörüne göre çözer ve dışarı taşan her yolu reddeder.
    public static class SafePath
    {
        public static string Combine(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) throw new UnsafePathException(relativePath);

            string normalized = relativePath.Replace('\\', '/');
            if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
                throw new UnsafePathException(relativePath);

            string rootFull = Path.GetFullPath(root);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar)) rootFull += Path.DirectorySeparatorChar;

            string full = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || full.Length == rootFull.Length)
                throw new UnsafePathException(relativePath);

            return full;
        }

        // "./mods/" + "a.jar" -> "mods/a.jar" (manifestoda tutulan standart biçim)
        public static string NormalizeRelative(string relativePath)
        {
            string normalized = relativePath.Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized.Substring(2);
            return normalized.TrimStart('/');
        }

        public static string ToRelative(string root, string fullPath)
            => NormalizeRelative(Path.GetRelativePath(root, fullPath));

        public static string MakeSafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return string.IsNullOrEmpty(cleaned) ? "Profil" : cleaned;
        }
    }
}
