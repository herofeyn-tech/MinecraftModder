using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftModder.Core.Util
{
    // Launcher'ın kendi dosyaları (ayarlar, profiller) için JSON okuma/yazma.
    // Yazma önce .tmp dosyasına yapılıp sonra yerine taşınır; böylece yazma sırasında
    // elektrik kesilse/uygulama çökse bile eski dosya bozulmaz.
    public static class JsonFile
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public static T? Read<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }

        public static void Write<T>(string path, T value)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tempPath = path + ".tmp";
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, value, Options);
            }
            File.Move(tempPath, path, overwrite: true);
        }
    }
}
