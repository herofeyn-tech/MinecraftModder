using System.IO.Compression;
using System.Text;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Content.Modrinth;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Tests
{
    public class ModpackTests
    {
        private static readonly ProfileSource TestSource = new("test", "pack", "v1", "1.0");

        private static string CreateMrpack(TempDir temp, string indexJson, params (string Path, string Content)[] extraEntries)
        {
            string path = temp.Combine("pack.mrpack");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
            Add("modrinth.index.json", indexJson);
            foreach (var (p, c) in extraEntries) Add(p, c);
            return path;
        }

        private const string SampleIndex = """
        {
          "formatVersion": 1, "game": "minecraft", "versionId": "1.0", "name": "Test Pack",
          "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.16.5" },
          "files": [
            { "path": "mods/a.jar", "hashes": { "sha1": "aa", "sha512": "bb" }, "downloads": ["https://x/a.jar"], "fileSize": 10 },
            { "path": "mods/server-only.jar", "hashes": { "sha1": "cc" }, "env": { "client": "unsupported", "server": "required" }, "downloads": ["https://x/s.jar"], "fileSize": 5 },
            { "path": "resourcepacks/rp.zip", "hashes": { "sha1": "dd" }, "env": { "client": "optional", "server": "unsupported" }, "downloads": ["https://x/rp.zip"], "fileSize": 7 },
            { "path": "config/no-download.toml", "hashes": { "sha1": "ee" }, "downloads": [], "fileSize": 1 }
          ]
        }
        """;

        [Fact]
        public void MrpackReader_ReadsVersionLoaderAndClientFiles()
        {
            using var temp = new TempDir();
            string mrpack = CreateMrpack(temp, SampleIndex);

            var plan = MrpackReader.ReadPlan(mrpack, TestSource);

            Assert.Equal("Test Pack", plan.Name);
            Assert.Equal("1.20.1", plan.GameVersion);
            Assert.Equal(ModLoader.Fabric, plan.Loader);
            Assert.Equal("0.16.5", plan.LoaderVersion);
            Assert.Equal(new[] { "mods/a.jar", "resourcepacks/rp.zip" }, plan.Files.Select(f => f.RelativePath));
            Assert.Equal(ContentKind.Mod, plan.Files[0].Kind);
            Assert.Equal(ContentKind.ResourcePack, plan.Files[1].Kind);
            Assert.Equal("bb", plan.Files[0].Sha512);
        }

        [Theory]
        [InlineData("\"neoforge\": \"21.1.8\"", ModLoader.NeoForge)]
        [InlineData("\"forge\": \"47.2.0\"", ModLoader.Forge)]
        [InlineData("\"quilt-loader\": \"0.26.0\"", ModLoader.Quilt)]
        public void MrpackReader_DetectsOtherLoaders(string dependency, ModLoader expected)
        {
            using var temp = new TempDir();
            string index = $$"""{ "name": "P", "dependencies": { "minecraft": "1.21.1", {{dependency}} }, "files": [] }""";
            var plan = MrpackReader.ReadPlan(CreateMrpack(temp, index), TestSource);
            Assert.Equal(expected, plan.Loader);
        }

        [Fact]
        public void MrpackReader_RejectsArchiveWithoutIndex()
        {
            using var temp = new TempDir();
            string path = temp.Combine("bad.mrpack");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
            Assert.Throws<InvalidDataException>(() => MrpackReader.ReadPlan(path, TestSource));
        }

        [Fact]
        public void ExtractOverrides_ClientOverridesWinAndJarsAreReported()
        {
            using var temp = new TempDir();
            string mrpack = CreateMrpack(temp, SampleIndex,
                ("overrides/config/a.toml", "base"),
                ("client-overrides/config/a.toml", "client"),
                ("overrides/mods/hidden.jar", "jar"),
                ("unrelated/file.txt", "x"));
            string gameDir = temp.Combine("game");

            var written = ModpackInstaller.ExtractOverrides(
                new[] { new OverrideArchive(mrpack, new[] { "overrides/", "client-overrides/" }) }, gameDir);

            Assert.Equal("client", File.ReadAllText(Path.Combine(gameDir, "config", "a.toml")));
            Assert.Contains("mods/hidden.jar", written);
            Assert.Contains("config/a.toml", written);
            Assert.False(File.Exists(Path.Combine(gameDir, "file.txt")));
            Assert.False(Directory.Exists(Path.Combine(gameDir, "unrelated")));
        }

        [Fact]
        public void ExtractOverrides_RejectsZipSlip()
        {
            using var temp = new TempDir();
            string mrpack = CreateMrpack(temp, SampleIndex, ("overrides/../../escaped.txt", "evil"));
            string gameDir = temp.Combine("game");

            Assert.Throws<UnsafePathException>(() => ModpackInstaller.ExtractOverrides(
                new[] { new OverrideArchive(mrpack, new[] { "overrides/" }) }, gameDir));
            Assert.False(File.Exists(temp.Combine("escaped.txt")));
        }

        [Fact]
        public async Task InstallPlan_WithUnsafeFilePath_CreatesNoProfile()
        {
            using var temp = new TempDir();
            var paths = new AppPaths(temp.Path);
            paths.EnsureCreated();
            var store = new ProfileStore(paths);
            var installer = new ModpackInstaller(paths, store, new DownloadManager(new HttpClient(), () => 2));

            var plan = new ModpackPlan
            {
                Name = "Evil",
                GameVersion = "1.20.1",
                Loader = ModLoader.Fabric,
                Source = TestSource,
                Files = new[] { new PlannedFile { RelativePath = "../../evil.jar", Urls = new[] { "https://example.invalid/e.jar" } } }
            };

            await Assert.ThrowsAsync<UnsafePathException>(() => installer.InstallPlanAsync(plan, null));
            Assert.Empty(store.All);
            Assert.Empty(Directory.GetDirectories(paths.InstancesDir));
        }
    }
}
