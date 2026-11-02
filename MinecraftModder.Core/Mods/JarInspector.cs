using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using MinecraftModder.Core.Content;

namespace MinecraftModder.Core.Mods
{
    // Loader: bu bağımlılığı hangi bilgi dosyası söylüyor (aynı jar hem Fabric hem Forge bilgisi taşıyabilir).
    public sealed record ModDependency(string ModId, bool Required, IReadOnlyList<string> FabricPredicates, string? MavenRange, ModLoader Loader)
    {
        // Bu bağımlılığın istediği sürüm, verilen sürümle uyumlu mu?
        public bool Accepts(string version)
            => MavenRange != null ? VersionMatcher.MavenMatches(MavenRange, version) : VersionMatcher.FabricMatches(FabricPredicates, version);
    }

    // Bir mod dosyasının kendi içinde yazan bilgiler. Hangi siteden indirildiği önemli değil:
    // her mod jar'ı hangi loader için yazıldığını, hangi Minecraft'ı ve hangi modları istediğini söyler.
    public sealed class ModMetadata
    {
        public string ModId { get; set; } = "";
        public string? Name { get; set; }
        public string? Version { get; set; }

        // Bu jar'ın "sağladığı" tüm mod kimlikleri: kendisi, takma adları ve içine gömülü modlar.
        public HashSet<string> ProvidedIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<ModLoader> Loaders { get; } = new();
        public List<ModDependency> Dependencies { get; } = new();

        // Sadece sunucuda çalışan modlar oyuncunun bilgisayarında oyunu çökertir.
        public bool ServerOnly { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ModId : Name!;

        // Bu mod verilen loader'lı bir diskte çalışabilir mi?
        public bool RunsOn(ModLoader loader, string gameVersion) => loader switch
        {
            ModLoader.Fabric => Loaders.Contains(ModLoader.Fabric),
            ModLoader.Quilt => Loaders.Contains(ModLoader.Quilt) || Loaders.Contains(ModLoader.Fabric),
            ModLoader.Forge => Loaders.Contains(ModLoader.Forge),
            // NeoForge'un 1.20.1 sürümü Forge modlarını da çalıştırır.
            ModLoader.NeoForge => Loaders.Contains(ModLoader.NeoForge) || (Loaders.Contains(ModLoader.Forge) && gameVersion == "1.20.1"),
            _ => false
        };

        // Verilen loader'lı diskte geçerli olan bağımlılıklar (Fabric bilgisi Forge diskinde sayılmaz).
        public IEnumerable<ModDependency> DependenciesFor(ModLoader loader)
        {
            ModLoader source = loader switch
            {
                ModLoader.Quilt => Loaders.Contains(ModLoader.Quilt) ? ModLoader.Quilt : ModLoader.Fabric,
                ModLoader.NeoForge => Loaders.Contains(ModLoader.NeoForge) ? ModLoader.NeoForge : ModLoader.Forge,
                _ => loader
            };
            return Dependencies.Where(d => d.Loader == source);
        }

        // Minecraft sürüm şartı varsa ona uyuyor mu? Şart yoksa uyumlu sayılır.
        public bool SupportsMinecraft(ModLoader loader, string gameVersion)
            => DependenciesFor(loader).Where(d => d.ModId == "minecraft").All(d => d.Accepts(gameVersion));
    }

    public enum ArchiveKind
    {
        Unknown,
        Mod,
        ShaderPack,
        ResourcePack,
        ModrinthModpack,
        CurseForgeModpack
    }

    // Jar/zip dosyalarını açıp ne olduklarını ve (modsa) içindeki bilgileri okur. Ağa çıkmaz.
    public static class JarInspector
    {
        private const int MaxNestedDepth = 2;
        private static readonly ConcurrentDictionary<string, ModMetadata?> Cache = new();

        // Dosya değişmediği sürece tekrar açılmaz (yüzlerce modlu paketlerde her açılışta okumak yavaş olur).
        public static ModMetadata? Inspect(string path)
        {
            FileInfo info;
            try { info = new FileInfo(path); }
            catch { return null; }
            if (!info.Exists) return null;

            string key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            return Cache.GetOrAdd(key, _ =>
            {
                try
                {
                    using var zip = ZipFile.OpenRead(path);
                    return Read(zip, 0);
                }
                catch
                {
                    return null; // bozuk ya da jar olmayan dosya
                }
            });
        }

        public static ArchiveKind DetectKind(string path)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                bool Has(string name) => zip.GetEntry(name) != null;
                bool HasPrefix(string prefix) => zip.Entries.Any(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                if (Has("modrinth.index.json")) return ArchiveKind.ModrinthModpack;
                if (Has("fabric.mod.json") || Has("quilt.mod.json") || Has("META-INF/mods.toml") || Has("META-INF/neoforge.mods.toml") || Has("mcmod.info"))
                    return ArchiveKind.Mod;
                if (Has("manifest.json") && HasPrefix("overrides/")) return ArchiveKind.CurseForgeModpack;
                // Shader paketleri "shaders/" klasörü taşır (bazen bir üst klasörün içinde).
                if (zip.Entries.Any(e => e.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)
                                         || e.FullName.Contains("/shaders/", StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c == '/') <= 3))
                    return ArchiveKind.ShaderPack;
                if (Has("pack.mcmeta")) return ArchiveKind.ResourcePack;
                if (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && zip.Entries.Any(e => e.FullName.EndsWith(".class"))) return ArchiveKind.Mod;
                return ArchiveKind.Unknown;
            }
            catch
            {
                return ArchiveKind.Unknown;
            }
        }

