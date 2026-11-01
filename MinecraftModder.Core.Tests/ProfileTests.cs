using MinecraftModder.Core.Content;
using MinecraftModder.Core.Install;
using MinecraftModder.Core.Launch;
using MinecraftModder.Core.Profiles;
using MinecraftModder.Core.Settings;

namespace MinecraftModder.Core.Tests
{
    public class ProfileTests
    {
        [Fact]
        public void ProfileStore_PersistsProfilesAcrossRestarts()
        {
            using var temp = new TempDir();
            var paths = new AppPaths(temp.Path);
            paths.EnsureCreated();

            var store = new ProfileStore(paths);
            var created = store.Create("Hexxit", "1.20.1", ModLoader.Forge, "47.2.0");
            created.Content.Add(new ContentEntry { RelativePath = "mods/a.jar", Kind = ContentKind.Mod, Urls = { "https://x" } });
            store.Save(created);

            var reloaded = new ProfileStore(paths).Get(created.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("Hexxit", reloaded!.Name);
            Assert.Equal(ModLoader.Forge, reloaded.Loader);
            Assert.Equal("47.2.0", reloaded.LoaderVersion);
            Assert.Single(reloaded.Content);
            Assert.True(Directory.Exists(reloaded.GameDirectory));
        }

        [Fact]
        public void ProfileStore_GivesUniqueNamesAndFolders()
        {
            using var temp = new TempDir();
            var paths = new AppPaths(temp.Path);
            paths.EnsureCreated();
            var store = new ProfileStore(paths);

            var a = store.Create("Survival", "1.20.1", ModLoader.Fabric);
            var b = store.Create("Survival", "1.20.1", ModLoader.Fabric);

            Assert.Equal("Survival (2)", b.Name);
            Assert.NotEqual(a.GameDirectory, b.GameDirectory);
        }

        [Fact]
        public void ProfileStore_DeleteNeverRemovesFoldersOutsideInstances()
        {
            using var temp = new TempDir();
            var paths = new AppPaths(temp.Combine("data"));
            paths.EnsureCreated();
            var store = new ProfileStore(paths);
            string external = temp.Combine("Desktop", "Profil_Eski");
            Directory.CreateDirectory(external);

            var profile = store.Create("Eski", "1.20.1", ModLoader.Fabric, null, external);
            store.Delete(profile, deleteGameFiles: true);

            Assert.True(Directory.Exists(external));
            Assert.Empty(store.All);
        }

        [Fact]
        public void LegacyImporter_ReadsVersionFromFabricFolderAndSkipsDuplicates()
        {
            using var temp = new TempDir();
            var paths = new AppPaths(temp.Combine("data"));
            paths.EnsureCreated();
            var store = new ProfileStore(paths);
            string legacy = temp.Combine("MinecraftModder_Profiller");
            Directory.CreateDirectory(Path.Combine(legacy, "Profil_Hexxit", "versions", "fabric-loader-0.16.5-1.20.4"));
            Directory.CreateDirectory(Path.Combine(legacy, "Profil_Ana"));

            var imported = LegacyProfileImporter.Import(store, legacy, "1.20.1");
            var again = LegacyProfileImporter.Import(store, legacy, "1.20.1");

            Assert.Equal(2, imported.Count);
            Assert.Empty(again);
            var hexxit = imported.Single(p => p.Name == "Hexxit");
            Assert.Equal("1.20.4", hexxit.GameVersion);
            Assert.Equal("0.16.5", hexxit.LoaderVersion);
            Assert.Contains(imported, p => p.Name == "Ana Profil" && p.GameVersion == "1.20.1");
        }

        [Fact]
        public void Reconcile_AddsManualFilesTracksDisabledAndDropsDeletedOnes()
        {
            using var temp = new TempDir();
            var profile = new GameProfile { GameDirectory = temp.Path };
            temp.WriteFile("mods/manual.jar", "a");
            temp.WriteFile("mods/off.jar.disabled", "b");
            temp.WriteFile("mods/readme.txt", "ignored");
            temp.WriteFile("shaderpacks/bsl.zip", "c");
            profile.Content.Add(new ContentEntry { RelativePath = "mods/gone-manual.jar", Kind = ContentKind.Mod });
            profile.Content.Add(new ContentEntry { RelativePath = "mods/gone-repairable.jar", Kind = ContentKind.Mod, Urls = { "https://x" } });

            bool changed = ProfileContentService.ReconcileWithDisk(profile);

            Assert.True(changed);
            var paths = profile.Content.Select(e => e.RelativePath).ToList();
            Assert.Contains("mods/manual.jar", paths);
            Assert.Contains("mods/off.jar", paths);
            Assert.Contains("shaderpacks/bsl.zip", paths);
            Assert.Contains("mods/gone-repairable.jar", paths);
            Assert.DoesNotContain("mods/gone-manual.jar", paths);
            Assert.DoesNotContain(profile.Content, e => e.RelativePath.EndsWith(".txt"));
            Assert.False(profile.Content.Single(e => e.RelativePath == "mods/off.jar").Enabled);
            Assert.Equal(ContentOrigin.Manual, profile.Content.Single(e => e.RelativePath == "mods/manual.jar").Origin);
        }

        [Fact]
        public void Doctor_FindsDuplicateEnabledMods()
        {
            var profile = new GameProfile();
            profile.Content.Add(new ContentEntry { RelativePath = "mods/jei-1.jar", Kind = ContentKind.Mod, ProviderId = "modrinth", ProjectId = "jei", Title = "JEI" });
            profile.Content.Add(new ContentEntry { RelativePath = "mods/jei-2.jar", Kind = ContentKind.Mod, ProviderId = "modrinth", ProjectId = "jei", Title = "JEI" });
            profile.Content.Add(new ContentEntry { RelativePath = "mods/x.jar", Kind = ContentKind.Mod, ProviderId = "modrinth", ProjectId = "x" });

            var duplicates = ProfileDoctor.FindDuplicates(profile);

            Assert.Single(duplicates);
            Assert.Contains("jei-1.jar", duplicates[0]);
        }
    }

