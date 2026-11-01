using CmlLib.Core;

namespace MinecraftModder.Core
{
    // Launcher'ın diskte kullandığı tüm klasörler tek yerden yönetilir.
    //
    //   %AppData%\MinecraftModder\
    //     settings.json        -> uygulama ayarları
    //     profiles\<id>.json   -> her profilin bilgisi ve içerik manifestosu
    //     instances\<isim>\    -> profillerin oyun klasörleri (mods, config, saves...)
    //     shared\              -> TÜM profillerin ortak kullandığı libraries/assets/versions/runtime
    //     temp\                -> modpack kurulumu sırasında geçici dosyalar
    //
    // "shared" klasörü sayesinde yeni bir profil açıldığında Minecraft'ın ~500 MB'lık
    // temel dosyaları ve Java tekrar indirilmez; sadece modlar iner.
    public sealed class AppPaths
    {
        public AppPaths(string? root = null)
        {
            Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MinecraftModder");
        }

        public string Root { get; }
        public string SettingsFile => Path.Combine(Root, "settings.json");
        public string ProfilesDir => Path.Combine(Root, "profiles");
        public string InstancesDir => Path.Combine(Root, "instances");
        public string SharedGameDir => Path.Combine(Root, "shared");
        public string TempDir => Path.Combine(Root, "temp");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ProfilesDir);
            Directory.CreateDirectory(InstancesDir);
            Directory.CreateDirectory(SharedGameDir);
            Directory.CreateDirectory(TempDir);
        }

        // Oyun dosyaları (libraries, assets, versions, runtime) ortak klasörde,
        // oyunun kendi klasörü (--gameDir: mods, saves, config) ise profilin klasöründe durur.
        public MinecraftPath CreateMinecraftPath(string gameDirectory)
        {
            var path = new MinecraftPath(SharedGameDir);
            path.BasePath = gameDirectory;
            path.CreateDirs();
            return path;
        }

        public string CreateTempWorkDir()
        {
            string dir = Path.Combine(TempDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
