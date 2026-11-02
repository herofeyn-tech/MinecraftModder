using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MinecraftModder.Core.Settings;

namespace MinecraftModder.Core.Content.CurseForge
{
    // CurseForge modları. İki yoldan çalışır:
    //  - Ayarlar'da API anahtarı varsa CurseForge'un resmi API'si (mod + shader + kaynak paketi)
    //  - Anahtar yoksa Feed The Beast'in açık API'si üzerinden (sadece modlar; arama, sürümler, indirme)
    // Hangi yol kullanılırsa kullanılsın kimlikler aynı (CurseForge proje/dosya numaraları), böylece
    // kurulu modlar anahtar eklenip çıkarılınca karışmaz.
    // Not: CurseForge MODPACK'leri her zaman FtbProvider üzerinden kurulur.
    public sealed class CurseForgeProvider : IContentProvider
    {
        public const string ProviderId = "curseforge";
        private const string Api = "https://api.curseforge.com/v1";
        private const int MinecraftGameId = 432;

        private static readonly ContentKind[] KeyKinds = { ContentKind.Mod, ContentKind.ShaderPack, ContentKind.ResourcePack };
        private static readonly ContentKind[] KeylessKinds = { ContentKind.Mod };

        private readonly HttpClient _http;
        private readonly SettingsStore _settings;
        private readonly FtbCurseClient _ftb;

        public CurseForgeProvider(HttpClient http, SettingsStore settings)
        {
            _http = http;
            _settings = settings;
            _ftb = new FtbCurseClient(http);
        }

        public string Id => ProviderId;
        public string DisplayName => "CurseForge";
        public bool IsAvailable => true;
        public IReadOnlyList<ContentKind> SupportedKinds => HasApiKey ? KeyKinds : KeylessKinds;

        private bool HasApiKey => !string.IsNullOrWhiteSpace(_settings.Current.CurseForgeApiKey);

        // Elle atılmış bir jar'ı CurseForge parmak iziyle tanır (anahtar gerekmez).
        public Task<FingerprintMatch?> LookupFingerprintAsync(uint fingerprint, CancellationToken ct = default)
            => _ftb.LookupFingerprintAsync(fingerprint, ct);

        public async Task<SearchPage> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            if (!HasApiKey) return await _ftb.SearchAsync(request, ct);

            int? classId = ClassId(request.Kind);
            if (classId == null) return new SearchPage(Array.Empty<ContentItem>(), 0, request.Offset);

            // CurseForge index + pageSize toplamı 10.000'i geçemez.
            int offset = Math.Min(request.Offset, 9950);
            int pageSize = Math.Clamp(request.Limit, 1, 50);
            var query = new List<string>
            {
                $"gameId={MinecraftGameId}",
                $"classId={classId}",
                $"index={offset}",
                $"pageSize={pageSize}",
                "sortField=2",      // popülerlik
                "sortOrder=desc"
            };
            if (!string.IsNullOrWhiteSpace(request.Query)) query.Add("searchFilter=" + Uri.EscapeDataString(request.Query));
            if (!string.IsNullOrEmpty(request.GameVersion)) query.Add("gameVersion=" + Uri.EscapeDataString(request.GameVersion));
            if (request.Kind == ContentKind.Mod && request.Loader is { } l && LoaderType(l) is int lt) query.Add($"modLoaderType={lt}");

            var result = await SendAsync<ListResponse<ModDto>>($"{Api}/mods/search?{string.Join("&", query)}", ct);
            var items = (result?.Data ?? new List<ModDto>()).Select(m => ToItem(m, request.Kind)).ToList();
            return new SearchPage(items, Math.Min(result?.Pagination?.TotalCount ?? items.Count, 10000), offset);
        }

        public async Task<ContentItem?> GetProjectAsync(string projectId, ContentKind kindHint, CancellationToken ct = default)
        {
            if (!HasApiKey) return await _ftb.GetProjectAsync(projectId, ct);

            var result = await SendAsync<SingleResponse<ModDto>>($"{Api}/mods/{Uri.EscapeDataString(projectId)}", ct);
            return result?.Data == null ? null : ToItem(result.Data, KindFromClass(result.Data.ClassId) ?? kindHint);
        }

        public async Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(ContentItem item, string? gameVersion, ModLoader? loader, CancellationToken ct = default)
        {
            if (!HasApiKey) return await _ftb.GetVersionsAsync(item, ct);

            var query = new List<string> { "pageSize=50" };
            if (!string.IsNullOrEmpty(gameVersion)) query.Add("gameVersion=" + Uri.EscapeDataString(gameVersion));
            if (item.Kind == ContentKind.Mod && loader is { } l && LoaderType(l) is int lt) query.Add($"modLoaderType={lt}");

            var result = await SendAsync<ListResponse<FileDto>>($"{Api}/mods/{Uri.EscapeDataString(item.Id)}/files?{string.Join("&", query)}", ct);
            return (result?.Data ?? new List<FileDto>()).Where(f => f.IsAvailable).Select(ToVersion).OrderByDescending(v => v.Published).ToList();
        }

        public async Task<ContentVersion?> GetVersionByIdAsync(string versionId, CancellationToken ct = default)
        {
            // CurseForge'da dosya kimliği tek başına yeterli değil (mod kimliği de lazım); bağımlılıklar zaten modId ile gelir.
            await Task.CompletedTask;
            return null;
        }

        private async Task<T?> SendAsync<T>(string url, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-api-key", _settings.Current.CurseForgeApiKey!.Trim());
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException("CurseForge API anahtarı geçersiz. Ayarlar'dan kontrol et.");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }

