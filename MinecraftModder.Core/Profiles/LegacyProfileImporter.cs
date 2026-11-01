using System.Text.RegularExpressions;
using MinecraftModder.Core.Content;

namespace MinecraftModder.Core.Profiles
{
    // Launcher'ın eski sürümü profilleri Masaüstü\MinecraftModder_Profiller\Profil_* klasörlerinde,
    // hiçbir bilgi dosyası olmadan tutuyordu. Bu sınıf o klasörleri yerinde bırakarak yeni sisteme kaydeder.
    public static class LegacyProfileImporter
    {
        public static string DefaultLegacyRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MinecraftModder_Profiller");

        public static IReadOnlyList<GameProfile> Import(ProfileStore store, string legacyRoot, string fallbackGameVersion)
        {
            var imported = new List<GameProfile>();
            if (!Directory.Exists(legacyRoot)) return imported;

            foreach (string dir in Directory.GetDirectories(legacyRoot, "Profil_*"))
            {
                if (store.IsDirectoryUsed(dir)) continue;

                string name = Path.GetFileName(dir).Substring("Profil_".Length).Replace('_', ' ').Trim();
                if (name.Equals("Ana", StringComparison.OrdinalIgnoreCase)) name = "Ana Profil";

                var (gameVersion, loaderVersion) = DetectFabricVersion(dir);
                var profile = store.Create(name, gameVersion ?? fallbackGameVersion, ModLoader.Fabric, loaderVersion, dir);
                imported.Add(profile);
            }
            return imported;
        }

        // Eski profiller hep Fabric'ti; versions\fabric-loader-0.16.5-1.20.1 gibi klasör adından sürüm okunur.
        private static (string? GameVersion, string? LoaderVersion) DetectFabricVersion(string profileDir)
        {
            string versionsDir = Path.Combine(profileDir, "versions");
            if (!Directory.Exists(versionsDir)) return (null, null);

            var regex = new Regex(@"^fabric-loader-(?<loader>[^-]+)-(?<game>.+)$", RegexOptions.IgnoreCase);
            foreach (string dir in Directory.GetDirectories(versionsDir).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                var match = regex.Match(Path.GetFileName(dir));
                if (match.Success) return (match.Groups["game"].Value, match.Groups["loader"].Value);
            }
            return (null, null);
        }
    }
}
