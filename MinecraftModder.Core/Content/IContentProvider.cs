using MinecraftModder.Core.Install;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Content
{
    // Her mod kaynağı (Modrinth, CurseForge, FTB...) bu arayüzü uygular.
    // Yeni bir kaynak eklemek = yeni bir IContentProvider yazıp ContentHub'a kaydetmek.
    public interface IContentProvider
    {
        string Id { get; }
        string DisplayName { get; }

        // Örn. CurseForge, API anahtarı girilmeden kullanılamaz.
        bool IsAvailable { get; }

        IReadOnlyList<ContentKind> SupportedKinds { get; }

        Task<SearchPage> SearchAsync(SearchRequest request, CancellationToken ct = default);

        Task<ContentItem?> GetProjectAsync(string projectId, ContentKind kindHint, CancellationToken ct = default);

        // gameVersion/loader verilirse sadece uyumlu sürümler döner. En yeni sürüm başta gelir.
        Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(ContentItem item, string? gameVersion, ModLoader? loader, CancellationToken ct = default);

        // Bağımlılıklar bazen sadece sürüm kimliğiyle verilir (Modrinth). Desteklemeyen kaynak null döner.
        Task<ContentVersion?> GetVersionByIdAsync(string versionId, CancellationToken ct = default);
    }

    // Modpack kurabilen kaynaklar ek olarak bunu uygular.
    public interface IModpackProvider : IContentProvider
    {
        // Paketin neleri içerdiğini (sürüm, loader, indirilecek dosyalar, overrides) çözer.
        // workDir: paket arşivleri gibi geçici dosyaların indirileceği klasör.
        Task<ModpackPlan> ResolveModpackAsync(ContentItem pack, ContentVersion version, string workDir,
            IProgress<ProgressInfo>? progress, CancellationToken ct = default);
    }
}
