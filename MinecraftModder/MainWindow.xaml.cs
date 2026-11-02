using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MinecraftModder.Core;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Launch;
using MinecraftModder.Core.Mods;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;
using MinecraftModder.Views;

namespace MinecraftModder
{
    // Ana pencere: giriş ekranı, sayfalar, alt bar + Oyna bloğu, bilgi balonu ve soru kutusu.
    // Tüm iş (indirme, kurulum, profil, başlatma) MinecraftModder.Core'da; sayfalar IShell üzerinden konuşur.
    public partial class MainWindow : Window, IShell
    {
        private readonly LauncherCore _core;
        private readonly Dictionary<ShellPage, UserControl> _pages = new();
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };
        private GameProfile _activeProfile;

        // Uzun işlemler aynı anda tek bir tane çalışır.
        private CancellationTokenSource? _busyCts;
        private bool _isBusy;
        private bool _launching;
        private DateTime _lastProgressUpdate;

        private Process? _gameProcess;
        private TaskCompletionSource<int>? _dialog;
        private bool _closeConfirmed;

        public MainWindow()
        {
            InitializeComponent();
            // Geliştirirken gerçek profillere dokunmamak için veri klasörü ortam değişkeniyle değiştirilebilir.
            _core = LauncherCore.Create(Environment.GetEnvironmentVariable("MINECRAFTMODDER_DATA"));
            _activeProfile = _core.EnsureActiveProfile(); // sayfalar oluşurken takılı disk belli olmalı
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                if (!_isBusy) HideToast();
            };

