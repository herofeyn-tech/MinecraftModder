using MinecraftModder.Core.Content;
using MinecraftModder.Core.Profiles;

namespace MinecraftModder.Core.Install
{
    // Bir modpack'in kurulumu için gereken her şey: hangi Minecraft/loader sürümü,
    // hangi dosyalar nereden inecek ve hangi arşivlerden hangi klasörler çıkarılacak.
    // Modrinth (.mrpack), FTB ve CurseForge paketleri hepsi bu ortak biçime çevrilir.
    public sealed record ModpackPlan
    {
        public required string Name { get; init; }
        public required string GameVersion { get; init; }
        public required ModLoader Loader { get; init; }
        public string? LoaderVersion { get; init; }
        public string? IconUrl { get; init; }
        public int? RecommendedRamMb { get; init; }
        public required ProfileSource Source { get; init; }
        public IReadOnlyList<PlannedFile> Files { get; init; } = Array.Empty<PlannedFile>();
        public IReadOnlyList<OverrideArchive> Overrides { get; init; } = Array.Empty<OverrideArchive>();
    }

    public sealed record PlannedFile
    {
        public required string RelativePath { get; init; } // ör. "mods/jei.jar"
        public required IReadOnlyList<string> Urls { get; init; }
        public string? Sha1 { get; init; }
        public string? Sha512 { get; init; }
        public long Size { get; init; }
        public string? Title { get; init; }
        public string? ProjectId { get; init; }
        public ContentKind Kind => ContentKindExtensions.FromRelativePath(RelativePath);
    }

    // Yerel bir zip arşivi + içinden oyun klasörüne çıkarılacak klasör önekleri ("overrides/").
    // Sonradan gelen önek öncekinin üzerine yazar (ör. client-overrides, overrides'ı ezer).
    public sealed record OverrideArchive(string ArchivePath, IReadOnlyList<string> Prefixes);
}
