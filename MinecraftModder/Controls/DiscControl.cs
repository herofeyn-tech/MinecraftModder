using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Profiles;

namespace MinecraftModder.Controls
{
    // Bir profili CD olarak çizer: dış halka pizza dilimleri gibi bölünür ve her dilimde profildeki
    // bir modun ikonu durur (en büyük modlar önce). Ortadaki etikette paketin/profilin ikonu olur.
    // Fare üstüne gelince disk döner.
    public sealed class DiscControl : FrameworkElement
    {
        private const int MaxSlices = 8;

        public static readonly DependencyProperty ProfileProperty =
            DependencyProperty.Register(nameof(Profile), typeof(GameProfile), typeof(DiscControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((DiscControl)d).Reload()));

        // true: hep yavaşça döner (detay ekranı, oyun açıkken Oyna bloğu). false: sadece fare üstündeyken.
        public static readonly DependencyProperty AlwaysSpinProperty =
            DependencyProperty.Register(nameof(AlwaysSpin), typeof(bool), typeof(DiscControl),
                new PropertyMetadata(false, (d, _) => ((DiscControl)d).UpdateSpin()));

        public static readonly DependencyProperty SpinOnHoverProperty =
            DependencyProperty.Register(nameof(SpinOnHover), typeof(bool), typeof(DiscControl), new PropertyMetadata(true));

        private readonly RotateTransform _rotation = new();
        private readonly List<(Color Color, BitmapSource? Icon)> _slices = new();
        private BitmapSource? _labelIcon;
        private int _loadVersion;
        private bool _spinning;

        public DiscControl()
        {
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = _rotation;
            SnapsToDevicePixels = false;
            MouseEnter += (_, _) => UpdateSpin();
            MouseLeave += (_, _) => UpdateSpin();
            Unloaded += (_, _) => StopSpin();
            Loaded += (_, _) => UpdateSpin();
        }

        public GameProfile? Profile
        {
            get => (GameProfile?)GetValue(ProfileProperty);
            set => SetValue(ProfileProperty, value);
        }

        public bool AlwaysSpin
        {
            get => (bool)GetValue(AlwaysSpinProperty);
            set => SetValue(AlwaysSpinProperty, value);
        }

        public bool SpinOnHover
        {
            get => (bool)GetValue(SpinOnHoverProperty);
            set => SetValue(SpinOnHoverProperty, value);
        }

        // Profilin içeriği değişince (mod kuruldu/silindi) dilimleri yeniden hesaplar.
        public void Reload()
        {
            int version = ++_loadVersion;
            var profile = Profile;
            _slices.Clear();
            _labelIcon = null;

            string seed = profile?.Id ?? "disc";
            var mods = profile?.Content
                .Where(e => e.Kind == ContentKind.Mod && e.Enabled && !string.IsNullOrEmpty(e.IconUrl))
                .OrderByDescending(e => e.Size)
                .GroupBy(e => e.IconUrl)
                .Select(g => g.First())
                .Take(MaxSlices)
                .ToList() ?? new List<ContentEntry>();

            // Modsuz (ya da ikonsuz) profil: pastel renkli dört dilim.
            int count = mods.Count >= 3 ? mods.Count : 4;
            for (int i = 0; i < count; i++) _slices.Add((PixelArt.PastelFor(seed, i), null));
            InvalidateVisual();

            if (mods.Count >= 3) _ = LoadIconsAsync(version, mods.Select(m => m.IconUrl!).ToList());
            if (profile?.IconUrl != null) _ = LoadLabelAsync(version, profile.IconUrl);
        }

        private async Task LoadIconsAsync(int version, List<string> urls)
        {
            var icons = await Task.WhenAll(urls.Select(u => AsyncImageLoader.LoadAsync(u, 96)));
            if (version != _loadVersion) return;
            for (int i = 0; i < icons.Length && i < _slices.Count; i++) _slices[i] = (_slices[i].Color, icons[i]);
            InvalidateVisual();
        }