        private static ModMetadata? Read(ZipArchive zip, int depth)
        {
            var meta = new ModMetadata();
            bool any = false;

            if (zip.GetEntry("fabric.mod.json") is { } fabric) any |= ReadFabric(ReadText(fabric), meta);
            if (zip.GetEntry("quilt.mod.json") is { } quilt) any |= ReadQuilt(ReadText(quilt), meta);
            if (zip.GetEntry("META-INF/neoforge.mods.toml") is { } neo) any |= ReadForgeToml(ReadText(neo), meta, ModLoader.NeoForge);
            if (zip.GetEntry("META-INF/mods.toml") is { } forge) any |= ReadForgeToml(ReadText(forge), meta, ModLoader.Forge);
            if (!any && zip.GetEntry("mcmod.info") is { } legacy) any |= ReadMcModInfo(ReadText(legacy), meta);
            if (!any) return null;

            // İçine gömülü modlar (jar-in-jar): bunları ayrıca kurmaya gerek yok, ana jar sağlıyor.
            if (depth < MaxNestedDepth)
            {
                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                                                             && (e.FullName.StartsWith("META-INF/jars/") || e.FullName.StartsWith("META-INF/jarjar/"))))
                {
                    try
                    {
                        using var ms = new MemoryStream();
                        using (var s = entry.Open()) s.CopyTo(ms);
                        ms.Position = 0;
                        using var nested = new ZipArchive(ms, ZipArchiveMode.Read);
                        var inner = Read(nested, depth + 1);
                        if (inner != null) meta.ProvidedIds.UnionWith(inner.ProvidedIds);
                    }
                    catch
                    {
                        // okunamayan gömülü jar önemli değil
                    }
                }
            }
            return meta;
        }

        private static string ReadText(ZipArchiveEntry entry)
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }

        // ---------- Fabric ----------
        private static bool ReadFabric(string json, ModMetadata meta)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            string? id = Str(root, "id");
            if (string.IsNullOrEmpty(id)) return false;

            SetIdentity(meta, id, Str(root, "name"), Str(root, "version"));
            meta.Loaders.Add(ModLoader.Fabric);
            if (Str(root, "environment") == "server") meta.ServerOnly = true;

            if (root.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
                foreach (var p in provides.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String) meta.ProvidedIds.Add(p.GetString()!);

            if (root.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Object)
            {
                foreach (var dep in depends.EnumerateObject())
                {
                    var predicates = dep.Value.ValueKind switch
                    {
                        JsonValueKind.String => new List<string> { dep.Value.GetString()! },
                        JsonValueKind.Array => dep.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList(),
                        _ => new List<string>()
                    };
                    AddDependency(meta, new ModDependency(dep.Name, true, predicates, null, ModLoader.Fabric));
                }
            }
            return true;
        }

        // ---------- Quilt ----------
        private static bool ReadQuilt(string json, ModMetadata meta)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("quilt_loader", out var ql)) return false;
            string? id = Str(ql, "id");
            if (string.IsNullOrEmpty(id)) return false;

            string? name = ql.TryGetProperty("metadata", out var md) ? Str(md, "name") : null;
            SetIdentity(meta, id, name, Str(ql, "version"));
            meta.Loaders.Add(ModLoader.Quilt);
            if (doc.RootElement.TryGetProperty("minecraft", out var mc) && Str(mc, "environment") == "dedicated_server") meta.ServerOnly = true;

            if (ql.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
                foreach (var p in provides.EnumerateArray())
                {
                    string? pid = p.ValueKind == JsonValueKind.String ? p.GetString() : Str(p, "id");
                    if (!string.IsNullOrEmpty(pid)) meta.ProvidedIds.Add(pid);
                }

            if (ql.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in depends.EnumerateArray())
                {
                    if (dep.ValueKind == JsonValueKind.String)
                    {
                        AddDependency(meta, new ModDependency(StripNamespace(dep.GetString()!), true, Array.Empty<string>(), null, ModLoader.Quilt));
                        continue;
                    }
                    string? depId = Str(dep, "id");
                    if (string.IsNullOrEmpty(depId)) continue;
                    bool optional = dep.TryGetProperty("optional", out var opt) && opt.ValueKind == JsonValueKind.True;
                    var predicates = dep.TryGetProperty("versions", out var v)
                        ? v.ValueKind == JsonValueKind.String ? new List<string> { v.GetString()! }
                          : v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                          : new List<string>()
                        : new List<string>();
                    AddDependency(meta, new ModDependency(StripNamespace(depId), !optional, predicates, null, ModLoader.Quilt));
                }
            }
            return true;
        }

        // ---------- Forge / NeoForge (mods.toml) ----------
        private static bool ReadForgeToml(string toml, ModMetadata meta, ModLoader loader)
        {
            var tables = MiniToml.Parse(toml);
            var mods = tables.Where(t => t.Name == "mods").ToList();
            if (mods.Count == 0) return false;

            var first = mods[0].Values;
            if (!first.TryGetValue("modId", out string? id) || string.IsNullOrEmpty(id)) return false;
            SetIdentity(meta, id, first.GetValueOrDefault("displayName"), first.GetValueOrDefault("version"));
            foreach (var extra in mods.Skip(1))
                if (extra.Values.TryGetValue("modId", out string? extraId)) meta.ProvidedIds.Add(extraId);
            meta.Loaders.Add(loader);

            foreach (var table in tables.Where(t => t.Name.StartsWith("dependencies.", StringComparison.Ordinal)))
            {
                var v = table.Values;
                if (!v.TryGetValue("modId", out string? depId) || string.IsNullOrEmpty(depId)) continue;
                if (v.TryGetValue("side", out string? side) && side.Equals("SERVER", StringComparison.OrdinalIgnoreCase)) continue;

                // Forge: mandatory=true ; NeoForge: type="required"
                bool required = v.TryGetValue("type", out string? type)
                    ? type.Equals("required", StringComparison.OrdinalIgnoreCase)
                    : v.TryGetValue("mandatory", out string? mandatory) && mandatory.Equals("true", StringComparison.OrdinalIgnoreCase);
                AddDependency(meta, new ModDependency(depId, required, Array.Empty<string>(), v.GetValueOrDefault("versionRange") ?? "*", loader));
            }
            return true;
        }

        // ---------- Çok eski Forge (1.12 ve öncesi) ----------
        private static bool ReadMcModInfo(string json, ModMetadata meta)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modList", out var list)) root = list;
            if (root.ValueKind != JsonValueKind.Array) return false;
            var mod = root.EnumerateArray().FirstOrDefault();
            if (mod.ValueKind != JsonValueKind.Object) return false;
            string? id = Str(mod, "modid");
            if (string.IsNullOrEmpty(id)) return false;

            SetIdentity(meta, id, Str(mod, "name"), Str(mod, "version"));
            meta.Loaders.Add(ModLoader.Forge);
            string? mc = Str(mod, "mcversion");
            if (!string.IsNullOrEmpty(mc) && !mc.Contains('$'))
                AddDependency(meta, new ModDependency("minecraft", true, Array.Empty<string>(), mc.Contains('[') || mc.Contains('(') ? mc : $"[{mc}]", ModLoader.Forge));
            return true;
        }

        private static void SetIdentity(ModMetadata meta, string id, string? name, string? version)
        {
            if (string.IsNullOrEmpty(meta.ModId))
            {
                meta.ModId = id;
                meta.Name = name;
                // "${file.jarVersion}" gibi derleme değişkenleri gerçek sürüm değildir.
                meta.Version = version != null && !version.Contains("${") ? version : null;
            }
            meta.ProvidedIds.Add(id);
        }

        private static void AddDependency(ModMetadata meta, ModDependency dep)
        {
            if (meta.Dependencies.Any(d => d.ModId.Equals(dep.ModId, StringComparison.OrdinalIgnoreCase) && d.Loader == dep.Loader)) return;
            meta.Dependencies.Add(dep);
        }

        private static string StripNamespace(string id) => id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id;

        private static string? Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    // mods.toml için yeterli kadar TOML: [tablo], [[dizi tablo]] ve anahtar = değer satırları.
    // Çok satırlı açıklamalar ('''...''') atlanır.
    internal static class MiniToml
    {
        public sealed record Table(string Name, Dictionary<string, string> Values);

        public static List<Table> Parse(string text)
        {
            var tables = new List<Table>();
            var current = new Table("", new Dictionary<string, string>(StringComparer.Ordinal));
            tables.Add(current);
            string? multilineQuote = null;

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (multilineQuote != null)
                {
                    if (line.Contains(multilineQuote)) multilineQuote = null;
                    continue;
                }
                if (line.Length == 0 || line.StartsWith('#')) continue;

                if (line.StartsWith('['))
                {
                    string name = line.Trim('[', ']', ' ').Replace("\"", "").Replace("'", "");
                    current = new Table(name, new Dictionary<string, string>(StringComparer.Ordinal));
                    tables.Add(current);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim().Trim('"');
                string value = line[(eq + 1)..].Trim();

                if (value.StartsWith("'''") || value.StartsWith("\"\"\""))
                {
                    string quote = value[..3];
                    if (value.Length < 6 || !value[3..].Contains(quote)) multilineQuote = quote;
                    continue;
                }
                current.Values[key] = Unquote(value);
            }
            return tables;
        }

        private static string Unquote(string value)
        {
            if (value.Length > 0 && (value[0] == '"' || value[0] == '\''))
            {
                char q = value[0];
                int end = value.IndexOf(q, 1);
                return end > 0 ? value[1..end] : value[1..];
            }
            int hash = value.IndexOf('#');
            return (hash >= 0 ? value[..hash] : value).Trim();
        }
    }
}
