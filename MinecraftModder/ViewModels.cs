using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Profiles;

namespace MinecraftModder
{
    // Arayüzde listelenen öğelerin görüntü bilgileri. Çekirdekteki modeller arayüzden habersiz kalsın diye
    // renk, metin ve görünürlük gibi şeyler burada hesaplanır.

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }

    // Kartların kurulum/favori işlerini yaptırdığı yer (MainWindow).
    public interface ICardHost
    {
        string ActiveProfileName { get; }
        bool IsInstalled(ContentItem item);
        bool IsFavorite(ContentItem item);
        Task InstallAsync(CardViewModel card);
        void ToggleFavorite(CardViewModel card);
    }

    public sealed class CardViewModel : INotifyPropertyChanged
    {
        private readonly ICardHost _host;
        private bool _isInstalled;
        private bool _isFavorite;
        private bool _isBusy;

        public CardViewModel(ContentItem item, ICardHost host)
        {
            Item = item;
            _host = host;
            InstallCommand = new RelayCommand(() => _ = _host.InstallAsync(this));
            FavoriteCommand = new RelayCommand(() => _host.ToggleFavorite(this));
            Refresh();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ContentItem Item { get; }
        public ICommand InstallCommand { get; }
        public ICommand FavoriteCommand { get; }

        public BitmapSource FallbackBanner => PixelArt.GrassSide(Item.Key);
        public BitmapSource FallbackIcon => PixelArt.Avatar(Item.Key);

        public Visibility PackVisibility => Item.Kind == ContentKind.Modpack ? Visibility.Visible : Visibility.Collapsed;

        public string Subtitle
        {
            get
            {
                string downloads = $"{FormatCount(Item.Downloads)} indirme";
                return string.IsNullOrEmpty(Item.Author) ? downloads : $"{downloads} · {Item.Author}";
            }
        }

        public string Tooltip => string.IsNullOrWhiteSpace(Item.Description) ? Item.Title : $"{Item.Title}\n\n{Item.Description}";

        public string ActionTooltip => Item.Kind == ContentKind.Modpack
            ? "Yeni bir disk olarak kur (diğer disklerin etkilenmez)"
            : $"'{_host.ActiveProfileName}' diskine kur";

        public string ActionGlyph => "";
        public string HeartGlyph => _isFavorite ? "" : "";
        public Visibility InstalledVisibility => _isInstalled && !_isBusy ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BusyVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;

        // Hangi siteden geldiği: küçük renkli harf rozeti.
        public string ProviderLetter => ProviderKind switch { "cf" => "C", "ftb" => "F", _ => "M" };
        public string ProviderName => ProviderKind switch { "cf" => "CurseForge", "ftb" => "Feed The Beast", _ => "Modrinth" };
        public Brush ProviderBrush => ProviderKind switch
        {
            "cf" => Frozen(Color.FromRgb(0xF1, 0x85, 0x4F)),
            "ftb" => Frozen(Color.FromRgb(0xF2, 0x62, 0x6B)),
            _ => Frozen(Color.FromRgb(0x4F, 0xD9, 0x86))
        };

        private string ProviderKind => Item.ProviderId switch
        {
            "curseforge" => "cf",
            "ftb" when Item.Id.StartsWith("cf-", StringComparison.Ordinal) => "cf",
            "ftb" => "ftb",
            _ => "modrinth"
        };

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                Raise(nameof(BusyVisibility), nameof(InstalledVisibility));
            }
        }

        // Aktif profil, favoriler ya da kurulu içerik değişince çağrılır.
        public void Refresh()
        {
            _isInstalled = _host.IsInstalled(Item);
            _isFavorite = _host.IsFavorite(Item);
            Raise(nameof(InstalledVisibility), nameof(HeartGlyph), nameof(ActionTooltip));
        }

        private void Raise(params string[] names)
        {
            foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // 1.234 / 45,6 B (bin) / 12,3 Mn (milyon)
        public static string FormatCount(long value)
        {
            var tr = CultureInfo.GetCultureInfo("tr-TR");
            if (value >= 1_000_000) return (value / 1_000_000d).ToString("0.#", tr) + " Mn";
            if (value >= 10_000) return (value / 1_000d).ToString("0.#", tr) + " B";
            return value.ToString("N0", tr);
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    // Disk (profil) detayındaki içerik satırı.
    public sealed class ContentEntryView
    {
        public ContentEntryView(ContentEntry entry, bool isMissing)
        {
            Entry = entry;
            IsMissing = isMissing;
        }

        public ContentEntry Entry { get; }
        public bool IsMissing { get; }

        public BitmapSource FallbackIcon => PixelArt.Avatar(Entry.ProjectId ?? Entry.FileName);
        public string Details => $"{Entry.Kind.DisplayName()} · {OriginText(Entry.Origin)}";
        public string StateText => IsMissing ? "EKSİK" : "";
        public Visibility StateVisibility => IsMissing ? Visibility.Visible : Visibility.Collapsed;
        public double RowOpacity => Entry.Enabled && !IsMissing ? 1 : 0.5;

        private static string OriginText(ContentOrigin origin) => origin switch
        {
            ContentOrigin.User => "Senin kurduğun",
            ContentOrigin.Dependency => "Gerekli ek",
            ContentOrigin.Pack or ContentOrigin.PackOverride => "Paketin parçası",
            _ => "Klasöre elle atılmış"
        };
    }

    // Disk rafındaki tek bir disk.
    public sealed class ProfileView
    {
        public ProfileView(GameProfile profile, bool isActive)
        {
            Profile = profile;
            IsActive = isActive;
        }

        public GameProfile Profile { get; }
        public bool IsActive { get; }

        public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PackVisibility => Profile.IsModpack ? Visibility.Visible : Visibility.Collapsed;

        public string Summary
        {
            get
            {
                int mods = Profile.Content.Count(e => e.Kind == ContentKind.Mod);
                return Profile.Loader == ModLoader.Vanilla ? "Modsuz" : $"{mods} mod";
            }
        }
    }

    public sealed record LoaderOption(ModLoader Loader, string Name, string Hint);
}
