using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MinecraftModder.Core;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Launch;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Settings;
using MinecraftModder.Core.Util;

namespace MinecraftModder
{
    // Bu pencere sadece arayüzdür: tüm iş (indirme, kurulum, profil, başlatma) MinecraftModder.Core'da.
    // Arayüz yeniden tasarlanırken bu dosya baştan yazılabilir, çekirdeğe dokunmak gerekmez.
    public partial class MainWindow : Window
    {
        private static readonly string[] FallbackGameVersions = { "1.21.1", "1.20.1", "1.19.2", "1.18.2", "1.16.5", "1.12.2" };

        private readonly LauncherCore _core;
        private GameProfile _activeProfile = null!;

        // Uzun işlemler (kurulum, onarma, oyun hazırlama) aynı anda tek bir tane çalışır.
        private CancellationTokenSource? _busyCts;
        private bool _isBusy;
        private DateTime _lastProgressUpdate;

        // Arama, kurulum sürerken bile kullanılabilsin diye ayrı yönetilir.
        private CancellationTokenSource? _searchCts;
        private SearchRequest? _lastSearch;
        private readonly ObservableCollection<SearchResultView> _results = new();

        private Process? _gameProcess;
        private bool _suppressProfileCombo;
        private bool _uiReady;

        public MainWindow()
        {
            InitializeComponent();
            _core = LauncherCore.Create();

            KindCombo.ItemsSource = new[]
            {
                new KindOption(ContentKind.Mod, "Modlar"),
                new KindOption(ContentKind.Modpack, "Mod Paketleri"),
                new KindOption(ContentKind.ShaderPack, "Shader'lar"),
                new KindOption(ContentKind.ResourcePack, "Kaynak Paketleri"),
            };
            NewProfileLoaderCombo.ItemsSource = new[]
            {
                new LoaderOption(ModLoader.Fabric, "Fabric (önerilen)"),
                new LoaderOption(ModLoader.Forge, "Forge"),
                new LoaderOption(ModLoader.NeoForge, "NeoForge"),
                new LoaderOption(ModLoader.Quilt, "Quilt"),
                new LoaderOption(ModLoader.Vanilla, "Vanilla (modsuz)"),
            };
            NewProfileLoaderCombo.SelectedIndex = 0;
            ResultsList.ItemsSource = _results;
        }

        private GameProfile ActiveProfile => _activeProfile;
        private AppSettings Settings => _core.Settings.Current;
        private ContentKind SelectedKind => (KindCombo.SelectedItem as KindOption)?.Kind ?? ContentKind.Mod;

        // ==================== AÇILIŞ VE GİRİŞ ====================

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _activeProfile = _core.EnsureActiveProfile();
            RefreshProfiles();
            LoadSettingsIntoUi();
            OfflineUsernameBox.Text = Settings.LastOfflineUsername ?? "Oyuncu";
            _uiReady = true;
            KindCombo.SelectedIndex = 0;
            _ = LoadGameVersionsAsync();

            XboxLoginStatus.Text = "Kayıtlı oturum kontrol ediliyor...";
            var session = await _core.Accounts.TrySilentLoginAsync();
            if (session != null)
            {
                XboxLoggedOutPanel.Visibility = Visibility.Collapsed;
                XboxLoggedInPanel.Visibility = Visibility.Visible;
                XboxRememberedUsername.Text = $"Kayıtlı Hesap: {session.Username}";
                XboxLoginStatus.Text = "Önceki oturum bulundu!";
            }
            else
            {
                XboxLoginStatus.Text = "";
            }
        }