        private async Task LoadLabelAsync(int version, string url)
        {
            var icon = await AsyncImageLoader.LoadAsync(url, 128);
            if (version != _loadVersion) return;
            _labelIcon = icon;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double size = double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width;
            if (!double.IsInfinity(availableSize.Height)) size = Math.Min(size, availableSize.Height);
            return new Size(size, size);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 4) return;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            double r = size / 2 - 2;
            double ringOuter = r - size * 0.035;
            double labelR = r * 0.40;
            double holeR = r * 0.11;
            double stroke = Math.Max(1.5, size / 60);
            var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x11, 0x13, 0x1D)), stroke);

            // Gümüş disk gövdesi
            var silver = new LinearGradientBrush(Color.FromRgb(0xE9, 0xED, 0xF7), Color.FromRgb(0x9C, 0xA4, 0xC0), 45);
            dc.DrawEllipse(silver, outline, c, r, r);

            // Dilimler
            int n = _slices.Count;
            double step = 360.0 / n;
            for (int i = 0; i < n; i++)
            {
                var wedge = Wedge(c, ringOuter, labelR, -90 + i * step, -90 + (i + 1) * step);
                var (color, icon) = _slices[i];
                dc.DrawGeometry(new SolidColorBrush(color), null, wedge);
                if (icon != null)
                {
                    dc.PushClip(wedge);
                    var b = wedge.Bounds;
                    double side = Math.Max(b.Width, b.Height);
                    dc.DrawImage(icon, new Rect(b.X + (b.Width - side) / 2, b.Y + (b.Height - side) / 2, side, side));
                    dc.Pop();
                }
                dc.DrawGeometry(null, new Pen(outline.Brush, stroke * 0.7), wedge);
            }

            // CD parlaması: hafif gökkuşağı yansıması
            var sheen = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.20),
                    new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.38),
                    new GradientStop(Color.FromArgb(0x30, 0x9F, 0xE8, 0xFF), 0.46),
                    new GradientStop(Color.FromArgb(0x30, 0xFF, 0xB4, 0xE6), 0.54),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.66),
                }
            };
            dc.DrawEllipse(sheen, null, c, ringOuter, ringOuter);

            // Orta etiket: profilin/paketin ikonu; yoksa diske özel piksel bir yüz
            var labelGeometry = new EllipseGeometry(c, labelR, labelR);
            string seed = Profile?.Id ?? "disc";
            dc.PushClip(labelGeometry);
            dc.DrawImage(_labelIcon ?? PixelArt.Avatar(seed, 8), new Rect(c.X - labelR, c.Y - labelR, labelR * 2, labelR * 2));
            dc.Pop();
            dc.DrawEllipse(null, outline, c, labelR, labelR);

            // Delik
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x19, 0x1B, 0x28)), new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0xE9, 0xED, 0xF7)), stroke * 1.4), c, holeR, holeR);

            dc.DrawEllipse(null, outline, c, r, r);
        }

        private static Geometry Wedge(Point c, double outer, double inner, double startDeg, double endDeg)
        {
            static Point P(Point c, double rad, double deg)
            {
                double a = deg * Math.PI / 180;
                return new Point(c.X + rad * Math.Cos(a), c.Y + rad * Math.Sin(a));
            }

            bool large = endDeg - startDeg > 180;
            var figure = new PathFigure { StartPoint = P(c, inner, startDeg), IsClosed = true };
            figure.Segments.Add(new LineSegment(P(c, outer, startDeg), true));
            figure.Segments.Add(new ArcSegment(P(c, outer, endDeg), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(P(c, inner, endDeg), true));
            figure.Segments.Add(new ArcSegment(P(c, inner, startDeg), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
            var geometry = new PathGeometry(new[] { figure });
            geometry.Freeze();
            return geometry;
        }

        private void UpdateSpin()
        {
            bool shouldSpin = AlwaysSpin || (SpinOnHover && IsMouseOver);
            if (shouldSpin == _spinning) return;
            if (shouldSpin) StartSpin();
            else StopSpin();
        }

        private void StartSpin()
        {
            _spinning = true;
            double from = _rotation.Angle % 360;
            var spin = new DoubleAnimation(from, from + 360, TimeSpan.FromSeconds(AlwaysSpin ? 6 : 2.4))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            _rotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }

        private void StopSpin()
        {
            _spinning = false;
            // Durduğu yerde yavaşça dursun (birden sıfıra zıplamasın)
            double current = _rotation.Angle;
            var stop = new DoubleAnimation(current, current + 25, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            _rotation.BeginAnimation(RotateTransform.AngleProperty, stop);
        }
    }
}
