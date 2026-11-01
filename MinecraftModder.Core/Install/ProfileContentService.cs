using MinecraftModder.Core.Content;
using MinecraftModder.Core.Content.Modrinth;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Install
{
    // "Kurulu içerik" ekranının arkasındaki iş: profil manifestosu ile disk arasındaki farkları uzlaştırır,
    // isimsiz dosyaları Modrinth'te tanır, toplu etkinleştirme/devre dışı bırakma/silme yapar.
    public sealed class ProfileContentService
    {
        private static readonly ContentKind[] TrackedKinds = { ContentKind.Mod, ContentKind.ShaderPack, ContentKind.ResourcePack };

        private readonly ProfileStore _profiles;
        private readonly ModrinthProvider _modrinth;

        public ProfileContentService(ProfileStore profiles, ModrinthProvider modrinth)
        {
            _profiles = profiles;
            _modrinth = modrinth;
        }

        public async Task<IReadOnlyList<ContentEntry>> RefreshAsync(GameProfile profile, CancellationToken ct = default)
        {
            bool changed = await Task.Run(() => ReconcileWithDisk(profile), ct);
            changed |= await IdentifyUnknownAsync(profile, ct);
            if (changed) _profiles.Save(profile);

            return profile.Content
                .Where(e => e.Kind != ContentKind.Other)
                .OrderBy(e => e.Kind)
                .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static bool IsMissing(GameProfile profile, ContentEntry entry) => !File.Exists(entry.GetFullPath(profile));

        public void SetEnabled(GameProfile profile, IEnumerable<ContentEntry> entries, bool enabled)
        {
            var errors = new List<string>();
            foreach (var entry in entries.Where(e => e.Enabled != enabled).ToList())
            {
                string from = entry.GetFullPath(profile);
                entry.Enabled = enabled;
                string to = entry.GetFullPath(profile);
                try
                {
                    if (File.Exists(from)) File.Move(from, to, overwrite: true);
                }
                catch (Exception ex)
                {
                    entry.Enabled = !enabled;
                    errors.Add($"{entry.DisplayName}: {ex.Message}");
                }
            }

            _profiles.Save(profile);
            if (errors.Count > 0) throw new IOException("Bazı dosyalar değiştirilemedi (oyun açık olabilir):\n" + string.Join("\n", errors));
        }

        public void Remove(GameProfile profile, IEnumerable<ContentEntry> entries)
        {
            var errors = new List<string>();
            foreach (var entry in entries.ToList())
            {
                try
                {
                    string path = entry.GetFullPath(profile);
                    if (File.Exists(path)) File.Delete(path);
                    profile.Content.Remove(entry);
                }
                catch (Exception ex)
                {
                    errors.Add($"{entry.DisplayName}: {ex.Message}");
                }
            }

            _profiles.Save(profile);
            if (errors.Count > 0) throw new IOException("Bazı dosyalar silinemedi (oyun açık olabilir):\n" + string.Join("\n", errors));
        }

        // Kullanıcı klasöre elle mod attıysa ya da sildiyse manifestoyu buna göre günceller.
        internal static bool ReconcileWithDisk(GameProfile profile)
        {
            bool changed = false;
            var onDisk = new Dictionary<string, (string FullPath, bool Enabled, ContentKind Kind)>(StringComparer.OrdinalIgnoreCase);

            foreach (var kind in TrackedKinds)
            {
                string folder = kind.FolderName()!;
                string dir = Path.Combine(profile.GameDirectory, folder);
                if (!Directory.Exists(dir)) continue;

                foreach (string file in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    bool enabled = !name.EndsWith(ContentEntry.DisabledSuffix, StringComparison.OrdinalIgnoreCase);
                    string clean = enabled ? name : name.Substring(0, name.Length - ContentEntry.DisabledSuffix.Length);

                    string expectedExt = kind == ContentKind.Mod ? ".jar" : ".zip";
                    if (!clean.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase)) continue;

                    onDisk[$"{folder}/{clean}"] = (file, enabled, kind);
                }
            }

            foreach (var (relative, info) in onDisk)
            {
                var entry = profile.Content.FirstOrDefault(e => e.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    profile.Content.Add(new ContentEntry
                    {
                        RelativePath = relative,
                        Kind = info.Kind,
                        Origin = ContentOrigin.Manual,
                        Enabled = info.Enabled,
                        Sha1 = TryHash(info.FullPath),
                        Size = new FileInfo(info.FullPath).Length
                    });
                    changed = true;
                }
                else if (entry.Enabled != info.Enabled)
                {
                    entry.Enabled = info.Enabled;
                    changed = true;
                }
            }

            // Elle silinmiş ve tekrar indirilemeyecek kayıtlar listeden düşer;
            // indirme adresi olanlar "eksik" olarak kalır ki Onar butonu geri getirebilsin.
            int removed = profile.Content.RemoveAll(e =>
                e.Kind != ContentKind.Other && !e.CanRedownload && !onDisk.ContainsKey(e.RelativePath));
            return changed || removed > 0;
        }

        private async Task<bool> IdentifyUnknownAsync(GameProfile profile, CancellationToken ct)
        {
            var unknown = profile.Content
                .Where(e => !e.LookedUp && e.Kind != ContentKind.Other && (e.Title == null || e.IconUrl == null || e.ProjectId == null))
                .ToList();
            if (unknown.Count == 0) return false;

            foreach (var entry in unknown.Where(e => string.IsNullOrEmpty(e.Sha1)))
            {
                string path = entry.GetFullPath(profile);
                if (File.Exists(path)) entry.Sha1 = TryHash(path);
            }

            Dictionary<string, (ContentItem Project, ContentVersion Version)> found;
            try
            {
                var hashes = unknown.Where(e => !string.IsNullOrEmpty(e.Sha1)).Select(e => e.Sha1!).ToList();
                found = await _modrinth.IdentifyBySha1Async(hashes, ct);
            }
            catch (HttpRequestException)
            {
                return false; // internet yoksa bir dahaki sefere tekrar denenir
            }

            foreach (var entry in unknown)
            {
                entry.LookedUp = true;
                if (entry.Sha1 == null || !found.TryGetValue(entry.Sha1, out var match)) continue;

                entry.Title ??= match.Project.Title;
                entry.IconUrl ??= match.Project.IconUrl;
                if (entry.ProjectId == null)
                {
                    entry.ProviderId = ModrinthProvider.ProviderId;
                    entry.ProjectId = match.Project.Id;
                    entry.VersionId = match.Version.Id;
                }

                // Elle atılan ama Modrinth'te bulunan dosya artık onarılabilir hale gelir.
                if (entry.Urls.Count == 0)
                {
                    var file = match.Version.Files.FirstOrDefault(f => string.Equals(f.Sha1, entry.Sha1, StringComparison.OrdinalIgnoreCase));
                    if (file?.Url != null)
                    {
                        entry.Urls.Add(file.Url);
                        entry.Sha512 ??= file.Sha512;
                    }
                }
            }
            return true;
        }

        private static string? TryHash(string path)
        {
            try { return Hashing.Sha1OfFile(path); }
            catch (IOException) { return null; }
        }
    }
}
