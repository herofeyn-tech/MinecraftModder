using System.Text.Json.Serialization;
using MinecraftModder.Core.Content;

namespace MinecraftModder.Core.Profiles
{
    // Bir profil = kendi Minecraft sürümü, loader'ı ve mod listesi olan izole bir oyun kurulumu.
    // Hexxit gibi her modpack kendi profiline kurulur; böylece sürümler/loader'lar asla karışmaz.
    public sealed class GameProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Yeni Profil";
        public string GameVersion { get; set; } = "1.20.1";
        public ModLoader Loader { get; set; } = ModLoader.Fabric;

        // null = en güncel kararlı loader sürümü ilk açılışta seçilip buraya kaydedilir.
        public string? LoaderVersion { get; set; }

        // Oyunun --gameDir klasörü (mods, saves, config...). Eski Masaüstü profilleri yerinde kalabilsin diye tam yol tutulur.
        public string GameDirectory { get; set; } = "";

        public string? IconUrl { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? LastPlayedAt { get; set; }

        // null = otomatik RAM. Doluysa bu profil her zaman bu kadar RAM ile açılır.
        public int? RamMb { get; set; }

        // Modpack'in yapımcısının önerdiği RAM (FTB gibi kaynaklar bildiriyor).
        public int? RecommendedRamMb { get; set; }

        // Profil bir modpack'ten kurulduysa nereden geldiği (güncelleme ve paylaşım için).
        public ProfileSource? Source { get; set; }

        // Profilde kurulu her şeyin listesi: mod onarma, paylaşım kodu ve arayüz bunu kullanır.
        public List<ContentEntry> Content { get; set; } = new();

        [JsonIgnore] public bool IsModpack => Source != null;

        public string DisplayVersion => Loader == ModLoader.Vanilla ? GameVersion : $"{GameVersion} · {Loader}";
    }

    public sealed record ProfileSource(string ProviderId, string ProjectId, string VersionId, string? VersionName);

    public enum ContentOrigin
    {
        User,        // kullanıcı launcher'dan kurdu
        Dependency,  // başka bir modun ihtiyacı olduğu için otomatik kuruldu
        Pack,        // modpack'in dosya listesinden indi
        PackOverride,// modpack'in içine gömülü geldi (overrides klasörü)
        Manual       // kullanıcı klasöre kendisi attı
    }

    public sealed class ContentEntry
    {
        // Etkin hâldeki göreli yol, ör. "mods/sodium-0.5.jar" (devre dışıysa diskte sonuna ".disabled" eklenir).
        public string RelativePath { get; set; } = "";
        public ContentKind Kind { get; set; }
        public ContentOrigin Origin { get; set; }
        public bool Enabled { get; set; } = true;

        public string? ProviderId { get; set; }
        public string? ProjectId { get; set; }
        public string? VersionId { get; set; }
        public string? Title { get; set; }
        public string? IconUrl { get; set; }

        public string? Sha1 { get; set; }
        public string? Sha512 { get; set; }
        public long Size { get; set; }
        public List<string> Urls { get; set; } = new();

        // Modrinth'te hash ile tanınma denendi mi? (tanınmayan dosyalar için her açılışta tekrar sorulmasın)
        public bool LookedUp { get; set; }

        public const string DisabledSuffix = ".disabled";

        [JsonIgnore] public string FileName => Path.GetFileName(RelativePath);
        [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Title) ? FileName : Title!;
        [JsonIgnore] public string PhysicalRelativePath => Enabled ? RelativePath : RelativePath + DisabledSuffix;
        [JsonIgnore] public bool CanRedownload => Urls.Count > 0;

        public string GetFullPath(GameProfile profile)
            => Path.Combine(profile.GameDirectory, PhysicalRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
