using System.IO.Compression;
using System.Text;
using MinecraftModder.Core.Content;
using MinecraftModder.Core.Mods;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Tests
{
    // Sahte jar'lar üreten küçük yardımcı.
    internal static class FakeJar
    {
        public static string Create(string path, params (string Name, string Content)[] entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Bytes(entries));
            return path;
        }

        public static byte[] Bytes(params (string Name, object Content)[] entries)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in entries)
                {
                    using var s = zip.CreateEntry(name).Open();
                    byte[] data = content as byte[] ?? Encoding.UTF8.GetBytes((string)content);
                    s.Write(data);
                }
            }
            return ms.ToArray();
        }

        public static byte[] Bytes(params (string Name, string Content)[] entries)
            => Bytes(entries.Select(e => (e.Name, (object)e.Content)).ToArray());

        public static string Fabric(string dir, string id, string depends = "{}", string environment = "*", string? file = null)
            => Create(Path.Combine(dir, file ?? $"{id}.jar"), ("fabric.mod.json",
                $"{{\"schemaVersion\":1,\"id\":\"{id}\",\"name\":\"{id.ToUpperInvariant()}\",\"version\":\"1.0.0\",\"environment\":\"{environment}\",\"depends\":{depends}}}"));

        public static string Forge(string dir, string id, string mcRange = "[1.20.1,1.21)")
            => Create(Path.Combine(dir, $"{id}.jar"), ("META-INF/mods.toml", $@"modLoader=""javafml""
loaderVersion=""[47,)""
[[mods]]
modId=""{id}""
version=""2.0""
displayName=""{id} Forge""
description='''
Uzun açıklama = burada
'''
[[dependencies.{id}]]
    modId=""minecraft""
    mandatory=true
    versionRange=""{mcRange}""
    side=""BOTH""
[[dependencies.{id}]]
    modId=""optionalthing""
    mandatory=false
    versionRange=""[1,)""
"));
    }

    public class VersionMatcherTests
    {
        [Theory]
        [InlineData("~1.20.1", "1.20.1", true)]
        [InlineData("~1.20.1", "1.20.4", true)]
        [InlineData("~1.20.1", "1.21", false)]
        [InlineData("~1.20.1", "1.20", false)]
        [InlineData(">=1.20 <1.21", "1.20.6", true)]
        [InlineData(">=1.20 <1.21", "1.21.1", false)]
        [InlineData("1.20.x", "1.20.4", true)]
        [InlineData("1.20.x", "1.19.4", false)]
        [InlineData("1.20.1", "1.20.1", true)]
        [InlineData("1.20.1", "1.20.2", false)]
        [InlineData(">=1.20.1-", "1.20.1", true)]
        [InlineData("*", "1.7.10", true)]
        [InlineData("^1.20.1", "1.21.4", true)]
        public void Fabric(string predicate, string version, bool expected)
            => Assert.Equal(expected, VersionMatcher.FabricMatches(predicate, version));

        [Fact]
        public void Fabric_ListMeansAnyOf()
            => Assert.True(VersionMatcher.FabricMatches(new[] { "1.20", "1.20.1" }, "1.20.1"));

        [Theory]
        [InlineData("[1.20.1,1.21)", "1.20.1", true)]
        [InlineData("[1.20.1,1.21)", "1.20.6", true)]
        [InlineData("[1.20.1,1.21)", "1.21", false)]
        [InlineData("[1.20,)", "1.21.4", true)]
        [InlineData("(,1.19]", "1.20.1", false)]
        [InlineData("[1.19.2]", "1.19.2", true)]
        [InlineData("[1.19.2]", "1.20.1", false)]
        [InlineData("[1.18.2],[1.20.1,1.20.4]", "1.20.2", true)]
        [InlineData("1.20.1", "1.21", true)] // köşeli parantezsiz = zorunlu değil
        public void Maven(string range, string version, bool expected)
            => Assert.Equal(expected, VersionMatcher.MavenMatches(range, version));

        [Fact]
        public void Compare_TreatsPreReleasesAsOlder()
        {
            Assert.True(VersionMatcher.Compare("1.21-pre1", "1.21") < 0);
            Assert.True(VersionMatcher.Compare("1.20.10", "1.20.9") > 0);
            Assert.Equal(0, VersionMatcher.Compare("1.20", "1.20.0"));
        }
    }

    public class JarInspectorTests
    {
        [Fact]
        public void ReadsFabricModWithNestedLibrary()
        {
            using var temp = new TempDir();
            byte[] inner = FakeJar.Bytes(("fabric.mod.json", "{\"id\":\"inner-lib\",\"version\":\"1\"}"));
            string path = temp.Combine("a.jar");
            File.WriteAllBytes(path, FakeJar.Bytes(
                ("fabric.mod.json", (object)"{\"id\":\"coolmod\",\"name\":\"Cool Mod\",\"version\":\"2.1\",\"provides\":[\"cool\"],\"depends\":{\"minecraft\":\"~1.20.1\",\"cloth-config\":\">=11\",\"fabricloader\":\">=0.14\"}}"),
                ("META-INF/jars/inner.jar", inner)));

            var meta = JarInspector.Inspect(path)!;

            Assert.Equal("coolmod", meta.ModId);
            Assert.Equal("Cool Mod", meta.DisplayName);
            Assert.Contains(ModLoader.Fabric, meta.Loaders);
            Assert.Contains("inner-lib", meta.ProvidedIds);
            Assert.Contains("cool", meta.ProvidedIds);
            Assert.True(meta.SupportsMinecraft(ModLoader.Fabric, "1.20.1"));
            Assert.False(meta.SupportsMinecraft(ModLoader.Fabric, "1.21"));
            Assert.True(meta.RunsOn(ModLoader.Quilt, "1.20.1"));
            Assert.False(meta.RunsOn(ModLoader.Forge, "1.20.1"));
            Assert.Contains(meta.DependenciesFor(ModLoader.Fabric), d => d.ModId == "cloth-config");
        }

        [Fact]
        public void ReadsForgeTomlIncludingMultilineDescription()
        {
            using var temp = new TempDir();
            var meta = JarInspector.Inspect(FakeJar.Forge(temp.Path, "bigmod"))!;

            Assert.Equal("bigmod", meta.ModId);
            Assert.Equal("bigmod Forge", meta.Name);
            Assert.Contains(ModLoader.Forge, meta.Loaders);
            Assert.True(meta.SupportsMinecraft(ModLoader.Forge, "1.20.1"));
            Assert.False(meta.SupportsMinecraft(ModLoader.Forge, "1.19.2"));
            Assert.DoesNotContain(meta.DependenciesFor(ModLoader.Forge), d => d.ModId == "optionalthing" && d.Required);
            Assert.True(meta.RunsOn(ModLoader.NeoForge, "1.20.1"));  // NeoForge 1.20.1 Forge modlarını çalıştırır
            Assert.False(meta.RunsOn(ModLoader.NeoForge, "1.21.1"));
        }

        [Fact]
        public void ReadsNeoForgeRequiredType()
        {
            using var temp = new TempDir();
            string path = FakeJar.Create(temp.Combine("n.jar"), ("META-INF/neoforge.mods.toml", @"
[[mods]]
modId=""neomod""
[[dependencies.neomod]]
modId=""geckolib""
type=""required""
versionRange=""[4,)""
"));
            var meta = JarInspector.Inspect(path)!;
            Assert.Contains(ModLoader.NeoForge, meta.Loaders);
            Assert.Contains(meta.DependenciesFor(ModLoader.NeoForge), d => d.ModId == "geckolib" && d.Required);
        }

        [Fact]
        public void DetectsArchiveKinds()
        {
            using var temp = new TempDir();
            Assert.Equal(ArchiveKind.ShaderPack, JarInspector.DetectKind(FakeJar.Create(temp.Combine("s.zip"), ("shaders/composite.fsh", "x"))));
            Assert.Equal(ArchiveKind.ShaderPack, JarInspector.DetectKind(FakeJar.Create(temp.Combine("s2.zip"), ("BSL v8/shaders/final.fsh", "x"))));
            Assert.Equal(ArchiveKind.ResourcePack, JarInspector.DetectKind(FakeJar.Create(temp.Combine("r.zip"), ("pack.mcmeta", "{}"), ("assets/x.png", "x"))));
            Assert.Equal(ArchiveKind.ModrinthModpack, JarInspector.DetectKind(FakeJar.Create(temp.Combine("p.mrpack"), ("modrinth.index.json", "{}"))));
            Assert.Equal(ArchiveKind.Mod, JarInspector.DetectKind(FakeJar.Fabric(temp.Path, "m")));
            Assert.Equal(ArchiveKind.Unknown, JarInspector.DetectKind(temp.WriteFile("not-a-zip.jar", "merhaba")));
        }

        [Fact]
        public void CurseForgeFingerprintIgnoresWhitespace()
            => Assert.Equal(Hashing.CurseForgeFingerprint(Encoding.ASCII.GetBytes("ab c\r\nd\te")), Hashing.CurseForgeFingerprint(Encoding.ASCII.GetBytes("abcde")));
    }

    public class CompatibilityCheckerTests
    {
        private static (ContentEntry, ModMetadata?) Mod(string path, ContentOrigin origin = ContentOrigin.Manual)
            => (new ContentEntry { RelativePath = "mods/" + Path.GetFileName(path), Kind = ContentKind.Mod, Origin = origin }, JarInspector.Inspect(path));

        [Fact]
        public void FindsWrongLoaderMissingDependencyServerOnlyAndDuplicates()
        {
            using var temp = new TempDir();
            var mods = new[]
            {
                Mod(FakeJar.Fabric(temp.Path, "needsclothing", "{\"cloth-config\":\"*\",\"fabric-lifecycle-events-v1\":\"*\"}")),
                Mod(FakeJar.Fabric(temp.Path, "fabric-api-ish", file: "fapi.jar")),
                Mod(FakeJar.Forge(temp.Path, "forgeonly")),
                Mod(FakeJar.Fabric(temp.Path, "serverthing", environment: "server")),
                Mod(FakeJar.Fabric(temp.Path, "dupe", file: "dupe-a.jar")),
                Mod(FakeJar.Fabric(temp.Path, "dupe", file: "dupe-b.jar")),
            };

            var issues = CompatibilityChecker.Check(ModLoader.Fabric, "1.20.1", mods);

            Assert.Contains(issues, i => i.Kind == ModIssueKind.WrongLoader && i.Meta.ModId == "forgeonly");
            Assert.Contains(issues, i => i.Kind == ModIssueKind.ServerOnly && i.Meta.ModId == "serverthing");
            Assert.Single(issues, i => i.Kind == ModIssueKind.Duplicate);
            Assert.Contains(issues, i => i.Kind == ModIssueKind.MissingDependency && i.DependencyId == "cloth-config");
            Assert.Contains(issues, i => i.Kind == ModIssueKind.MissingDependency && i.DependencyId == "fabric-lifecycle-events-v1");
        }

        [Fact]
        public void QuiltDiskAcceptsFabricModsAndForgeDependenciesDoNotLeakIntoFabric()
        {
            using var temp = new TempDir();
            // Hem Fabric hem Forge bilgisi taşıyan jar: Forge tarafındaki bağımlılık Fabric diskinde aranmamalı.
            string both = FakeJar.Create(temp.Combine("both.jar"),
                ("fabric.mod.json", "{\"id\":\"both\",\"depends\":{}}"),
                ("META-INF/mods.toml", "[[mods]]\nmodId=\"both\"\n[[dependencies.both]]\nmodId=\"kotlinforforge\"\nmandatory=true\n"));
            var mods = new[] { Mod(both), Mod(FakeJar.Fabric(temp.Path, "plain")) };

            Assert.Empty(CompatibilityChecker.Check(ModLoader.Quilt, "1.20.1", mods));
            Assert.Empty(CompatibilityChecker.Check(ModLoader.Fabric, "1.20.1", mods));
            Assert.Contains(CompatibilityChecker.Check(ModLoader.Forge, "1.20.1", new[] { Mod(both) }), i => i.DependencyId == "kotlinforforge");
        }

        [Theory]
        [InlineData("fabric-lifecycle-events-v1", "fabric-api")]
        [InlineData("fabric", "fabric-api")]
        [InlineData("fabric-language-kotlin", "fabric-language-kotlin")]
        [InlineData("Cloth-Config", "cloth-config")]
        public void CanonicalDependencyIds(string id, string expected) => Assert.Equal(expected, DependencyFinder.CanonicalId(id));
    }

    public class ModCompatibilityServiceTests
    {
        // İnternet yokmuş gibi davranır: hiçbir site cevap vermez, sadece yerel düzeltmeler yapılabilir.
        private sealed class OfflineHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => throw new HttpRequestException("çevrimdışı");
        }

        [Fact]
        public async Task OfflineFix_DisablesWhatCannotRunAndMovesMisplacedPacks()
        {
            using var temp = new TempDir();
            var core = LauncherCore.Create(temp.Combine("data"), new HttpClient(new OfflineHandler()));
            var profile = core.Profiles.Create("Kuzen", "1.20.1", ModLoader.Fabric);
            string mods = Path.Combine(profile.GameDirectory, "mods");

            FakeJar.Fabric(mods, "good");
            FakeJar.Forge(mods, "forgeonly");
            FakeJar.Fabric(mods, "needsmissing", "{\"some-unknown-lib\":\"*\"}");
            FakeJar.Fabric(mods, "oldmod", "{\"minecraft\":\"1.16.5\"}");
            FakeJar.Create(Path.Combine(mods, "shader.zip"), ("shaders/final.fsh", "x"));

            var report = await core.Compatibility.CheckAndFixAsync(profile, null);

            bool Enabled(string file) => profile.Content.Single(e => e.RelativePath == $"mods/{file}").Enabled;
            Assert.True(Enabled("good.jar"));
            Assert.False(Enabled("forgeonly.jar"));
            Assert.False(Enabled("needsmissing.jar"));
            Assert.False(Enabled("oldmod.jar"));
            Assert.True(File.Exists(Path.Combine(profile.GameDirectory, "shaderpacks", "shader.zip")));
            Assert.Equal(4, report.Fixed.Count);
            Assert.Empty(CompatibilityChecker.Check(ModLoader.Fabric, "1.20.1", ModCompatibilityService.InspectAll(profile)));
        }

        [Fact]
        public async Task Import_SuggestsAForgeDiskForForgeMods()
        {
            using var temp = new TempDir();
            var core = LauncherCore.Create(temp.Combine("data"), new HttpClient(new OfflineHandler()));
            var fabric = core.Profiles.Create("Ana", "1.20.1", ModLoader.Fabric);
            string downloads = temp.Combine("indirilenler");
            string forgeJar = FakeJar.Forge(downloads, "tinkerstuff");
            string fabricJar = FakeJar.Fabric(downloads, "nicefabric");

            var result = await core.Importer.ImportAsync(fabric, new[] { forgeJar, fabricJar }, null);

            Assert.NotNull(result.Suggestion);
            Assert.Equal(ModLoader.Forge, result.Suggestion!.Loader);
            Assert.Equal("1.20.1", result.Suggestion.GameVersion);
            Assert.Equal(new[] { "tinkerstuff.jar" }, result.Suggestion.FileNames);
            Assert.True(profileHas(fabric, "nicefabric.jar", enabled: true));

            var (disk, moved) = await core.Importer.MoveToSuggestedDiskAsync(fabric, result.Suggestion, null);
            Assert.Equal(ModLoader.Forge, disk.Loader);
            Assert.True(profileHas(disk, "tinkerstuff.jar", enabled: true));
            Assert.DoesNotContain(fabric.Content, e => e.FileName == "tinkerstuff.jar");
            Assert.Null(moved.Suggestion);

            static bool profileHas(GameProfile p, string file, bool enabled)
                => p.Content.Any(e => e.FileName == file && e.Enabled == enabled) && File.Exists(Path.Combine(p.GameDirectory, "mods", enabled ? file : file + ".disabled"));
        }
    }
}
