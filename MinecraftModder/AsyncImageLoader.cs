using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace MinecraftModder
{
    // WPF'in Image kontrolü, Source'a direkt bir URL string verildiğinde kendi dahili
    // (WinINet tabanlı) indirme mekanizmasını kullanır. Bu mekanizma bizim MainWindow'da
    // düzelttiğimiz IPv4-zorlayan HttpClient'tan HABERSİZDİR — yani mod indirmeleri hızlı
    // olsa bile ikonlar hâlâ eski (yavaş/IPv6 sorunlu) yoldan iner.
    //
    // Bu sınıf, ikonları da MainWindow.SharedHttpClient üzerinden indirip WPF'e "işte hazır
    // resim" olarak veriyor. Kullanımı: Image.Source yerine AsyncImageLoader.SourceUrl kullan.
    public static class AsyncImageLoader
    {
        public static readonly DependencyProperty SourceUrlProperty =
            DependencyProperty.RegisterAttached(
                "SourceUrl",
                typeof(string),
                typeof(AsyncImageLoader),
                new PropertyMetadata(null, OnSourceUrlChanged));

        public static string? GetSourceUrl(DependencyObject obj) => (string?)obj.GetValue(SourceUrlProperty);
        public static void SetSourceUrl(DependencyObject obj, string? value) => obj.SetValue(SourceUrlProperty, value);

        // Aynı ikonu tekrar tekrar indirmemek için basit, kalıcı olmayan bir bellek-içi önbellek.
        private static readonly Dictionary<string, BitmapImage> _cache = new();

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
                byte[] data = await MainWindow.SharedHttpClient.GetByteArrayAsync(url);

                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(data))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // stream'i hemen kapatabilelim diye tamamen belleğe al
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze(); // farklı öğeler arasında güvenle paylaşılabilsin diye donduruyoruz

                _cache[url] = bitmap;

                // Bu süre zarfında liste değişmiş ve Image başka bir URL istiyor olabilir; kontrol edelim.
                if (GetSourceUrl(image) == url)
                    image.Source = bitmap;
            }
            catch
            {
                // İkon indirilemezse (link bozuk, ağ hatası vb.) sessizce boş bırakıyoruz, uygulama çökmesin.
            }
        }
    }
}
