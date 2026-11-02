using MinecraftModder.Core;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Views
{
    // Sayfaların ana pencereden istediği her şey. Sayfalar birbirini tanımaz; sadece bunu kullanır.
    public interface IShell : ICardHost
    {
        LauncherCore Core { get; }
        GameProfile ActiveProfile { get; }
        bool IsBusy { get; }
        bool IsGameRunning { get; }

        // Aktif disk değişti (uyumluluk süzgeci, "kurulu mu?" bilgisi değişir).
        event EventHandler? ActiveProfileChanged;

        // Bir şey kuruldu/silindi/favorilendi: görünen kartlar kendini yenilesin.
        event EventHandler? ContentChanged;

        void SetActiveProfile(GameProfile profile);
        void Play();
        void RaiseContentChanged();

        // Uzun işlemler (kurulum, onarım, oyun hazırlama) için: tek seferde bir tane, iptal edilebilir.
        Task<bool> RunBusyAsync(string startMessage, Func<IProgress<ProgressInfo>, CancellationToken, Task> work);

        // Alt köşedeki kısa bilgi balonu.
        void Notify(string text);

        // Kendi temamızla açılan soru kutusu. Basılan butonun sırasını döner; kapatılırsa -1.
        Task<int> AskAsync(string title, string message, params string[] buttons);

        void OpenProfileDetail(GameProfile profile);
        void OpenNewProfile();
        void ShowPage(ShellPage page);
        void OpenPath(string pathOrUrl);
        string FriendlyMessage(Exception ex);

        // CurseForge anahtarı eklenip/kaldırılınca arama sayfalarındaki kaynak seçenekleri yenilenir.
        void RefreshProviders();
        Task SignOutAsync();
    }

    public enum ShellPage
    {
        Discover,
        Mods,
        Packs,
        Looks,
        Discs,
        Settings
    }
}
