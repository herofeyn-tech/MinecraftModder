using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftModder.Core.Net;

namespace MinecraftModder
{
    // WPF'in Image kontrolü, Source'a direkt bir URL verildiğinde kendi (WinINet tabanlı, IPv6 sorunlu)
    // indirme yolunu kullanır. Bu sınıf resimleri launcher'ın hızlı, IPv4 zorlamalı HttpClient'ı ile indirir.
    // Kullanımı: Image.Source yerine local:AsyncImageLoader.SourceUrl (+ isteğe bağlı DecodeWidth).
    public static class AsyncImageLoader
    {
        // İkonlar en fazla 128 px gösteriliyor; tam boyutta (bazen 1024 px) çözmek boşuna RAM yer.
        private const int DefaultDecodeWidth = 128;
        private const int MaxCachedImages = 600;

        public static readonly DependencyProperty SourceUrlProperty =
            DependencyProperty.RegisterAttached(
                "SourceUrl",
                typeof(string),
                typeof(AsyncImageLoader),
                new PropertyMetadata(null, OnSourceUrlChanged));

        // Çok küçük bir değer (ör. 28) verip resmi büyütmek, ucuz ve yumuşak bir bulanıklık verir.
        public static readonly DependencyProperty DecodeWidthProperty =
            DependencyProperty.RegisterAttached(
                "DecodeWidth",
                typeof(int),
                typeof(AsyncImageLoader),
                new PropertyMetadata(DefaultDecodeWidth));

        public static string? GetSourceUrl(DependencyObject obj) => (string?)obj.GetValue(SourceUrlProperty);
        public static void SetSourceUrl(DependencyObject obj, string? value) => obj.SetValue(SourceUrlProperty, value);
        public static int GetDecodeWidth(DependencyObject obj) => (int)obj.GetValue(DecodeWidthProperty);
        public static void SetDecodeWidth(DependencyObject obj, int value) => obj.SetValue(DecodeWidthProperty, value);

        private static readonly ConcurrentDictionary<string, BitmapSource> _cache = new();

        private static async void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image image) return;

            image.Source = null; // liste öğeleri yeniden kullanılırken eski resim kalmasın

            string? url = e.NewValue as string;
            if (string.IsNullOrEmpty(url)) return;

            var bitmap = await LoadAsync(url, GetDecodeWidth(image));

            // Bu sürede liste değişmiş ve Image başka bir URL istiyor olabilir.
            if (bitmap != null && GetSourceUrl(image) == url) image.Source = bitmap;
        }

        // Kod içinden resim gerektiğinde (ör. disk dilimleri). İndirilemezse null döner, asla fırlatmaz.
        public static async Task<BitmapSource?> LoadAsync(string? url, int decodeWidth = DefaultDecodeWidth)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string key = $"{decodeWidth}|{url}";
            if (_cache.TryGetValue(key, out var cached)) return cached;

            try
            {
                byte[] data = await LauncherHttp.Shared.GetByteArrayAsync(url);
                var bitmap = await Task.Run(() => Decode(data, decodeWidth));

                if (_cache.Count >= MaxCachedImages) _cache.Clear();
                _cache[key] = bitmap;
                return bitmap;
            }
            catch
            {
                // Bozuk link, desteklenmeyen biçim (ör. SVG) ya da ağ hatası: boş bırakılır, uygulama çökmesin.
                return null;
            }
        }

        private static BitmapSource Decode(byte[] data, int decodeWidth)
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(data))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze(); // farklı thread'ler ve öğeler arasında güvenle paylaşılabilsin
            return bitmap;
        }
    }

    // Border.CornerRadius içeriği kırpmaz; bu yardımcı, öğeyi boyutu değiştikçe yuvarlak köşeli kırpar.
    public static class RoundClip
    {
        public static readonly DependencyProperty RadiusProperty =
            DependencyProperty.RegisterAttached("Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0d, OnRadiusChanged));

        public static double GetRadius(DependencyObject obj) => (double)obj.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject obj, double value) => obj.SetValue(RadiusProperty, value);

        private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element) return;
            element.SizeChanged -= OnSizeChanged;
            element.SizeChanged += OnSizeChanged;
            Apply(element);
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

        private static void Apply(FrameworkElement element)
        {
            double r = GetRadius(element);
            element.Clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), r, r);
        }
    }
}
