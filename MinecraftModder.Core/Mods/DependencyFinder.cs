using MinecraftModder.Core.Content;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Mods
{
    // Bir modun jar'ında yazan "şu modu istiyorum" kimliğinden (ör. "cloth-config", "architectury")
    // o modu Modrinth'te ya da CurseForge'da bulup diske uygun sürümünü kurar.
    // Kurulan jar'ın gerçekten o kimliği sağladığı kontrol edilir; yanlış mod kurulmuşsa geri alınır.
    public sealed class DependencyFinder
    {
        // Mod kimliği ile Modrinth'teki kısa adı farklı olan bilinen kütüphaneler.
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["architectury"] = "architectury-api",
            ["kotlinforforge"] = "kotlin-for-forge",
            ["forgeconfigapiport"] = "forge-config-api-port",
            ["yet_another_config_lib_v3"] = "yacl",
            ["yet-another-config-lib"] = "yacl",
            ["yacl"] = "yacl",
            ["owo"] = "owo-lib",
            ["puzzleslib"] = "puzzles-lib",
            ["resourcefullib"] = "resourceful-lib",
            ["resourcefulconfig"] = "resourceful-config",
            ["bookshelf"] = "bookshelf-lib",
            ["supermartijn642corelib"] = "supermartijn642s-core-lib",
            ["supermartijn642configlib"] = "supermartijn642s-config-lib",
            ["cloth_config"] = "cloth-config",
            ["cloth-config2"] = "cloth-config",
            ["cardinal-components-base"] = "cardinal-components-api",
            ["cardinal-components-entity"] = "cardinal-components-api",
            ["fzzy_config"] = "fzzy-config",
            ["cristellib"] = "cristel-lib",
            ["prism"] = "prism-lib",
            ["ftblibrary"] = "ftb-library",
            ["ftbteams"] = "ftb-teams",
            ["ftbquests"] = "ftb-quests",
            ["athena"] = "athena-ctm",
            ["mixinsquared"] = "mixinsquared",
        };

        private readonly ContentHub _hub;
        private readonly ContentInstaller _installer;

        public DependencyFinder(ContentHub hub, ContentInstaller installer)
        {
            _hub = hub;
            _installer = installer;
        }

        // Bulup kurabilirse kurulan modun adını, bulamazsa null döner.
        public async Task<string?> InstallByModIdAsync(GameProfile profile, string modId, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            string wanted = CanonicalId(modId);
            progress?.Report(ProgressInfo.Step($"Gereken '{wanted}' modu aranıyor..."));

            foreach (var item in await CandidatesAsync(profile, wanted, ct))
            {
                var before = profile.Content.Select(e => e.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                try
                {
                    await _installer.InstallAsync(profile, item, progress, ct, ContentOrigin.Dependency);
                }
                catch (HttpRequestException)
                {
                    continue;
                }

                // Artık bu kimliği sağlayan açık bir mod var mı? (yeni kurulan ya da zaten olan)
                if (IsProvided(profile, modId) || IsProvided(profile, wanted)) return item.Title;

                // Yanlış mod indi: geri al ve sıradaki adaya geç.
                foreach (var added in profile.Content.Where(e => !before.Contains(e.RelativePath)).ToList())
                {
                    try { File.Delete(added.GetFullPath(profile)); } catch (IOException) { }
                    profile.Content.Remove(added);
                }
            }
            return null;
        }

        // Fabric API'nin modülleri ("fabric-lifecycle-events-v1" gibi) tek tek değil, Fabric API olarak kurulur.
        internal static string CanonicalId(string modId)
        {
            string id = modId.Trim().ToLowerInvariant();
            if (id == "fabric") return "fabric-api";
            if (id.StartsWith("fabric-") && !id.StartsWith("fabric-language-") && id != "fabric-api") return "fabric-api";
            if (id.StartsWith("quilted_fabric_api") || id.StartsWith("qsl")) return "qsl";
            return id;
        }

        private static bool IsProvided(GameProfile profile, string modId)
            => profile.Content.Any(e => e.Kind == ContentKind.Mod && e.Enabled
                                        && JarInspector.Inspect(e.GetFullPath(profile))?.ProvidedIds.Contains(modId) == true);

        private async Task<List<ContentItem>> CandidatesAsync(GameProfile profile, string id, CancellationToken ct)
        {
            var result = new List<ContentItem>();
            void Add(ContentItem? item)
            {
                if (item != null && item.Kind == ContentKind.Mod && result.All(r => r.Key != item.Key) && result.Count < 6) result.Add(item);
            }

            // 1) Kimlik çoğu zaman Modrinth'teki kısa adın aynısıdır.
            var slugs = new[] { Aliases.GetValueOrDefault(id), id, id.Replace('_', '-') }.OfType<string>().Where(s => s.Length > 0).Distinct();
            foreach (string slug in slugs)
            {
                try { Add(await _hub.Modrinth.GetProjectAsync(slug, ContentKind.Mod, ct)); }
                catch (HttpRequestException) { }
            }

            // 2) Değilse iki sitede de isimle ara.
            var request = new SearchRequest
            {
                Query = id.Replace('_', ' ').Replace('-', ' '),
                Kind = ContentKind.Mod,
                GameVersion = profile.GameVersion,
                Loader = profile.Loader,
                Limit = 5
            };
            foreach (var provider in new IContentProvider[] { _hub.Modrinth, _hub.CurseForge })
            {
                try
                {
                    var page = await provider.SearchAsync(request, ct);
                    foreach (var item in page.Items.Where(i => LooksLike(i, id))) Add(item);
                }
                catch (HttpRequestException) { }
            }
            return result;
        }

        private static bool LooksLike(ContentItem item, string id)
        {
            string wanted = ContentMatching.Normalize(id);
            if (wanted.Length == 0) return false;
            foreach (string? name in new[] { item.Slug, item.Title })
            {
                if (string.IsNullOrEmpty(name)) continue;
                string n = ContentMatching.Normalize(name);
                if (n == wanted || n.StartsWith(wanted) || (n.Length >= 4 && wanted.StartsWith(n))) return true;
            }
            return false;
        }
    }
}
