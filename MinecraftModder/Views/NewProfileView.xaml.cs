using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Profiles;

namespace MinecraftModder.Views
{
    public partial class NewProfileView : UserControl
    {
        private static readonly string[] FallbackGameVersions = { "1.21.1", "1.20.1", "1.19.2", "1.18.2", "1.16.5", "1.12.2" };

        private static readonly LoaderOption[] Loaders =
        {
            new(ModLoader.Fabric, "Fabric", "Önerilen. Modrinth'teki modların çoğu Fabric için."),
            new(ModLoader.Forge, "Forge", "Eski ve büyük modlar (ör. çoğu tech/büyü modu) için."),
            new(ModLoader.NeoForge, "NeoForge", "Forge'un yeni sürümü; 1.20.2 ve sonrası için."),
            new(ModLoader.Quilt, "Quilt", "Fabric modlarını da çalıştırır."),
            new(ModLoader.Vanilla, "Modsuz", "Orijinal Minecraft; sadece shader ve kaynak paketi."),
        };

        private readonly IShell _shell;
        private readonly GameProfile _preview = new() { Name = "Yeni" };
        private ModLoader _loader = ModLoader.Fabric;

        public NewProfileView(IShell shell)
        {
            InitializeComponent();
            _shell = shell;
            PreviewDisc.Profile = _preview;

            foreach (var option in Loaders)
            {
                var chip = new RadioButton { Content = option.Name, GroupName = "loader", Style = (Style)FindResource("Chip"), IsChecked = option.Loader == _loader };
                chip.Checked += (_, _) =>
                {
                    _loader = option.Loader;
                    LoaderHint.Text = option.Hint;
                };
                LoaderChips.Children.Add(chip);
            }
            LoaderHint.Text = Loaders[0].Hint;

            Loaded += async (_, _) =>
            {
                NameBox.Focus();
                await LoadVersionsAsync();
            };
        }

        public event EventHandler? CloseRequested;

        private async Task LoadVersionsAsync()
        {
            IReadOnlyList<string> versions;
            try
            {
                versions = await _shell.Core.GameVersions.GetReleaseVersionsAsync();
            }
            catch
            {
                versions = FallbackGameVersions;
            }
            VersionCombo.ItemsSource = versions;
            VersionCombo.SelectedItem = versions.Contains(_shell.Core.Settings.Current.DefaultGameVersion)
                ? _shell.Core.Settings.Current.DefaultGameVersion
                : versions.FirstOrDefault();
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Önizleme diskinin ortasındaki harf isimle değişsin.
            _preview.Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "Yeni";
            _preview.Id = _preview.Name;
            PreviewDisc.Reload();
        }

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Create_Click(sender, e);
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (name.Length == 0)
            {
                _shell.Notify("Diske bir isim ver.");
                NameBox.Focus();
                return;
            }
            if (VersionCombo.SelectedItem is not string version)
            {
                _shell.Notify("Sürüm listesi yükleniyor, bir saniye...");
                return;
            }

            var profile = _shell.Core.Profiles.Create(name, version, _loader);
            _shell.SetActiveProfile(profile);
            _shell.Notify($"✓ '{profile.Name}' hazır ve takıldı. Şimdi mod ekleyebilirsin!");
            CloseRequested?.Invoke(this, EventArgs.Empty);
            _shell.ShowPage(_loader.SupportsMods() ? ShellPage.Mods : ShellPage.Looks);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