        private ContentItem ToItem(ModDto m, ContentKind kind) => new()
        {
            ProviderId = Id,
            Id = m.Id.ToString(),
            Slug = m.Slug,
            Title = m.Name,
            Description = m.Summary ?? "",
            Author = m.Authors?.FirstOrDefault()?.Name,
            IconUrl = m.Logo?.ThumbnailUrl ?? m.Logo?.Url,
            BannerUrl = m.Screenshots?.FirstOrDefault()?.ThumbnailUrl,
            Downloads = (long)m.DownloadCount,
            Kind = kind,
            Tags = (m.Categories ?? new List<CategoryDto>()).Select(c => c.Name).ToList(),
            WebUrl = m.Links?.WebsiteUrl
        };

        private static ContentVersion ToVersion(FileDto f)
        {
            var loaderNames = new[] { "fabric", "quilt", "forge", "neoforge" };
            var gameVersions = f.GameVersions ?? new List<string>();

            return new ContentVersion
            {
                Id = f.Id.ToString(),
                ProjectId = f.ModId.ToString(),
                Name = f.DisplayName ?? f.FileName,
                Channel = f.ReleaseType switch { 2 => ReleaseChannel.Beta, 3 => ReleaseChannel.Alpha, _ => ReleaseChannel.Release },
                Published = f.FileDate,
                GameVersions = gameVersions.Where(v => v.Length > 0 && char.IsDigit(v[0])).ToList(),
                Loaders = gameVersions.Select(v => v.ToLowerInvariant()).Where(loaderNames.Contains).ToList(),
                Files = new[]
                {
                    new ContentFile
                    {
                        FileName = f.FileName,
                        // downloadUrl boşsa yapımcı launcher indirmesine izin vermiyor; kurulumda kullanıcıya söylenir.
                        Url = f.DownloadUrl,
                        Sha1 = f.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value,
                        Size = f.FileLength,
                        Primary = true
                    }
                },
                Dependencies = (f.Dependencies ?? new List<DependencyDto>())
                    .Where(d => d.RelationType == 3) // 3 = zorunlu bağımlılık
                    .Select(d => new ContentDependency(d.ModId.ToString(), null, true))
                    .ToList()
            };
        }

        private static int? ClassId(ContentKind kind) => kind switch
        {
            ContentKind.Mod => 6,
            ContentKind.ResourcePack => 12,
            ContentKind.ShaderPack => 6552,
            _ => null
        };

        private static ContentKind? KindFromClass(int? classId) => classId switch
        {
            6 => ContentKind.Mod,
            12 => ContentKind.ResourcePack,
            6552 => ContentKind.ShaderPack,
            4471 => ContentKind.Modpack,
            _ => null
        };

        private static int? LoaderType(ModLoader loader) => loader switch
        {
            ModLoader.Forge => 1,
            ModLoader.Fabric => 4,
            ModLoader.Quilt => 5,
            ModLoader.NeoForge => 6,
            _ => null
        };

        // ---- CurseForge API cevap modelleri ----

        private sealed class ListResponse<T>
        {
            [JsonPropertyName("data")] public List<T>? Data { get; set; }
            [JsonPropertyName("pagination")] public PaginationDto? Pagination { get; set; }
        }

        private sealed class SingleResponse<T>
        {
            [JsonPropertyName("data")] public T? Data { get; set; }
        }

        private sealed class PaginationDto
        {
            [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
        }

        private sealed class ModDto
        {
            [JsonPropertyName("id")] public long Id { get; set; }
            [JsonPropertyName("classId")] public int? ClassId { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("slug")] public string? Slug { get; set; }
            [JsonPropertyName("summary")] public string? Summary { get; set; }
            [JsonPropertyName("downloadCount")] public double DownloadCount { get; set; }
            [JsonPropertyName("logo")] public LogoDto? Logo { get; set; }
            [JsonPropertyName("authors")] public List<AuthorDto>? Authors { get; set; }
            [JsonPropertyName("categories")] public List<CategoryDto>? Categories { get; set; }
            [JsonPropertyName("links")] public LinksDto? Links { get; set; }
            [JsonPropertyName("screenshots")] public List<LogoDto>? Screenshots { get; set; }
        }

        private sealed class LogoDto
        {
            [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }
            [JsonPropertyName("url")] public string? Url { get; set; }
        }

        private sealed class AuthorDto { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
        private sealed class CategoryDto { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
        private sealed class LinksDto { [JsonPropertyName("websiteUrl")] public string? WebsiteUrl { get; set; } }

        private sealed class FileDto
        {
            [JsonPropertyName("id")] public long Id { get; set; }
            [JsonPropertyName("modId")] public long ModId { get; set; }
            [JsonPropertyName("isAvailable")] public bool IsAvailable { get; set; } = true;
            [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
            [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
            [JsonPropertyName("releaseType")] public int ReleaseType { get; set; }
            [JsonPropertyName("fileDate")] public DateTimeOffset FileDate { get; set; }
            [JsonPropertyName("fileLength")] public long FileLength { get; set; }
            [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
            [JsonPropertyName("hashes")] public List<HashDto>? Hashes { get; set; }
            [JsonPropertyName("gameVersions")] public List<string>? GameVersions { get; set; }
            [JsonPropertyName("dependencies")] public List<DependencyDto>? Dependencies { get; set; }
        }

        private sealed class HashDto
        {
            [JsonPropertyName("value")] public string Value { get; set; } = "";
            [JsonPropertyName("algo")] public int Algo { get; set; }
        }

        private sealed class DependencyDto
        {
            [JsonPropertyName("modId")] public long ModId { get; set; }
            [JsonPropertyName("relationType")] public int RelationType { get; set; }
        }
    }
}
