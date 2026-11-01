using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Install
{
    public sealed record DownloadItem(
        string TargetPath,
        IReadOnlyList<string> Urls,
        string? Sha1 = null,
        string? Sha512 = null,
        long Size = 0,
        string? Label = null)
    {
        public bool HasExpectations => !string.IsNullOrEmpty(Sha1) || !string.IsNullOrEmpty(Sha512) || Size > 0;
        public string DisplayName => Label ?? Path.GetFileName(TargetPath);
    }

    public sealed record DownloadFailure(DownloadItem Item, string Reason);

    public sealed record DownloadReport(int Downloaded, int Skipped, IReadOnlyList<DownloadFailure> Failed)
    {
        public bool Success => Failed.Count == 0;
    }

    public sealed class HashMismatchException : IOException
    {
        public HashMismatchException(string name) : base($"'{name}' bozuk indi (hash eşleşmedi).") { }
    }

    // Tüm dosya indirmeleri buradan geçer:
    //  - Aynı anda birden çok dosya iner (ayarlardan değiştirilebilir, varsayılan 8).
    //  - Diskte zaten doğru hash'le duran dosya tekrar indirilmez (onarma ve yeniden kurulum hızlıdır).
    //  - İnen her dosya hash ile doğrulanır; bozuksa yedek adreslerden (mirror) tekrar denenir.
    //  - Önce ".part" dosyasına yazılır, sadece doğrulanınca asıl adına taşınır; yarım dosya kalmaz.
    public sealed class DownloadManager
    {
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
        private const int AttemptsPerUrl = 3;

        private readonly HttpClient _http;
        private readonly Func<int> _parallelism;

        public DownloadManager(HttpClient http, Func<int> parallelism)
        {
            _http = http;
            _parallelism = parallelism;
        }

        public static bool IsUpToDate(DownloadItem item)
            => item.HasExpectations && Hashing.Matches(item.TargetPath, item.Sha1, item.Sha512, item.Size);

        public async Task<DownloadReport> DownloadAllAsync(IReadOnlyList<DownloadItem> items, IProgress<ProgressInfo>? progress,
            string label, CancellationToken ct = default)
        {
            // Aynı hedefe iki kez yazmaya çalışmak paralel indirmede dosya çakışması yaratır.
            var unique = items
                .GroupBy(i => Path.GetFullPath(i.TargetPath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            int done = 0, downloaded = 0, skipped = 0;
            var failures = new ConcurrentBag<DownloadFailure>();
            using var gate = new SemaphoreSlim(Math.Clamp(_parallelism(), 1, 32));

            progress?.Report(ProgressInfo.Of($"{label} (0/{unique.Count})", 0, unique.Count));

            var tasks = unique.Select(async item =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (await Task.Run(() => IsUpToDate(item), ct))
                    {
                        Interlocked.Increment(ref skipped);
                    }
                    else
                    {
                        await DownloadAsync(item, ct);
                        Interlocked.Increment(ref downloaded);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(new DownloadFailure(item, ex.Message));
                }
                finally
                {
                    gate.Release();
                    int d = Interlocked.Increment(ref done);
                    progress?.Report(ProgressInfo.Of($"{label} ({d}/{unique.Count})", d, unique.Count));
                }
            });

            await Task.WhenAll(tasks);
            return new DownloadReport(downloaded, skipped, failures.ToList());
        }

        public async Task DownloadAsync(DownloadItem item, CancellationToken ct = default)
        {
            if (item.Urls.Count == 0)
                throw new InvalidOperationException($"'{item.DisplayName}' için indirme adresi yok.");

            string? dir = Path.GetDirectoryName(item.TargetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Exception? lastError = null;
            foreach (string url in item.Urls)
            {
                for (int attempt = 1; attempt <= AttemptsPerUrl; attempt++)
                {
                    try
                    {
                        await DownloadOnceAsync(url, item, ct);
                        return;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (HashMismatchException ex)
                    {
                        lastError = ex;
                        break; // aynı adres aynı bozuk dosyayı verecektir; sıradaki yedek adrese geç
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                    {
                        lastError = ex;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (attempt < AttemptsPerUrl) await Task.Delay(400 * attempt, ct);
                    }
                }
            }

            throw new IOException($"'{item.DisplayName}' indirilemedi: {lastError?.Message}", lastError);
        }

        private async Task DownloadOnceAsync(string url, DownloadItem item, CancellationToken ct)
        {
            string tempPath = item.TargetPath + ".part";
            try
            {
                using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await CopyWithStallTimeoutAsync(source, target, ct);
                }

                if (item.HasExpectations && !Hashing.Matches(tempPath, item.Sha1, item.Sha512, item.Size))
                    throw new HashMismatchException(item.DisplayName);

                File.Move(tempPath, item.TargetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* bir sonraki denemede üzerine yazılır */ }
                }
            }
        }

        // Bağlantı açık kalıp veri gelmeyi keserse (donan indirme) sonsuza kadar beklememek için.
        private static async Task CopyWithStallTimeoutAsync(Stream source, Stream target, CancellationToken ct)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer.AsMemory(), stall.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException("Sunucu veri göndermeyi kesti.");
                    }

                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
