using MinecraftModder.Core.Content;
using MinecraftModder.Core.Util;

namespace MinecraftModder.Core.Profiles
{
    // Profilleri diske kaydeder/yükler. Her profil profiles\<id>.json dosyasında durur;
    // böylece uygulama kapanıp açılınca profiller kaybolmaz.
    public sealed class ProfileStore
    {
        private readonly AppPaths _paths;
        private readonly object _lock = new();
        private readonly List<GameProfile> _profiles = new();

        public ProfileStore(AppPaths paths)
        {
            _paths = paths;
            Load();
        }

        public event EventHandler? Changed;

        // Son oynanan en üstte; hiç oynanmamışlar oluşturulma sırasına göre.
        public IReadOnlyList<GameProfile> All
        {
            get
            {
                lock (_lock)
                {
                    return _profiles
                        .OrderByDescending(p => p.LastPlayedAt ?? DateTimeOffset.MinValue)
                        .ThenBy(p => p.CreatedAt)
                        .ToList();
                }
            }
        }

        public GameProfile? Get(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock) return _profiles.FirstOrDefault(p => p.Id == id);
        }

        public GameProfile Create(string name, string gameVersion, ModLoader loader, string? loaderVersion = null, string? gameDirectory = null)
        {
            var profile = new GameProfile
            {
                Name = MakeUniqueName(name),
                GameVersion = gameVersion,
                Loader = loader,
                LoaderVersion = loaderVersion,
            };
            profile.GameDirectory = gameDirectory ?? AllocateGameDirectory(profile.Name);
            Directory.CreateDirectory(profile.GameDirectory);

            lock (_lock) _profiles.Add(profile);
            Save(profile);
            return profile;
        }

        // Kayıt dışında hazırlanmış (ör. modpack kurulumu bitene kadar bekletilmiş) bir profili ekler.
        public void Add(GameProfile profile)
        {
            lock (_lock)
            {
                if (_profiles.Any(p => p.Id == profile.Id)) return;
                _profiles.Add(profile);
            }
            Save(profile);
        }

        public void Save(GameProfile profile)
        {
            lock (_lock)
            {
                JsonFile.Write(ProfileFile(profile.Id), profile);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Delete(GameProfile profile, bool deleteGameFiles)
        {
            lock (_lock)
            {
                _profiles.RemoveAll(p => p.Id == profile.Id);
                string file = ProfileFile(profile.Id);
                if (File.Exists(file)) File.Delete(file);
            }

            // Güvenlik: sadece launcher'ın kendi instances klasöründeki oyun klasörleri silinir.
            // İçe aktarılmış (ör. Masaüstündeki) eski klasörlere dokunulmaz.
            if (deleteGameFiles && IsInsideInstances(profile.GameDirectory) && Directory.Exists(profile.GameDirectory))
                Directory.Delete(profile.GameDirectory, recursive: true);

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public string MakeUniqueName(string name)
        {
            string baseName = string.IsNullOrWhiteSpace(name) ? "Yeni Profil" : name.Trim();
            lock (_lock)
            {
                if (!_profiles.Any(p => p.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase))) return baseName;
                for (int i = 2; ; i++)
                {
                    string candidate = $"{baseName} ({i})";
                    if (!_profiles.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
                }
            }
        }

        public string AllocateGameDirectory(string name)
        {
            string safe = SafePath.MakeSafeFileName(name);
            string dir = Path.Combine(_paths.InstancesDir, safe);
            for (int i = 2; Directory.Exists(dir) || IsDirectoryUsed(dir); i++)
                dir = Path.Combine(_paths.InstancesDir, $"{safe} ({i})");
            return dir;
        }

        public bool IsDirectoryUsed(string dir)
        {
            string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            lock (_lock)
            {
                return _profiles.Any(p => string.Equals(Path.GetFullPath(p.GameDirectory).TrimEnd(Path.DirectorySeparatorChar), full, StringComparison.OrdinalIgnoreCase));
            }
        }

        private bool IsInsideInstances(string dir)
        {
            string root = Path.GetFullPath(_paths.InstancesDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(dir).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private string ProfileFile(string id) => Path.Combine(_paths.ProfilesDir, id + ".json");

        private void Load()
        {
            if (!Directory.Exists(_paths.ProfilesDir)) return;

            foreach (string file in Directory.GetFiles(_paths.ProfilesDir, "*.json"))
            {
                try
                {
                    var profile = JsonFile.Read<GameProfile>(file);
                    if (profile == null || string.IsNullOrEmpty(profile.GameDirectory)) continue;
                    _profiles.Add(profile);
                }
                catch
                {
                    // Bozuk tek bir profil dosyası diğerlerinin yüklenmesini engellemesin.
                }
            }
        }
    }
}
