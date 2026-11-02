using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MinecraftModder.Core.Install;

namespace MinecraftModder.Views
{
    // Disk rafı: her profil bir CD. Tıklayınca o disk Oyna bloğuna takılır (aktif profil olur).
    public partial class ProfilesPage : UserControl
    {
        private readonly IShell _shell;

        public ProfilesPage(IShell shell)
        {
            InitializeComponent();
            _shell = shell;
            shell.ActiveProfileChanged += (_, _) => Refresh();
            shell.Core.Profiles.Changed += (_, _) => Dispatcher.BeginInvoke(Refresh);
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible) Refresh();
            };
        }

        public void Refresh()
        {
            var active = _shell.ActiveProfile;
            Discs.ItemsSource = _shell.Core.Profiles.All.Select(p => new ProfileView(p, p.Id == active.Id)).ToList();
        }

        private void Disc_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) != null) return;
            if (sender is not FrameworkElement { Tag: ProfileView view }) return;

            // Takılı diske tekrar tıklamak onu düzenlemeye açar.
            if (view.IsActive)
            {
                _shell.OpenProfileDetail(view.Profile);
                return;
            }
            if (_shell.IsBusy)
            {
                _shell.Notify("Bir işlem sürerken disk değiştirilemez.");
                return;
            }
            _shell.SetActiveProfile(view.Profile);
            _shell.Notify($"'{view.Profile.Name}' takıldı. Oynamak için bloğa bas!");
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ProfileView view }) _shell.OpenProfileDetail(view.Profile);
        }

        private void NewDisc_Click(object sender, RoutedEventArgs e) => _shell.OpenNewProfile();

        private async void ImportPack_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Modrinth paketi (*.mrpack)|*.mrpack", Title = "Paket dosyası seç" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            ModpackInstallResult? result = null;
            bool ok = await _shell.RunBusyAsync("Paket açılıyor...",
                async (progress, ct) => result = await _shell.Core.Modpacks.InstallLocalMrpackAsync(dialog.FileName, progress, ct));
            if (!ok || result == null) return;

            _shell.SetActiveProfile(result.Profile);
            _shell.Notify($"✓ '{result.Profile.Name}' diski hazır ve takıldı.");
            if (result.Downloads.Failed.Count > 0)
                await _shell.AskAsync("Bazı dosyalar inemedi",
                    $"{result.Downloads.Failed.Count} dosya indirilemedi. Diski düzenleyip 'Onar'a basarak tekrar deneyebilirsin.", "Tamam");
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            for (var node = child; node != null;
                 node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node is T match) return match;
            return null;
        }
    }
}
