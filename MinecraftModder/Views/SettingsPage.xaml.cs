using System.Windows;
using System.Windows.Controls;
using MinecraftModder.Core.Launch;
using MinecraftModder.Core.Settings;

namespace MinecraftModder.Views
{
    public partial class SettingsPage : UserControl
    {
        private readonly IShell _shell;
        private bool _loading;

        public SettingsPage(IShell shell)
        {
            InitializeComponent();
            _shell = shell;
            shell.ActiveProfileChanged += (_, _) => UpdateRamInfo();
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible) LoadFromSettings();
            };
        }

        private AppSettings Settings => _shell.Core.Settings.Current;

        private void LoadFromSettings()
        {
            _loading = true;
            var s = Settings;
            long total = RamAdvisor.TotalSystemMb();
            RamSlider.Maximum = Math.Max(1024, RamAdvisor.MaxAllowed(total) / 512 * 512);
            RamSlider.Value = Math.Min(s.ManualRamMb, RamSlider.Maximum);
            RamManualSwitch.IsChecked = s.RamMode == RamMode.Manual;
            ParallelSlider.Value = s.MaxParallelDownloads;
            HideLauncherSwitch.IsChecked = s.HideLauncherWhilePlaying;
            CurseForgeKeyBox.Text = s.CurseForgeApiKey ?? "";

            var session = _shell.Core.Accounts.Current;
            AccountText.Text = session == null ? "" : $"Giriş: {session.Username} ({(_shell.Core.Accounts.IsMicrosoft ? "Microsoft hesabı" : "çevrimdışı")})";
            DataFolderText.Text = $"Diskler ve oyun dosyaları: {_shell.Core.Paths.Root}";
            _loading = false;
            UpdateRamInfo();
        }

        private void UpdateRamInfo()
        {
            long total = RamAdvisor.TotalSystemMb();
            bool manual = RamManualSwitch.IsChecked == true;
            RamManualPanel.IsEnabled = manual;
            RamManualPanel.Opacity = manual ? 1 : 0.4;
            RamValueText.Text = $"{RamSlider.Value / 1024:0.#} GB";

            var profile = _shell.ActiveProfile;
            int auto = RamAdvisor.Decide(new AppSettings { RamMode = RamMode.Auto }, profile, total);
            RamInfoText.Text = $"Bilgisayarında {total / 1024.0:0.#} GB RAM var. Ayarı bize bırakırsan '{profile.Name}' için {auto / 1024.0:0.#} GB kullanılır; " +
                               "mod sayısına göre artar ve Windows'a her zaman pay bırakılır.";
        }

        private void Save()
        {
            if (_loading) return;
            _shell.Core.Settings.Save();
        }

        private void RamManualSwitch_Click(object sender, RoutedEventArgs e)
        {
            Settings.RamMode = RamManualSwitch.IsChecked == true ? RamMode.Manual : RamMode.Auto;
            Save();
            UpdateRamInfo();
        }

        private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RamValueText == null) return;
            RamValueText.Text = $"{e.NewValue / 1024:0.#} GB";
            if (_loading) return;
            Settings.ManualRamMb = (int)e.NewValue;
            Save();
        }

        private void ParallelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ParallelValueText == null) return;
            ParallelValueText.Text = $"{(int)e.NewValue} dosya";
            if (_loading) return;
            Settings.MaxParallelDownloads = (int)e.NewValue;
            Save();
        }

        private void HideLauncherSwitch_Click(object sender, RoutedEventArgs e)
        {
            Settings.HideLauncherWhilePlaying = HideLauncherSwitch.IsChecked == true;
            Save();
        }

        private void CurseForgeKeyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            string? key = string.IsNullOrWhiteSpace(CurseForgeKeyBox.Text) ? null : CurseForgeKeyBox.Text.Trim();
            if (key == Settings.CurseForgeApiKey) return;
            Settings.CurseForgeApiKey = key;
            Save();
            _shell.RefreshProviders(); // CurseForge kaynağı arama sayfalarında görünsün/kaybolsun
            _shell.Notify(key == null ? "CurseForge anahtarı kaldırıldı." : "✓ CurseForge anahtarı kaydedildi.");
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => _shell.OpenPath(_shell.Core.Paths.Root);

        private async void SignOut_Click(object sender, RoutedEventArgs e) => await _shell.SignOutAsync();
    }
}
