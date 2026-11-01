using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MinecraftModder.Core.Net;

namespace MinecraftModder
{
    // WPF'in Image kontrolü, Source'a direkt bir URL verildiğinde kendi (WinINet tabanlı, IPv6 sorunlu)
    // indirme yolunu kullanır. Bu sınıf ikonları launcher'ın hızlı, IPv4 zorlamalı HttpClient'ı ile indirir.
    // Kullanımı: Image.Source yerine local:AsyncImageLoader.SourceUrl.
    public static class AsyncImageLoader
    {
        // İkonlar en fazla 128 px gösteriliyor; tam boyutta (bazen 1024 px) çözmek boşuna RAM yer.
        private const int DecodePixelWidth = 128;
        private const int MaxCachedImages = 600;

        public static readonly DependencyProperty SourceUrlProperty =
            DependencyProperty.RegisterAttached(
                "SourceUrl",
                typeof(string),
                typeof(AsyncImageLoader),
                new PropertyMetadata(null, OnSourceUrlChanged));

        public static string? GetSourceUrl(DependencyObject obj) => (string?)obj.GetValue(SourceUrlProperty);
        public static void SetSourceUrl(DependencyObject obj, string? value) => obj.SetValue(SourceUrlProperty, value);

        private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new();

        private static async void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image image) return;

            image.Source = null; // ListBox öğeleri yeniden kullanılırken (virtualization) eski resim kalmasın

            string? url = e.NewValue as string;
            if (string.IsNullOrEmpty(url)) return;

            if (_cache.TryGetValue(url, out var cachedBitmap))
            {
                image.Source = cachedBitmap;
                return;
            }

            try
            {
                byte[] data = await LauncherHttp.Shared.GetByteArrayAsync(url);
                var bitmap = await Task.Run(() => Decode(data));

                if (_cache.Count >= MaxCachedImages) _cache.Clear();
                _cache[url] = bitmap;

                // Bu sürede liste değişmiş ve Image başka bir URL istiyor olabilir.
                if (GetSourceUrl(image) == url) image.Source = bitmap;
            }
            catch
            {
                // İkon indirilemezse (bozuk link, desteklenmeyen biçim, ağ hatası) boş bırakılır; uygulama çökmesin.
            }
        }

        private static BitmapImage Decode(byte[] data)
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(data))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = DecodePixelWidth;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze(); // farklı thread'ler ve öğeler arasında güvenle paylaşılabilsin
            return bitmap;
        }
    }
}
