using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftModder.Core.Install;

namespace MinecraftModder.Core.Tests
{
    public class DownloadManagerTests
    {
        // Ağa çıkmadan indirmeyi taklit eden sahte sunucu.
        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, byte[]> _files;
            public List<string> Requests { get; } = new();

            public FakeHandler(Dictionary<string, byte[]> files) => _files = files;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                string url = request.RequestUri!.ToString();
                lock (Requests) Requests.Add(url);
                return Task.FromResult(_files.TryGetValue(url, out var data)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

        [Fact]
        public async Task Download_VerifiesHashAndWritesFile()
        {
            using var temp = new TempDir();
            byte[] data = Encoding.UTF8.GetBytes("mod contents");
            var handler = new FakeHandler(new() { ["https://a/mod.jar"] = data });
            var manager = new DownloadManager(new HttpClient(handler), () => 4);
            string target = temp.Combine("mods", "mod.jar");

            await manager.DownloadAsync(new DownloadItem(target, new[] { "https://a/mod.jar" }, Sha1(data), null, data.Length));

            Assert.Equal(data, File.ReadAllBytes(target));
            Assert.False(File.Exists(target + ".part"));
        }

        [Fact]
        public async Task Download_FallsBackToMirrorWhenHashMismatches()
        {
            using var temp = new TempDir();
            byte[] good = Encoding.UTF8.GetBytes("good");
            var handler = new FakeHandler(new()
            {
                ["https://primary/x.jar"] = Encoding.UTF8.GetBytes("corrupt"),
                ["https://mirror/x.jar"] = good
            });
            var manager = new DownloadManager(new HttpClient(handler), () => 4);
            string target = temp.Combine("x.jar");

            await manager.DownloadAsync(new DownloadItem(target, new[] { "https://primary/x.jar", "https://mirror/x.jar" }, Sha1(good)));

            Assert.Equal(good, File.ReadAllBytes(target));
        }

        [Fact]
        public async Task DownloadAll_SkipsUpToDateFilesAndReportsFailures()
        {
            using var temp = new TempDir();
            byte[] existing = Encoding.UTF8.GetBytes("already here");
            byte[] fresh = Encoding.UTF8.GetBytes("new");
            string existingPath = temp.WriteFile("mods/existing.jar", "already here");

            var handler = new FakeHandler(new() { ["https://a/new.jar"] = fresh });
            var manager = new DownloadManager(new HttpClient(handler), () => 4);

            var report = await manager.DownloadAllAsync(new[]
            {
                new DownloadItem(existingPath, new[] { "https://a/existing.jar" }, Sha1(existing)),
                new DownloadItem(temp.Combine("mods", "new.jar"), new[] { "https://a/new.jar" }, Sha1(fresh)),
                new DownloadItem(temp.Combine("mods", "missing.jar"), new[] { "https://a/missing.jar" }, "abc"),
            }, null, "test");

            Assert.Equal(1, report.Downloaded);
            Assert.Equal(1, report.Skipped);
            Assert.Single(report.Failed);
            Assert.DoesNotContain("https://a/existing.jar", handler.Requests);
        }

        [Fact]
        public async Task DownloadAll_DeduplicatesSameTarget()
        {
            using var temp = new TempDir();
            byte[] data = Encoding.UTF8.GetBytes("x");
            var handler = new FakeHandler(new() { ["https://a/x.jar"] = data });
            var manager = new DownloadManager(new HttpClient(handler), () => 4);
            string target = temp.Combine("x.jar");

            var report = await manager.DownloadAllAsync(new[]
            {
                new DownloadItem(target, new[] { "https://a/x.jar" }, Sha1(data)),
                new DownloadItem(target, new[] { "https://a/x.jar" }, Sha1(data)),
            }, null, "test");

            Assert.Equal(1, report.Downloaded);
            Assert.Single(handler.Requests);
        }
    }
}
