using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Profiles;

namespace MinecraftModder
{
    // Arayüzde listelenen öğelerin görüntü bilgileri. Çekirdekteki modeller arayüzden habersiz kalsın diye
    // renk, metin ve görünürlük gibi şeyler burada hesaplanır.

    public sealed record KindOption(ContentKind Kind, string Name)
    {
        public override string ToString() => Name;
    }

    public sealed record LoaderOption(ModLoader Loader, string Name)
    {
        public override string ToString() => Name;
    }

    public sealed class SearchResultView : INotifyPropertyChanged
    {
        private bool _isInstalled;

        public SearchResultView(ContentItem item, bool isInstalled)
        {
            Item = item;
            _isInstalled = isInstalled;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ContentItem Item { get; }

        public bool CanInstall => !_isInstalled;

        public string ActionText => _isInstalled ? "✓ Kurulu"
            : Item.Kind == ContentKind.Modpack ? "Profil olarak kur" : "Kur";

        public string Subtitle
        {
            get
            {
                string downloads = $"↓ {FormatCount(Item.Downloads)}";
                return string.IsNullOrEmpty(Item.Author) ? downloads : $"{Item.Author} · {downloads}";
            }
        }

        public Visibility PackBadgeVisibility => Item.Kind == ContentKind.Modpack ? Visibility.Visible : Visibility.Collapsed;

        public void MarkInstalled()
        {
            _isInstalled = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstall)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionText)));
        }

        // 1.234 / 45,6 B (bin) / 12,3 Mn (milyon)
        public static string FormatCount(long value)
        {
            var tr = CultureInfo.GetCultureInfo("tr-TR");
            if (value >= 1_000_000) return (value / 1_000_000d).ToString("0.#", tr) + " Mn";
            if (value >= 10_000) return (value / 1_000d).ToString("0.#", tr) + " B";
            return value.ToString("N0", tr);
        }
    }

    public sealed class ContentEntryView
    {
        private static readonly Brush MissingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xED, 0x42, 0x45)));
        private static readonly Brush DisabledBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4F, 0x54, 0x5C)));
        private static readonly Brush ActiveName = Brushes.White;
        private static readonly Brush InactiveName = Freeze(new SolidColorBrush(Color.FromRgb(0x72, 0x76, 0x7D)));

        public ContentEntryView(ContentEntry entry, bool isMissing)
        {
            Entry = entry;
            IsMissing = isMissing;
        }

        public ContentEntry Entry { get; }
        public bool IsMissing { get; }

        public string Details => $"{Entry.Kind.DisplayName()} · {OriginText(Entry.Origin)} · {Entry.FileName}";

        public string StateText => IsMissing ? "EKSİK" : !Entry.Enabled ? "KAPALI" : "";
        public Brush StateBrush => IsMissing ? MissingBrush : DisabledBrush;
        public Visibility StateVisibility => StateText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Brush NameBrush => Entry.Enabled && !IsMissing ? ActiveName : InactiveName;
        public double IconOpacity => Entry.Enabled && !IsMissing ? 1 : 0.35;

        private static string OriginText(ContentOrigin origin) => origin switch
        {
            ContentOrigin.User => "Senin kurduğun",
            ContentOrigin.Dependency => "Gerekli ek",
            ContentOrigin.Pack or ContentOrigin.PackOverride => "Paketin parçası",
            _ => "Klasöre elle atılmış"
        };

        private static Brush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }
    }

    public sealed class ProfileView
    {
        public ProfileView(GameProfile profile, bool isActive)
        {
            Profile = profile;
            IsActive = isActive;
        }

        public GameProfile Profile { get; }
        public bool IsActive { get; }

        public string ActiveMarker => IsActive ? "  ● aktif" : "";
        public Visibility PackBadgeVisibility => Profile.IsModpack ? Visibility.Visible : Visibility.Collapsed;

        public string Summary
        {
            get
            {
                int mods = Profile.Content.Count(e => e.Kind == ContentKind.Mod);
                string played = Profile.LastPlayedAt is { } t ? $" · son oynanma {t.LocalDateTime:dd.MM.yyyy}" : "";
                return $"{mods} mod{played}";
            }
        }
    }
}
