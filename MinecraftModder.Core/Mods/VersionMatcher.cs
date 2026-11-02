using System.Text.RegularExpressions;

namespace MinecraftModder.Core.Mods
{
    // Modların "hangi Minecraft / hangi mod sürümüyle çalışırım" bilgisini yorumlar.
    // İki yazım var:
    //   Fabric/Quilt: ">=1.20 <1.21", "~1.20.1", "1.20.x", "*" ya da bunlardan oluşan bir liste (biri tutsa yeter)
    //   Forge/NeoForge (Maven aralığı): "[1.20.1,1.21)", "[1.20,)", "[1.20.1]", virgülle birden çok aralık
    // Emin olunamayan (çözülemeyen) yazımlarda "uyumlu" denir: yanlışlıkla çalışan bir modu kapatmaktansa
    // oyunun kendi hata mesajına güvenmek daha iyi.
    public static class VersionMatcher
    {
        private static readonly Regex MavenPart = new(@"[\[\(][^\]\)]*[\]\)]", RegexOptions.Compiled);

        // Fabric: liste içindekilerden herhangi biri tutarsa uyumlu.
        public static bool FabricMatches(IEnumerable<string> predicates, string version)
        {
            var list = predicates.ToList();
            return list.Count == 0 || list.Any(p => FabricMatches(p, version));
        }

        public static bool FabricMatches(string predicate, string version)
        {
            predicate = predicate.Trim();
            if (predicate.Length == 0 || predicate == "*") return true;

            // Boşlukla ayrılmış koşulların hepsi tutmalı (">=1.20 <1.21").
            foreach (string part in predicate.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!MatchesSingle(part, version)) return false;
            }
            return true;
        }

        private static bool MatchesSingle(string part, string version)
        {
            string op = part.StartsWith(">=") || part.StartsWith("<=") ? part[..2]
                : part[0] is '>' or '<' or '=' or '~' or '^' ? part[..1]
                : "";
            string target = part[op.Length..].Trim().TrimEnd('-'); // "1.20-" = ön sürümler dahil
            if (target.Length == 0 || target == "*") return true;

            // "1.20.x" / "1.20.*": önek eşleşmesi
            if (target.EndsWith(".x", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".*"))
            {
                string prefix = target[..^2];
                return Compare(Core(version), prefix) >= 0 && Compare(Core(version), Bump(prefix, prefix.Count(c => c == '.'))) < 0;
            }

            int cmp = Compare(version, target);
            return op switch
            {
                ">=" => cmp >= 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                "<" => cmp < 0,
                // ~1.20.1 → >=1.20.1 <1.21 ; ~1.20 → >=1.20 <1.21
                "~" => cmp >= 0 && Compare(version, Bump(target, 1)) < 0,
                // ^1.20.1 → >=1.20.1 <2
                "^" => cmp >= 0 && Compare(version, Bump(target, 0)) < 0,
                _ => cmp == 0
            };
        }

        // Maven aralığı. Köşeli parantez yoksa (ör. "1.20.1") Maven'da bu "tercih edilen" demektir, zorunlu değil.
        public static bool MavenMatches(string? range, string version)
        {
            if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*") return true;
            var parts = MavenPart.Matches(range);
            if (parts.Count == 0) return true;
            return parts.Any(m => MatchesInterval(m.Value, version));
        }

        private static bool MatchesInterval(string interval, string version)
        {
            bool lowInclusive = interval[0] == '[';
            bool highInclusive = interval[^1] == ']';
            string body = interval[1..^1];
            int comma = body.IndexOf(',');

            if (comma < 0) // [1.20.1] → tam eşit
                return Compare(version, body.Trim()) == 0;

            string low = body[..comma].Trim();
            string high = body[(comma + 1)..].Trim();
            if (low.Length > 0)
            {
                int c = Compare(version, low);
                if (lowInclusive ? c < 0 : c <= 0) return false;
            }
            if (high.Length > 0)
            {
                int c = Compare(version, high);
                if (highInclusive ? c > 0 : c >= 0) return false;
            }
            return true;
        }

        // Sayısal parça parça karşılaştırma: 1.20.10 > 1.20.9, 1.20 == 1.20.0, 1.21-pre1 < 1.21.
        public static int Compare(string a, string b)
        {
            var (coreA, preA) = Split(a);
            var (coreB, preB) = Split(b);
            var pa = coreA.Split('.');
            var pb = coreB.Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                string x = i < pa.Length ? pa[i] : "0";
                string y = i < pb.Length ? pb[i] : "0";
                int c = int.TryParse(x, out int nx) && int.TryParse(y, out int ny)
                    ? nx.CompareTo(ny)
                    : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return Math.Sign(c);
            }
            if (preA.Length == 0 && preB.Length == 0) return 0;
            if (preA.Length == 0) return 1;  // 1.21 > 1.21-rc1
            if (preB.Length == 0) return -1;
            return Math.Sign(string.Compare(preA, preB, StringComparison.OrdinalIgnoreCase));
        }

        private static (string Core, string Pre) Split(string v)
        {
            v = v.Trim();
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v[..plus]; // derleme bilgisi sıralamayı etkilemez
            int dash = v.IndexOf('-');
            return dash < 0 ? (v, "") : (v[..dash], v[(dash + 1)..]);
        }

        private static string Core(string v) => Split(v).Core;

        // Bump("1.20.1", 1) = "1.21" ; Bump("1.20", 0) = "2"
        private static string Bump(string version, int index)
        {
            var parts = Core(version).Split('.').ToList();
            while (parts.Count <= index) parts.Add("0");
            parts[index] = int.TryParse(parts[index], out int n) ? (n + 1).ToString() : parts[index] + "~";
            return string.Join(".", parts.Take(index + 1));
        }
    }
}
