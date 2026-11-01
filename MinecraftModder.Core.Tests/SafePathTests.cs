using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Tests
{
    public class SafePathTests
    {
        [Theory]
        [InlineData("mods/sodium.jar")]
        [InlineData("./mods/sodium.jar")]
        [InlineData("config/sub/../file.toml")]
        public void Combine_AllowsPathsInsideRoot(string relative)
        {
            using var temp = new TempDir();
            string full = SafePath.Combine(temp.Path, relative);
            Assert.StartsWith(temp.Path, full);
        }

        [Theory]
        [InlineData("../evil.dll")]
        [InlineData("mods/../../evil.dll")]
        [InlineData("/etc/passwd")]
        [InlineData("C:/Windows/evil.dll")]
        [InlineData("C:evil.dll")]
        [InlineData("..")]
        [InlineData("")]
        public void Combine_RejectsPathsEscapingRoot(string relative)
        {
            using var temp = new TempDir();
            Assert.Throws<UnsafePathException>(() => SafePath.Combine(temp.Path, relative));
        }

        [Fact]
        public void Combine_RejectsSiblingFolderWithSamePrefix()
        {
            using var temp = new TempDir();
            string root = temp.Combine("game");
            Assert.Throws<UnsafePathException>(() => SafePath.Combine(root, "../game2/x.jar"));
        }

        [Theory]
        [InlineData("./mods/a.jar", "mods/a.jar")]
        [InlineData(".\\mods\\a.jar", "mods/a.jar")]
        [InlineData("/mods/a.jar", "mods/a.jar")]
        public void NormalizeRelative_ProducesManifestForm(string input, string expected)
            => Assert.Equal(expected, SafePath.NormalizeRelative(input));

        [Fact]
        public void MakeSafeFileName_ReplacesInvalidCharacters()
        {
            Assert.Equal("All the Mods_ 10", SafePath.MakeSafeFileName("All the Mods: 10"));
            Assert.Equal("Profil", SafePath.MakeSafeFileName("..."));
        }
    }
}
