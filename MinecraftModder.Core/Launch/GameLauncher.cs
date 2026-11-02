using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Mods;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Settings;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Launch
{
    // "Oyna" tuşunun arkasındaki her şey: loader kurulumu, eksik kütüphaneler, Java,
    // RAM kararı ve oyunun başlatılması. Kullanıcıya hiçbir şey sorulmaz.
    public sealed record LaunchResult(Process Process, CompatibilityReport Compatibility);

    public sealed class GameLauncher
    {
        public const string LauncherName = "MinecraftModder";
        public const string LauncherVersion = "0.2";

        private readonly AppPaths _paths;
        private readonly HttpClient _http;
        private readonly ProfileStore _profiles;
        private readonly SettingsStore _settings;
        private readonly LoaderInstaller _loaders;
        private readonly ContentInstaller _installer;
        private readonly ModCompatibilityService _compatibility;

        public GameLauncher(AppPaths paths, HttpClient http, ProfileStore profiles, SettingsStore settings,
            LoaderInstaller loaders, ContentInstaller installer, ModCompatibilityService compatibility)
        {
            _compatibility = compatibility;
            _paths = paths;
            _http = http;
            _profiles = profiles;
            _settings = settings;
            _loaders = loaders;
            _installer = installer;
        }

        public MinecraftLauncher CreateLauncher(GameProfile profile)
        {
            var path = _paths.CreateMinecraftPath(profile.GameDirectory);
            return new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(path, _http));
        }

        // Dönen rapor: oyun açılmadan önce modlarda neyin düzeltildiği (arayüz kullanıcıya kısaca söyler).
        public async Task<LaunchResult> LaunchAsync(GameProfile profile, MSession session, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            if (!Directory.Exists(profile.GameDirectory))
                throw new DirectoryNotFoundException($"Profil klasörü bulunamadı: {profile.GameDirectory}");

            var launcher = CreateLauncher(profile);
            string versionName = await _loaders.EnsureInstalledAsync(profile, launcher, progress, ct);

            try
            {
                await _installer.EnsureFabricApiAsync(profile, progress, ct);
            }
            catch (HttpRequestException)
            {
                // İnternet yoksa Fabric API zaten kuruluysa oyun yine açılabilir; burada durdurmuyoruz.
            }

            var report = await _compatibility.CheckAndFixAsync(profile, progress, ct);

            int ram = RamAdvisor.Decide(_settings.Current, profile);
            var option = new MLaunchOption
            {
                Session = session,
                MaximumRamMb = ram,
                MinimumRamMb = Math.Min(1024, ram),
                GameLauncherName = LauncherName,
                GameLauncherVersion = LauncherVersion
            };

            progress?.Report(ProgressInfo.Step("Oyun dosyaları kontrol ediliyor..."));
            var fileProgress = progress == null ? null : new Progress<InstallerProgressChangedEventArgs>(e =>
                progress.Report(ProgressInfo.Of($"Oyun dosyaları ({e.ProgressedTasks}/{e.TotalTasks})", e.ProgressedTasks, e.TotalTasks)));

            var process = await launcher.InstallAndBuildProcessAsync(versionName, option, fileProgress, null, ct);

            progress?.Report(ProgressInfo.Step($"Oyun başlatılıyor ({ram / 1024.0:0.#} GB RAM)..."));
            process.Start();

            profile.LastPlayedAt = DateTimeOffset.Now;
            _profiles.Save(profile);
            return new LaunchResult(process, report);
        }

        // Oyun çökerse son çökme raporunun yolu (yoksa en son log). Mod doktoru ve arayüz için.
        public static string? FindLatestCrashReport(GameProfile profile, DateTimeOffset since)
        {
            string dir = Path.Combine(profile.GameDirectory, "crash-reports");
            if (!Directory.Exists(dir)) return null;

            return new DirectoryInfo(dir).GetFiles("*.txt")
                .Where(f => f.LastWriteTime >= since.LocalDateTime)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault()?.FullName;
        }
    }
}