    public class RamAdvisorTests
    {
        [Theory]
        [InlineData(4096, 0, null, 1536)]
        [InlineData(8192, 0, null, 3072)]
        [InlineData(16384, 0, null, 6144)]
        [InlineData(16384, 200, null, 6144)]
        [InlineData(32768, 0, null, 8192)]
        [InlineData(8192, 200, null, 6144)]   // çok mod ama 8 GB: Windows'a 2 GB bırakılır
        [InlineData(16384, 10, 8192, 8192)]   // paket önerisi uygulanır
        [InlineData(4096, 300, 8192, 2048)]   // 4 GB'lık bilgisayarda asla 2 GB'ı aşmaz
        public void Recommend_RespectsSystemLimits(long totalMb, int mods, int? packRam, int expected)
            => Assert.Equal(expected, RamAdvisor.Recommend(totalMb, mods, packRam));

        [Fact]
        public void Decide_UsesProfileOverrideThenManualThenAuto()
        {
            var settings = new AppSettings { RamMode = RamMode.Manual, ManualRamMb = 5000 };
            var profile = new GameProfile();

            Assert.Equal(4608, RamAdvisor.Decide(settings, profile, 16384));

            profile.RamMb = 3000;
            Assert.Equal(2560, RamAdvisor.Decide(settings, profile, 16384));

            profile.RamMb = null;
            settings.RamMode = RamMode.Auto;
            Assert.Equal(6144, RamAdvisor.Decide(settings, profile, 16384));
        }
    }

    public class VersionPickerTests
    {
        private static ContentVersion V(string id, ReleaseChannel channel, int day, string game = "1.20.1", string loader = "fabric") => new()
        {
            Id = id,
            ProjectId = "p",
            Channel = channel,
            Published = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
            GameVersions = new[] { game },
            Loaders = new[] { loader },
            Files = new[] { new ContentFile { FileName = id + ".jar", Url = "https://x", Primary = true } }
        };

        [Fact]
        public void PickBest_PrefersNewestReleaseOverNewerBeta()
        {
            var best = VersionPicker.PickBest(new[]
            {
                V("beta-new", ReleaseChannel.Beta, 20),
                V("release-old", ReleaseChannel.Release, 1),
                V("release-new", ReleaseChannel.Release, 10),
            }, "1.20.1", ModLoader.Fabric);
            Assert.Equal("release-new", best?.Id);
        }

        [Fact]
        public void PickBest_FiltersByGameVersionAndLoader()
        {
            var versions = new[]
            {
                V("forge", ReleaseChannel.Release, 30, loader: "forge"),
                V("other-mc", ReleaseChannel.Release, 29, game: "1.21.1"),
                V("ok", ReleaseChannel.Alpha, 1),
            };
            Assert.Equal("ok", VersionPicker.PickBest(versions, "1.20.1", ModLoader.Fabric)?.Id);
            Assert.Null(VersionPicker.PickBest(versions, "1.19.2", ModLoader.Fabric));
        }

        [Fact]
        public void PickBest_QuiltAcceptsFabricMods()
            => Assert.Equal("f", VersionPicker.PickBest(new[] { V("f", ReleaseChannel.Release, 1) }, "1.20.1", ModLoader.Quilt)?.Id);

        [Fact]
        public void PickStableLoader_PrefersNewestNonBetaWhenNothingMarkedStable()
        {
            var quilt = new (string?, bool)[] { ("0.20.0-beta.9", false), ("0.29.1", false), ("0.29.2-beta.1", false), ("0.26.4", false) };
            Assert.Equal("0.29.1", LoaderInstaller.PickStable(quilt));

            var allFlagged = new (string?, bool)[] { ("0.31.0-beta.5", true), ("0.29.1", true) };
            Assert.Equal("0.29.1", LoaderInstaller.PickStable(allFlagged));

            var fabric = new (string?, bool)[] { ("0.17.0", false), ("0.16.14", true), ("0.16.9", true) };
            Assert.Equal("0.16.14", LoaderInstaller.PickStable(fabric));
        }

        [Fact]
        public void LoaderVersionKey_SortsNumerically()
        {
            var sorted = new[] { "21.1.8", "21.1.10", "21.1.9" }.OrderByDescending(LoaderInstaller.ParseVersionKey).ToList();
            Assert.Equal("21.1.10", sorted[0]);
        }
    }
}
