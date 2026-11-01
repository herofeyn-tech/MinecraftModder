using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installers;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Launch
{
    // Profilin loader'ını (Fabric, Quilt, Forge, NeoForge) kurar ve başlatılacak sürüm adını döner.
    // Loader sürümü profile kaydedilir; böylece bir dahaki açılışta aynı sürüm kullanılır ve
    // Forge gibi kurulumu uzun süren loader'lar tekrar kurulmaz.
    public sealed class LoaderInstaller
    {
        private readonly HttpClient _http;
        private readonly ProfileStore _profiles;

        public LoaderInstaller(HttpClient http, ProfileStore profiles)
        {
            _http = http;
            _profiles = profiles;
        }

        public async Task<string> EnsureInstalledAsync(GameProfile profile, MinecraftLauncher launcher,
            IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            string game = profile.GameVersion;
            string? loaderVersion = profile.LoaderVersion;
            string versionName;

            switch (profile.Loader)
            {
                case ModLoader.Vanilla:
                    return game;

                case ModLoader.Fabric:
                {
                    progress?.Report(ProgressInfo.Step("Fabric hazırlanıyor..."));
                    var fabric = new FabricInstaller(_http);
                    loaderVersion ??= PickStable((await fabric.GetLoaders(game)).Select(l => (l.Version, l.Stable)))
                                      ?? throw new InvalidOperationException($"Fabric, Minecraft {game} sürümünü desteklemiyor.");
                    versionName = FabricInstaller.GetVersionName(game, loaderVersion);
                    if (!File.Exists(launcher.MinecraftPath.GetVersionJsonPath(versionName)))
                        versionName = await fabric.Install(game, loaderVersion, launcher.MinecraftPath);
                    break;
                }

                case ModLoader.Quilt:
                {
                    progress?.Report(ProgressInfo.Step("Quilt hazırlanıyor..."));
                    var quilt = new QuiltInstaller(_http);
                    loaderVersion ??= PickStable((await quilt.GetLoaders(game)).Select(l => (l.Version, l.Stable)))
                                      ?? throw new InvalidOperationException($"Quilt, Minecraft {game} sürümünü desteklemiyor.");
                    versionName = QuiltInstaller.GetVersionName(game, loaderVersion);
                    if (!File.Exists(launcher.MinecraftPath.GetVersionJsonPath(versionName)))
                        versionName = await quilt.Install(game, loaderVersion, launcher.MinecraftPath);
                    break;
                }

                case ModLoader.Forge:
                {
                    progress?.Report(ProgressInfo.Step("Forge hazırlanıyor (ilk seferde birkaç dakika sürebilir)..."));
                    var forge = new ForgeInstaller(launcher, _http);
                    if (loaderVersion == null)
                    {
                        var versions = (await forge.GetForgeVersions(game)).ToList();
                        var pick = versions.FirstOrDefault(v => v.IsRecommendedVersion)
                                   ?? versions.FirstOrDefault(v => v.IsLatestVersion)
                                   ?? versions.FirstOrDefault()
                                   ?? throw new InvalidOperationException($"Forge, Minecraft {game} sürümünü desteklemiyor.");
                        loaderVersion = pick.ForgeVersionName;
                    }
                    versionName = await forge.Install(game, loaderVersion, CreateForgeOptions(progress, ct));
                    break;
                }

                case ModLoader.NeoForge:
                {
                    progress?.Report(ProgressInfo.Step("NeoForge hazırlanıyor (ilk seferde birkaç dakika sürebilir)..."));
                    var neo = new NeoForgeInstaller(launcher, _http);
                    if (loaderVersion == null)
                    {
                        var versions = await neo.GetNeoForgeVersions(game);
                        loaderVersion = versions
                            .Select(v => v.NeoForgeVersionName)
                            .Where(v => !v.Contains("beta", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(ParseVersionKey)
                            .FirstOrDefault()
                            ?? versions.Select(v => v.NeoForgeVersionName).OrderByDescending(ParseVersionKey).FirstOrDefault()
                            ?? throw new InvalidOperationException($"NeoForge, Minecraft {game} sürümünü desteklemiyor.");
                    }
                    versionName = await neo.Install(game, loaderVersion, CreateForgeOptions(progress, ct));
                    break;
                }

                default:
                    throw new NotSupportedException($"Desteklenmeyen loader: {profile.Loader}");
            }

            if (profile.LoaderVersion != loaderVersion)
            {
                profile.LoaderVersion = loaderVersion;
                _profiles.Save(profile);
            }
            return versionName;
        }

        private static ForgeInstallOptions CreateForgeOptions(IProgress<ProgressInfo>? progress, CancellationToken ct) => new()
        {
            SkipIfAlreadyInstalled = true,
            CancellationToken = ct,
            FileProgress = progress == null ? null : new Progress<InstallerProgressChangedEventArgs>(e =>
                progress.Report(ProgressInfo.Of($"Loader dosyaları: {e.Name}", e.ProgressedTasks, e.TotalTasks)))
        };

        // Önce "kararlı" işaretli, yoksa adında beta/alpha/rc geçmeyen sürümler; aralarından en yenisi.
        // (Quilt hiçbir sürümünü kararlı diye işaretlemiyor ve listeyi sıralı göndermiyor.)
        internal static string? PickStable(IEnumerable<(string? Version, bool Stable)> loaders)
        {
            var list = loaders.Where(l => !string.IsNullOrEmpty(l.Version)).ToList();
            static bool IsPreRelease(string v) => new[] { "beta", "alpha", "rc", "pre" }.Any(t => v.Contains(t, StringComparison.OrdinalIgnoreCase));

            // Quilt'in "stable" bilgisi güvenilmez (beta'lar da işaretli gelebiliyor), bu yüzden ada da bakılır.
            var pool = list.Where(l => l.Stable && !IsPreRelease(l.Version!)).ToList();
            if (pool.Count == 0) pool = list.Where(l => !IsPreRelease(l.Version!)).ToList();
            if (pool.Count == 0) pool = list;

            return pool.Select(l => l.Version!).OrderByDescending(ParseVersionKey, StringComparer.Ordinal).FirstOrDefault();
        }

        // "21.1.8" > "21.1.10" gibi yanlış metin sıralamasını önlemek için sayısal karşılaştırma anahtarı.
        internal static string ParseVersionKey(string version)
            => string.Join(".", version.Split('.', '-').Select(part => int.TryParse(part, out int n) ? n.ToString("D6") : part));
    }
}
