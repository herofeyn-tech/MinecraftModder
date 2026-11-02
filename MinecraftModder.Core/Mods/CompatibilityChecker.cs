using MinecraftModder.Core.Content;
using MinecraftModder.Core.Profiles;

namespace MinecraftModder.Core.Mods
{
    public enum ModIssueKind
    {
        WrongLoader,       // Forge modu Fabric diskinde vb.
        WrongGameVersion,  // 1.21 modu 1.20.1 diskinde
        ServerOnly,        // sadece sunucuda çalışan mod
        Duplicate,         // aynı mod iki kez (farklı sitelerden ya da farklı sürümleri)
        MissingDependency  // modun istediği başka bir mod yok
    }

    public sealed record ModIssue(ModIssueKind Kind, ContentEntry Entry, ModMetadata Meta, string Message, string? DependencyId = null);

    // Bir diskteki modların birbiriyle ve diskle uyumunu, sadece jar'ların içindeki bilgiye bakarak kontrol eder.
    // Modun hangi siteden geldiği önemli değildir. Ağa çıkmaz; düzeltmeyi ModCompatibilityService yapar.
    public static class CompatibilityChecker
    {
        // Loader'ın ya da oyunun kendisinin sağladığı, ayrıca kurulmayan kimlikler.
        private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
        {
            "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "forge", "neoforge",
            "javafml", "lowcodefml", "mixinextras"
        };

        public static IReadOnlyList<ModIssue> Check(ModLoader loader, string gameVersion, IEnumerable<(ContentEntry Entry, ModMetadata? Meta)> mods)
        {
            var issues = new List<ModIssue>();
            var active = new List<(ContentEntry Entry, ModMetadata Meta)>();

            foreach (var (entry, meta) in mods)
            {
                if (!entry.Enabled || meta == null) continue;

                if (meta.ServerOnly)
                    issues.Add(new ModIssue(ModIssueKind.ServerOnly, entry, meta, $"{meta.DisplayName} sadece sunucularda çalışan bir mod."));
                else if (!meta.RunsOn(loader, gameVersion))
                    issues.Add(new ModIssue(ModIssueKind.WrongLoader, entry, meta, $"{meta.DisplayName} bir {LoaderNames(meta)} modu; bu disk {loader}."));
                else if (!meta.SupportsMinecraft(loader, gameVersion))
                    issues.Add(new ModIssue(ModIssueKind.WrongGameVersion, entry, meta, $"{meta.DisplayName} Minecraft {gameVersion} ile çalışmıyor."));
                else
                    active.Add((entry, meta));
            }

            // Aynı mod iki kez: en yeni sürümü kalır.
            foreach (var group in active.GroupBy(m => m.Meta.ModId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
            {
                var keep = group
                    .OrderByDescending(m => m.Meta.Version ?? "", Comparer<string>.Create(VersionMatcher.Compare))
                    .ThenBy(m => m.Entry.Origin == ContentOrigin.Manual ? 1 : 0)
                    .First();
                foreach (var extra in group.Where(m => !ReferenceEquals(m.Entry, keep.Entry)))
                {
                    issues.Add(new ModIssue(ModIssueKind.Duplicate, extra.Entry, extra.Meta, $"{extra.Meta.DisplayName} iki kez kurulu."));
                    active.Remove(extra);
                }
            }

            var provided = new HashSet<string>(BuiltIn, StringComparer.OrdinalIgnoreCase);
            foreach (var (_, meta) in active) provided.UnionWith(meta.ProvidedIds);

            foreach (var (entry, meta) in active)
            {
                foreach (var dep in meta.DependenciesFor(loader).Where(d => d.Required))
                {
                    if (provided.Contains(dep.ModId)) continue;
                    issues.Add(new ModIssue(ModIssueKind.MissingDependency, entry, meta,
                        $"{meta.DisplayName} çalışmak için '{dep.ModId}' modunu istiyor.", dep.ModId));
                }
            }
            return issues;
        }

        private static string LoaderNames(ModMetadata meta) => string.Join("/", meta.Loaders.OrderBy(l => l));
    }
}
