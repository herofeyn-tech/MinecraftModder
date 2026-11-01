using CmlLib.Core;

namespace MinecraftModder.Core.Launch
{
    // Profil oluştururken gösterilecek Minecraft sürümleri (sadece tam sürümler, snapshot'lar hariç).
    public sealed class GameVersionService
    {
        private readonly AppPaths _paths;
        private readonly HttpClient _http;
        private IReadOnlyList<string>? _cache;

        public GameVersionService(AppPaths paths, HttpClient http)
        {
            _paths = paths;
            _http = http;
        }

        public async Task<IReadOnlyList<string>> GetReleaseVersionsAsync(CancellationToken ct = default)
        {
            if (_cache != null) return _cache;

            var path = new MinecraftPath(_paths.SharedGameDir);
            var launcher = new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(path, _http));
            var all = await launcher.GetAllVersionsAsync(ct);

            _cache = all
                .Where(v => v.Type == "release")
                .OrderByDescending(v => v.ReleaseTime)
                .Select(v => v.Name)
                .Distinct()
                .ToList();
            return _cache;
        }
    }
}
