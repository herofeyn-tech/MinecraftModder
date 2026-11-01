using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Profiles;

namespace MinecraftModder.Core.Content.Modrinth
{
    // Modrinth'in .mrpack biçimini (https://support.modrinth.com/en/articles/8802351) ortak ModpackPlan'a çevirir.
    // Bilgisayardaki bir .mrpack dosyasını içe aktarmak için de kullanılabilir.
    public static class MrpackReader
    {
        public static ModpackPlan ReadPlan(string mrpackPath, ProfileSource source, string? iconUrl = null, string? fallbackName = null)
        {
            MrpackIndex index;
            using (var archive = ZipFile.OpenRead(mrpackPath))
            {
                var entry = archive.GetEntry("modrinth.index.json")
                    ?? throw new InvalidDataException("Bu dosya geçerli bir Modrinth paketi değil (modrinth.index.json yok).");
                using var stream = entry.Open();
                index = JsonSerializer.Deserialize<MrpackIndex>(stream)
                    ?? throw new InvalidDataException("Paket bilgisi okunamadı.");
            }

            var deps = index.Dependencies ?? new Dictionary<string, string>();
            if (!deps.TryGetValue("minecraft", out string? gameVersion) || string.IsNullOrEmpty(gameVersion))
                throw new InvalidDataException("Paket hangi Minecraft sürümü için olduğunu belirtmiyor.");

            var (loader, loaderVersion) = DetectLoader(deps);

            var files = new List<PlannedFile>();
            foreach (var f in index.Files ?? new List<MrpackFile>())
            {
                // Sadece sunucuda çalışan dosyaları atla; "optional" olanlar varsayılan olarak kurulur.
                if (string.Equals(f.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase)) continue;
                if (f.Downloads == null || f.Downloads.Count == 0 || string.IsNullOrEmpty(f.Path)) continue;

                files.Add(new PlannedFile
                {
                    RelativePath = Util.SafePath.NormalizeRelative(f.Path),
                    Urls = f.Downloads,
                    Sha1 = f.Hashes?.GetValueOrDefault("sha1"),
                    Sha512 = f.Hashes?.GetValueOrDefault("sha512"),
                    Size = f.FileSize
                });
            }

            return new ModpackPlan
            {
                Name = string.IsNullOrWhiteSpace(index.Name) ? fallbackName ?? "Modpack" : index.Name,
                GameVersion = gameVersion,
                Loader = loader,
                LoaderVersion = loaderVersion,
                IconUrl = iconUrl,
                Source = source,
                Files = files,
                Overrides = new[] { new OverrideArchive(mrpackPath, new[] { "overrides/", "client-overrides/" }) }
            };
        }

        private static (ModLoader, string?) DetectLoader(Dictionary<string, string> deps)
        {
            if (deps.TryGetValue("fabric-loader", out var fabric)) return (ModLoader.Fabric, fabric);
            if (deps.TryGetValue("quilt-loader", out var quilt)) return (ModLoader.Quilt, quilt);
            if (deps.TryGetValue("neoforge", out var neo)) return (ModLoader.NeoForge, neo);
            if (deps.TryGetValue("forge", out var forge)) return (ModLoader.Forge, forge);
            return (ModLoader.Vanilla, null);
        }

        private sealed class MrpackIndex
        {
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("versionId")] public string? VersionId { get; set; }
            [JsonPropertyName("files")] public List<MrpackFile>? Files { get; set; }
            [JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }
        }

        private sealed class MrpackFile
        {
            [JsonPropertyName("path")] public string Path { get; set; } = "";
            [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
            [JsonPropertyName("env")] public MrpackEnv? Env { get; set; }
            [JsonPropertyName("downloads")] public List<string>? Downloads { get; set; }
            [JsonPropertyName("fileSize")] public long FileSize { get; set; }
        }

        private sealed class MrpackEnv
        {
            [JsonPropertyName("client")] public string? Client { get; set; }
            [JsonPropertyName("server")] public string? Server { get; set; }
        }
    }
}
