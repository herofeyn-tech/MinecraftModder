namespace MinecraftModder.Core.Content
{
    public static class VersionPicker
    {
        // Kuzen-dostu seçim: önce kararlı (release) sürümler, yoksa beta, en son alpha.
        // Aynı kanal içinde en yeni olan seçilir.
        public static ContentVersion? PickBest(IEnumerable<ContentVersion> versions, string? gameVersion = null, ModLoader? loader = null)
        {
            var candidates = versions.Where(v => v.Files.Count > 0);

            if (!string.IsNullOrEmpty(gameVersion))
                candidates = candidates.Where(v => v.GameVersions.Count == 0 || v.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase));

            if (loader is { } l && l.SupportsMods())
            {
                var tags = l.CompatibleTags();
                candidates = candidates.Where(v => v.Loaders.Count == 0 || v.Loaders.Any(x => tags.Contains(x, StringComparer.OrdinalIgnoreCase)));
            }

            return candidates
                .OrderBy(v => v.Channel)
                .ThenByDescending(v => v.Published)
                .FirstOrDefault();
        }

        // Bir sürümün içinden asıl indirilecek dosya (birincil olan, yoksa uygun uzantılı ilki).
        public static ContentFile? PickFile(ContentVersion version, ContentKind kind)
        {
            string[] extensions = kind switch
            {
                ContentKind.Mod => new[] { ".jar" },
                ContentKind.Modpack => new[] { ".mrpack", ".zip" },
                _ => new[] { ".zip" }
            };

            bool HasExt(ContentFile f) => extensions.Any(e => f.FileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

            return version.Files.FirstOrDefault(f => f.Primary && HasExt(f))
                ?? version.Files.FirstOrDefault(HasExt)
                ?? (kind == ContentKind.Mod ? null : version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault());
        }
    }
}
