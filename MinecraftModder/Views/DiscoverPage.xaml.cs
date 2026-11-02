using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MinecraftModder.Core.Content;

namespace MinecraftModder.Views
{
    // Keşfet: mod bilmeyen biri için başlangıç noktası. Her gün değişen 5 mod ve 5 paket
    // (en popüler ~150 içinden, güne göre seçilir; aynı gün herkes aynısını görür), favoriler
    // ve en sevilen shader'lar.
    public partial class DiscoverPage : UserControl
    {
        private readonly IShell _shell;
        private readonly ObservableCollection<CardViewModel> _mods = new();
        private readonly ObservableCollection<CardViewModel> _packs = new();
        private readonly ObservableCollection<CardViewModel> _shaders = new();
        private readonly ObservableCollection<CardViewModel> _favorites = new();
        private string _shownFavorites = "";
        private bool _loaded;

        public DiscoverPage(IShell shell)
        {
            InitializeComponent();
            _shell = shell;
            DailyMods.ItemsSource = _mods;
            DailyPacks.ItemsSource = _packs;
            PopularShaders.ItemsSource = _shaders;
            FavoriteCards.ItemsSource = _favorites;

            shell.ActiveProfileChanged += (_, _) =>
            {
                if (!_loaded) return;
                _ = LoadModsAsync();
                _ = LoadShadersAsync();
                RefreshAll();
            };
            shell.ContentChanged += (_, _) =>
            {
                RefreshAll();
                if (_loaded && FavoritesKey() != _shownFavorites) _ = LoadFavoritesAsync();
            };
            IsVisibleChanged += (_, _) =>
            {
                if (!IsVisible || _loaded) return;
                _loaded = true;
                _ = LoadModsAsync();
                _ = LoadPacksAsync();
                _ = LoadShadersAsync();
                _ = LoadFavoritesAsync();
            };
        }

        public void SetUserName(string name)
        {
            int hour = DateTime.Now.Hour;
            string hello = hour < 6 ? "İyi geceler" : hour < 12 ? "Günaydın" : hour < 18 ? "Selam" : "İyi akşamlar";
            GreetingText.Text = $"{hello}, {name}! Bugün ne oynasak?";
        }

        private static Random DailyRandom(string salt) => new(PixelArt.Hash(DateTime.Today.ToString("yyyyMMdd") + salt));

        private async Task LoadModsAsync()
        {
            var profile = _shell.ActiveProfile;
            ModsForText.Text = $"'{profile.Name}' diskine uygun";
            if (!profile.Loader.SupportsMods())
            {
                _mods.Clear();
                ShowInfo(DailyModsInfo, $"'{profile.Name}' modsuz bir disk. Mod önerilerini görmek için Disklerim'den Fabric'li bir disk tak.");
                return;
            }

            await LoadRowAsync(_mods, DailyModsInfo, new SearchRequest
            {
                Kind = ContentKind.Mod,
                GameVersion = profile.GameVersion,
                Loader = profile.Loader,
                Offset = DailyRandom("mods").Next(0, 150),
                Limit = 5
            });
        }

        private Task LoadPacksAsync() => LoadRowAsync(_packs, DailyPacksInfo, new SearchRequest
        {
            Kind = ContentKind.Modpack,
            Offset = DailyRandom("packs").Next(0, 100),
            Limit = 5
        });

        private Task LoadShadersAsync() => LoadRowAsync(_shaders, ShadersInfo, new SearchRequest
        {
            Kind = ContentKind.ShaderPack,
            GameVersion = _shell.ActiveProfile.GameVersion,
            Limit = 3
        });

        private async Task LoadRowAsync(ObservableCollection<CardViewModel> target, TextBlock info, SearchRequest request)
        {
            info.Visibility = Visibility.Collapsed;
            try
            {
                var page = await _shell.Core.Content.Modrinth.SearchAsync(request);
                // Seçilen gün listenin sonuna denk geldiyse (az sonuçlu eski sürümler) baştan göster.
                if (page.Items.Count == 0 && request.Offset > 0)
                    page = await _shell.Core.Content.Modrinth.SearchAsync(request with { Offset = 0 });

                target.Clear();
                foreach (var item in page.Items) target.Add(new CardViewModel(item, _shell));
                if (target.Count == 0) ShowInfo(info, "Bu sürüm için öneri bulunamadı.");
            }
            catch (Exception ex)
            {
                target.Clear();
                ShowInfo(info, "Öneriler getirilemedi: " + _shell.FriendlyMessage(ex));
            }
        }

        private async Task LoadFavoritesAsync()
        {
            var keys = _shell.Core.Settings.Current.FavoriteKeys.ToList();
            _shownFavorites = FavoritesKey();
            if (keys.Count == 0)
            {
                _favorites.Clear();
                FavoritesSection.Visibility = Visibility.Collapsed;
                return;
            }

            var items = await Task.WhenAll(keys.Select(async key =>
            {
                int split = key.IndexOf(':');
                if (split <= 0 || _shell.Core.Content.Get(key[..split]) is not { IsAvailable: true } provider) return null;
                try { return await provider.GetProjectAsync(key[(split + 1)..], ContentKind.Mod); }
                catch { return null; }
            }));

            _favorites.Clear();
            foreach (var item in items.Where(i => i != null)) _favorites.Add(new CardViewModel(item!, _shell));
            FavoritesSection.Visibility = _favorites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private string FavoritesKey() => string.Join("|", _shell.Core.Settings.Current.FavoriteKeys);

        private void RefreshAll()
        {
            foreach (var card in _mods.Concat(_packs).Concat(_shaders).Concat(_favorites)) card.Refresh();
        }

        private static void ShowInfo(TextBlock info, string text)
        {
            info.Text = text;
            info.Visibility = Visibility.Visible;
        }

        private void AllMods_Click(object sender, RoutedEventArgs e) => _shell.ShowPage(ShellPage.Mods);
        private void AllPacks_Click(object sender, RoutedEventArgs e) => _shell.ShowPage(ShellPage.Packs);
        private void AllLooks_Click(object sender, RoutedEventArgs e) => _shell.ShowPage(ShellPage.Looks);
    }
}
