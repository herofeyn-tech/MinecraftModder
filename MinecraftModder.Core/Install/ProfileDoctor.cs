using MinecraftModder.Core.Content;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Install
{
    public sealed record RepairReport(
        int Checked,
        int Repaired,
        IReadOnlyList<string> Failed,
        IReadOnlyList<string> Unrepairable,
        IReadOnlyList<string> Duplicates)
    {
        public bool IsHealthy => Repaired == 0 && Failed.Count == 0 && Unrepairable.Count == 0 && Duplicates.Count == 0;
    }

    // "Mod doktoru": oyun bozulduğunda modpack'i silip baştan kurmak yerine
    // sadece eksik ya da bozulmuş dosyaları bulup yeniden indirir.
    public sealed class ProfileDoctor
    {
        private readonly DownloadManager _downloads;

        public ProfileDoctor(DownloadManager downloads)
        {
            _downloads = downloads;
        }

        public async Task<RepairReport> VerifyAndRepairAsync(GameProfile profile, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        {
            var toCheck = new List<DownloadItem>();
            var unrepairable = new List<string>();

            foreach (var entry in profile.Content)
            {
                string path = entry.GetFullPath(profile);
                bool exists = File.Exists(path);

                // Config gibi dosyaları kullanıcı değiştirmiş olabilir: sadece hiç yoksa geri getirilir.
                if (entry.Kind == ContentKind.Other && exists) continue;

                if (entry.CanRedownload && (exists ? HasHash(entry) : true))
                {
                    toCheck.Add(new DownloadItem(path, entry.Urls, entry.Sha1, entry.Sha512, entry.Size, entry.DisplayName));
                }
                else if (!exists)
                {
                    unrepairable.Add(entry.DisplayName);
                }
            }

            var report = await _downloads.DownloadAllAsync(toCheck, progress, "Dosyalar kontrol ediliyor", ct);

            return new RepairReport(
                Checked: toCheck.Count,
                Repaired: report.Downloaded,
                Failed: report.Failed.Select(f => $"{f.Item.DisplayName}: {f.Reason}").ToList(),
                Unrepairable: unrepairable,
                Duplicates: FindDuplicates(profile));
        }

        // Aynı modun iki sürümü birlikte etkinse oyun açılırken çöker; kullanıcıya gösterilir.
        public static IReadOnlyList<string> FindDuplicates(GameProfile profile)
            => profile.Content
                .Where(e => e.Kind == ContentKind.Mod && e.Enabled && !string.IsNullOrEmpty(e.ProjectId))
                .GroupBy(e => (e.ProviderId, e.ProjectId))
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.First().DisplayName} ({string.Join(", ", g.Select(e => e.FileName))})")
                .ToList();

        private static bool HasHash(ContentEntry e) => !string.IsNullOrEmpty(e.Sha1) || !string.IsNullOrEmpty(e.Sha512);
    }
}
