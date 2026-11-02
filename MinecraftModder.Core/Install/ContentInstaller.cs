using MinecraftModder.Core.Content;
using MinecraftModder.Core.Content.Modrinth;
using MinecraftModder.Core.Mods;
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
        // Forge'da shader'ları çalıştıran mod (Fabric/NeoForge'daki Iris'in karşılığı).
        private const string OculusSlug = "oculus";

        private readonly ProfileStore _profiles;
        private readonly ContentHub _hub;
        private readonly DownloadManager _downloads;

        public ContentInstaller(ProfileStore profiles, ContentHub hub, DownloadManager downloads)
        {
            _profiles = profiles;
            _hub = hub;
            _downloads = downloads;
        }

        // rootOrigin: kullanıcı mı istedi, yoksa başka bir modun ihtiyacı olarak launcher mı kuruyor.
        public async Task<ContentInstallResult> InstallAsync(GameProfile profile, ContentItem item,
            IProgress<ProgressInfo>? progress, CancellationToken ct = default, ContentOrigin rootOrigin = ContentOrigin.User)
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
            queue.Enqueue((provider, item, rootOrigin));

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
                var file = version == null ? null : VersionPicker.PickFile(version, current.Kind);

                // Bu sitede uygun dosya yoksa (sürüm yok ya da yapımcı indirmeye izin vermiyor)
                // aynı modu diğer sitede dene. Kullanıcı hangi siteden geldiğini bilmek zorunda değil.
                if (version == null || file == null || file.AllUrls().Count == 0)
                {
                    var alternative = await _hub.FindAlternativeAsync(current, profile.GameVersion, loaderFilter, ct);
                    if (alternative is { } alt)
                    {
                        if (IsInstalled(profile, alt.Item))
                        {
                            if (origin == ContentOrigin.User) result.AlreadyInstalled.Add(current.Title);
                            continue;
                        }
                        (p, current, version, file) = (alt.Provider, alt.Item with { IconUrl = alt.Item.IconUrl ?? current.IconUrl }, alt.Version, alt.File);
                    }
                }

                if (version == null)
                {
                    result.Skipped.Add(new SkippedContent(current.Title, $"{profile.DisplayVersion} ile uyumlu bir sürümü yok.", current.WebUrl));
                    continue;
                }
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

                // Aynı dosya (başka bir siteden gelmiş olsa bile) zaten diskte: tekrar indirmeye gerek yok.
                if (file.Sha1 != null && profile.Content.Any(e => e.Enabled && e.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)
                                                                  && string.Equals(e.Sha1, file.Sha1, StringComparison.OrdinalIgnoreCase) && File.Exists(target)))
                {
                    if (origin == ContentOrigin.User) result.AlreadyInstalled.Add(current.Title);
                    continue;
                }

                progress?.Report(ProgressInfo.Step($"{current.Title} indiriliyor..."));
                await _downloads.DownloadAsync(new DownloadItem(target, urls, file.Sha1, file.Sha512, file.Size, current.Title), ct);

                RemoveOlderVersions(profile, current, relative);
                profile.Content.RemoveAll(e => e.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));

                // Aynı mod başka bir siteden zaten kurulu olabilir (ör. Fabric API hem Modrinth hem CurseForge'da var).
                // Jar'ın kendi kimliğine bakılır; varsa yenisi silinir, çünkü ikisi birden oyunu çökertir.
                string? modId = current.Kind == ContentKind.Mod ? JarInspector.Inspect(target)?.ModId : null;
                if (modId != null && FindByModId(profile, modId, exceptRelative: relative) is { } existing)
                {
                    TryDelete(target);
                    if (origin == ContentOrigin.User) result.AlreadyInstalled.Add($"{current.Title} ({existing.DisplayName} olarak)");
                    continue;
                }

                profile.Content.Add(new ContentEntry
                {
                    ModId = modId,
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
                    LookedUp = true,
                    DependenciesChecked = true // aşağıda kuyruğa ekleniyor
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

        // Diskte açık shader varsa ama shader modu yoksa ekler (elle atılan shader'lar için). Kurduğu modun adını döner.
        public async Task<string?> EnsureShaderLoaderAsync(GameProfile profile, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            if (!profile.Loader.SupportsMods()) return null;
            if (FindByModId(profile, "iris") != null || FindByModId(profile, "oculus") != null) return null;

            string projectId = profile.Loader == ModLoader.Forge ? OculusSlug : ModrinthProvider.IrisProjectId;
            var item = await _hub.Modrinth.GetProjectAsync(projectId, ContentKind.Mod, ct);
            if (item == null) return null;
            var result = await InstallAsync(profile, item, progress, ct, ContentOrigin.Dependency);
            return result.Installed.Count > 0 ? item.Title : null;
        }

        public static bool IsInstalled(GameProfile profile, ContentItem item)
            => profile.Content.Any(e => e.ProviderId == item.ProviderId && e.ProjectId == item.Id && File.Exists(e.GetFullPath(profile)));

        // Diskte bu mod kimliğini taşıyan, açık bir mod var mı? Kimliği bilinmeyen kayıtlar için jar okunur.
        public static ContentEntry? FindByModId(GameProfile profile, string modId, string? exceptRelative = null)
        {
            foreach (var entry in profile.Content.Where(e => e.Kind == ContentKind.Mod && e.Enabled))
            {
                if (exceptRelative != null && entry.RelativePath.Equals(exceptRelative, StringComparison.OrdinalIgnoreCase)) continue;
                entry.ModId ??= JarInspector.Inspect(entry.GetFullPath(profile))?.ModId;
                if (string.Equals(entry.ModId, modId, StringComparison.OrdinalIgnoreCase)) return entry;
            }
            return null;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { /* bir sonraki kontrol yine yakalar */ }
        }

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
                    var oculus = await _hub.Modrinth.GetProjectAsync(OculusSlug, ContentKind.Mod, ct);
                    if (oculus != null) queue.Enqueue((_hub.Modrinth, oculus, ContentOrigin.Dependency));
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
