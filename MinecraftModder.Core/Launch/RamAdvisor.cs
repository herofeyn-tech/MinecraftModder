using MinecraftModder.Core.Content;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Settings;

namespace MinecraftModder.Core.Launch
{
    // Kullanıcı RAM ayarıyla uğraşmasın diye: bilgisayarın toplam RAM'i, profildeki mod sayısı ve
    // modpack yapımcısının önerisi birlikte değerlendirilir; Windows'a her zaman pay bırakılır.
    public static class RamAdvisor
    {
        public const int MinimumMb = 1024;

        public static long TotalSystemMb()
        {
            try
            {
                return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            }
            catch
            {
                return 8192;
            }
        }

        public static int Recommend(long totalMb, int modCount, int? packRecommendedMb)
        {
            int ram = totalMb switch
            {
                <= 4096 => 1536,
                <= 6144 => 2048,
                <= 8192 => 3072,
                <= 12288 => 4096,
                <= 16384 => 6144,
                _ => 8192
            };

            // Çok modlu paketler daha fazla RAM ister.
            if (modCount > 150) ram = Math.Max(ram, 6144);
            else if (modCount > 60) ram = Math.Max(ram, 4096);

            if (packRecommendedMb is > 0) ram = Math.Max(ram, packRecommendedMb.Value);

            return Clamp(ram, totalMb);
        }

        public static int Decide(AppSettings settings, GameProfile profile, long? totalMbOverride = null)
        {
            long totalMb = totalMbOverride ?? TotalSystemMb();

            if (profile.RamMb is > 0) return Clamp(profile.RamMb.Value, totalMb);
            if (settings.RamMode == RamMode.Manual) return Clamp(settings.ManualRamMb, totalMb);

            int modCount = profile.Content.Count(e => e.Kind == ContentKind.Mod && e.Enabled);
            return Recommend(totalMb, modCount, profile.RecommendedRamMb);
        }

        // Windows ve arka plandaki programlar için en az 2 GB ya da RAM'in dörtte biri boşta kalır.
        public static int MaxAllowed(long totalMb)
        {
            long reserve = Math.Max(2048, totalMb / 4);
            return (int)Math.Max(MinimumMb, totalMb - reserve);
        }

        private static int Clamp(int ram, long totalMb)
        {
            int clamped = Math.Clamp(ram, MinimumMb, MaxAllowed(totalMb));
            return clamped / 512 * 512; // 512 MB'ın katlarına yuvarla
        }
    }
}