        private async void XboxLoginBtn_Click(object sender, RoutedEventArgs e)
        {
            XboxLoginBtn.IsEnabled = false;
            XboxLoginStatus.Text = "Tarayıcıda Microsoft ekranı bekleniyor...";
            try
            {
                await _core.Accounts.LoginInteractivelyAsync();
                SwitchToMainScreen();
            }
            catch (Exception ex)
            {
                XboxLoginStatus.Text = "Giriş yapılamadı.";
                MessageBox.Show($"Microsoft girişi başarısız oldu: {ex.Message}", "Giriş Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                XboxLoginBtn.IsEnabled = true;
            }
        }

        private void XboxContinueBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_core.Accounts.Current != null) SwitchToMainScreen();
        }

        private async void XboxLogoutBtn_Click(object sender, RoutedEventArgs e)
        {
            await _core.Accounts.SignOutAsync();
            XboxLoggedInPanel.Visibility = Visibility.Collapsed;
            XboxLoggedOutPanel.Visibility = Visibility.Visible;
            XboxLoginStatus.Text = "Hesap unutuldu.";
        }

        private void OfflineLoginBtn_Click(object sender, RoutedEventArgs e)
        {
            string username = OfflineUsernameBox.Text.Trim();
            try
            {
                _core.Accounts.UseOffline(username);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, "Kullanıcı adı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Settings.LastOfflineUsername = username;
            _core.Settings.Save();
            SwitchToMainScreen();
        }

        private void SwitchToMainScreen()
        {
            var session = _core.Accounts.Current!;
            WelcomeText.Text = $"Hoş geldin, {session.Username}!";
            AccountText.Text = session.UserType == "msa" ? "Microsoft hesabı" : "Çevrimdışı hesap";
            LoginScreen.Visibility = Visibility.Collapsed;
            MainScreen.Visibility = Visibility.Visible;
            _ = SearchAsync(reset: true);
        }

        private async void SignOutBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_core.Accounts.Current?.UserType == "msa") await _core.Accounts.SignOutAsync();
            XboxLoggedInPanel.Visibility = Visibility.Collapsed;
            XboxLoggedOutPanel.Visibility = Visibility.Visible;
            XboxLoginStatus.Text = "";
            MainScreen.Visibility = Visibility.Collapsed;
            LoginScreen.Visibility = Visibility.Visible;
        }

        // ==================== OYNA ====================

        private async void PlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_gameProcess is { HasExited: false })
            {
                StatusText.Text = "Oyun zaten açık.";
                return;
            }

            var session = _core.Accounts.Current;
            if (session == null)
            {
                SignOutBtn_Click(sender, e);
                return;
            }

            var profile = ActiveProfile;
            Process? process = null;
            bool ok = await RunBusyAsync($"'{profile.Name}' hazırlanıyor...",
                async (progress, ct) => process = await _core.Launcher.LaunchAsync(profile, session, progress, ct));
            if (!ok || process == null) return;

            _gameProcess = process;
            var startedAt = DateTimeOffset.Now;
            PlayBtn.IsEnabled = false;
            PlayBtn.Content = "Oyun açık";
            StatusText.Text = $"İyi oyunlar! ({profile.Name})";

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.Invoke(() => OnGameExited(profile, process, startedAt));
            if (process.HasExited) OnGameExited(profile, process, startedAt);
            else if (Settings.HideLauncherWhilePlaying) WindowState = WindowState.Minimized;
        }

        private void OnGameExited(GameProfile profile, Process process, DateTimeOffset startedAt)
        {
            if (_gameProcess != process) return;
            _gameProcess = null;
            PlayBtn.IsEnabled = !_isBusy;
            PlayBtn.Content = "▶  OYNA";
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();

            int exitCode;
            try { exitCode = process.ExitCode; } catch { exitCode = 0; }
            StatusText.Text = "Hazır!";
            if (exitCode == 0) return;

            string? crashReport = GameLauncher.FindLatestCrashReport(profile, startedAt);
            string message = $"Oyun beklenmedik şekilde kapandı (kod {exitCode}).\n\n" +
                             "Genelde sebep uyumsuz ya da bozuk bir moddur. 'Kurulu' sekmesindeki 'Onar' butonunu dene; " +
                             "sorun sürerse en son kurduğun modu kapatıp tekrar aç.";
            if (crashReport == null)
            {
                MessageBox.Show(message, "Oyun kapandı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show(message + "\n\nÇökme raporunu açmak ister misin?", "Oyun kapandı",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                OpenPath(crashReport);
        }

        // ==================== KEŞFET / ARA ====================

        private void KindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            RefreshProviders();
        }

        private void RefreshProviders()
        {
            var current = ProviderCombo.SelectedItem as IContentProvider;
            var providers = _core.Content.ForKind(SelectedKind).ToList();
            ProviderCombo.ItemsSource = providers;
            ProviderCombo.SelectedItem = providers.Contains(current!) ? current : providers.FirstOrDefault();
            UpdateSearchHint();
        }

        private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_uiReady && MainScreen.Visibility == Visibility.Visible) _ = SearchAsync(reset: true);
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) _ = SearchAsync(reset: true);
        }

        private void SearchBtn_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(reset: true);

        private void LoadMoreBtn_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(reset: false);

        private void UpdateSearchHint()
        {
            SearchHintText.Text = SelectedKind switch
            {
                ContentKind.Modpack => "Mod paketleri kendi yeni profillerine kurulur; mevcut profilin bozulmaz. Arama boşken en popülerler gösterilir.",
                ContentKind.Mod when !ActiveProfile.Loader.SupportsMods() =>
                    $"'{ActiveProfile.Name}' bir Vanilla profili; mod kurmak için Profiller sekmesinden Fabric profili seç ya da oluştur.",
                _ => $"Sadece '{ActiveProfile.Name}' ({ActiveProfile.DisplayVersion}) ile uyumlu sonuçlar gösteriliyor. Arama boşken en popülerler gelir."
            };
        }

        private async Task SearchAsync(bool reset)
        {
            if (ProviderCombo.SelectedItem is not IContentProvider provider) return;
            if (!reset && _lastSearch == null) return;

            _searchCts?.Cancel();
            var cts = _searchCts = new CancellationTokenSource();

            var kind = SelectedKind;
            var request = reset
                ? new SearchRequest
                {
                    Query = SearchBox.Text.Trim(),
                    Kind = kind,
                    GameVersion = kind == ContentKind.Modpack ? null : ActiveProfile.GameVersion,
                    Loader = kind == ContentKind.Mod ? ActiveProfile.Loader : null,
                    Limit = 30
                }
                : _lastSearch! with { Offset = _lastSearch.Offset + _lastSearch.Limit };

            if (reset)
            {
                _results.Clear();
                LoadMoreBtn.Visibility = Visibility.Collapsed;
            }
            if (!_isBusy) StatusText.Text = $"{provider.DisplayName} üzerinde aranıyor...";

            SearchPage page;
            try
            {
                page = await provider.SearchAsync(request, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!_isBusy) StatusText.Text = "Arama başarısız: " + FriendlyMessage(ex);
                return;
            }
            if (cts.IsCancellationRequested) return;

            _lastSearch = request;
            foreach (var item in page.Items) _results.Add(new SearchResultView(item, IsInstalled(item)));
            LoadMoreBtn.Visibility = page.HasMore ? Visibility.Visible : Visibility.Collapsed;

            if (!_isBusy)
            {
                StatusText.Text = _results.Count == 0
                    ? "Sonuç bulunamadı. Farklı bir kelime ya da kaynak dene."
                    : $"{_results.Count} sonuç gösteriliyor. Kurmak için 'Kur'a bas.";
            }
        }

        private bool IsInstalled(ContentItem item)
        {
            if (item.Kind == ContentKind.Modpack)
                return _core.Profiles.All.Any(p => p.Source?.ProviderId == item.ProviderId && p.Source.ProjectId == item.Id);
            return ContentInstaller.IsInstalled(ActiveProfile, item);
        }

        private async void InstallItemBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: SearchResultView view }) await InstallAsync(view);
        }

        private async void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsList.SelectedItem is SearchResultView { CanInstall: true } view) await InstallAsync(view);
        }

        private async Task InstallAsync(SearchResultView view)
        {
            var item = view.Item;
            if (item.Kind == ContentKind.Modpack)
            {
                await InstallModpackAsync(view);
                return;
            }

            var profile = ActiveProfile;
            ContentInstallResult? result = null;
            bool ok = await RunBusyAsync($"{item.Title} kuruluyor...",
                async (progress, ct) => result = await _core.Installer.InstallAsync(profile, item, progress, ct));
            if (!ok || result == null) return;

            if (result.Installed.Count > 0 || result.AlreadyInstalled.Count > 0) view.MarkInstalled();

            // Her şey yolundaysa pencere açıp kullanıcıyı yormuyoruz; sadece alt barda yazıyor.
            if (result.Skipped.Count == 0 && result.Warnings.Count == 0)
            {
                int extras = result.Installed.Count - 1;
                StatusText.Text = result.Installed.Count == 0
                    ? $"{item.Title} zaten kurulu."
                    : $"✓ {item.Title} kuruldu" + (extras > 0 ? $" (+{extras} gerekli ek)" : "") + $" → {profile.Name}";
                return;
            }

            var text = new StringBuilder();
            if (result.Installed.Count > 0) text.AppendLine("Kurulanlar:\n• " + string.Join("\n• ", result.Installed) + "\n");
            if (result.Skipped.Count > 0) text.AppendLine("Kurulamayanlar:\n• " + string.Join("\n• ", result.Skipped.Select(s => $"{s.Title}: {s.Reason}")) + "\n");
            if (result.Warnings.Count > 0) text.AppendLine("Not:\n• " + string.Join("\n• ", result.Warnings));
            StatusText.Text = result.Installed.Count > 0 ? $"{item.Title} kuruldu (bazı uyarılar var)." : $"{item.Title} kurulamadı.";

            var withPage = result.Skipped.FirstOrDefault(s => s.WebUrl != null);
            if (withPage != null)
            {
                if (MessageBox.Show(text + $"\n'{withPage.Title}' sayfasını tarayıcıda açayım mı?", "Kurulum", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenPath(withPage.WebUrl!);
            }
            else
            {
                MessageBox.Show(text.ToString(), "Kurulum", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async Task InstallModpackAsync(SearchResultView view)
        {
            var item = view.Item;
            if (_core.Content.Get(item.ProviderId) is not IModpackProvider provider) return;

            if (MessageBox.Show($"'{item.Title}' kendi yeni profiline kurulacak. Mevcut profillerin etkilenmez.\n\nDevam edilsin mi?",
                    "Mod paketi kur", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            ModpackInstallResult? result = null;
            bool ok = await RunBusyAsync($"{item.Title} kuruluyor...",
                async (progress, ct) => result = await _core.Modpacks.InstallAsync(provider, item, null, progress, ct));
            if (!ok || result == null) return;

            view.MarkInstalled();
            ShowModpackResult(result);
        }

        private void ShowModpackResult(ModpackInstallResult result)
        {
            var profile = result.Profile;
            SetActiveProfile(profile);

            var text = new StringBuilder($"'{profile.Name}' kuruldu ve aktif profil yapıldı.\n{profile.DisplayVersion}\n");
            if (result.Downloads.Failed.Count > 0)
            {
                text.AppendLine($"\n{result.Downloads.Failed.Count} dosya inemedi. 'Kurulu' sekmesinden 'Onar' ile tekrar deneyebilirsin:");
                foreach (var f in result.Downloads.Failed.Take(5)) text.AppendLine("• " + f.Item.DisplayName);
                if (result.Downloads.Failed.Count > 5) text.AppendLine("• ...");
            }
            else
            {
                text.AppendLine("\nOynamak için yukarıdaki OYNA'ya bas. (İlk açılış loader kurulumu yüzünden biraz uzun sürebilir.)");
            }
            StatusText.Text = $"✓ {profile.Name} kuruldu.";
            MessageBox.Show(text.ToString(), "Mod paketi kuruldu", MessageBoxButton.OK,
                result.Downloads.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        // ==================== KURULU İÇERİK ====================

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // İçteki ComboBox/ListBox'ların SelectionChanged olayları da buraya kadar kabarır.
            if (!ReferenceEquals(e.OriginalSource, MainTabControl)) return;

            switch (MainTabControl.SelectedIndex)
            {
                case 1: _ = RefreshInstalledAsync(); break;
                case 2: RefreshProfiles(); break;
                case 3: LoadSettingsIntoUi(); break;
            }
        }

        private async Task RefreshInstalledAsync()
        {
            var profile = ActiveProfile;
            InstalledTitle.Text = $"{profile.Name} · içerik";
            InstalledSummary.Text = "Taranıyor...";

            IReadOnlyList<ContentEntry> entries;
            try
            {
                entries = await _core.ProfileContent.RefreshAsync(profile);
            }
            catch (Exception ex)
            {
                InstalledSummary.Text = "Liste okunamadı: " + FriendlyMessage(ex);
                return;
            }
            if (profile != ActiveProfile) return;

            var views = entries.Select(e => new ContentEntryView(e, ProfileContentService.IsMissing(profile, e))).ToList();
            InstalledList.ItemsSource = views;
            SelectAllCheck.IsChecked = false;

            int mods = entries.Count(e => e.Kind == ContentKind.Mod);
            int shaders = entries.Count(e => e.Kind == ContentKind.ShaderPack);
            int packs = entries.Count(e => e.Kind == ContentKind.ResourcePack);
            int disabled = entries.Count(e => !e.Enabled);
            int missing = views.Count(v => v.IsMissing);

            var parts = new List<string> { $"{mods} mod" };
            if (shaders > 0) parts.Add($"{shaders} shader");
            if (packs > 0) parts.Add($"{packs} kaynak paketi");
            if (disabled > 0) parts.Add($"{disabled} kapalı");
            if (missing > 0) parts.Add($"{missing} eksik (Onar'a bas)");
            var duplicates = ProfileDoctor.FindDuplicates(profile);
            if (duplicates.Count > 0) parts.Add($"⚠ {duplicates.Count} mod iki kez kurulu");
            InstalledSummary.Text = string.Join(" · ", parts) + ".  Ctrl/Shift ile birden çok seçebilirsin.";
        }

        private List<ContentEntry> SelectedEntries() => InstalledList.SelectedItems.Cast<ContentEntryView>().Select(v => v.Entry).ToList();

        private void InstalledList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int total = InstalledList.Items.Count;
            SelectAllCheck.IsChecked = total > 0 && InstalledList.SelectedItems.Count == total;
        }

        private void SelectAllCheck_Click(object sender, RoutedEventArgs e)
        {
            if (SelectAllCheck.IsChecked == true) InstalledList.SelectAll();
            else InstalledList.UnselectAll();
        }

        private async void EnableSelectedBtn_Click(object sender, RoutedEventArgs e) => await SetSelectedEnabledAsync(true);
        private async void DisableSelectedBtn_Click(object sender, RoutedEventArgs e) => await SetSelectedEnabledAsync(false);

        private async Task SetSelectedEnabledAsync(bool enabled)
        {
            var selected = SelectedEntries();
            if (selected.Count == 0)
            {
                StatusText.Text = "Önce listeden seçim yap (Ctrl/Shift ile çoklu seçim).";
                return;
            }
            await ApplyContentChangeAsync(() => _core.ProfileContent.SetEnabled(ActiveProfile, selected, enabled));
            StatusText.Text = $"{selected.Count} öğe {(enabled ? "açıldı" : "kapatıldı")}.";
        }

        private async void EntryToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { Tag: ContentEntryView view } box) return;
            bool enabled = box.IsChecked == true;
            await ApplyContentChangeAsync(() => _core.ProfileContent.SetEnabled(ActiveProfile, new[] { view.Entry }, enabled));
        }

        private async void DeleteSelectedBtn_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedEntries();
            if (selected.Count == 0)
            {
                StatusText.Text = "Önce listeden seçim yap (Ctrl/Shift ile çoklu seçim).";
                return;
            }

            string warning = selected.Any(s => s.Origin is ContentOrigin.Pack or ContentOrigin.PackOverride)
                ? "\n\nDikkat: seçtiklerinin bir kısmı mod paketinin parçası; silersen paket düzgün çalışmayabilir. Silmek yerine 'Kapat' daha güvenli."
                : "";
            if (MessageBox.Show($"{selected.Count} öğe kalıcı olarak silinsin mi?{warning}", "Emin misin?",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            await ApplyContentChangeAsync(() => _core.ProfileContent.Remove(ActiveProfile, selected));
            StatusText.Text = $"{selected.Count} öğe silindi.";
        }

        private async Task ApplyContentChangeAsync(Action change)
        {
            if (_gameProcess is { HasExited: false })
            {
                MessageBox.Show("Oyun açıkken modlar değiştirilemez. Önce oyunu kapat.", "Oyun açık", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshInstalledAsync();
                return;
            }

            try
            {
                change();
            }
            catch (Exception ex)
            {
                MessageBox.Show(FriendlyMessage(ex), "Değiştirilemedi", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await RefreshInstalledAsync();
        }

        private async void RepairBtn_Click(object sender, RoutedEventArgs e)
        {
            var profile = ActiveProfile;
            RepairReport? report = null;
            bool ok = await RunBusyAsync("Dosyalar kontrol ediliyor...",
                async (progress, ct) => report = await _core.Doctor.VerifyAndRepairAsync(profile, progress, ct));
            await RefreshInstalledAsync();
            if (!ok || report == null) return;

            if (report.IsHealthy)
            {
                StatusText.Text = $"✓ Her şey yolunda! {report.Checked} dosya kontrol edildi.";
                return;
            }

            var text = new StringBuilder();
            if (report.Repaired > 0) text.AppendLine($"✓ {report.Repaired} bozuk/eksik dosya yeniden indirildi.\n");
            if (report.Failed.Count > 0) text.AppendLine("İndirilemeyenler:\n• " + string.Join("\n• ", report.Failed.Take(8)) + "\n");
            if (report.Unrepairable.Count > 0) text.AppendLine("Kaynağı bilinmeyen eksik dosyalar (elle eklenmiş olabilir):\n• " + string.Join("\n• ", report.Unrepairable.Take(8)) + "\n");
            if (report.Duplicates.Count > 0) text.AppendLine("İki kez kurulu modlar (birini kapat ya da sil):\n• " + string.Join("\n• ", report.Duplicates));
            StatusText.Text = report.Repaired > 0 ? $"✓ {report.Repaired} dosya onarıldı." : "Kontrol tamamlandı.";
            MessageBox.Show(text.ToString(), "Mod Doktoru", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenModsFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            string mods = Path.Combine(ActiveProfile.GameDirectory, "mods");
            Directory.CreateDirectory(mods);
            OpenPath(mods);
        }

        private async void RefreshInstalledBtn_Click(object sender, RoutedEventArgs e) => await RefreshInstalledAsync();

        // ==================== PROFİLLER ====================

        private void RefreshProfiles()
        {
            var all = _core.Profiles.All;
            ProfilesList.ItemsSource = all.Select(p => new ProfileView(p, p.Id == ActiveProfile.Id)).ToList();

            _suppressProfileCombo = true;
            ActiveProfileCombo.ItemsSource = all;
            ActiveProfileCombo.SelectedItem = all.FirstOrDefault(p => p.Id == ActiveProfile.Id);
            _suppressProfileCombo = false;
        }

        private void SetActiveProfile(GameProfile profile)
        {
            bool changed = profile.Id != _activeProfile?.Id;
            _activeProfile = profile;
            _core.SetActiveProfile(profile);
            RefreshProfiles();
            UpdateSearchHint();
            UpdateRamInfo();

            // Uyumluluk süzgeci (sürüm/loader) değişti; mod sonuçlarını yeni profile göre yenile.
            if (changed && SelectedKind != ContentKind.Modpack && MainScreen.Visibility == Visibility.Visible) _ = SearchAsync(reset: true);
            if (MainTabControl.SelectedIndex == 1) _ = RefreshInstalledAsync();
        }

        private void ActiveProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProfileCombo || ActiveProfileCombo.SelectedItem is not GameProfile profile || profile.Id == ActiveProfile.Id) return;
            if (_isBusy)
            {
                StatusText.Text = "İşlem sürerken profil değiştirilemez.";
                RefreshProfiles();
                return;
            }
            SetActiveProfile(profile);
            StatusText.Text = $"Aktif profil: {profile.Name}";
        }

        private GameProfile? SelectedProfile => (ProfilesList.SelectedItem as ProfileView)?.Profile;

        private void UseProfileBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedProfile is not { } profile)
            {
                StatusText.Text = "Önce listeden bir profil seç.";
                return;
            }
            SetActiveProfile(profile);
            StatusText.Text = $"Aktif profil: {profile.Name}";
        }

        private void ProfilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => UseProfileBtn_Click(sender, e);

        private void OpenProfileFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var profile = SelectedProfile ?? ActiveProfile;
            Directory.CreateDirectory(profile.GameDirectory);
            OpenPath(profile.GameDirectory);
        }

        private void DeleteProfileBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedProfile is not { } profile)
            {
                StatusText.Text = "Önce listeden silinecek profili seç.";
                return;
            }
            if (_isBusy || (_gameProcess is { HasExited: false } && profile.Id == ActiveProfile.Id))
            {
                StatusText.Text = "İşlem sürerken ya da oyun açıkken profil silinemez.";
                return;
            }

            bool insideLauncher = profile.GameDirectory.StartsWith(_core.Paths.InstancesDir, StringComparison.OrdinalIgnoreCase);
            string detail = insideLauncher
                ? "Profilin klasörü, içindeki modlar ve DÜNYALARIN (kayıtların) da silinecek!"
                : $"Profil listeden kaldırılacak; klasörü ({profile.GameDirectory}) yerinde bırakılacak.";
            if (MessageBox.Show($"'{profile.Name}' silinsin mi?\n\n{detail}", "Profili sil",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                _core.Profiles.Delete(profile, deleteGameFiles: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Profil klasörü tamamen silinemedi: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (profile.Id == ActiveProfile.Id) SetActiveProfile(_core.EnsureActiveProfile());
            else RefreshProfiles();
            StatusText.Text = $"'{profile.Name}' silindi.";
        }

        private void CreateProfileBtn_Click(object sender, RoutedEventArgs e)
        {
            string name = NewProfileNameBox.Text.Trim();
            string version = NewProfileVersionCombo.Text.Trim();
            var loader = (NewProfileLoaderCombo.SelectedItem as LoaderOption)?.Loader ?? ModLoader.Fabric;

            if (name.Length == 0 || version.Length == 0)
            {
                MessageBox.Show("Profil adı ve Minecraft sürümü boş olamaz.", "Eksik bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (NewProfileVersionCombo.ItemsSource is IEnumerable<string> known && known.Any() && !known.Contains(version))
            {
                MessageBox.Show($"'{version}' diye bir Minecraft sürümü bulunamadı. Listeden seçebilirsin.", "Sürüm", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var profile = _core.Profiles.Create(name, version, loader);
            NewProfileNameBox.Text = "";
            SetActiveProfile(profile);
            StatusText.Text = $"✓ '{profile.Name}' oluşturuldu ve aktif yapıldı. Keşfet'ten mod ekleyebilirsin.";
        }

        private async void ImportMrpackBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Modrinth paketi (*.mrpack)|*.mrpack", Title = "Paket dosyası seç" };
            if (dialog.ShowDialog(this) != true) return;

            ModpackInstallResult? result = null;
            bool ok = await RunBusyAsync("Paket içe aktarılıyor...",
                async (progress, ct) => result = await _core.Modpacks.InstallLocalMrpackAsync(dialog.FileName, progress, ct));
            if (ok && result != null) ShowModpackResult(result);
        }

        private async Task LoadGameVersionsAsync()
        {
            IReadOnlyList<string> versions;
            try
            {
                versions = await _core.GameVersions.GetReleaseVersionsAsync();
            }
            catch
            {
                versions = FallbackGameVersions;
            }
            NewProfileVersionCombo.ItemsSource = versions;
            NewProfileVersionCombo.Text = Settings.DefaultGameVersion;
        }

        // ==================== AYARLAR ====================

        private void LoadSettingsIntoUi()
        {
            var s = Settings;
            long total = RamAdvisor.TotalSystemMb();
            RamSlider.Maximum = Math.Max(1024, RamAdvisor.MaxAllowed(total) / 512 * 512);
            RamSlider.Value = Math.Min(s.ManualRamMb, RamSlider.Maximum);
            RamAutoRadio.IsChecked = s.RamMode == RamMode.Auto;
            RamManualRadio.IsChecked = s.RamMode == RamMode.Manual;
            ParallelSlider.Value = s.MaxParallelDownloads;
            HideLauncherCheck.IsChecked = s.HideLauncherWhilePlaying;
            CurseForgeKeyBox.Text = s.CurseForgeApiKey ?? "";
            DataFolderText.Text = $"Profiller ve oyun dosyaları: {_core.Paths.Root}";
            UpdateRamInfo();
        }

        private void UpdateRamInfo()
        {
            if (RamInfoText == null || _activeProfile == null) return;
            long total = RamAdvisor.TotalSystemMb();
            bool manual = RamManualRadio.IsChecked == true;
            RamManualPanel.IsEnabled = manual;
            RamManualPanel.Opacity = manual ? 1 : 0.4;

            var autoSettings = new AppSettings { RamMode = RamMode.Auto };
            int auto = RamAdvisor.Decide(autoSettings, ActiveProfile, total);
            RamInfoText.Text = $"Bilgisayarında {total / 1024.0:0.#} GB RAM var. Otomatik modda '{ActiveProfile.Name}' için {auto / 1024.0:0.#} GB kullanılır " +
                               "(mod sayısına ve paketin önerisine göre ayarlanır, Windows'a her zaman pay bırakılır).";
            RamValueText.Text = $"{RamSlider.Value / 1024:0.#} GB";
        }

        private void RamMode_Changed(object sender, RoutedEventArgs e) => UpdateRamInfo();

        private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RamValueText != null) RamValueText.Text = $"{e.NewValue / 1024:0.#} GB";
        }

        private void ParallelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ParallelValueText != null) ParallelValueText.Text = $"{(int)e.NewValue} dosya";
        }

        private void SaveSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var s = Settings;
            s.RamMode = RamManualRadio.IsChecked == true ? RamMode.Manual : RamMode.Auto;
            s.ManualRamMb = (int)RamSlider.Value;
            s.MaxParallelDownloads = (int)ParallelSlider.Value;
            s.HideLauncherWhilePlaying = HideLauncherCheck.IsChecked == true;
            s.CurseForgeApiKey = string.IsNullOrWhiteSpace(CurseForgeKeyBox.Text) ? null : CurseForgeKeyBox.Text.Trim();
            _core.Settings.Save();

            RefreshProviders(); // CurseForge anahtarı eklendiyse kaynak listesinde görünsün
            StatusText.Text = "✓ Ayarlar kaydedildi.";
        }

        private void OpenDataFolderBtn_Click(object sender, RoutedEventArgs e) => OpenPath(_core.Paths.Root);

        // ==================== ORTAK YARDIMCILAR ====================

        private async Task<bool> RunBusyAsync(string startMessage, Func<IProgress<ProgressInfo>, CancellationToken, Task> work)
        {
            if (_isBusy)
            {
                StatusText.Text = "Önce devam eden işlemin bitmesini bekle.";
                return false;
            }

            _isBusy = true;
            using var cts = _busyCts = new CancellationTokenSource();
            SetBusyUi(true);
            var progress = new Progress<ProgressInfo>(ShowProgress);
            ShowProgress(ProgressInfo.Step(startMessage));

            try
            {
                await work(progress, cts.Token);
                return true;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                StatusText.Text = "İşlem iptal edildi.";
                return false;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Bir sorun oluştu.";
                MessageBox.Show(FriendlyMessage(ex), "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                _isBusy = false;
                _busyCts = null;
                SetBusyUi(false);
            }
        }

        private void ShowProgress(ProgressInfo info)
        {
            if (!_isBusy) return;

            // Binlerce küçük dosyada arayüz donmasın diye saniyede en fazla ~25 güncelleme.
            var now = DateTime.UtcNow;
            bool important = info.Fraction is null or >= 1;
            if (!important && (now - _lastProgressUpdate).TotalMilliseconds < 40) return;
            _lastProgressUpdate = now;

            StatusText.Text = info.Message;
            BusyProgress.IsIndeterminate = info.Fraction == null;
            if (info.Fraction is { } f) BusyProgress.Value = f;
        }

        private void SetBusyUi(bool busy)
        {
            PlayBtn.IsEnabled = !busy && _gameProcess is not { HasExited: false };
            CreateProfileBtn.IsEnabled = !busy;
            BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            CancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!busy) BusyProgress.IsIndeterminate = false;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            _busyCts?.Cancel();
            StatusText.Text = "İptal ediliyor...";
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isBusy) return;
            if (MessageBox.Show("Bir işlem hâlâ devam ediyor. Yine de kapatılsın mı?", "Çıkış",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _busyCts?.Cancel();
        }

        private static string FriendlyMessage(Exception ex) => ex switch
        {
            HttpRequestException => "İnternete bağlanılamadı ya da sunucu cevap vermedi. Bağlantını kontrol edip tekrar dene.",
            TaskCanceledException or TimeoutException => "Sunucu çok geç cevap verdi. Biraz sonra tekrar dene.",
            UnsafePathException => ex.Message + " Bu paket güvenli değil, kurulmadı.",
            IOException when ex.InnerException is HttpRequestException => "İndirme başarısız: internet bağlantını kontrol et.",
            _ => ex.Message
        };

        private static void OpenPath(string pathOrUrl)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = pathOrUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
