using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Content.Ftb
{
    // Feed The Beast'in açık API'si. Hem FTB'nin kendi paketlerini (StoneBlock, Skies, Revelation...)
    // hem de CurseForge modpack'lerini (All the Mods, RLCraft...) API anahtarı olmadan sunar;
    // tüm dosyalar indirme adresi ve hash'iyle gelir.
    public sealed class FtbProvider : IModpackProvider
    {
        public const string ProviderId = "ftb";
        private const string Api = "https://api.feed-the-beast.com/v1/modpacks/public";
        private const int MaxSearchResults = 50;

        private readonly HttpClient _http;
        private readonly DownloadManager _downloads;
        private readonly ConcurrentDictionary<string, PackDto> _packCache = new();
        private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _searchCache = new();

        public FtbProvider(HttpClient http, DownloadManager downloads)
        {
            _http = http;
            _downloads = downloads;
        }

        public string Id => ProviderId;
        public string DisplayName => "FTB ve CurseForge Paketleri";
        public bool IsAvailable => true;
        public IReadOnlyList<ContentKind> SupportedKinds { get; } = new[] { ContentKind.Modpack };

        public async Task<SearchPage> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            if (request.Kind != ContentKind.Modpack) return new SearchPage(Array.Empty<ContentItem>(), 0, request.Offset);

            var keys = await GetSearchKeysAsync(request.Query, ct);
            var pageKeys = keys.Skip(request.Offset).Take(request.Limit).ToList();

            var items = new ContentItem?[pageKeys.Count];
            using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(pageKeys.Select(async (key, i) =>
            {
                await gate.WaitAsync(ct);
                try { items[i] = await GetProjectAsync(key, ContentKind.Modpack, ct); }
                catch { /* gizli/silinmiş paketler listeyi bozmasın */ }
                finally { gate.Release(); }
            }));

            var list = items.Where(i => i != null).Cast<ContentItem>()
                .Where(i => string.IsNullOrEmpty(request.GameVersion) || i.Tags.Contains(request.GameVersion))
                .ToList();
            return new SearchPage(list, keys.Count, request.Offset);
        }

        // Kimlik biçimi: "ftb-35" (FTB paketi) veya "cf-925200" (CurseForge paketi).
        public async Task<ContentItem?> GetProjectAsync(string projectId, ContentKind kindHint, CancellationToken ct = default)
        {
            var pack = await GetPackAsync(projectId, ct);
            if (pack == null) return null;

            var art = pack.Art ?? new List<ArtDto>();
            string? icon = art.FirstOrDefault(a => a.Type == "square")?.Url ?? art.FirstOrDefault(a => a.Type == "logo")?.Url;

            // Arayüzde sürüm rozeti gösterebilmek ve sürüme göre süzebilmek için en güncel
            // sürümün Minecraft sürümünü etiketlere ekliyoruz.
            var latest = pack.Versions?.OrderByDescending(v => v.Updated).FirstOrDefault();
            string? mc = latest?.Targets?.FirstOrDefault(t => t.Type == "game")?.Version;
            var tags = (pack.Tags ?? new List<TagDto>()).Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
            if (!string.IsNullOrEmpty(mc)) tags.Insert(0, mc);

            bool isCurse = projectId.StartsWith("cf-", StringComparison.Ordinal);
            return new ContentItem
            {
                ProviderId = Id,
                Id = projectId,
                Title = pack.Name,
                Description = pack.Synopsis ?? "",
                Author = pack.Authors?.FirstOrDefault()?.Name ?? (isCurse ? "CurseForge" : "FTB"),
                IconUrl = icon,
                BannerUrl = art.FirstOrDefault(a => a.Type == "splash")?.Url ?? art.FirstOrDefault(a => a.Type == "screenshot")?.Url,
                Downloads = pack.Installs,
                Kind = ContentKind.Modpack,
                Tags = tags,
                WebUrl = isCurse ? null : $"https://www.feed-the-beast.com/modpacks/{pack.Id}-{pack.Slug}"
            };
        }

        public async Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(ContentItem item, string? gameVersion, ModLoader? loader, CancellationToken ct = default)
        {
            var pack = await GetPackAsync(item.Id, ct);
            if (pack?.Versions == null) return Array.Empty<ContentVersion>();

            return pack.Versions
                .Select(v => new ContentVersion
                {
                    Id = v.Id.ToString(),
                    ProjectId = item.Id,
                    Name = v.Name ?? v.Id.ToString(),
                    Channel = ParseChannel(v.Type),
                    Published = DateTimeOffset.FromUnixTimeSeconds(v.Updated),
                    GameVersions = (v.Targets ?? new List<TargetDto>()).Where(t => t.Type == "game").Select(t => t.Version).ToList(),
                    Loaders = (v.Targets ?? new List<TargetDto>()).Where(t => t.Type == "modloader").Select(t => t.Name.ToLowerInvariant()).ToList(),
                    // FTB paketlerinin dosyaları sürüm detayında gelir; seçim yapılabilsin diye yer tutucu bir dosya koyuyoruz.
                    Files = new[] { new ContentFile { FileName = "ftb-manifest", Primary = true } }
                })
                .OrderByDescending(v => v.Published)
                .ToList();
        }

        public Task<ContentVersion?> GetVersionByIdAsync(string versionId, CancellationToken ct = default)
            => Task.FromResult<ContentVersion?>(null);

        public async Task<ModpackPlan> ResolveModpackAsync(ContentItem pack, ContentVersion version, string workDir,
            IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            progress?.Report(ProgressInfo.Step($"{pack.Title} paket bilgisi alınıyor..."));
            var detail = await _http.GetFromJsonAsync<VersionDetailDto>($"{PackUrl(pack.Id)}/{version.Id}", ct)
                         ?? throw new InvalidOperationException("Paket sürüm bilgisi alınamadı.");

            var targets = detail.Targets ?? new List<TargetDto>();
            string gameVersion = targets.FirstOrDefault(t => t.Type == "game")?.Version
                                 ?? throw new InvalidDataException("Paket Minecraft sürümünü belirtmiyor.");
            var loaderTarget = targets.FirstOrDefault(t => t.Type == "modloader");
            var loader = ModLoaderExtensions.Parse(loaderTarget?.Name) ?? ModLoader.Vanilla;

            var files = new List<PlannedFile>();
            var overrides = new List<OverrideArchive>();

            foreach (var f in detail.Files ?? new List<FileDto>())
            {
                if (f.ServerOnly) continue;

                // CurseForge paketleri: config gibi dosyalar paketin kendi zip'inin "overrides" klasöründe gelir.
                if (f.Type == "cf-extract")
                {
                    string zipPath = Path.Combine(workDir, $"overrides-{overrides.Count}.zip");
                    progress?.Report(ProgressInfo.Step($"{pack.Title} ayar dosyaları indiriliyor..."));
                    await _downloads.DownloadAsync(new DownloadItem(zipPath, AllUrls(f), f.Sha1, null, f.Size, "overrides"), ct);
                    overrides.Add(new OverrideArchive(zipPath, new[] { ReadCurseOverridesFolder(zipPath) + "/" }));
                    continue;
                }

                var urls = AllUrls(f);
                if (urls.Count == 0 || string.IsNullOrEmpty(f.Name)) continue;

                files.Add(new PlannedFile
                {
                    RelativePath = SafePath.NormalizeRelative(Path.Combine(f.Path ?? "", f.Name).Replace('\\', '/')),
                    Urls = urls,
                    Sha1 = string.IsNullOrEmpty(f.Sha1) ? null : f.Sha1,
                    Size = f.Size,
                    Title = f.Type == "mod" && !string.IsNullOrEmpty(f.Version) && !char.IsDigit(f.Version[0]) ? f.Version : null
                });
            }

            int? recommendedRam = detail.Specs?.Recommended > 0 ? detail.Specs.Recommended : null;

            return new ModpackPlan
            {
                Name = pack.Title,
                GameVersion = gameVersion,
                Loader = loader,
                LoaderVersion = string.IsNullOrEmpty(loaderTarget?.Version) || !char.IsDigit(loaderTarget.Version[0]) ? null : loaderTarget.Version,
                IconUrl = pack.IconUrl,
                RecommendedRamMb = recommendedRam,
                Source = new ProfileSource(Id, pack.Id, version.Id, version.Name),
                Files = files,
                Overrides = overrides
            };
        }

        private async Task<IReadOnlyList<string>> GetSearchKeysAsync(string query, CancellationToken ct)
        {
            string term = query.Trim();
            if (_searchCache.TryGetValue(term, out var cached)) return cached;

            SearchDto? result = string.IsNullOrEmpty(term)
                ? await _http.GetFromJsonAsync<SearchDto>($"{Api}/modpack/popular/installs/FTB/{MaxSearchResults}", ct)
                : await _http.GetFromJsonAsync<SearchDto>($"{Api}/modpack/search/{MaxSearchResults}?term={Uri.EscapeDataString(term)}", ct);

            var keys = new List<string>();
            keys.AddRange((result?.Packs ?? new List<long>()).Select(id => $"ftb-{id}"));
            keys.AddRange((result?.Curseforge ?? new List<long>()).Select(id => $"cf-{id}"));

            _searchCache[term] = keys;
            return keys;
        }

        private async Task<PackDto?> GetPackAsync(string projectId, CancellationToken ct)
        {
            if (_packCache.TryGetValue(projectId, out var cached)) return cached;

            var pack = await _http.GetFromJsonAsync<PackDto>(PackUrl(projectId), ct);
            if (pack == null || pack.Status != "success") return null;

            _packCache[projectId] = pack;
            return pack;
        }

        private static string PackUrl(string projectId)
        {
            if (projectId.StartsWith("ftb-", StringComparison.Ordinal)) return $"{Api}/modpack/{projectId.Substring(4)}";
            if (projectId.StartsWith("cf-", StringComparison.Ordinal)) return $"{Api}/curseforge/{projectId.Substring(3)}";
            throw new ArgumentException($"Tanınmayan FTB paket kimliği: {projectId}");
        }

        private static List<string> AllUrls(FileDto f)
        {
            var urls = new List<string>();
            if (!string.IsNullOrEmpty(f.Url)) urls.Add(f.Url);
            if (f.Mirrors != null) urls.AddRange(f.Mirrors.Where(m => !string.IsNullOrEmpty(m)));
            return urls;
        }

        private static ReleaseChannel ParseChannel(string? type) => type?.ToLowerInvariant() switch
        {
            "beta" => ReleaseChannel.Beta,
            "alpha" => ReleaseChannel.Alpha,
            _ => ReleaseChannel.Release
        };

        // CurseForge paket zip'indeki manifest.json, overrides klasörünün adını söyler (genelde "overrides").
        private static string ReadCurseOverridesFolder(string zipPath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var entry = archive.GetEntry("manifest.json");
                if (entry != null)
                {
                    using var stream = entry.Open();
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("overrides", out var o) && o.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(o.GetString()))
                        return o.GetString()!.Trim('/');
                }
            }
            catch (InvalidDataException) { }
            return "overrides";
        }

        // ---- FTB API cevap modelleri ----

        private sealed class SearchDto
        {
            [JsonPropertyName("packs")] public List<long>? Packs { get; set; }
            [JsonPropertyName("curseforge")] public List<long>? Curseforge { get; set; }
        }

        private sealed class PackDto
        {
            [JsonPropertyName("status")] public string? Status { get; set; }
            [JsonPropertyName("id")] public long Id { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("slug")] public string? Slug { get; set; }
            [JsonPropertyName("synopsis")] public string? Synopsis { get; set; }
            [JsonPropertyName("installs")] public long Installs { get; set; }
            [JsonPropertyName("tags")] public List<TagDto>? Tags { get; set; }
            [JsonPropertyName("art")] public List<ArtDto>? Art { get; set; }
            [JsonPropertyName("authors")] public List<AuthorDto>? Authors { get; set; }
            [JsonPropertyName("versions")] public List<VersionDto>? Versions { get; set; }
        }

        private sealed class TagDto { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
        private sealed class AuthorDto { [JsonPropertyName("name")] public string Name { get; set; } = ""; }

        private sealed class ArtDto
        {
            [JsonPropertyName("type")] public string? Type { get; set; }
            [JsonPropertyName("url")] public string? Url { get; set; }
        }

        private sealed class VersionDto
        {
            [JsonPropertyName("id")] public long Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("type")] public string? Type { get; set; }
            [JsonPropertyName("updated")] public long Updated { get; set; }
            [JsonPropertyName("targets")] public List<TargetDto>? Targets { get; set; }
        }

        private sealed class TargetDto
        {
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("version")] public string Version { get; set; } = "";
            [JsonPropertyName("type")] public string Type { get; set; } = "";
        }

        private sealed class VersionDetailDto
        {
            [JsonPropertyName("targets")] public List<TargetDto>? Targets { get; set; }
            [JsonPropertyName("specs")] public SpecsDto? Specs { get; set; }
            [JsonPropertyName("files")] public List<FileDto>? Files { get; set; }
        }

        private sealed class SpecsDto
        {
            [JsonPropertyName("minimum")] public int Minimum { get; set; }
            [JsonPropertyName("recommended")] public int Recommended { get; set; }
        }

        private sealed class FileDto
        {
            [JsonPropertyName("path")] public string? Path { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("version")] public JsonElement VersionRaw { get; set; }
            [JsonPropertyName("url")] public string? Url { get; set; }
            [JsonPropertyName("mirrors")] public List<string>? Mirrors { get; set; }
            [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
            [JsonPropertyName("size")] public long Size { get; set; }
            [JsonPropertyName("type")] public string? Type { get; set; }
            [JsonPropertyName("serveronly")] public bool ServerOnly { get; set; }

            // FTB "version" alanını bazen sayı bazen metin olarak gönderiyor.
            [JsonIgnore]
            public string? Version => VersionRaw.ValueKind switch
            {
                JsonValueKind.String => VersionRaw.GetString(),
                JsonValueKind.Number => VersionRaw.GetRawText(),
                _ => null
            };
        }
    }
}
