using MinecraftModder.Core.Content;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Mods
{
    // Bu diske uymayan modlar için öneri: "bunlar Forge 1.20.1 istiyor; yeni bir disk açayım mı?"
    public sealed record DiskSuggestion(ModLoader Loader, string GameVersion, string SuggestedName, IReadOnlyList<string> FileNames, GameProfile? ExistingDisk);

    public sealed class ImportResult
    {
        public List<string> Added { get; } = new();
        public List<string> Skipped { get; } = new();
        public List<GameProfile> NewDisks { get; } = new();
        public CompatibilityReport Compatibility { get; set; } = CompatibilityReport.Empty;
        public DiskSuggestion? Suggestion { get; set; }
    }

    // Kullanıcı nereden indirdiği belli olmayan bir dosyayı launcher'a sürükleyip bırakır; ne olduğu
    // dosyanın içinden anlaşılır ve doğru yere konur:
    //   .jar mod → mods (uyumsuzsa doğru sürümüyle değiştirilir ya da uygun bir disk önerilir)
    //   shader / kaynak paketi zip'i → shaderpacks / resourcepacks
    //   .mrpack → yeni bir disk
    public sealed class ContentImporter
    {
        // Disk önerirken denenecek, modların en çok yapıldığı sürümler.
        private static readonly string[] CommonVersions = { "1.21.1", "1.20.1", "1.19.2", "1.18.2", "1.16.5", "1.12.2", "1.7.10" };

        private readonly ProfileStore _profiles;
        private readonly ModpackInstaller _modpacks;
        private readonly ModCompatibilityService _compatibility;

        public ContentImporter(ProfileStore profiles, ModpackInstaller modpacks, ModCompatibilityService compatibility)
        {
            _profiles = profiles;
            _modpacks = modpacks;
            _compatibility = compatibility;
        }

        public async Task<ImportResult> ImportAsync(GameProfile profile, IEnumerable<string> paths, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            var result = new ImportResult();
            var importedMods = new List<string>();
            var misfits = new List<(string Path, ModMetadata Meta)>();

            foreach (string path in paths.Where(File.Exists))
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                progress?.Report(ProgressInfo.Step($"{name} inceleniyor..."));

                switch (JarInspector.DetectKind(path))
                {
                    case ArchiveKind.ModrinthModpack:
                        var pack = await _modpacks.InstallLocalMrpackAsync(path, progress, ct);
                        result.NewDisks.Add(pack.Profile);
                        result.Added.Add($"{pack.Profile.Name} (yeni disk)");
                        break;

                    case ArchiveKind.CurseForgeModpack:
                        result.Skipped.Add($"{name}: bu bir CurseForge paket dosyası. Paketler sekmesinde adıyla aratıp kurabilirsin.");
                        break;

                    case ArchiveKind.ShaderPack:
                        if (!profile.Loader.SupportsMods())
                        {
                            result.Skipped.Add($"{name}: shader'lar modsuz diskte çalışmaz; Fabric'li bir diske at.");
                            break;
                        }
                        Copy(path, profile, ContentKind.ShaderPack);
                        result.Added.Add(name);
                        break;

                    case ArchiveKind.ResourcePack:
                        Copy(path, profile, ContentKind.ResourcePack);
                        result.Added.Add(name);
                        break;

                    case ArchiveKind.Mod:
                        var meta = JarInspector.Inspect(path);
                        if (!profile.Loader.SupportsMods())
                        {
                            if (meta != null) misfits.Add((path, meta));
                            else result.Skipped.Add($"{name}: bu disk modsuz (Vanilla).");
                            break;
                        }
                        Copy(path, profile, ContentKind.Mod);
                        importedMods.Add(name);
                        result.Added.Add(meta?.DisplayName ?? name);
                        break;

                    default:
                        result.Skipped.Add($"{name}: ne olduğu anlaşılamadı (mod, shader ya da kaynak paketi değil).");
                        break;
                }
            }

            if (result.Added.Count > result.NewDisks.Count && profile.Loader.SupportsMods())
            {
                // Eksik gereksinimleri kurar, yanlış sürümleri değiştirir ya da kapatır.
                result.Compatibility = await _compatibility.CheckAndFixAsync(profile, progress, ct);

                // Değiştirilemeyip kapatılan (bu diske hiç uymayan) modlar başka bir diske önerilir.
                foreach (string name in importedMods)
                {
                    var entry = profile.Content.FirstOrDefault(e => e.RelativePath.Equals($"mods/{name}", StringComparison.OrdinalIgnoreCase));
                    if (entry is not { Enabled: false }) continue;
                    var meta = JarInspector.Inspect(entry.GetFullPath(profile));
                    if (meta != null && (!meta.RunsOn(profile.Loader, profile.GameVersion) || !meta.SupportsMinecraft(profile.Loader, profile.GameVersion)))
                        misfits.Add((entry.GetFullPath(profile), meta));
                }
            }

            if (misfits.Count > 0) result.Suggestion = Suggest(profile, misfits);
            _profiles.Save(profile);
            return result;
        }

        // Öneriyi kabul eden kullanıcı için: uymayan modları uygun diske (yoksa yeni açılan diske) taşır.
        public async Task<(GameProfile Disk, ImportResult Result)> MoveToSuggestedDiskAsync(GameProfile from, DiskSuggestion suggestion,
            IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            var disk = suggestion.ExistingDisk ?? _profiles.Create(suggestion.SuggestedName, suggestion.GameVersion, suggestion.Loader);

            var sources = new List<string>();
            foreach (string name in suggestion.FileNames)
            {
                var entry = from.Content.FirstOrDefault(e => e.Kind == ContentKind.Mod && e.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                string? path = entry != null ? entry.GetFullPath(from) : null;
                if (path != null && File.Exists(path)) sources.Add(path);
            }

            // Dosyalar yeni diske adlarıyla kopyalanır (".disabled" eki olmadan), sonra eski diskten silinir.
            string staging = Path.Combine(Path.GetTempPath(), "MinecraftModder-tasima-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var staged = sources.Select(src =>
                {
                    string clean = Path.GetFileName(src);
                    if (clean.EndsWith(ContentEntry.DisabledSuffix, StringComparison.OrdinalIgnoreCase)) clean = clean[..^ContentEntry.DisabledSuffix.Length];
                    string dest = Path.Combine(staging, clean);
                    File.Copy(src, dest, overwrite: true);
                    return dest;
                }).ToList();

                var result = await ImportAsync(disk, staged, progress, ct);

                foreach (string src in sources)
                {
                    try { File.Delete(src); } catch (IOException) { }
                }
                from.Content.RemoveAll(e => e.Kind == ContentKind.Mod && suggestion.FileNames.Contains(e.FileName, StringComparer.OrdinalIgnoreCase));
                _profiles.Save(from);
                return (disk, result);
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
            }
        }

        internal DiskSuggestion Suggest(GameProfile current, List<(string Path, ModMetadata Meta)> misfits)
        {
            var metas = misfits.Select(m => m.Meta).ToList();

            // En çok modun istediği loader (Fabric+Forge taşıyan çok-loader'lı jarlar Fabric'i seçtirir).
            ModLoader loader = new[] { ModLoader.Fabric, ModLoader.NeoForge, ModLoader.Forge, ModLoader.Quilt }
                .OrderByDescending(l => metas.Count(m => m.Loaders.Contains(l)))
                .First();

            // Hepsinin çalıştığı ilk sürüm; yoksa ilk modun çalıştığı.
            var candidates = new[] { current.GameVersion }.Concat(CommonVersions).Distinct().ToList();
            string version = candidates.FirstOrDefault(v => metas.All(m => m.SupportsMinecraft(loader, v)))
                             ?? candidates.FirstOrDefault(v => metas[0].SupportsMinecraft(loader, v))
                             ?? "1.20.1";

            var existing = _profiles.All.FirstOrDefault(p => !p.IsModpack && p.Loader == loader && p.GameVersion == version && p.Id != current.Id);
            string name = metas.Count == 1 ? $"{metas[0].DisplayName} diski" : $"{loader} {version} diski";
            return new DiskSuggestion(loader, version, name, misfits.Select(m => CleanName(m.Path)).ToList(), existing);
        }

        private static string CleanName(string path)
        {
            string name = Path.GetFileName(path);
            return name.EndsWith(ContentEntry.DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^ContentEntry.DisabledSuffix.Length] : name;
        }

        private static void Copy(string source, GameProfile profile, ContentKind kind)
        {
            string target = SafePath.Combine(profile.GameDirectory, $"{kind.FolderName()}/{Path.GetFileName(source)}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, overwrite: true);
        }
    }
}
