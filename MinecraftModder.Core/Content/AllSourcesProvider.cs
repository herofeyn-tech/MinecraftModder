namespace MinecraftModder.Core.Content
{
    // "Hepsi": kullanıcı hangi siteye bakacağını bilmek zorunda değil. Modrinth ve CurseForge'da aynı anda
    // arar, aynı modu bir kez gösterir. Sonuçlar kendi kaynaklarının kimliğini taşır; kurulum gerçek kaynaktan yapılır.
    public sealed class AllSourcesProvider : IContentProvider
    {
        public const string ProviderId = "all";

        private readonly Func<IEnumerable<IContentProvider>> _sources;

        public AllSourcesProvider(Func<IEnumerable<IContentProvider>> sources) => _sources = sources;

        public string Id => ProviderId;
        public string DisplayName => "Hepsi";
        public bool IsAvailable => true;
        public IReadOnlyList<ContentKind> SupportedKinds { get; } = new[] { ContentKind.Mod };

        public async Task<SearchPage> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            var sources = _sources().Where(p => p.SupportedKinds.Contains(request.Kind)).ToList();
            var pages = await Task.WhenAll(sources.Select(async p =>
            {
                try { return await p.SearchAsync(request, ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { return null; } // bir site çökerse diğeri yine gösterilsin
            }));

            var items = new List<ContentItem>();
            var seen = new HashSet<string>();
            foreach (var page in pages.Where(p => p != null))
            {
                foreach (var item in page!.Items)
                {
                    if (seen.Add(ContentMatching.Normalize(item.Title))) items.Add(item);
                }
            }

            bool hasMore = pages.Any(p => p?.HasMore == true);
            if (items.Count == 0 && pages.All(p => p == null))
                throw new HttpRequestException("Hiçbir mod sitesine ulaşılamadı.");
            return new SearchPage(items, request.Offset + items.Count + (hasMore ? 1 : 0), request.Offset);
        }

        public Task<ContentItem?> GetProjectAsync(string projectId, ContentKind kindHint, CancellationToken ct = default)
            => Task.FromResult<ContentItem?>(null);

        public Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(ContentItem item, string? gameVersion, ModLoader? loader, CancellationToken ct = default)
            => _sources().First(p => p.Id == item.ProviderId).GetVersionsAsync(item, gameVersion, loader, ct);

        public Task<ContentVersion?> GetVersionByIdAsync(string versionId, CancellationToken ct = default)
            => Task.FromResult<ContentVersion?>(null);
    }

    public static class ContentMatching
    {
        // "Sodium", "sodium (Fabric)", "Sodium!" → "sodium"
        public static string Normalize(string text)
        {
            string lower = text.ToLowerInvariant();
            foreach (string noise in new[] { "(fabric)", "(forge)", "(neoforge)", "(quilt)", "[fabric]", "[forge]" })
                lower = lower.Replace(noise, "");
            return new string(lower.Where(char.IsLetterOrDigit).ToArray());
        }

        // İki farklı sitedeki proje aynı mod mu? (isim ya da kısa ad eşleşmesi)
        public static bool SameProject(ContentItem a, ContentItem b)
            => Normalize(a.Title) == Normalize(b.Title)
               || (!string.IsNullOrEmpty(a.Slug) && !string.IsNullOrEmpty(b.Slug) && Normalize(a.Slug) == Normalize(b.Slug));
    }
}