            _pages[ShellPage.Discover] = new DiscoverPage(this);
            _pages[ShellPage.Mods] = new BrowsePage(this, "Modlar", new[] { (ContentKind.Mod, "Modlar") }, (DataTemplate)FindResource("ModCardTemplate"));
            _pages[ShellPage.Packs] = new BrowsePage(this, "Paketler", new[] { (ContentKind.Modpack, "Paketler") }, (DataTemplate)FindResource("ModCardTemplate"));
            _pages[ShellPage.Looks] = new BrowsePage(this, "Görünüm",
                new[] { (ContentKind.ShaderPack, "Shader'lar"), (ContentKind.ResourcePack, "Kaynak paketleri") }, (DataTemplate)FindResource("LookCardTemplate"));
            _pages[ShellPage.Discs] = new ProfilesPage(this);
            _pages[ShellPage.Settings] = new SettingsPage(this);
            foreach (var page in _pages.Values)
            {
                page.Visibility = Visibility.Collapsed;
                PageHost.Children.Add(page);
            }
        }

        public LauncherCore Core => _core;
        public GameProfile ActiveProfile => _activeProfile;
        public bool IsBusy => _isBusy;
        public bool IsGameRunning => _gameProcess is { HasExited: false };
        public string ActiveProfileName => _activeProfile?.Name ?? "";

        public event EventHandler? ActiveProfileChanged;
        public event EventHandler? ContentChanged;

        // ==================== AÇILIŞ VE GİRİŞ ====================

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateActiveProfileUi(animate: false);
            LoginDisc.Profile = new GameProfile { Id = "login", Name = "M" };
            OfflineNameBox.Text = _core.Settings.Current.LastOfflineUsername ?? "Oyuncu";

            await ShowRememberedAccountAsync();
        }

        // Daha önce Microsoft ile girildiyse "Bu hesapla devam et" gösterilir.
        private async Task ShowRememberedAccountAsync()
        {
            LoginStatus.Text = "Kayıtlı hesap kontrol ediliyor...";
            var session = await _core.Accounts.TrySilentLoginAsync();
            MsLoggedOutPanel.Visibility = session == null ? Visibility.Visible : Visibility.Collapsed;
            MsLoggedInPanel.Visibility = session == null ? Visibility.Collapsed : Visibility.Visible;
            if (session != null) RememberedText.Text = session.Username;
            LoginStatus.Text = "";
        }

        private async void MsLoginBtn_Click(object sender, RoutedEventArgs e)
        {
            MsLoginBtn.IsEnabled = false;
            LoginStatus.Text = "Açılan Microsoft penceresinde giriş yap...";
            try
            {
                await _core.Accounts.LoginInteractivelyAsync();
                SwitchToMainScreen();
            }
            catch (Exception ex)
            {
                LoginStatus.Text = "Giriş yapılamadı: " + FriendlyMessage(ex);
            }
            finally
            {
                MsLoginBtn.IsEnabled = true;
            }
        }

        private void MsContinueBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_core.Accounts.Current != null) SwitchToMainScreen();
        }

        private async void MsForgetBtn_Click(object sender, RoutedEventArgs e)
        {
            await _core.Accounts.SignOutAsync();
            MsLoggedInPanel.Visibility = Visibility.Collapsed;
            MsLoggedOutPanel.Visibility = Visibility.Visible;
            LoginStatus.Text = "Hesap unutuldu.";
        }

        private void OfflineNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OfflineLoginBtn_Click(sender, e);
        }

        private void OfflineLoginBtn_Click(object sender, RoutedEventArgs e)
        {
            string username = OfflineNameBox.Text.Trim();
            try
            {
                _core.Accounts.UseOffline(username);
            }
            catch (ArgumentException ex)
            {
                LoginStatus.Text = ex.Message;
                return;
            }

            _core.Settings.Current.LastOfflineUsername = username;
            _core.Settings.Save();
            SwitchToMainScreen();
        }

        private void SwitchToMainScreen()
        {
            var session = _core.Accounts.Current!;
            AccountText.Text = _core.Accounts.IsMicrosoft ? $"{session.Username} · Microsoft" : $"{session.Username} · çevrimdışı";
            ((DiscoverPage)_pages[ShellPage.Discover]).SetUserName(session.Username ?? "Oyuncu");
            LoginScreen.Visibility = Visibility.Collapsed;
            LoginDisc.AlwaysSpin = false;
            MainScreen.Visibility = Visibility.Visible;
            TabDiscover.IsChecked = true;
        }

        public async Task SignOutAsync()
        {
            // Çevrimdışı oyuncu çıkınca kayıtlı Microsoft hesabı silinmesin.
            if (_core.Accounts.IsMicrosoft) await _core.Accounts.SignOutAsync();
            MainScreen.Visibility = Visibility.Collapsed;
            LoginScreen.Visibility = Visibility.Visible;
            LoginDisc.AlwaysSpin = true;
            await ShowRememberedAccountAsync();
        }

        // ==================== SAYFALAR ====================

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out ShellPage page)) ShowPageCore(page);
        }

        public void ShowPage(ShellPage page)
        {
            var tab = page switch
            {
                ShellPage.Mods => TabMods,
                ShellPage.Packs => TabPacks,
                ShellPage.Looks => TabLooks,
                ShellPage.Discs => TabDiscs,
                ShellPage.Settings => TabSettings,
                _ => TabDiscover
            };
            if (tab.IsChecked == true) ShowPageCore(page);
            else tab.IsChecked = true; // Tab_Checked sayfayı gösterir
        }

        private void ShowPageCore(ShellPage page)
        {
            foreach (var (key, control) in _pages)
            {
                bool show = key == page;
                if (show && control.Visibility != Visibility.Visible)
                {
                    control.Visibility = Visibility.Visible;
                    control.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                }
                else if (!show)
                {
                    control.Visibility = Visibility.Collapsed;
                }
            }
        }

        public void RefreshProviders()
        {
            foreach (var page in _pages.Values.OfType<BrowsePage>()) page.BuildProviderChips();
        }

        // ==================== TAKILI DİSK (aktif profil) ====================

        public void SetActiveProfile(GameProfile profile)
        {
            bool changed = profile.Id != _activeProfile?.Id;
            _activeProfile = profile;
            _core.SetActiveProfile(profile);
            UpdateActiveProfileUi(animate: changed);
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateActiveProfileUi(bool animate)
        {
            PillName.Text = _activeProfile.Name;
            PillVersion.Text = _activeProfile.DisplayVersion;
            PillDisc.Profile = _activeProfile;
            PillDisc.Reload();
            PlayBlock.InsertDisc(_activeProfile, animate);
            PlayBlock.ToolTip = $"Oyna: {_activeProfile.Name} ({_activeProfile.DisplayVersion})";
        }

        private void DiscPill_Click(object sender, RoutedEventArgs e)
        {
            var active = _activeProfile;
            PopupDiscs.ItemsSource = _core.Profiles.All.Select(p => new ProfileView(p, p.Id == active.Id)).ToList();
            DiscPopup.IsOpen = true;
        }

        private void PopupDisc_Click(object sender, RoutedEventArgs e)
        {
            DiscPopup.IsOpen = false;
            if (sender is not FrameworkElement { Tag: ProfileView view } || view.IsActive) return;
            if (_isBusy)
            {
                Notify("Bir işlem sürerken disk değiştirilemez.");
                return;
            }
            SetActiveProfile(view.Profile);
            Notify($"'{view.Profile.Name}' takıldı.");
        }

        private void AllDiscs_Click(object sender, RoutedEventArgs e)
        {
            DiscPopup.IsOpen = false;
            ShowPage(ShellPage.Discs);
        }

        public void RaiseContentChanged()
        {
            PlayBlock.RefreshDisc();
            PillDisc.Reload();
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        // ==================== OYNA ====================

        private void PlayBlock_Click(object sender, RoutedEventArgs e) => Play();

        public async void Play()
        {
            if (IsGameRunning)
            {
                Notify("Oyun zaten açık.");
                return;
            }
            var session = _core.Accounts.Current;
            if (session == null)
            {
                await SignOutAsync();
                return;
            }

            var profile = _activeProfile;
            LaunchResult? launch = null;
            _launching = true;
            PlayBlock.SetState("Hazırlanıyor", spinDisc: true);
            bool ok;
            try
            {
                ok = await RunBusyAsync($"'{profile.Name}' hazırlanıyor...",
                    async (progress, ct) => launch = await _core.Launcher.LaunchAsync(profile, session, progress, ct));
            }
            finally
            {
                _launching = false;
            }
            if (!ok || launch == null)
            {
                PlayBlock.SetState(null, spinDisc: false);
                return;
            }

            var process = launch.Process;
            _gameProcess = process;
            var startedAt = DateTimeOffset.Now;
            PlayBlock.SetState("Oyunda", spinDisc: true);
            if (!launch.Compatibility.IsClean) RaiseContentChanged();
            Notify(launch.Compatibility.Fixed.Count == 0
                ? $"İyi oyunlar! ({profile.Name})"
                : $"İyi oyunlar! Açmadan önce {launch.Compatibility.Fixed.Count} şeyi düzelttim: {launch.Compatibility.Fixed[0]}");

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.Invoke(() => OnGameExited(profile, process, startedAt));
            if (process.HasExited) OnGameExited(profile, process, startedAt);
            else if (_core.Settings.Current.HideLauncherWhilePlaying) WindowState = WindowState.Minimized;
        }

        private async void OnGameExited(GameProfile profile, Process process, DateTimeOffset startedAt)
        {
            if (_gameProcess != process) return;
            _gameProcess = null;
            PlayBlock.SetState(null, spinDisc: false);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();

            int exitCode;
            try { exitCode = process.ExitCode; } catch { exitCode = 0; }
            if (exitCode == 0) return;

            string? crashReport = GameLauncher.FindLatestCrashReport(profile, startedAt);
            string message = "Genelde sebep uyumsuz ya da bozuk bir moddur. Diski düzenleyip 'Onar'a bas; " +
                             "sorun sürerse en son kurduğun modu kapatıp tekrar dene.";
            var buttons = crashReport == null ? new[] { "Diski düzenle", "Tamam" } : new[] { "Diski düzenle", "Çökme raporu", "Tamam" };
            int answer = await AskAsync($"Oyun kapandı (kod {exitCode})", message, buttons);
            if (answer == 0) OpenProfileDetail(profile);
            else if (answer == 1 && crashReport != null) OpenPath(crashReport);
        }

        // ==================== SÜRÜKLE-BIRAK: HER TÜRLÜ DOSYA ====================

        // Kullanıcı nereden indirdiği belli olmayan mod/shader/paket dosyasını pencereye bırakır; gerisini launcher halleder.
        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (MainScreen.Visibility != Visibility.Visible || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            await ImportFilesAsync(files);
        }

        public async Task ImportFilesAsync(IReadOnlyList<string> files)
        {
            var profile = _activeProfile;
            ImportResult? result = null;
            bool ok = await RunBusyAsync("Dosyalar ekleniyor...",
                async (progress, ct) => result = await _core.Importer.ImportAsync(profile, files, progress, ct));
            if (!ok || result == null) return;

            RaiseContentChanged();
            if (result.NewDisks.Count > 0) SetActiveProfile(result.NewDisks[^1]);

            var text = new StringBuilder();
            if (result.Added.Count > 0) text.AppendLine($"'{profile.Name}' diskine eklendi:\n• " + string.Join("\n• ", result.Added) + "\n");
            if (result.Compatibility.Fixed.Count > 0) text.AppendLine("Kendiliğimden düzelttiklerim:\n• " + string.Join("\n• ", result.Compatibility.Fixed) + "\n");
            if (result.Skipped.Count > 0) text.AppendLine("Eklenemeyenler:\n• " + string.Join("\n• ", result.Skipped));

            if (result.Suggestion is { } suggestion)
            {
                string where = suggestion.ExistingDisk != null ? $"'{suggestion.ExistingDisk.Name}' diskine" : $"yeni bir '{suggestion.SuggestedName}'e";
                text.AppendLine($"\n{suggestion.FileNames.Count} mod bu diske uymuyor; {suggestion.Loader} {suggestion.GameVersion} istiyor. Onları {where} taşıyayım mı?");
                if (await AskAsync("Dosyalar eklendi", text.ToString().Trim(), "Evet, taşı", "Hayır") != 0) return;

                (GameProfile Disk, ImportResult Result)? moved = null;
                if (!await RunBusyAsync("Yeni disk hazırlanıyor...",
                        async (progress, ct) => moved = await _core.Importer.MoveToSuggestedDiskAsync(profile, suggestion, progress, ct))
                    || moved == null) return;
                SetActiveProfile(moved.Value.Disk);
                RaiseContentChanged();
                Notify($"✓ '{moved.Value.Disk.Name}' takıldı. Oynamak için bloğa bas!");
                return;
            }

            bool onlyAdded = result.Skipped.Count == 0 && result.Compatibility.IsClean;
            if (onlyAdded) Notify($"✓ {result.Added.Count} dosya '{profile.Name}' diskine eklendi.");
            else await AskAsync("Dosyalar eklendi", text.ToString().Trim(), "Tamam");
        }

        // ==================== KARTLAR: KUR / FAVORİ ====================

        public bool IsInstalled(ContentItem item)
        {
            if (_activeProfile == null) return false;
            if (item.Kind == ContentKind.Modpack)
                return _core.Profiles.All.Any(p => p.Source?.ProviderId == item.ProviderId && p.Source.ProjectId == item.Id);
            return ContentInstaller.IsInstalled(_activeProfile, item);
        }

        public bool IsFavorite(ContentItem item) => _core.Settings.Current.FavoriteKeys.Contains(item.Key);

        public void ToggleFavorite(CardViewModel card)
        {
            var favorites = _core.Settings.Current.FavoriteKeys;
            bool added = !favorites.Remove(card.Item.Key);
            if (added) favorites.Insert(0, card.Item.Key);
            _core.Settings.Save();
            Notify(added ? $"♥ {card.Item.Title} favorilere eklendi (Keşfet'te görünür)." : $"{card.Item.Title} favorilerden çıkarıldı.");
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task InstallAsync(CardViewModel card)
        {
            if (card.IsBusy) return;
            if (_isBusy)
            {
                Notify("Önce devam eden işin bitmesini bekle.");
                return;
            }
            if (card.Item.Kind == ContentKind.Modpack)
            {
                await InstallModpackAsync(card);
                return;
            }
            if (IsGameRunning)
            {
                await AskAsync("Oyun açık", "Oyun açıkken mod eklenemez. Önce oyunu kapat.", "Tamam");
                return;
            }

            var item = card.Item;
            var profile = _activeProfile;
            ContentInstallResult? result = null;
            card.IsBusy = true;
            bool ok;
            try
            {
                ok = await RunBusyAsync($"{item.Title} kuruluyor...",
                    async (progress, ct) => result = await _core.Installer.InstallAsync(profile, item, progress, ct));
            }
            finally
            {
                card.IsBusy = false;
            }
            RaiseContentChanged();
            if (!ok || result == null) return;

            // Her şey yolundaysa kutu açıp yormuyoruz; sadece bilgi balonunda yazıyor.
            if (result.Skipped.Count == 0 && result.Warnings.Count == 0)
            {
                int extras = result.Installed.Count - 1;
                Notify(result.Installed.Count == 0
                    ? $"{item.Title} zaten kurulu."
                    : $"✓ {item.Title} '{profile.Name}' diskine kuruldu" + (extras > 0 ? $" (+{extras} gerekli ek)" : "") + ".");
                return;
            }

            var text = new StringBuilder();
            if (result.Installed.Count > 0) text.AppendLine("Kurulanlar:\n• " + string.Join("\n• ", result.Installed) + "\n");
            if (result.Skipped.Count > 0) text.AppendLine("Kurulamayanlar:\n• " + string.Join("\n• ", result.Skipped.Select(s => $"{s.Title}: {s.Reason}")) + "\n");
            if (result.Warnings.Count > 0) text.AppendLine("Not:\n• " + string.Join("\n• ", result.Warnings));
            string title = result.Installed.Count > 0 ? $"{item.Title} kuruldu" : $"{item.Title} kurulamadı";

            var withPage = result.Skipped.FirstOrDefault(s => s.WebUrl != null);
            if (withPage != null)
            {
                if (await AskAsync(title, text.ToString().Trim(), "Sayfasını aç", "Tamam") == 0) OpenPath(withPage.WebUrl!);
            }
            else
            {
                await AskAsync(title, text.ToString().Trim(), "Tamam");
            }
        }

        private async Task InstallModpackAsync(CardViewModel card)
        {
            var item = card.Item;
            if (_core.Content.Get(item.ProviderId) is not IModpackProvider provider) return;

            if (await AskAsync(item.Title, "Bu paket yeni bir disk olarak kurulacak. Diğer disklerin hiç etkilenmez.\n\nBüyük paketlerin inmesi birkaç dakika sürebilir.",
                    "Kur", "Vazgeç") != 0)
                return;

            ModpackInstallResult? result = null;
            card.IsBusy = true;
            bool ok;
            try
            {
                ok = await RunBusyAsync($"{item.Title} kuruluyor...",
                    async (progress, ct) => result = await _core.Modpacks.InstallAsync(provider, item, null, progress, ct));
            }
            finally
            {
                card.IsBusy = false;
            }
            if (!ok || result == null) return;

            var profile = result.Profile;
            SetActiveProfile(profile);
            RaiseContentChanged();

            string message = $"'{profile.Name}' diski hazır ve takıldı ({profile.DisplayVersion}).";
            if (result.Downloads.Failed.Count > 0)
            {
                message += $"\n\n{result.Downloads.Failed.Count} dosya inemedi. Diski düzenleyip 'Onar' ile tekrar deneyebilirsin:\n• " +
                           string.Join("\n• ", result.Downloads.Failed.Take(5).Select(f => f.Item.DisplayName));
            }
            else
            {
                message += "\n\nİlk açılış biraz uzun sürebilir (mod yükleyici kuruluyor).";
            }
            if (await AskAsync("Paket kuruldu!", message, "Hemen oyna", "Sonra") == 0) Play();
        }

        // ==================== DİSK DETAYI / YENİ DİSK ====================

        public void OpenProfileDetail(GameProfile profile)
        {
            var view = new ProfileDetailView(this, profile);
            view.CloseRequested += (_, _) => CloseOverlay();
            ShowOverlay(view);
        }

        public void OpenNewProfile()
        {
            var view = new NewProfileView(this);
            view.CloseRequested += (_, _) => CloseOverlay();
            ShowOverlay(view);
        }

        private void ShowOverlay(UserControl content)
        {
            OverlayHost.Content = content;
            OverlayLayer.Visibility = Visibility.Visible;
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            OverlayScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            OverlayScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            OverlayLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        private void CloseOverlay()
        {
            if (OverlayLayer.Visibility != Visibility.Visible) return;
            Keyboard.ClearFocus(); // isim kutusundaki değişiklik kaydedilsin (LostFocus)
            OverlayLayer.Visibility = Visibility.Collapsed;
            OverlayHost.Content = null;
            ((ProfilesPage)_pages[ShellPage.Discs]).Refresh();
        }

        private void OverlayLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Sadece karartılmış boşluğa tıklayınca kapanır; panelin içi değil.
            if (ReferenceEquals(e.OriginalSource, OverlayLayer)) CloseOverlay();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (DialogLayer.Visibility == Visibility.Visible) CloseDialog(-1);
            else if (OverlayLayer.Visibility == Visibility.Visible) CloseOverlay();
            else return;
            e.Handled = true;
        }

        // ==================== SORU KUTUSU / BİLGİ BALONU ====================

        public Task<int> AskAsync(string title, string message, params string[] buttons)
        {
            _dialog?.TrySetResult(-1);
            var tcs = _dialog = new TaskCompletionSource<int>();

            DialogTitle.Text = title;
            DialogMessage.Text = message;
            DialogButtons.Children.Clear();
            if (buttons.Length == 0) buttons = new[] { "Tamam" };
            // Birinci buton (önerilen) renkli ve en sağda dursun.
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                int index = i;
                var button = new Button
                {
                    Content = buttons[i],
                    Style = (Style)FindResource(i == 0 ? "Btn" : "SoftBtn"),
                    Margin = new Thickness(DialogButtons.Children.Count == 0 ? 0 : 8, 0, 0, 0)
                };
                button.Click += (_, _) => CloseDialog(index);
                DialogButtons.Children.Add(button);
            }

            DialogLayer.Visibility = Visibility.Visible;
            DialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            ((Button)DialogButtons.Children[^1]).Focus();
            return tcs.Task;
        }

        private void CloseDialog(int result)
        {
            DialogLayer.Visibility = Visibility.Collapsed;
            var tcs = _dialog;
            _dialog = null;
            tcs?.TrySetResult(result);
        }

        public void Notify(string text)
        {
            ToastText.Text = text;
            ShowToast();
            _toastTimer.Stop();
            if (!_isBusy) _toastTimer.Start();
        }

        private void ShowToast()
        {
            if (Toast.Visibility == Visibility.Visible) return;
            Toast.Visibility = Visibility.Visible;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        private void HideToast() => Toast.Visibility = Visibility.Collapsed;

        // ==================== UZUN İŞLEMLER ====================

        public async Task<bool> RunBusyAsync(string startMessage, Func<IProgress<ProgressInfo>, CancellationToken, Task> work)
        {
            if (_isBusy)
            {
                Notify("Önce devam eden işin bitmesini bekle.");
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
                Notify("İşlem iptal edildi.");
                return false;
            }
            catch (Exception ex)
            {
                await AskAsync("Bir sorun oldu", FriendlyMessage(ex), "Tamam");
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

            ToastText.Text = info.Message;
            BusyProgress.IsIndeterminate = info.Fraction == null;
            if (info.Fraction is { } f) BusyProgress.Value = f;
            if (_launching) PlayBlock.SetState(info.Fraction is { } p and < 1 ? $"%{p * 100:0}" : "Hazırlanıyor", spinDisc: true);
        }

        private void SetBusyUi(bool busy)
        {
            BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            CancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                _toastTimer.Stop();
                ShowToast();
            }
            else
            {
                BusyProgress.IsIndeterminate = false;
                _toastTimer.Start();
            }
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            _busyCts?.Cancel();
            ToastText.Text = "İptal ediliyor...";
        }

        private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isBusy || _closeConfirmed) return;
            e.Cancel = true;
            if (await AskAsync("Bir iş hâlâ sürüyor", "Yine de kapatılsın mı? Yarım kalan indirme bir sonraki seferde tamamlanır.", "Kapat", "Bekle") != 0) return;
            _closeConfirmed = true;
            _busyCts?.Cancel();
            Close();
        }

        public string FriendlyMessage(Exception ex) => ex switch
        {
            HttpRequestException => "İnternete bağlanılamadı ya da sunucu cevap vermedi. Bağlantını kontrol edip tekrar dene.",
            TaskCanceledException or TimeoutException => "Sunucu çok geç cevap verdi. Biraz sonra tekrar dene.",
            UnsafePathException => ex.Message + " Bu paket güvenli değil, kurulmadı.",
            IOException when ex.InnerException is HttpRequestException => "İndirme başarısız: internet bağlantını kontrol et.",
            _ => ex.Message
        };

        public void OpenPath(string pathOrUrl)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = pathOrUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Notify($"Açılamadı: {ex.Message}");
            }
        }

        // Pencerenin başlık çubuğu da koyu olsun (Windows 10 20H1+ / 11).
        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
            }
            catch
            {
                // Eski Windows: başlık çubuğu açık renk kalır, sorun değil.
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
