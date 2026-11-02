using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Launch;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Settings;

namespace MinecraftModder.Views
{
    // Diske "yakınlaşınca" açılan düzenleme paneli: içindekiler (toplu aç/kapat/sil), Mod Doktoru,
    // diske özel RAM, isim değiştirme ve silme.
    public partial class ProfileDetailView : UserControl
    {
        private readonly IShell _shell;
        private readonly GameProfile _profile;
        private bool _loading = true;

        public ProfileDetailView(IShell shell, GameProfile profile)
        {
            InitializeComponent();
            _shell = shell;
            _profile = profile;

            Disc.Profile = profile;
            NameBox.Text = profile.Name;
            VersionText.Text = profile.IsModpack
                ? $"Minecraft {profile.DisplayVersion} · {profile.Source?.VersionName ?? "paket"}"
                : $"Minecraft {profile.DisplayVersion}";

            long total = RamAdvisor.TotalSystemMb();
            RamSlider.Maximum = Math.Max(1024, RamAdvisor.MaxAllowed(total) / 512 * 512);
            RamSlider.Value = Math.Min(profile.RamMb ?? RamAdvisor.Decide(new AppSettings(), profile, total), RamSlider.Maximum);
            RamSwitch.IsChecked = profile.RamMb != null;
            _loading = false;
            UpdateRamUi();
            UpdateUseButton();

            shell.ActiveProfileChanged += OnActiveProfileChanged;
            Unloaded += (_, _) => shell.ActiveProfileChanged -= OnActiveProfileChanged;
            Loaded += async (_, _) => await RefreshEntriesAsync();
        }

        public event EventHandler? CloseRequested;

        private void OnActiveProfileChanged(object? sender, EventArgs e) => UpdateUseButton();

        private void UpdateUseButton()
        {
            bool active = _shell.ActiveProfile.Id == _profile.Id;
            UseBtnText.Text = active ? "Oyna!" : "Bu diski tak";
        }

        // ==================== İçindekiler ====================

        private async Task RefreshEntriesAsync()
        {
            SummaryText.Text = "Taranıyor...";
            IReadOnlyList<ContentEntry> entries;
            try
            {
                entries = await _shell.Core.ProfileContent.RefreshAsync(_profile);
            }
            catch (Exception ex)
            {
                SummaryText.Text = "Liste okunamadı: " + _shell.FriendlyMessage(ex);
                return;
            }

            var views = entries
                .OrderBy(e => e.Kind)
                .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new ContentEntryView(e, ProfileContentService.IsMissing(_profile, e)))
                .ToList();
            Entries.ItemsSource = views;
            Disc.Reload();

            int mods = entries.Count(e => e.Kind == ContentKind.Mod);
            int shaders = entries.Count(e => e.Kind == ContentKind.ShaderPack);
            int packs = entries.Count(e => e.Kind == ContentKind.ResourcePack);
            int disabled = entries.Count(e => !e.Enabled);
            int missing = views.Count(v => v.IsMissing);

            var parts = new List<string> { $"{mods} mod" };
            if (shaders > 0) parts.Add($"{shaders} shader");
            if (packs > 0) parts.Add($"{packs} kaynak paketi");
            if (disabled > 0) parts.Add($"{disabled} kapalı");
            if (missing > 0) parts.Add($"{missing} eksik → Onar'a bas");
            var duplicates = ProfileDoctor.FindDuplicates(_profile);
            if (duplicates.Count > 0) parts.Add($"⚠ {duplicates.Count} mod iki kez kurulu");
            SummaryText.Text = entries.Count == 0
                ? (_profile.Loader.SupportsMods() ? "Bu disk henüz boş. '+ Mod ekle' ile başla!" : "Bu modsuz (Vanilla) bir disk; shader ve kaynak paketi eklenebilir.")
                : string.Join(" · ", parts);
            UpdateSelectionUi();
        }

        private List<ContentEntry> Selected() => Entries.SelectedItems.Cast<ContentEntryView>().Select(v => v.Entry).ToList();

