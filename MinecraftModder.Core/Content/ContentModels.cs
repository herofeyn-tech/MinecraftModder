namespace MinecraftModder.Core.Content
{
    public enum ModLoader
    {
        Vanilla,
        Fabric,
        Quilt,
        Forge,
        NeoForge
    }

    public enum ContentKind
    {
        Mod,
        Modpack,
        ShaderPack,
        ResourcePack,
        Other // modpack'lerden gelen config vb. dosyalar (aramada kullanılmaz)
    }

    public static class ContentKindExtensions
    {
        // Oyun klasöründe bu türün durduğu alt klasör.
        public static string? FolderName(this ContentKind kind) => kind switch
        {
            ContentKind.Mod => "mods",
            ContentKind.ShaderPack => "shaderpacks",
            ContentKind.ResourcePack => "resourcepacks",
            _ => null
        };

        public static ContentKind FromRelativePath(string relativePath)
        {
            string p = relativePath.Replace('\\', '/');
            if (p.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) return ContentKind.Mod;
            if (p.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase)) return ContentKind.ShaderPack;
            if (p.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase)) return ContentKind.ResourcePack;
            return ContentKind.Other;
        }

        public static string DisplayName(this ContentKind kind) => kind switch
        {
            ContentKind.Mod => "Mod",
            ContentKind.Modpack => "Mod Paketi",
            ContentKind.ShaderPack => "Shader",
            ContentKind.ResourcePack => "Kaynak Paketi",
            _ => "Dosya"
        };
    }

    public static class ModLoaderExtensions
    {
        // Bir profilde çalışabilecek modların taşıdığı loader etiketleri.
        // Quilt, Fabric modlarını da çalıştırabildiği için ikisini birden kabul eder.
        public static IReadOnlyList<string> CompatibleTags(this ModLoader loader) => loader switch
        {
            ModLoader.Fabric => new[] { "fabric" },
            ModLoader.Quilt => new[] { "quilt", "fabric" },
            ModLoader.Forge => new[] { "forge" },
            ModLoader.NeoForge => new[] { "neoforge" },
            _ => Array.Empty<string>()
        };

        public static bool SupportsMods(this ModLoader loader) => loader != ModLoader.Vanilla;

        public static ModLoader? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
        {
            "fabric" or "fabric-loader" => ModLoader.Fabric,
            "quilt" or "quilt-loader" => ModLoader.Quilt,
            "forge" => ModLoader.Forge,
            "neoforge" => ModLoader.NeoForge,
            "vanilla" or "minecraft" => ModLoader.Vanilla,
            _ => null
        };
    }

    // Arama sonuçlarındaki tek bir proje (mod, modpack, shader...).
    public sealed record ContentItem
    {
        public required string ProviderId { get; init; }
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required ContentKind Kind { get; init; }
        public string? Slug { get; init; }
        public string Description { get; init; } = "";
        public string? Author { get; init; }
        public string? IconUrl { get; init; }
        // Kartın arka planı için oyun içinden bir görsel (galeri/kapak). Yoksa arayüz kendi desenini çizer.
        public string? BannerUrl { get; init; }
        public long Downloads { get; init; }
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
        public string? WebUrl { get; init; }

        // Favoriler ve "zaten kurulu mu?" kontrolleri için sağlayıcıdan bağımsız anahtar.
        public string Key => $"{ProviderId}:{Id}";
    }

    public sealed record SearchRequest
    {
        public string Query { get; init; } = "";
        public ContentKind Kind { get; init; } = ContentKind.Mod;
        public string? GameVersion { get; init; }
        public ModLoader? Loader { get; init; }
        public int Offset { get; init; }
        public int Limit { get; init; } = 30;
    }

    public sealed record SearchPage(IReadOnlyList<ContentItem> Items, int TotalHits, int Offset)
    {
        public bool HasMore => Offset + Items.Count < TotalHits;
    }

    public enum ReleaseChannel { Release, Beta, Alpha }

    public sealed record ContentVersion
    {
        public required string Id { get; init; }
        public required string ProjectId { get; init; }
        public string Name { get; init; } = "";
        public ReleaseChannel Channel { get; init; } = ReleaseChannel.Release;
        public DateTimeOffset Published { get; init; }
        public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Loaders { get; init; } = Array.Empty<string>();
        public IReadOnlyList<ContentFile> Files { get; init; } = Array.Empty<ContentFile>();
        public IReadOnlyList<ContentDependency> Dependencies { get; init; } = Array.Empty<ContentDependency>();
    }

    public sealed record ContentFile
    {
        public required string FileName { get; init; }
        // Boş olabilir: bazı CurseForge yapımcıları dosyalarının launcher'lar tarafından indirilmesine izin vermiyor.
        public string? Url { get; init; }
        public IReadOnlyList<string> Mirrors { get; init; } = Array.Empty<string>();
        public string? Sha1 { get; init; }
        public string? Sha512 { get; init; }
        public long Size { get; init; }
        public bool Primary { get; init; }

        public IReadOnlyList<string> AllUrls()
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(Url)) list.Add(Url);
            list.AddRange(Mirrors.Where(m => !string.IsNullOrEmpty(m)));
            return list;
        }
    }

    public sealed record ContentDependency(string? ProjectId, string? VersionId, bool Required);
}
