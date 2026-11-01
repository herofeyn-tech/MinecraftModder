using System.IO.Compression;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Content.Modrinth;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Install
{
    public sealed record ModpackInstallResult(GameProfile Profile, DownloadReport Downloads);

    // Her modpack KENDİ YENİ PROFİLİNE kurulur (Hexxit'in Forge 1.x'i ile senin Fabric 1.20.1 dünyan asla karışmaz).
    // Kurulum yarıda kalırsa yarım klasör silinir ve profil listesinde bozuk bir profil bırakılmaz.
    public sealed class ModpackInstaller
    {
        private readonly AppPaths _paths;
        private readonly ProfileStore _profiles;
        private readonly DownloadManager _downloads;

        public ModpackInstaller(AppPaths paths, ProfileStore profiles, DownloadManager downloads)
        {
            _paths = paths;
            _profiles = profiles;
            _downloads = downloads;
        }

        public async Task<ModpackInstallResult> InstallAsync(IModpackProvider provider, ContentItem pack, ContentVersion? version,
            IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            if (version == null)
            {
                progress?.Report(ProgressInfo.Step($"{pack.Title} sürümleri kontrol ediliyor..."));
                var versions = await provider.GetVersionsAsync(pack, null, null, ct);
                version = VersionPicker.PickBest(versions)
                          ?? throw new InvalidOperationException($"'{pack.Title}' için kurulabilir bir sürüm bulunamadı.");
            }

            string workDir = _paths.CreateTempWorkDir();
            try
            {
                var plan = await provider.ResolveModpackAsync(pack, version, workDir, progress, ct);
                return await InstallPlanAsync(plan, progress, ct);
            }
            finally
            {
                TryDeleteDirectory(workDir);
            }
        }

        // Bilgisayardaki bir .mrpack dosyasını içe aktarır (arkadaşın gönderdiği paket vb.).
        public Task<ModpackInstallResult> InstallLocalMrpackAsync(string mrpackPath, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            var source = new ProfileSource("file", Path.GetFileName(mrpackPath), "local", null);
            var plan = MrpackReader.ReadPlan(mrpackPath, source, null, Path.GetFileNameWithoutExtension(mrpackPath));
            return InstallPlanAsync(plan, progress, ct);
        }

        public async Task<ModpackInstallResult> InstallPlanAsync(ModpackPlan plan, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            string name = _profiles.MakeUniqueName(plan.Name);
            string gameDir = _profiles.AllocateGameDirectory(name);
            Directory.CreateDirectory(gameDir);

            bool completed = false;
            try
            {
                // Yol kontrolü indirmeden ÖNCE yapılır: tek bir kötü yol bile varsa paket hiç kurulmaz.
                var planned = plan.Files
                    .Select(f => (File: f, Item: new DownloadItem(SafePath.Combine(gameDir, f.RelativePath), f.Urls, f.Sha1, f.Sha512, f.Size, f.Title)))
                    .ToList();

                var report = await _downloads.DownloadAllAsync(planned.Select(p => p.Item).ToList(), progress, $"{plan.Name} dosyaları iniyor", ct);

                progress?.Report(ProgressInfo.Step("Paket ayarları yerleştiriliyor..."));
                var overrideFiles = await Task.Run(() => ExtractOverrides(plan.Overrides, gameDir), ct);

                var entries = new Dictionary<string, ContentEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var (file, _) in planned)
                {
                    string rel = SafePath.NormalizeRelative(file.RelativePath);
                    entries[rel] = new ContentEntry
                    {
                        RelativePath = rel,
                        Kind = file.Kind,
                        Origin = ContentOrigin.Pack,
                        Title = file.Title,
                        ProjectId = file.ProjectId,
                        Sha1 = file.Sha1,
                        Sha512 = file.Sha512,
                        Size = file.Size,
                        Urls = file.Urls.ToList()
                    };
                }

                // Paketin içine gömülü gelen modlar da (eskiden listede görünmüyordu) manifestoya eklenir.
                // Overrides bir dosyanın üzerine yazdıysa disktekiyle eşleşen kayıt override kaydıdır;
                // aksi halde "onar" bu dosyayı orijinaliyle değiştirip paketin ayarını bozardı.
                foreach (string rel in overrideFiles)
                {
                    var kind = ContentKindExtensions.FromRelativePath(rel);
                    if (kind == ContentKind.Other)
                    {
                        entries.Remove(rel);
                        continue;
                    }

                    string full = SafePath.Combine(gameDir, rel);
                    entries[rel] = new ContentEntry
                    {
                        RelativePath = rel,
                        Kind = kind,
                        Origin = ContentOrigin.PackOverride,
                        Sha1 = Hashing.Sha1OfFile(full),
                        Size = new FileInfo(full).Length
                    };
                }

                var profile = new GameProfile
                {
                    Name = name,
                    GameVersion = plan.GameVersion,
                    Loader = plan.Loader,
                    LoaderVersion = plan.LoaderVersion,
                    GameDirectory = gameDir,
                    IconUrl = plan.IconUrl,
                    Source = plan.Source,
                    RecommendedRamMb = plan.RecommendedRamMb,
                    Content = entries.Values.OrderBy(e => e.RelativePath).ToList()
                };

                _profiles.Add(profile);
                completed = true;
                return new ModpackInstallResult(profile, report);
            }
            finally
            {
                if (!completed) TryDeleteDirectory(gameDir);
            }
        }

        // Arşivdeki "overrides/" gibi klasörlerin içeriğini oyun klasörüne çıkarır, yazılan göreli yolları döner.
        internal static List<string> ExtractOverrides(IEnumerable<OverrideArchive> archives, string gameDir)
        {
            var written = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var archive in archives)
            {
                using var zip = ZipFile.OpenRead(archive.ArchivePath);
                foreach (string prefix in archive.Prefixes)
                {
                    foreach (var entry in zip.Entries)
                    {
                        string fullName = entry.FullName.Replace('\\', '/');
                        if (!fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || fullName.EndsWith('/')) continue;

                        string relative = SafePath.NormalizeRelative(fullName.Substring(prefix.Length));
                        if (relative.Length == 0) continue;

                        string dest = SafePath.Combine(gameDir, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        entry.ExtractToFile(dest, overwrite: true);

                        if (seen.Add(relative)) written.Add(relative);
                    }
                }
            }
            return written;
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // temp klasörü bir sonraki açılışta temizlenebilir; kurulumu bu yüzden başarısız saymayız.
            }
        }
    }
}