        private void Entries_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionUi();

        private void UpdateSelectionUi()
        {
            int count = Entries.SelectedItems.Count;
            SelectAll.IsChecked = count > 0 && count == Entries.Items.Count;
            EnableBtn.IsEnabled = DisableBtn.IsEnabled = RemoveBtn.IsEnabled = count > 0;
            RemoveBtn.Content = count > 1 ? $"Sil ({count})" : "Sil";
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (SelectAll.IsChecked == true) Entries.SelectAll();
            else Entries.UnselectAll();
        }

        private async void Enable_Click(object sender, RoutedEventArgs e) => await SetEnabledAsync(Selected(), true);
        private async void Disable_Click(object sender, RoutedEventArgs e) => await SetEnabledAsync(Selected(), false);

        private async void EntrySwitch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox { Tag: ContentEntryView view } box)
                await SetEnabledAsync(new List<ContentEntry> { view.Entry }, box.IsChecked == true);
        }

        private async Task SetEnabledAsync(List<ContentEntry> entries, bool enabled)
        {
            if (entries.Count == 0) return;
            await ApplyChangeAsync(() => _shell.Core.ProfileContent.SetEnabled(_profile, entries, enabled));
            if (entries.Count > 1) _shell.Notify($"{entries.Count} öğe {(enabled ? "açıldı" : "kapatıldı")}.");
        }

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var selected = Selected();
            if (selected.Count == 0) return;

            string warning = selected.Any(s => s.Origin is ContentOrigin.Pack or ContentOrigin.PackOverride)
                ? "\n\nDikkat: bir kısmı paketin parçası; silersen paket düzgün çalışmayabilir. Silmek yerine kapatmak daha güvenli."
                : "";
            int answer = await _shell.AskAsync("Silinsin mi?", $"{selected.Count} öğe bu diskten kalıcı olarak silinecek.{warning}", "Sil", "Vazgeç");
            if (answer != 0) return;

            await ApplyChangeAsync(() => _shell.Core.ProfileContent.Remove(_profile, selected));
            _shell.Notify($"{selected.Count} öğe silindi.");
        }

        private async Task ApplyChangeAsync(Action change)
        {
            if (_shell.IsGameRunning && _shell.ActiveProfile.Id == _profile.Id)
            {
                await _shell.AskAsync("Oyun açık", "Oyun açıkken modlar değiştirilemez. Önce oyunu kapat.", "Tamam");
                await RefreshEntriesAsync();
                return;
            }
            try
            {
                change();
            }
            catch (Exception ex)
            {
                await _shell.AskAsync("Değiştirilemedi", _shell.FriendlyMessage(ex), "Tamam");
            }
            await RefreshEntriesAsync();
            _shell.RaiseContentChanged();
        }

        private void AddMods_Click(object sender, RoutedEventArgs e)
        {
            if (_shell.ActiveProfile.Id != _profile.Id) _shell.SetActiveProfile(_profile);
            CloseRequested?.Invoke(this, EventArgs.Empty);
            _shell.ShowPage(_profile.Loader.SupportsMods() ? ShellPage.Mods : ShellPage.Looks);
        }

        // ==================== Disk işlemleri ====================

        private void Use_Click(object sender, RoutedEventArgs e)
        {
            if (_shell.ActiveProfile.Id == _profile.Id)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                _shell.Play();
                return;
            }
            if (_shell.IsBusy)
            {
                _shell.Notify("Bir işlem sürerken disk değiştirilemez.");
                return;
            }
            _shell.SetActiveProfile(_profile);
            _shell.Notify($"'{_profile.Name}' takıldı.");
        }

        private async void Repair_Click(object sender, RoutedEventArgs e)
        {
            RepairReport? report = null;
            bool ok = await _shell.RunBusyAsync("Dosyalar kontrol ediliyor...",
                async (progress, ct) => report = await _shell.Core.Doctor.VerifyAndRepairAsync(_profile, progress, ct));
            await RefreshEntriesAsync();
            if (!ok || report == null) return;

            if (report.IsHealthy)
            {
                _shell.Notify($"✓ Her şey yolunda! {report.Checked} dosya kontrol edildi.");
                return;
            }

            var text = new StringBuilder();
            if (report.Repaired > 0) text.AppendLine($"✓ {report.Repaired} bozuk/eksik dosya yeniden indirildi.\n");
            if (report.Failed.Count > 0) text.AppendLine("İndirilemeyenler:\n• " + string.Join("\n• ", report.Failed.Take(8)) + "\n");
            if (report.Unrepairable.Count > 0) text.AppendLine("Nereden geldiği bilinmeyen eksik dosyalar (elle eklenmiş olabilir):\n• " + string.Join("\n• ", report.Unrepairable.Take(8)) + "\n");
            if (report.Duplicates.Count > 0) text.AppendLine("İki kez kurulu modlar (birini kapat ya da sil):\n• " + string.Join("\n• ", report.Duplicates));
            await _shell.AskAsync("Mod Doktoru", text.ToString().Trim(), "Tamam");
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(_profile.GameDirectory);
            _shell.OpenPath(_profile.GameDirectory);
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_shell.IsBusy || (_shell.IsGameRunning && _shell.ActiveProfile.Id == _profile.Id))
            {
                _shell.Notify("Bir işlem sürerken ya da oyun açıkken disk silinemez.");
                return;
            }

            bool insideLauncher = _profile.GameDirectory.StartsWith(_shell.Core.Paths.InstancesDir, StringComparison.OrdinalIgnoreCase);
            string detail = insideLauncher
                ? "Diskin içindeki modlar ve DÜNYALARIN (kayıtların) da silinecek! Bu geri alınamaz."
                : $"Disk listeden kaldırılacak; klasörü ({_profile.GameDirectory}) olduğu yerde kalacak.";
            if (await _shell.AskAsync($"'{_profile.Name}' silinsin mi?", detail, "Evet, sil", "Vazgeç") != 0) return;

            try
            {
                _shell.Core.Profiles.Delete(_profile, deleteGameFiles: true);
            }
            catch (Exception ex)
            {
                await _shell.AskAsync("Tamamen silinemedi", "Diskin klasörü tamamen silinemedi: " + ex.Message, "Tamam");
            }

            if (_shell.ActiveProfile.Id == _profile.Id) _shell.SetActiveProfile(_shell.Core.EnsureActiveProfile());
            _shell.Notify($"'{_profile.Name}' silindi.");
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Keyboard.ClearFocus();
        }

        private void NameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (name.Length == 0 || name == _profile.Name)
            {
                NameBox.Text = _profile.Name;
                return;
            }
            _profile.Name = _shell.Core.Profiles.MakeUniqueName(name);
            NameBox.Text = _profile.Name;
            _shell.Core.Profiles.Save(_profile);
            Disc.Reload();
            if (_shell.ActiveProfile.Id == _profile.Id) _shell.SetActiveProfile(_profile);
        }

        // ==================== RAM ====================

        private void RamSwitch_Click(object sender, RoutedEventArgs e)
        {
            _profile.RamMb = RamSwitch.IsChecked == true ? (int)RamSlider.Value : null;
            _shell.Core.Profiles.Save(_profile);
            UpdateRamUi();
        }

        private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            UpdateRamUi();
            if (RamSwitch.IsChecked != true) return;
            _profile.RamMb = (int)e.NewValue;
            _shell.Core.Profiles.Save(_profile);
        }

        private void UpdateRamUi()
        {
            bool custom = RamSwitch.IsChecked == true;
            RamPanel.IsEnabled = custom;
            RamPanel.Opacity = custom ? 1 : 0.4;
            RamText.Text = $"{RamSlider.Value / 1024:0.#} GB";
            RamHint.Text = custom
                ? "Bu disk her zaman bu kadar RAM ile açılır."
                : "Kapalıyken genel ayar kullanılır (önerilen).";
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
