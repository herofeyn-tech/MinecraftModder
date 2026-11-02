using MinecraftModder.Core.Content.CurseForge;
using MinecraftModder.Core.Content.Ftb;
using MinecraftModder.Core.Content.Modrinth;

namespace MinecraftModder.Core.Content
{
    // Tüm içerik kaynaklarını bir arada tutar. Arayüz hangi kaynakları göstereceğini buradan öğrenir.
    public sealed class ContentHub
    {
        private readonly List<IContentProvider> _providers;

        public ContentHub(ModrinthProvider modrinth, FtbProvider ftb, CurseForgeProvider curseForge)
        {
            Modrinth = modrinth;
            Ftb = ftb;
            CurseForge = curseForge;
            AllSources = new AllSourcesProvider(() => new IContentProvider[] { modrinth, curseForge }.Where(p => p.IsAvailable));
            // "Hepsi" başta: modlarda varsayılan seçenek o olsun.
            _providers = new List<IContentProvider> { AllSources, modrinth, ftb, curseForge };
        }

        public ModrinthProvider Modrinth { get; }
        public FtbProvider Ftb { get; }
        public CurseForgeProvider CurseForge { get; }
        public AllSourcesProvider AllSources { get; }

        public IReadOnlyList<IContentProvider> All => _providers;

        public IEnumerable<IContentProvider> Available => _providers.Where(p => p.IsAvailable);

        public IEnumerable<IContentProvider> ForKind(ContentKind kind)
            => Available.Where(p => p.SupportedKinds.Contains(kind));

        public IContentProvider? Get(string? providerId)
            => _providers.FirstOrDefault(p => p.Id == providerId);

        // Seçilen sitede bu disk için uygun sürüm yoksa (ya da yapımcı indirmeye izin vermiyorsa)
        // aynı modu diğer sitede arar. Kullanıcı hangi siteden geldiğini bilmek zorunda değil.
        public async Task<(IContentProvider Provider, ContentItem Item, ContentVersion Version, ContentFile File)?> FindAlternativeAsync(
            ContentItem item, string gameVersion, ModLoader? loader, CancellationToken ct = default)
        {
            foreach (var provider in new IContentProvider[] { Modrinth, CurseForge })
            {
                if (provider.Id == item.ProviderId || !provider.IsAvailable || !provider.SupportedKinds.Contains(item.Kind)) continue;
                try
                {
                    var page = await provider.SearchAsync(new SearchRequest
                    {
                        Query = item.Title,
                        Kind = item.Kind,
                        GameVersion = gameVersion,
                        Loader = item.Kind == ContentKind.Mod ? loader : null,
                        Limit = 5
                    }, ct);

                    foreach (var candidate in page.Items.Where(c => ContentMatching.SameProject(item, c)))
                    {
                        var versions = await provider.GetVersionsAsync(candidate, gameVersion, loader, ct);
                        var version = VersionPicker.PickBest(versions, gameVersion, loader);
                        var file = version == null ? null : VersionPicker.PickFile(version, item.Kind);
                        if (version != null && file != null && file.AllUrls().Count > 0) return (provider, candidate, version, file);
                    }
                }
                catch (HttpRequestException)
                {
                    // diğer site de ulaşılamıyorsa yapacak bir şey yok
                }
            }
            return null;
        }
    }
}
