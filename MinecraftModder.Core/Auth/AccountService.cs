using System.Text.RegularExpressions;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;

namespace MinecraftModder.Core.Auth
{
    // Microsoft (Xbox) ve çevrimdışı giriş. Microsoft oturumu CmlLib tarafından güvenli şekilde
    // hatırlanır; uygulama tekrar açılınca sessizce giriş denenir.
    public sealed class AccountService
    {
        private static readonly Regex OfflineNameRule = new("^[A-Za-z0-9_]{3,16}$");
        private readonly JELoginHandler _loginHandler = JELoginHandlerBuilder.BuildDefault();

        public MSession? Current { get; private set; }

        public async Task<MSession?> TrySilentLoginAsync(CancellationToken ct = default)
        {
            try
            {
                var session = await _loginHandler.AuthenticateSilently(ct);
                if (session != null && session.CheckIsValid()) Current = session;
                return Current;
            }
            catch
            {
                return null; // kayıtlı hesap yok ya da süresi dolmuş: kullanıcı normal giriş yapar
            }
        }

        public async Task<MSession> LoginInteractivelyAsync(CancellationToken ct = default)
        {
            Current = await _loginHandler.AuthenticateInteractively(ct);
            return Current;
        }

        public async Task SignOutAsync()
        {
            try { await _loginHandler.Signout(); } catch { /* zaten çıkış yapılmış olabilir */ }
            Current = null;
        }

        // Minecraft kullanıcı adı kuralları: 3-16 karakter, harf/rakam/alt çizgi.
        public static bool IsValidOfflineName(string name) => OfflineNameRule.IsMatch(name);

        public MSession UseOffline(string username)
        {
            if (!IsValidOfflineName(username))
                throw new ArgumentException("Kullanıcı adı 3-16 karakter olmalı ve sadece harf, rakam ya da _ içermeli.");
            Current = MSession.CreateOfflineSession(username);
            return Current;
        }
    }
}
