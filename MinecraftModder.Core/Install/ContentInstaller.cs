using MinecraftModder.Core.Content;
using MinecraftModder.Core.Content.Modrinth;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Install
{
    public sealed record SkippedContent(string Title, string Reason, string? WebUrl = null);

    public sealed class ContentInstallResult
    {
        public List<string> Installed { get; } = new();
        public List<string> AlreadyInstalled { get; } = new();
        public List<SkippedContent> Skipped { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    // Tek bir mod / shader / kaynak paketini, gereken bağımlılıklarıyla birlikte bir profile kurar.
    // Kullanıcı sadece "kur"a basar; uyumlu sürümü seçmek, Fabric API'yi, shader için Iris'i
    // eklemek launcher'ın işidir.
    public sealed class ContentInstaller
    {
        private readonly ProfileStore _profiles;
        private readonly ContentHub _hub;
        private readonly DownloadManager _downloads;

        public ContentInstaller(ProfileStore profiles, ContentHub hub, DownloadManager downloads)
        {
            _profiles = profiles;
            _hub = hub;
            _downloads = downloads;
        }

        public async Task<ContentInstallResult> InstallAsync(GameProfile profile, ContentItem item,
            IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            if (item.Kind == ContentKind.Modpack)
                throw new InvalidOperationException("Mod paketleri kendi profillerine kurulur; ModpackInstaller kullanılmalı.");

            var provider = _hub.Get(item.ProviderId) ?? throw new InvalidOperationException($"Bilinmeyen kaynak: {item.ProviderId}");
            var result = new ContentInstallResult();

            if (item.Kind == ContentKind.Mod && !profile.Loader.SupportsMods())
            {
                result.Skipped.Add(new SkippedContent(item.Title, "Bu profil Vanilla. Mod kurmak için Fabric, Forge veya NeoForge profili seç."));
                return result;
            }

            var queue = new Queue<(IContentProvider Provider, ContentItem Item, ContentOrigin Origin)>();
            queue.Enqueue((provider, item, ContentOrigin.User));

            if (item.Kind == ContentKind.ShaderPack)
                await AddShaderLoaderAsync(profile, queue, result, ct);

            var visited = new HashSet<string>();
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (p, current, origin) = queue.Dequeue();
                if (!visited.Add(current.Key)) continue;

                if (IsInstalled(profile, current))
                {
                    if (origin == ContentOrigin.User) result.AlreadyInstalled.Add(current.Title);
                    continue;
                }

                progress?.Report(ProgressInfo.Step($"{current.Title} hazırlanıyor..."));
                ModLoader? loaderFilter = current.Kind == ContentKind.Mod ? profile.Loader : null;
                var versions = await p.GetVersionsAsync(current, profile.GameVersion, loaderFilter, ct);
                var version = VersionPicker.PickBest(versions, profile.GameVersion, loaderFilter);

                if (version == null)
                {
                    result.Skipped.Add(new SkippedContent(current.Title, $"{profile.DisplayVersion} ile uyumlu bir sürümü yok.", current.WebUrl));
                    continue;
                }

                var file = VersionPicker.PickFile(version, current.Kind);
                if (file == null)
                {
                    result.Skipped.Add(new SkippedContent(current.Title, "İndirilebilir bir dosyası yok.", current.WebUrl));
                    continue;
                }

                var urls = file.AllUrls();
                if (urls.Count == 0)
                {
                    result.Skipped.Add(new SkippedContent(current.Title,
                        "Yapımcısı launcher'lar üzerinden indirilmesine izin vermiyor. Sitesinden indirip profil klasörüne atabilirsin.", current.WebUrl));
                    continue;
                }

                string relative = $"{current.Kind.FolderName()}/{file.FileName}";
                string target = SafePath.Combine(profile.GameDirectory, relative);

                progress?.Report(ProgressInfo.Step($"{current.Title} indiriliyor..."));
                await _downloads.DownloadAsync(new DownloadItem(target, urls, file.Sha1, file.Sha512, file.Size, current.Title), ct);

                RemoveOlderVersions(profile, current, relative);
                profile.Content.RemoveAll(e => e.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                profile.Content.Add(new ContentEntry
                {
                    RelativePath = relative,
                    Kind = current.Kind,
                    Origin = origin,
                    ProviderId = p.Id,
                    ProjectId = current.Id,
                    VersionId = version.Id,
                    Title = current.Title,
                    IconUrl = current.IconUrl,
                    Sha1 = file.Sha1,
                    Sha512 = file.Sha512,
                    Size = file.Size,
                    Urls = urls.ToList(),
                    LookedUp = true
                });
                result.Installed.Add(current.Title);

                foreach (var dep in version.Dependencies.Where(d => d.Required))
                {
                    var depItem = await ResolveDependencyAsync(p, dep, ct);
                    if (depItem != null) queue.Enqueue((p, depItem, ContentOrigin.Dependency));
                }
            }

            _profiles.Save(profile);
            return result;
        }

        // Fabric profilinde modlar varken Fabric API eksikse oyun açılmaz; sessizce tamamlanır.
        // Modpack profillerine dokunulmaz (yapımcısı neyi istediğini bilir).
        public async Task EnsureFabricApiAsync(GameProfile profile, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            if (profile.Loader != ModLoader.Fabric || profile.IsModpack) return;

            bool hasMods = profile.Content.Any(e => e.Kind == ContentKind.Mod && e.Enabled);
            if (!hasMods) return;

            bool hasFabricApi = profile.Content.Any(e => e.ProjectId == ModrinthProvider.FabricApiProjectId)
                || Directory.Exists(Path.Combine(profile.GameDirectory, "mods"))
                   && Directory.GetFiles(Path.Combine(profile.GameDirectory, "mods"), "fabric-api*.jar").Length > 0;
            if (hasFabricApi) return;

            var fabricApi = await _hub.Modrinth.GetProjectAsync(ModrinthProvider.FabricApiProjectId, ContentKind.Mod, ct);
            if (fabricApi != null) await InstallAsync(profile, fabricApi, progress, ct);
        }

        public static bool IsInstalled(GameProfile profile, ContentItem item)
            => profile.Content.Any(e => e.ProviderId == item.ProviderId && e.ProjectId == item.Id && File.Exists(e.GetFullPath(profile)));

        // Shader'ları çalıştırmak için Iris modu gerekir; profile göre otomatik eklenir.
        private async Task AddShaderLoaderAsync(GameProfile profile, Queue<(IContentProvider, ContentItem, ContentOrigin)> queue,
            ContentInstallResult result, CancellationToken ct)
        {
            switch (profile.Loader)
            {
                case ModLoader.Fabric:
                case ModLoader.Quilt:
                case ModLoader.NeoForge:
                    var iris = await _hub.Modrinth.GetProjectAsync(ModrinthProvider.IrisProjectId, ContentKind.Mod, ct);
                    if (iris != null) queue.Enqueue((_hub.Modrinth, iris, ContentOrigin.Dependency));
                    break;
                case ModLoader.Forge:
                    result.Warnings.Add("Forge profillerinde shader için Oculus modu gerekir; arama kısmından kurabilirsin.");
                    break;
                default:
                    result.Warnings.Add("Vanilla Minecraft shader çalıştıramaz; Fabric profili önerilir.");
                    break;
            }
        }

        private static async Task<ContentItem?> ResolveDependencyAsync(IContentProvider provider, ContentDependency dep, CancellationToken ct)
        {
            try
            {
                string? projectId = dep.ProjectId;
                if (string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(dep.VersionId))
                    projectId = (await provider.GetVersionByIdAsync(dep.VersionId, ct))?.ProjectId;

                return string.IsNullOrEmpty(projectId) ? null : await provider.GetProjectAsync(projectId, ContentKind.Mod, ct);
            }
            catch (HttpRequestException)
            {
                return null; // silinmiş bağımlılık tüm kurulumu durdurmasın
            }
        }

        // Aynı modun eski sürümü klasörde kalırsa oyun "duplicate mod" hatasıyla çöker.
        private static void RemoveOlderVersions(GameProfile profile, ContentItem item, string newRelative)
        {
            var older = profile.Content
                .Where(e => e.ProviderId == item.ProviderId && e.ProjectId == item.Id
                            && !e.RelativePath.Equals(newRelative, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var entry in older)
            {
                string path = entry.GetFullPath(profile);
                if (File.Exists(path)) File.Delete(path);
                profile.Content.Remove(entry);
            }
        }
    }
}
