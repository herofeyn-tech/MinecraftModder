using System.Net;
using System.Net.Sockets;

namespace MinecraftModder.Core.Net
{
    // Bazı ağlarda IPv6 bağlantıları çok yavaş ya da hiç açılmıyor; bu yüzden tüm istekler
    // IPv4 adreslerine zorlanır (IPv4 yoksa normal yola düşülür). Tek bir paylaşılan
    // HttpClient kullanılır ki bağlantılar havuzdan tekrar kullanılabilsin.
    public static class LauncherHttp
    {
        public const string UserAgent = "MinecraftModder/0.2 (Windows; WPF)";

        public static HttpClient Shared { get; } = Create();

        public static HttpClient Create()
        {
            var socketsHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 16,
                ConnectCallback = ConnectIPv4FirstAsync
            };

            var client = new HttpClient(socketsHandler);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            // Büyük dosyalar ResponseHeadersRead ile akış olarak okunur; bu süre sadece
            // sunucunun cevap vermeye başlamasına kadar olan beklemeyi sınırlar.
            client.Timeout = TimeSpan.FromSeconds(60);
            return client;
        }

        private static async ValueTask<Stream> ConnectIPv4FirstAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, cancellationToken);
            var ipv4Addresses = entry.AddressList
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .ToArray();

            if (ipv4Addresses.Length == 0)
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            Exception? lastError = null;
            foreach (var ip in ipv4Addresses)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(ip, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    socket.Dispose();
                }
            }

            throw lastError ?? new InvalidOperationException($"{context.DnsEndPoint.Host} adresine bağlanılamadı.");
        }
    }
}
