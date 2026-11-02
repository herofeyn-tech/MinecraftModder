using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MinecraftModder.Core.Content;

namespace MinecraftModder.Views
{
    // Modlar, Paketler ve Görünüm (shader + kaynak paketi) sekmelerinin ortak sayfası.
    // Arama kutusu boşken en popülerler gelir; aşağı kaydırdıkça devamı yüklenir.
    public partial class BrowsePage : UserControl
    {
        private const int PageSize = 30;

        private readonly IShell _shell;
        private readonly ObservableCollection<CardViewModel> _cards = new();
        private readonly DispatcherTimer _typingTimer;
        private CancellationTokenSource? _searchCts;
        private SearchRequest? _lastRequest;
        private bool _hasMore;
        private bool _loading;
        private bool _loadedOnce;
        private ContentKind _kind;
        private IContentProvider? _provider;

        public BrowsePage(IShell shell, string title, IReadOnlyList<(ContentKind Kind, string Name)> kinds, DataTemplate cardTemplate)
        {
            InitializeComponent();
            _shell = shell;
            TitleText.Text = title;
            Cards.ItemTemplate = cardTemplate;
            Cards.ItemsSource = _cards;
            _kind = kinds[0].Kind;

            _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _typingTimer.Tick += (_, _) =>
            {
                _typingTimer.Stop();
                _ = SearchAsync(reset: true);
            };

            if (kinds.Count > 1)
            {
                foreach (var (kind, name) in kinds)
                {
                    var chip = new RadioButton { Content = name, GroupName = "kind" + GetHashCode(), Style = (Style)FindResource("Chip"), IsChecked = kind == _kind };
                    chip.Checked += (_, _) =>
                    {
                        _kind = kind;
                        BuildProviderChips();
                        _ = SearchAsync(reset: true);
                    };
                    KindChips.Children.Add(chip);
                }
            }
            BuildProviderChips();

            shell.ActiveProfileChanged += (_, _) =>
            {
                UpdateHint();
                // Uyumluluk süzgeci (sürüm/loader) değişti; paketler profilden bağımsız.
                if (_kind != ContentKind.Modpack && _loadedOnce) _ = SearchAsync(reset: true);
                else RefreshCards();
            };
            shell.ContentChanged += (_, _) => RefreshCards();
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible && !_loadedOnce) _ = SearchAsync(reset: true);
            };
            UpdateHint();
        }

        // CurseForge anahtarı eklenince kaynak listesi değişebilir.
        public void BuildProviderChips()
        {
            ProviderChips.Children.Clear();
            var providers = _shell.Core.Content.ForKind(_kind).ToList();
            if (_provider == null || !providers.Contains(_provider)) _provider = providers.FirstOrDefault();

            if (providers.Count > 1)
            {
                foreach (var provider in providers)
                {
                    var chip = new RadioButton
                    {
                        Content = provider.DisplayName,
                        GroupName = "provider" + GetHashCode(),
                        Style = (Style)FindResource("Chip"),
                        IsChecked = provider == _provider
                    };
                    chip.Checked += (_, _) =>
                    {
                        _provider = provider;
                        _ = SearchAsync(reset: true);
                    };
                    ProviderChips.Children.Add(chip);
                }
            }
            ChipDivider.Visibility = KindChips.Children.Count > 0 && ProviderChips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateHint();
        }

        private void UpdateHint()
        {
            var profile = _shell.ActiveProfile;
            HintText.Text = _kind switch
            {
                ContentKind.Modpack => "Her paket kendi yeni diskine kurulur; diğer disklerin hiç etkilenmez.",
                ContentKind.Mod when !profile.Loader.SupportsMods() =>
                    $"'{profile.Name}' modsuz (Vanilla) bir disk. Mod kurmak için Disklerim'den Fabric'li bir disk seç ya da yeni disk oluştur.",
                _ => $"Sadece '{profile.Name}' ({profile.DisplayVersion}) diskiyle uyumlu olanlar gösteriliyor."
            };
        }

        private void RefreshCards()
        {
            foreach (var card in _cards) card.Refresh();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _typingTimer.Stop();
            _typingTimer.Start();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            _typingTimer.Stop();
            _ = SearchAsync(reset: true);
        }

        private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_hasMore && !_loading && Scroller.VerticalOffset + Scroller.ViewportHeight > Scroller.ExtentHeight - 400)
                _ = SearchAsync(reset: false);
        }

        private async Task SearchAsync(bool reset)
        {
            if (_provider is not { } provider) return;
            if (!reset && _lastRequest == null) return;

            _searchCts?.Cancel();
            var cts = _searchCts = new CancellationTokenSource();
            _loading = true;
            _loadedOnce = true;

            var profile = _shell.ActiveProfile;
            var request = reset
                ? new SearchRequest
                {
                    Query = SearchBox.Text.Trim(),
                    Kind = _kind,
                    GameVersion = _kind == ContentKind.Modpack ? null : profile.GameVersion,
                    Loader = _kind == ContentKind.Mod ? profile.Loader : null,
                    Limit = PageSize
                }
                : _lastRequest! with { Offset = _lastRequest.Offset + _lastRequest.Limit };

            if (reset)
            {
                _cards.Clear();
                _hasMore = false;
                Scroller.ScrollToTop();
            }
            FooterText.Text = "Yükleniyor...";

            try
            {
                var page = await provider.SearchAsync(request, cts.Token);
                if (cts.IsCancellationRequested) return;

                _lastRequest = request;
                foreach (var item in page.Items) _cards.Add(new CardViewModel(item, _shell));
                _hasMore = page.HasMore;
                FooterText.Text = _cards.Count == 0
                    ? "Hiçbir şey bulunamadı. Başka bir kelime ya da kaynak dene."
                    : _hasMore ? "" : "Hepsi bu kadar!";
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                FooterText.Text = "Liste getirilemedi: " + _shell.FriendlyMessage(ex);
            }
            finally
            {
                if (_searchCts == cts) _loading = false;
            }
        }
    }
}
