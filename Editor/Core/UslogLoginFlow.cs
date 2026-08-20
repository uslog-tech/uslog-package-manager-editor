using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// ブラウザ連携ログイン。
    ///
    ///   1. verifier を作り、その sha256 を challenge としてブラウザに渡す
    ///   2. 本人が同意すると、レジストリが 127.0.0.1 へ「引き換えコード」を返す
    ///   3. verifier を添えて交換し、トークン本体を受け取る
    ///
    /// 平文トークンをリダイレクト URL に載せないための往復。載せると
    /// ブラウザの履歴に残り、そこから読めてしまう。
    /// </summary>
    public static class UslogLoginFlow
    {
        /// <summary>レジストリ側の引き換えコードの寿命（5 分）より短くしておく。</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(4);

        public sealed class LoginCancelledException : Exception
        {
            public LoginCancelledException(string message) : base(message) { }
        }

        /// <param name="openBrowser">
        /// URL をブラウザで開く。Unity では Application.OpenURL。
        /// 差し替えられるようにしてあるのは、この層を Unity 抜きで試せるようにするため。
        /// </param>
        public static async Task<string> LoginAsync(
            string registryUrl,
            string label,
            Action<string> openBrowser,
            TimeSpan? timeout = null,
            CancellationToken cancel = default)
        {
            if (openBrowser == null) throw new ArgumentNullException(nameof(openBrowser));

            var client = new UslogApiClient(registryUrl);
            if (string.IsNullOrEmpty(client.RegistryUrl))
            {
                throw new UslogApiException(0, "no_registry", "レジストリの URL が設定されていません。");
            }

            var verifier = RandomToken(32);
            var challenge = Challenge(verifier);
            var state = RandomToken(24);

            using (var server = new LoopbackAuthServer())
            {
                server.Start();

                openBrowser(BuildAuthorizeUrl(client.RegistryUrl, server.Port, state, challenge, label));

                var query = await server
                    .WaitForCallbackAsync(timeout ?? DefaultTimeout, cancel)
                    .ConfigureAwait(false);

                // state が違うのは、別の往復の戻りを掴んだということ。
                // 交換に進まず、その場で捨てる。
                if (!query.TryGetValue("state", out var returned) || !FixedTimeEquals(returned, state))
                {
                    throw new LoginCancelledException(
                        "ログインの往復が一致しませんでした。もう一度やり直してください。");
                }

                if (query.TryGetValue("error", out var error))
                {
                    throw new LoginCancelledException(
                        error == "denied"
                            ? "ブラウザで連携をキャンセルしました。"
                            : $"ログインできませんでした ({error})。");
                }

                if (!query.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
                {
                    throw new LoginCancelledException("引き換えコードを受け取れませんでした。");
                }

                return await client.ExchangeAsync(code, verifier, cancel).ConfigureAwait(false);
            }
        }

        internal static string BuildAuthorizeUrl(string registryUrl, int port, string state, string challenge, string label)
        {
            var query =
                $"port={port}" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&challenge={Uri.EscapeDataString(challenge)}" +
                $"&label={Uri.EscapeDataString(label ?? "Unity Editor")}";

            return $"{UslogApiClient.NormalizeRegistryUrl(registryUrl)}/-/uslog/editor/authorize?{query}";
        }

        internal static string Challenge(string verifier)
        {
            using (var sha = SHA256.Create())
            {
                return Base64Url(sha.ComputeHash(Encoding.UTF8.GetBytes(verifier)));
            }
        }

        internal static string RandomToken(int bytes)
        {
            var buffer = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }
            return Base64Url(buffer);
        }

        internal static string Base64Url(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        /// <summary>先頭何文字が一致したかを実行時間に出さない比較。</summary>
        internal static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
