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
            _providers = new List<IContentProvider> { modrinth, ftb, curseForge };
        }

        public ModrinthProvider Modrinth { get; }
        public FtbProvider Ftb { get; }
        public CurseForgeProvider CurseForge { get; }

        public IReadOnlyList<IContentProvider> All => _providers;

        public IEnumerable<IContentProvider> Available => _providers.Where(p => p.IsAvailable);

        public IEnumerable<IContentProvider> ForKind(ContentKind kind)
            => Available.Where(p => p.SupportedKinds.Contains(kind));

        public IContentProvider? Get(string? providerId)
            => _providers.FirstOrDefault(p => p.Id == providerId);
    }
}
