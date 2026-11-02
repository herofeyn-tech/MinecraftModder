using MinecraftModder.Core.Content;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Mods
{
    public sealed record CompatibilityReport(IReadOnlyList<string> Fixed, IReadOnlyList<string> Problems)
    {
        public static readonly CompatibilityReport Empty = new(Array.Empty<string>(), Array.Empty<string>());
        public bool IsClean => Fixed.Count == 0 && Problems.Count == 0;
    }

    // "Kuzen istediği modu atar ve oynar": oyun açılmadan önce diskteki modları jar'larının içine bakarak
    // kontrol eder ve sorunları kendisi düzeltir:
    //  - yanlış loader / yanlış Minecraft sürümü → aynı modun bu diske uygun sürümü varsa onunla değiştirir, yoksa kapatır
    //  - sadece-sunucu modları ve iki kez kurulmuş modlar → kapatır
    //  - eksik gereksinimler → Modrinth/CurseForge'da bulup kurar; bulamazsa isteyen modu kapatır
    //  - mods klasörüne yanlışlıkla atılmış shader/kaynak paketleri → doğru klasöre taşır
    // Mod paketinin kendi dosyalarına dokunulmaz (yapımcısı neyi neden koyduğunu bilir); sadece bildirilir.
    public sealed class ModCompatibilityService
    {
        private const int MaxRounds = 4;

        private readonly ProfileStore _profiles;
        private readonly ProfileContentService _content;
        private readonly ContentInstaller _installer;
        private readonly DependencyFinder _finder;
        private readonly ContentHub _hub;

        public ModCompatibilityService(ProfileStore profiles, ProfileContentService content, ContentInstaller installer, DependencyFinder finder, ContentHub hub)
        {
            _profiles = profiles;
            _content = content;
            _installer = installer;
            _finder = finder;
            _hub = hub;
        }

        public async Task<CompatibilityReport> CheckAndFixAsync(GameProfile profile, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            if (!profile.Loader.SupportsMods()) return CompatibilityReport.Empty;

            var fixes = new List<string>();
            var problems = new List<string>();
            progress?.Report(ProgressInfo.Step("Modlar kontrol ediliyor..."));

            MoveMisplacedArchives(profile, fixes);
            await _content.RefreshAsync(profile, ct);
            await InstallListedDependenciesAsync(profile, fixes, progress, ct);

            var triedDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var triedReplacements = new HashSet<ContentEntry>();

            for (int round = 0; round < MaxRounds; round++)
            {
                var issues = CompatibilityChecker.Check(profile.Loader, profile.GameVersion, InspectAll(profile));
                if (issues.Count == 0) break;
                bool changed = false;

                foreach (var issue in issues.Where(i => i.Kind != ModIssueKind.MissingDependency))
                {
                    if (!CanAutoFix(issue.Entry))
                    {
                        AddOnce(problems, issue.Message);
                        continue;
                    }

                    if (issue.Kind is ModIssueKind.WrongLoader or ModIssueKind.WrongGameVersion && triedReplacements.Add(issue.Entry))
                    {
                        string? replacement = await TryReplaceWithCompatibleAsync(profile, issue.Entry, progress, ct);
                        if (replacement != null)
                        {
                            fixes.Add($"{issue.Message} Yerine bu diske uygun sürümünü kurdum.");
                            changed = true;
                            continue;
                        }
                    }

                    _content.SetEnabled(profile, new[] { issue.Entry }, false);
                    fixes.Add($"{issue.Message} Oyun çökmesin diye kapattım.");
                    changed = true;
                }

                foreach (var issue in issues.Where(i => i.Kind == ModIssueKind.MissingDependency))
                {
                    if (!triedDependencies.Add(issue.DependencyId!)) continue;
                    string? installed;
                    try
                    {
                        installed = await _finder.InstallByModIdAsync(profile, issue.DependencyId!, progress, ct);
                    }
                    catch (HttpRequestException)
                    {
                        installed = null;
                    }
                    if (installed == null) continue;
                    fixes.Add($"{issue.Meta.DisplayName} için gereken {installed} kuruldu.");
                    changed = true;
                }

                if (!changed) break;
            }

            // Hâlâ eksik gereksinimi olan modlar oyunu açılmaz hale getirir; kapatılır.
            foreach (var issue in CompatibilityChecker.Check(profile.Loader, profile.GameVersion, InspectAll(profile))
                         .Where(i => i.Kind == ModIssueKind.MissingDependency))
            {
                if (CanAutoFix(issue.Entry) && issue.Entry.Enabled)
                {
                    _content.SetEnabled(profile, new[] { issue.Entry }, false);
                    fixes.Add($"{issue.Meta.DisplayName} kapatıldı: istediği '{issue.DependencyId}' modu hiçbir sitede bulunamadı.");
                }
                else
                {
                    AddOnce(problems, issue.Message);
                }
            }

            await EnsureShaderSupportAsync(profile, fixes, progress, ct);
            _profiles.Save(profile);
            return new CompatibilityReport(fixes, problems);
        }

        // Bazı modlar gereksinimlerini jar'larına yazmaz, sadece sitede listeler (ör. REI → Architectury, Cloth Config).
        // Elle atılıp sitede tanınan dosyalar için o listeye bir kez bakılır ve eksikler kurulur.
        private async Task InstallListedDependenciesAsync(GameProfile profile, List<string> fixes, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            var pending = profile.Content
                .Where(e => e.Kind == ContentKind.Mod && e.Enabled && !e.DependenciesChecked && CanAutoFix(e)
                            && e.ProviderId != null && e.ProjectId != null && e.VersionId != null)
                .ToList();

            foreach (var entry in pending)
            {
                if (_hub.Get(entry.ProviderId) is not { } provider) continue;
                try
                {
                    var version = await provider.GetVersionByIdAsync(entry.VersionId!, ct);
                    if (version == null && await provider.GetProjectAsync(entry.ProjectId!, ContentKind.Mod, ct) is { } project)
                        version = (await provider.GetVersionsAsync(project, null, null, ct)).FirstOrDefault(v => v.Id == entry.VersionId);

                    foreach (var dep in version?.Dependencies.Where(d => d.Required) ?? Enumerable.Empty<ContentDependency>())
                    {
                        string? projectId = dep.ProjectId ?? (dep.VersionId != null ? (await provider.GetVersionByIdAsync(dep.VersionId, ct))?.ProjectId : null);
                        if (projectId == null || await provider.GetProjectAsync(projectId, ContentKind.Mod, ct) is not { } depItem) continue;

                        var result = await _installer.InstallAsync(profile, depItem, progress, ct, ContentOrigin.Dependency);
                        foreach (string installed in result.Installed) fixes.Add($"{entry.DisplayName} için gereken {installed} kuruldu.");
                    }
                    entry.DependenciesChecked = true;
                }
                catch (HttpRequestException)
                {
                    // internet yoksa bir dahaki açılışta tekrar denenir
                }
            }
        }

        // Diskteki her açık mod için jar bilgisi (kimlik kayda da yazılır).
        public static List<(ContentEntry Entry, ModMetadata? Meta)> InspectAll(GameProfile profile)
        {
            var list = new List<(ContentEntry, ModMetadata?)>();
            foreach (var entry in profile.Content.Where(e => e.Kind == ContentKind.Mod && e.Enabled))
            {
                var meta = JarInspector.Inspect(entry.GetFullPath(profile));
                if (meta != null) entry.ModId = meta.ModId;
                list.Add((entry, meta));
            }
            return list;
        }

        private static bool CanAutoFix(ContentEntry entry) => entry.Origin is not (ContentOrigin.Pack or ContentOrigin.PackOverride);

        // Yanlış loader/sürüm için atılmış bir modun doğru sürümünü aynı projeden kurar
        // (ör. Forge için olan JEI, Fabric diskine atılınca JEI'nin Fabric sürümü).
        private async Task<string?> TryReplaceWithCompatibleAsync(GameProfile profile, ContentEntry wrong, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            if (wrong.ProviderId == null || wrong.ProjectId == null || _hub.Get(wrong.ProviderId) is not { } provider) return null;

            ContentItem? project;
            try { project = await provider.GetProjectAsync(wrong.ProjectId, ContentKind.Mod, ct); }
            catch (HttpRequestException) { return null; }
            if (project == null) return null;

            // Yanlış dosyayı kenara al ki kurulum onu "zaten kurulu" saymasın; başarısız olursa geri konur.
            string path = wrong.GetFullPath(profile);
            string aside = path + ".yanlis";
            try { File.Move(path, aside, overwrite: true); }
            catch (IOException) { return null; }
            profile.Content.Remove(wrong);

            ContentInstallResult? result = null;
            try
            {
                result = await _installer.InstallAsync(profile, project, progress, ct, wrong.Origin);
            }
            catch (HttpRequestException)
            {
            }

            if (result is { Installed.Count: > 0 })
            {
                try { File.Delete(aside); } catch (IOException) { }
                return project.Title;
            }

            File.Move(aside, path, overwrite: true);
            profile.Content.Add(wrong);
            return null;
        }

        // mods klasörüne atılmış shader ya da kaynak paketi oyunu etkilemez ama kafa karıştırır; yerine taşınır.
        private static void MoveMisplacedArchives(GameProfile profile, List<string> fixes)
        {
            string mods = Path.Combine(profile.GameDirectory, "mods");
            if (!Directory.Exists(mods)) return;

            foreach (string file in Directory.GetFiles(mods, "*.zip"))
            {
                var kind = JarInspector.DetectKind(file) switch
                {
                    ArchiveKind.ShaderPack => ContentKind.ShaderPack,
                    ArchiveKind.ResourcePack => ContentKind.ResourcePack,
                    _ => (ContentKind?)null
                };
                if (kind == null) continue;

                string targetDir = Path.Combine(profile.GameDirectory, kind.Value.FolderName()!);
                Directory.CreateDirectory(targetDir);
                string target = Path.Combine(targetDir, Path.GetFileName(file));
                try
                {
                    File.Move(file, target, overwrite: true);
                    string oldRelative = $"mods/{Path.GetFileName(file)}";
                    profile.Content.RemoveAll(e => e.RelativePath.Equals(oldRelative, StringComparison.OrdinalIgnoreCase));
                    fixes.Add($"{Path.GetFileName(file)} bir {kind.Value.DisplayName().ToLowerInvariant()}; mods klasöründeydi, doğru yerine taşıdım.");
                }
                catch (IOException)
                {
                    // dosya kullanımdaysa bir dahaki sefere
                }
            }
        }

        // Diskte açık bir shader varsa ama shader modu (Iris/Oculus) yoksa shader görünmez.
        private async Task EnsureShaderSupportAsync(GameProfile profile, List<string> fixes, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            if (profile.IsModpack || !profile.Content.Any(e => e.Kind == ContentKind.ShaderPack && e.Enabled)) return;
            try
            {
                string? added = await _installer.EnsureShaderLoaderAsync(profile, progress, ct);
                if (added != null) fixes.Add($"Shader'lar çalışsın diye {added} kuruldu.");
            }
            catch (HttpRequestException)
            {
            }
        }

        private static void AddOnce(List<string> list, string message)
        {
            if (!list.Contains(message)) list.Add(message);
        }
    }
}
