using MinecraftModder.Core.Auth;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Content.CurseForge;
using MinecraftModder.Core.Content.Ftb;
using MinecraftModder.Core.Content.Modrinth;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Launch;
using MinecraftModder.Core.Net;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Settings;

namespace MinecraftModder.Core
{
    // Launcher'ın tüm parçalarını bir araya getiren tek giriş noktası. Arayüz (bugünkü WPF ya da
    // ileride yapılacak yeni tasarım) sadece bu sınıfı oluşturup servislerini kullanır.
    public sealed class LauncherCore
    {
        private LauncherCore(AppPaths paths, HttpClient http)
        {
            Paths = paths;
            Paths.EnsureCreated();
            CleanTemp();

            Settings = new SettingsStore(paths);
            Profiles = new ProfileStore(paths);
            Downloads = new DownloadManager(http, () => Settings.Current.MaxParallelDownloads);

            var modrinth = new ModrinthProvider(http, Downloads);
            Content = new ContentHub(modrinth, new FtbProvider(http, Downloads), new CurseForgeProvider(http, Settings));

            Installer = new ContentInstaller(Profiles, Content, Downloads);
            Modpacks = new ModpackInstaller(paths, Profiles, Downloads);
            ProfileContent = new ProfileContentService(Profiles, modrinth);
            Doctor = new ProfileDoctor(Downloads);
            Loaders = new LoaderInstaller(http, Profiles);
            Launcher = new GameLauncher(paths, http, Profiles, Settings, Loaders, Installer);
            GameVersions = new GameVersionService(paths, http);
            Accounts = new AccountService();
        }

        public AppPaths Paths { get; }
        public SettingsStore Settings { get; }
        public ProfileStore Profiles { get; }
        public DownloadManager Downloads { get; }
        public ContentHub Content { get; }
        public ContentInstaller Installer { get; }
        public ModpackInstaller Modpacks { get; }
        public ProfileContentService ProfileContent { get; }
        public ProfileDoctor Doctor { get; }
        public LoaderInstaller Loaders { get; }
        public GameLauncher Launcher { get; }
        public GameVersionService GameVersions { get; }
        public AccountService Accounts { get; }

        public static LauncherCore Create(string? dataRoot = null, HttpClient? http = null)
            => new(new AppPaths(dataRoot), http ?? LauncherHttp.Shared);

        // İlk açılışta eski Masaüstü profillerini içe aktarır; hiç profil yoksa varsayılan bir tane açar.
        public GameProfile EnsureActiveProfile()
        {
            var settings = Settings.Current;

            if (!settings.LegacyImportDone)
            {
                try
                {
                    LegacyProfileImporter.Import(Profiles, LegacyProfileImporter.DefaultLegacyRoot, settings.DefaultGameVersion);
                }
                catch
                {
                    // Eski klasörler okunamazsa sıfırdan başlamak sorun değil.
                }
                settings.LegacyImportDone = true;
                Settings.Save();
            }

            var active = Profiles.Get(settings.ActiveProfileId) ?? Profiles.All.FirstOrDefault()
                         ?? Profiles.Create("Ana Profil", settings.DefaultGameVersion, ModLoader.Fabric);
            SetActiveProfile(active);
            return active;
        }

        public void SetActiveProfile(GameProfile profile)
        {
            Settings.Current.ActiveProfileId = profile.Id;
            Settings.Save();
        }

        private void CleanTemp()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Paths.TempDir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // kullanımdaki bir klasör varsa bir sonraki açılışa kalır
            }
        }
    }
}
