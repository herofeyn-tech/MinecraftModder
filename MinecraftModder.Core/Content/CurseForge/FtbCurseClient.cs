using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MinecraftModder.Core.Content.CurseForge
{
    public sealed record FingerprintMatch(string ProjectId, string FileId, string? Title, string? IconUrl, string? FileName);

    // CurseForge modlarına API anahtarı OLMADAN erişim: Feed The Beast'in açık API'si CurseForge'daki
    // modları (arama, sürümler, indirme adresi, sha1, bağımlılıklar) aynen sunuyor.
    //   v1 (api.feed-the-beast.com): tam sürüm listesi — kurulumda kullanılır (~350 KB sıkıştırılmış)
    //   modpacks.ch: son 50 sürümle hafif özet — arama kartları için (~6 KB)
    internal sealed class FtbCurseClient
    {
        private const string FullApi = "https://api.feed-the-beast.com/v1/modpacks/public";
        private const string LightApi = "https://api.modpacks.ch/public";
        private const int SearchSize = 50;
        private const int LightVersionLimit = 50; // modpacks.ch en fazla bu kadar sürüm döndürüyor

        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, LightModDto> _light = new();
        private readonly ConcurrentDictionary<string, FullModDto> _full = new();
        private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _search = new();

        public FtbCurseClient(HttpClient http) => _http = http;

        public async Task<SearchPage> SearchAsync(SearchRequest request, CancellationToken ct)
        {
            string term = request.Query.Trim();
            // FTB'nin "en popüler modlar" listesi yok; boş aramada Modrinth'in popüler listesi yeterli.
            if (request.Kind != ContentKind.Mod || term.Length == 0)
                return new SearchPage(Array.Empty<ContentItem>(), 0, request.Offset);

            if (!_search.TryGetValue(term, out var ids))
            {
                var result = await _http.GetFromJsonAsync<SearchDto>($"{FullApi}/mod/search/{SearchSize}?term={Uri.EscapeDataString(term)}", ct);
                ids = (result?.Mods ?? new List<long>()).Select(id => id.ToString()).ToList();
                _search[term] = ids;
            }

            var pageIds = ids.Skip(request.Offset).Take(request.Limit).ToList();
            var mods = new LightModDto?[pageIds.Count];
            using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(pageIds.Select(async (id, i) =>
            {
                await gate.WaitAsync(ct);
                try { mods[i] = await GetLightAsync(id, ct); }
                catch { /* silinmiş/gizli mod listeyi bozmasın */ }
                finally { gate.Release(); }
            }));

            var items = mods.Where(m => m != null && MightSupport(m, request.GameVersion, request.Loader)).Select(m => ToItem(m!)).ToList();
            return new SearchPage(items, ids.Count, request.Offset);
        }

        public async Task<ContentItem?> GetProjectAsync(string projectId, CancellationToken ct)
        {
            var mod = await GetLightAsync(projectId, ct);
            return mod == null ? null : ToItem(mod);
        }

        public async Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(ContentItem item, CancellationToken ct)
        {
            var mod = await GetFullAsync(item.Id, ct);
            if (mod?.Versions == null) return Array.Empty<ContentVersion>();

            return mod.Versions
                .Where(v => !v.ServerOnly && !string.IsNullOrEmpty(v.Name))
                .Select(v => new ContentVersion
                {
                    Id = v.Id.ToString(),
                    ProjectId = item.Id,
                    Name = v.Version ?? v.Name!,
                    Channel = v.Type switch { "beta" => ReleaseChannel.Beta, "alpha" => ReleaseChannel.Alpha, _ => ReleaseChannel.Release },
                    Published = DateTimeOffset.FromUnixTimeSeconds(v.Updated),
                    GameVersions = (v.Targets ?? new List<TargetDto>()).Where(t => t.Type == "game").Select(t => t.Version).ToList(),
                    Loaders = (v.Targets ?? new List<TargetDto>()).Where(t => t.Type == "modloader").Select(t => t.Name.ToLowerInvariant()).ToList(),
                    Files = new[]
                    {
                        new ContentFile
                        {
                            FileName = v.Name!,
                            Url = string.IsNullOrEmpty(v.Url) ? null : v.Url,
                            Mirrors = v.Mirrors ?? new List<string>(),
                            Sha1 = string.IsNullOrEmpty(v.Sha1) ? null : v.Sha1,
                            Size = v.Size,
                            Primary = true
                        }
                    },
                    Dependencies = (v.Dependencies ?? new List<DependencyDto>())
                        .Where(d => d.Required)
                        .Select(d => new ContentDependency(d.Id.ToString(), null, true))
                        .ToList()
                })
                .OrderByDescending(v => v.Published)
                .ToList();
        }

        // Elle atılmış bir jar'ı CurseForge parmak iziyle tanır. FTB bu adresi "eskidi" diye işaretliyor;
        // çalışmazsa sadece tanıma yapılamaz, başka hiçbir şey bozulmaz.
        public async Task<FingerprintMatch?> LookupFingerprintAsync(uint fingerprint, CancellationToken ct)
        {
            try
            {
                var result = await _http.GetFromJsonAsync<LookupDto>($"{FullApi}/mod/lookup/{fingerprint}", ct);
                var meta = result?.Meta;
                if (result?.Status != "success" || meta == null || meta.CurseProject <= 0) return null;
                return new FingerprintMatch(meta.CurseProject.ToString(), meta.CurseFile.ToString(), meta.Name, meta.Icon, meta.Filename);
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        private async Task<LightModDto?> GetLightAsync(string id, CancellationToken ct)
        {
            if (_light.TryGetValue(id, out var cached)) return cached;
            var mod = await _http.GetFromJsonAsync<LightModDto>($"{LightApi}/mod/{Uri.EscapeDataString(id)}", ct);
            if (mod == null || mod.Status != "success" || string.IsNullOrEmpty(mod.Name)) return null;
            return _light[id] = mod;
        }

        private async Task<FullModDto?> GetFullAsync(string id, CancellationToken ct)
        {
            if (_full.TryGetValue(id, out var cached)) return cached;
            var mod = await _http.GetFromJsonAsync<FullModDto>($"{FullApi}/mod/{Uri.EscapeDataString(id)}", ct);
            if (mod == null || mod.Status != "success") return null;
            return _full[id] = mod;
        }

        // Özet sadece son 50 sürümü içerir; orada yoksa ama liste kesilmişse eski sürümlerde olabilir, gösterilir.
        private static bool MightSupport(LightModDto mod, string? gameVersion, ModLoader? loader)
        {
            var versions = mod.Versions ?? new List<LightVersionDto>();
            if (versions.Count >= LightVersionLimit || versions.Count == 0) return true;
            var tags = loader is { } l && l.SupportsMods() ? l.CompatibleTags() : null;
            return versions.Any(v =>
            {
                var targets = v.Targets ?? new List<TargetDto>();
                bool gameOk = string.IsNullOrEmpty(gameVersion) || targets.Any(t => t.Type == "game" && t.Version == gameVersion);
                bool loaderOk = tags == null || targets.Any(t => t.Type == "modloader" && tags.Contains(t.Name.ToLowerInvariant()));
                return gameOk && loaderOk;
            });
        }

        private static ContentItem ToItem(LightModDto m)
        {
            var art = m.Art ?? new List<ArtDto>();
            return new ContentItem
            {
                ProviderId = CurseForgeProvider.ProviderId,
                Id = m.Id.ToString(),
                Title = m.Name!,
                Kind = ContentKind.Mod,
                Description = m.Synopsis ?? "",
                Author = m.Authors?.FirstOrDefault()?.Name,
                IconUrl = (art.FirstOrDefault(a => a.Type is "square" or "logo") ?? art.FirstOrDefault())?.Url,
                Downloads = m.Installs,
                Tags = (m.Tags ?? new List<TagDto>()).Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)).ToList(),
                WebUrl = m.Links?.FirstOrDefault()?.Link
            };
        }

        // ---- FTB cevap modelleri ----

        private sealed class SearchDto
        {
            [JsonPropertyName("mods")] public List<long>? Mods { get; set; }
        }

        private sealed class LightModDto
        {
            [JsonPropertyName("status")] public string? Status { get; set; }
            [JsonPropertyName("id")] public long Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("synopsis")] public string? Synopsis { get; set; }
            [JsonPropertyName("installs")] public long Installs { get; set; }
            [JsonPropertyName("art")] public List<ArtDto>? Art { get; set; }
            [JsonPropertyName("links")] public List<LinkDto>? Links { get; set; }
            [JsonPropertyName("authors")] public List<AuthorDto>? Authors { get; set; }
            [JsonPropertyName("tags")] public List<TagDto>? Tags { get; set; }
            [JsonPropertyName("versions")] public List<LightVersionDto>? Versions { get; set; }
        }

        private sealed class LightVersionDto
        {
            [JsonPropertyName("targets")] public List<TargetDto>? Targets { get; set; }
        }

        private sealed class FullModDto
        {
            [JsonPropertyName("status")] public string? Status { get; set; }
            [JsonPropertyName("versions")] public List<FullVersionDto>? Versions { get; set; }
        }

        private sealed class FullVersionDto
        {
            [JsonPropertyName("id")] public long Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("type")] public string? Type { get; set; }
            [JsonPropertyName("version")] public string? Version { get; set; }
            [JsonPropertyName("updated")] public long Updated { get; set; }
            [JsonPropertyName("url")] public string? Url { get; set; }
            [JsonPropertyName("mirrors")] public List<string>? Mirrors { get; set; }
            [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
            [JsonPropertyName("size")] public long Size { get; set; }
            [JsonPropertyName("serveronly")] public bool ServerOnly { get; set; }
            [JsonPropertyName("targets")] public List<TargetDto>? Targets { get; set; }
            [JsonPropertyName("dependencies")] public List<DependencyDto>? Dependencies { get; set; }
        }

        private sealed class TargetDto
        {
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("type")] public string Type { get; set; } = "";
            [JsonPropertyName("version")] public string Version { get; set; } = "";
        }

        private sealed class DependencyDto
        {
            [JsonPropertyName("id")] public long Id { get; set; }
            [JsonPropertyName("required")] public bool Required { get; set; }
        }

        private sealed class ArtDto
        {
            [JsonPropertyName("url")] public string? Url { get; set; }
            [JsonPropertyName("type")] public string? Type { get; set; }
        }

        private sealed class LinkDto { [JsonPropertyName("link")] public string? Link { get; set; } }
        private sealed class AuthorDto { [JsonPropertyName("name")] public string? Name { get; set; } }
        private sealed class TagDto { [JsonPropertyName("name")] public string Name { get; set; } = ""; }

        private sealed class LookupDto
        {
            [JsonPropertyName("status")] public string? Status { get; set; }
            [JsonPropertyName("meta")] public LookupMetaDto? Meta { get; set; }
        }

        private sealed class LookupMetaDto
        {
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("icon")] public string? Icon { get; set; }
            [JsonPropertyName("curseProject")] public long CurseProject { get; set; }
            [JsonPropertyName("curseFile")] public long CurseFile { get; set; }
            [JsonPropertyName("filename")] public string? Filename { get; set; }
        }
    }
}
