using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MinecraftModder.Core.Profiles;

namespace MinecraftModder.Controls
{
    public partial class PlayBlock : UserControl
    {
        private bool _pressed;

        public PlayBlock()
        {
            InitializeComponent();
            TopBrush.ImageSource = PixelArt.GrassTop("play");
            LeftBrush.ImageSource = PixelArt.GrassSide("play-left");
            RightBrush.ImageSource = PixelArt.GrassSide("play-right");
        }

        public event RoutedEventHandler? Click;

        // Bloğun üstünde Oyna üçgeni yerine kısa bir durum yazısı gösterir ("%42", "Oyunda"). null: üçgen.
        public void SetState(string? text, bool spinDisc)
        {
            bool hasState = text != null;
            StateText.Text = text ?? "";
            StateBadge.Visibility = hasState ? Visibility.Visible : Visibility.Collapsed;
            PlayGlyph.Visibility = hasState ? Visibility.Collapsed : Visibility.Visible;
            MiniDisc.AlwaysSpin = spinDisc;
        }

        // Aktif profil değişince eski disk çıkar, yenisi takılır.
        public void InsertDisc(GameProfile profile, bool animate)
        {
            if (!animate || MiniDisc.Profile == null)
            {
                MiniDisc.Profile = profile;
                return;
            }

            var eject = new DoubleAnimation(0, -46, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            eject.Completed += (_, _) =>
            {
                MiniDisc.Profile = profile;
                var insert = new DoubleAnimation(-46, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
                DiscSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, insert);
            };
            DiscSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, eject);
        }

        public void RefreshDisc() => MiniDisc.Reload();

        protected override void OnMouseEnter(MouseEventArgs e) => AnimateLift(-6, 1);

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _pressed = false;
            AnimateLift(0, 0);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _pressed = true;
            CaptureMouse();
            AnimateLift(3, 1);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            bool inside = IsMouseOver;
            AnimateLift(inside ? -6 : 0, inside ? 1 : 0);
            if (_pressed && inside && IsEnabled) Click?.Invoke(this, new RoutedEventArgs());
            _pressed = false;
            e.Handled = true;
        }

        private void AnimateLift(double y, double glow)
        {
            var duration = TimeSpan.FromMilliseconds(140);
            LiftTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(y, duration) { EasingFunction = new QuadraticEase() });
            TopGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(glow, duration));
        }
    }
}
