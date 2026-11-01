using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Settings
{
    public enum RamMode
    {
        Auto,   // bilgisayarın RAM'ine ve mod sayısına göre launcher karar verir
        Manual  // kullanıcının seçtiği sabit değer
    }

    public sealed class AppSettings
    {
        public string Language { get; set; } = "tr";
        public RamMode RamMode { get; set; } = RamMode.Auto;
        public int ManualRamMb { get; set; } = 4096;
        public int MaxParallelDownloads { get; set; } = 8;
        public bool HideLauncherWhilePlaying { get; set; } = true;

        // Hiç bilmeyen biri profil açarken karşısına çıkan varsayılan sürüm (modların çoğu bunu destekliyor).
        public string DefaultGameVersion { get; set; } = "1.20.1";

        // CurseForge'un modlarını doğrudan aramak için gerekiyor (https://console.curseforge.com).
        // Boşsa CurseForge mod araması kapalı kalır; CurseForge MODPACK'leri yine FTB üzerinden çalışır.
        public string? CurseForgeApiKey { get; set; }

        public string? ActiveProfileId { get; set; }
        public string? LastOfflineUsername { get; set; }
        public bool LegacyImportDone { get; set; }
        public List<string> FavoriteKeys { get; set; } = new();
    }

    public sealed class SettingsStore
    {
        private readonly string _path;
        private readonly object _lock = new();

        public SettingsStore(AppPaths paths)
        {
            _path = paths.SettingsFile;
            Current = Load();
        }

        public AppSettings Current { get; private set; }

        public void Save()
        {
            lock (_lock) JsonFile.Write(_path, Current);
        }

        private AppSettings Load()
        {
            try
            {
                var settings = JsonFile.Read<AppSettings>(_path) ?? new AppSettings();
                settings.MaxParallelDownloads = Math.Clamp(settings.MaxParallelDownloads, 1, 32);
                settings.ManualRamMb = Math.Clamp(settings.ManualRamMb, 1024, 65536);
                return settings;
            }
            catch
            {
                // Bozuk ayar dosyası launcher'ı açılmaz hale getirmesin; varsayılanlarla devam.
                return new AppSettings();
            }
        }
    }
}
