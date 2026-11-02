using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Content.Modrinth
{
    // https://docs.modrinth.com/api/ — anahtar gerektirmez.
    public sealed class ModrinthProvider : IModpackProvider
    {
        public const string ProviderId = "modrinth";
        public const string FabricApiProjectId = "P7dR8mSH";
        public const string IrisProjectId = "YL57xq9U";

        private const string Api = "https://api.modrinth.com/v2";
        private readonly HttpClient _http;
        private readonly DownloadManager _downloads;

        public ModrinthProvider(HttpClient http, DownloadManager downloads)
        {
            _http = http;
            _downloads = downloads;
        }

        public string Id => ProviderId;
        public string DisplayName => "Modrinth";
        public bool IsAvailable => true;
        public IReadOnlyList<ContentKind> SupportedKinds { get; } =
            new[] { ContentKind.Mod, ContentKind.Modpack, ContentKind.ShaderPack, ContentKind.ResourcePack };

        public async Task<SearchPage> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            var facets = new List<string[]> { new[] { $"project_type:{ProjectType(request.Kind)}" } };

            if (!string.IsNullOrEmpty(request.GameVersion))
                facets.Add(new[] { $"versions:{request.GameVersion}" });

            if (request.Loader is { } loader && loader.SupportsMods() && request.Kind is ContentKind.Mod or ContentKind.Modpack)
                facets.Add(loader.CompatibleTags().Select(t => $"categories:{t}").ToArray());

            // Arama kutusu boşsa en popülerler gösterilir (keşfet hissi).
            string index = string.IsNullOrWhiteSpace(request.Query) ? "downloads" : "relevance";
            string url = $"{Api}/search?query={Uri.EscapeDataString(request.Query)}&index={index}" +
                         $"&offset={request.Offset}&limit={Math.Clamp(request.Limit, 1, 100)}" +
                         $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";

            var result = await _http.GetFromJsonAsync<SearchResponse>(url, ct) ?? new SearchResponse();
            var items = result.Hits.Select(h => new ContentItem
            {
                ProviderId = Id,
                Id = h.ProjectId,
                Slug = h.Slug,
                Title = h.Title,
                Description = h.Description ?? "",
                Author = h.Author,
                IconUrl = string.IsNullOrEmpty(h.IconUrl) ? null : h.IconUrl,
                BannerUrl = h.FeaturedGallery ?? h.Gallery?.FirstOrDefault(),
                Downloads = h.Downloads,
                Kind = KindFromProjectType(h.ProjectType) ?? request.Kind,
                Tags = h.DisplayCategories ?? h.Categories ?? new List<string>(),
                WebUrl = $"https://modrinth.com/{h.ProjectType}/{h.Slug}"
            }).ToList();

            return new SearchPage(items, result.TotalHits, result.Offset);
        }

        public async Task<ContentItem?> GetProjectAsync(string projectId, ContentKind kindHint, CancellationToken ct = default)
        {
            try
            {
                var p = await _http.GetFromJsonAsync<ProjectDto>($"{Api}/project/{Uri.EscapeDataString(projectId)}", ct);
                return p == null ? null : ToItem(p, kindHint);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(ContentItem item, string? gameVersion, ModLoader? loader, CancellationToken ct = default)
        {
            var query = new List<string> { "include_changelog=false" };

            // Loader filtresi sadece modlar için anlamlı: shader'lar "iris/optifine", kaynak paketleri "minecraft" etiketi taşır.
            if (item.Kind == ContentKind.Mod && loader is { } l && l.SupportsMods())
                query.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(l.CompatibleTags())));

            if (!string.IsNullOrEmpty(gameVersion) && item.Kind != ContentKind.Modpack)
                query.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion })));

            string url = $"{Api}/project/{Uri.EscapeDataString(item.Id)}/version?{string.Join("&", query)}";
            var versions = await _http.GetFromJsonAsync<List<VersionDto>>(url, ct) ?? new List<VersionDto>();
            return versions.Select(ToVersion).OrderByDescending(v => v.Published).ToList();
        }

        public async Task<ContentVersion?> GetVersionByIdAsync(string versionId, CancellationToken ct = default)
        {
            var v = await _http.GetFromJsonAsync<VersionDto>($"{Api}/version/{Uri.EscapeDataString(versionId)}", ct);
            return v == null ? null : ToVersion(v);
        }

        public async Task<ModpackPlan> ResolveModpackAsync(ContentItem pack, ContentVersion version, string workDir,
            IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            var file = VersionPicker.PickFile(version, ContentKind.Modpack)
                ?? throw new InvalidOperationException($"'{pack.Title}' için indirilebilir paket dosyası bulunamadı.");

            progress?.Report(ProgressInfo.Step($"{pack.Title} paket bilgisi indiriliyor..."));
            string mrpackPath = Path.Combine(workDir, "pack.mrpack");
            await _downloads.DownloadAsync(new DownloadItem(mrpackPath, file.AllUrls(), file.Sha1, file.Sha512, file.Size, pack.Title), ct);

            var source = new ProfileSource(Id, pack.Id, version.Id, version.Name);
            return MrpackReader.ReadPlan(mrpackPath, source, pack.IconUrl, pack.Title);
        }

        // Diskteki dosyaları SHA1 ile Modrinth'te tanır (kullanıcının elle attığı modlar ve paket modları için
        // isim + ikon bulmaya yarar). Tanınmayan hash'ler sonuçta yer almaz.
        public async Task<Dictionary<string, (ContentItem Project, ContentVersion Version)>> IdentifyBySha1Async(
            IReadOnlyCollection<string> sha1Hashes, CancellationToken ct = default)
        {
            var result = new Dictionary<string, (ContentItem, ContentVersion)>(StringComparer.OrdinalIgnoreCase);
            if (sha1Hashes.Count == 0) return result;

            foreach (var chunk in sha1Hashes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(500))
            {
                using var response = await _http.PostAsJsonAsync($"{Api}/version_files", new { hashes = chunk, algorithm = "sha1" }, ct);
                if (!response.IsSuccessStatusCode) continue;

                var map = await response.Content.ReadFromJsonAsync<Dictionary<string, VersionDto>>(cancellationToken: ct);
                if (map == null || map.Count == 0) continue;

                var projectIds = map.Values.Select(v => v.ProjectId).Distinct().ToList();
                string idsJson = JsonSerializer.Serialize(projectIds);
                var projects = await _http.GetFromJsonAsync<List<ProjectDto>>($"{Api}/projects?ids={Uri.EscapeDataString(idsJson)}", ct)
                               ?? new List<ProjectDto>();
                var byId = projects.ToDictionary(p => p.Id);

                foreach (var (hash, version) in map)
                {
                    if (byId.TryGetValue(version.ProjectId, out var project))
                        result[hash] = (ToItem(project, ContentKind.Mod), ToVersion(version));
                }
            }
            return result;
        }

        private ContentItem ToItem(ProjectDto p, ContentKind kindHint) => new()
        {
            ProviderId = Id,
            Id = p.Id,
            Slug = p.Slug,
            Title = p.Title,
            Description = p.Description ?? "",
            IconUrl = string.IsNullOrEmpty(p.IconUrl) ? null : p.IconUrl,
            BannerUrl = (p.Gallery?.FirstOrDefault(g => g.Featured) ?? p.Gallery?.FirstOrDefault())?.Url,
            Downloads = p.Downloads,
            Kind = KindFromProjectType(p.ProjectType) ?? kindHint,
            Tags = p.Categories ?? new List<string>(),
            WebUrl = $"https://modrinth.com/{p.ProjectType}/{p.Slug}"
        };

        private static ContentVersion ToVersion(VersionDto v) => new()
        {
            Id = v.Id,
            ProjectId = v.ProjectId,
            Name = string.IsNullOrEmpty(v.VersionNumber) ? v.Name ?? v.Id : v.VersionNumber,
            Channel = v.VersionType switch { "beta" => ReleaseChannel.Beta, "alpha" => ReleaseChannel.Alpha, _ => ReleaseChannel.Release },
            Published = v.DatePublished,
            GameVersions = v.GameVersions ?? new List<string>(),
            Loaders = v.Loaders ?? new List<string>(),
            Files = (v.Files ?? new List<FileDto>()).Select(f => new ContentFile
            {
                FileName = f.Filename,
                Url = f.Url,
                Sha1 = f.Hashes?.GetValueOrDefault("sha1"),
                Sha512 = f.Hashes?.GetValueOrDefault("sha512"),
                Size = f.Size,
                Primary = f.Primary
            }).ToList(),
            Dependencies = (v.Dependencies ?? new List<DependencyDto>())
                .Where(d => d.DependencyType is "required" or "optional")
                .Select(d => new ContentDependency(d.ProjectId, d.VersionId, d.DependencyType == "required"))
                .ToList()
        };

        private static string ProjectType(ContentKind kind) => kind switch
        {
            ContentKind.Modpack => "modpack",
            ContentKind.ShaderPack => "shader",
            ContentKind.ResourcePack => "resourcepack",
            _ => "mod"
        };

        private static ContentKind? KindFromProjectType(string? type) => type switch
        {
            "mod" => ContentKind.Mod,
            "modpack" => ContentKind.Modpack,
            "shader" => ContentKind.ShaderPack,
            "resourcepack" => ContentKind.ResourcePack,
            _ => null
        };

        // ---- Modrinth API cevap modelleri ----

        private sealed class SearchResponse
        {
            [JsonPropertyName("hits")] public List<SearchHit> Hits { get; set; } = new();
            [JsonPropertyName("offset")] public int Offset { get; set; }
            [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
        }

        private sealed class SearchHit
        {
            [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
            [JsonPropertyName("slug")] public string? Slug { get; set; }
            [JsonPropertyName("title")] public string Title { get; set; } = "";
            [JsonPropertyName("description")] public string? Description { get; set; }
            [JsonPropertyName("author")] public string? Author { get; set; }
            [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
            [JsonPropertyName("downloads")] public long Downloads { get; set; }
            [JsonPropertyName("project_type")] public string? ProjectType { get; set; }
            [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
            [JsonPropertyName("display_categories")] public List<string>? DisplayCategories { get; set; }
            [JsonPropertyName("featured_gallery")] public string? FeaturedGallery { get; set; }
            [JsonPropertyName("gallery")] public List<string>? Gallery { get; set; }
        }

        private sealed class ProjectDto
        {
            [JsonPropertyName("id")] public string Id { get; set; } = "";
            [JsonPropertyName("slug")] public string? Slug { get; set; }
            [JsonPropertyName("title")] public string Title { get; set; } = "";
            [JsonPropertyName("description")] public string? Description { get; set; }
            [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
            [JsonPropertyName("downloads")] public long Downloads { get; set; }
            [JsonPropertyName("project_type")] public string? ProjectType { get; set; }
            [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
            [JsonPropertyName("gallery")] public List<GalleryDto>? Gallery { get; set; }
        }

        private sealed class GalleryDto
        {
            [JsonPropertyName("url")] public string? Url { get; set; }
            [JsonPropertyName("featured")] public bool Featured { get; set; }
        }

        private sealed class VersionDto
        {
            [JsonPropertyName("id")] public string Id { get; set; } = "";
            [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("version_number")] public string? VersionNumber { get; set; }
            [JsonPropertyName("version_type")] public string? VersionType { get; set; }
            [JsonPropertyName("date_published")] public DateTimeOffset DatePublished { get; set; }
            [JsonPropertyName("game_versions")] public List<string>? GameVersions { get; set; }
            [JsonPropertyName("loaders")] public List<string>? Loaders { get; set; }
            [JsonPropertyName("files")] public List<FileDto>? Files { get; set; }
            [JsonPropertyName("dependencies")] public List<DependencyDto>? Dependencies { get; set; }
        }

        private sealed class FileDto
        {
            [JsonPropertyName("url")] public string Url { get; set; } = "";
            [JsonPropertyName("filename")] public string Filename { get; set; } = "";
            [JsonPropertyName("primary")] public bool Primary { get; set; }
            [JsonPropertyName("size")] public long Size { get; set; }
            [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
        }

        private sealed class DependencyDto
        {
            [JsonPropertyName("version_id")] public string? VersionId { get; set; }
            [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
            [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "";
        }
    }
}
