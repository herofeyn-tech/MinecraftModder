using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MinecraftModder
{
    // Kod ile üretilen küçük piksel desenleri: resmi olmayan kartların zemini, ikonu olmayan modların
    // simgesi ve Oyna bloğunun yüzleri. Oyunun kendi dokuları kopyalanmaz; renkler rastgele ama
    // aynı anahtar için hep aynı çıkar (kart her açılışta aynı görünsün).
    public static class PixelArt
    {
        private static readonly Dictionary<string, BitmapSource> Cache = new();

        private static readonly Color[] Greens = { C(0x5F, 0xB8, 0x4E), C(0x6C, 0xC6, 0x5A), C(0x52, 0xA3, 0x45), C(0x78, 0xCF, 0x63), C(0x4A, 0x96, 0x3F) };
        private static readonly Color[] Dirts = { C(0x8C, 0x5E, 0x3C), C(0x7A, 0x51, 0x33), C(0x9B, 0x6B, 0x45), C(0x6B, 0x46, 0x2C), C(0x84, 0x5A, 0x3A) };

        // Kartlar ve diskler için yumuşak pastel tonlar.
        public static readonly Color[] Pastels =
        {
            C(0x8F, 0xD3, 0xF4), C(0xFF, 0xB4, 0xC6), C(0xB8, 0xE9, 0x94), C(0xFF, 0xD4, 0x5E),
            C(0xC3, 0xA6, 0xFF), C(0xFF, 0xB0, 0x7A), C(0x7F, 0xE0, 0xD0), C(0xA6, 0xB4, 0xFF)
        };

        // Çimen bloğunun yan yüzü: üstte çimen saçağı, altı toprak.
        public static BitmapSource GrassSide(string key) => Cached("side:" + key, () =>
        {
            var rnd = new Random(Hash(key));
            var px = new Color[16 * 16];
            int[] fringe = Enumerable.Range(0, 16).Select(_ => 3 + rnd.Next(0, 3)).ToArray();
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    px[y * 16 + x] = y < fringe[x] ? Pick(Greens, rnd) : Pick(Dirts, rnd);
            return Build(px, 16);
        });

        public static BitmapSource GrassTop(string key) => Cached("top:" + key, () =>
        {
            var rnd = new Random(Hash(key));
            return Build(Enumerable.Range(0, 256).Select(_ => Pick(Greens, rnd)).ToArray(), 16);
        });

        public static BitmapSource Dirt(string key) => Cached("dirt:" + key, () =>
        {
            var rnd = new Random(Hash(key));
            return Build(Enumerable.Range(0, 256).Select(_ => Pick(Dirts, rnd)).ToArray(), 16);
        });

        // İkonu olmayan içerik için 8x8, simetrik, pastel bir "yaratık" yüzü.
        // scale > 1: her piksel scale x scale büyütülür (çizimde keskin kalsın diye).
        public static BitmapSource Avatar(string key, int scale = 1) => Cached($"avatar{scale}:{key}", () =>
        {
            var rnd = new Random(Hash(key));
            Color bg = Pastels[rnd.Next(Pastels.Length)];
            Color fg = Darken(bg, 0.45);
            var px = new Color[64];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 4; x++)
                {
                    Color c = rnd.NextDouble() < 0.42 ? fg : bg;
                    px[y * 8 + x] = c;
                    px[y * 8 + (7 - x)] = c;
                }
            // gözler
            px[3 * 8 + 2] = px[3 * 8 + 5] = C(0x1A, 0x1C, 0x2A);
            return Build(px, 8, scale);
        });

        public static Color PastelFor(string key, int offset = 0) => Pastels[(int)((uint)(Hash(key) + offset) % Pastels.Length)];

        public static Color Darken(Color c, double amount) =>
            Color.FromRgb((byte)(c.R * (1 - amount)), (byte)(c.G * (1 - amount)), (byte)(c.B * (1 - amount)));

        // string.GetHashCode her açılışta değişir; desenler sabit kalsın diye FNV-1a.
        public static int Hash(string text)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char ch in text) h = (h ^ ch) * 16777619;
                return (int)(h & 0x7FFFFFFF);
            }
        }

        private static BitmapSource Cached(string key, Func<BitmapSource> create)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var bmp)) return bmp;
                if (Cache.Count > 400) Cache.Clear();
                return Cache[key] = create();
            }
        }

        private static BitmapSource Build(Color[] px, int size, int scale = 1)
        {
            int full = size * scale;
            var bytes = new byte[full * full * 4];
            for (int y = 0; y < full; y++)
                for (int x = 0; x < full; x++)
                {
                    var c = px[(y / scale) * size + x / scale];
                    int i = (y * full + x) * 4;
                    bytes[i] = c.B;
                    bytes[i + 1] = c.G;
                    bytes[i + 2] = c.R;
                    bytes[i + 3] = 255;
                }
            var bmp = BitmapSource.Create(full, full, 96, 96, PixelFormats.Bgra32, null, bytes, full * 4);
            bmp.Freeze();
            return bmp;
        }

        private static Color Pick(Color[] palette, Random rnd) => palette[rnd.Next(palette.Length)];
        private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    }
}
