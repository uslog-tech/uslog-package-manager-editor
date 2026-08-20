using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// レジストリからの返事を、利用者にそのまま見せられる言葉に変えて運ぶ。
    ///
    /// 403 を「パッケージがありません」と書かないこと。レジストリは
    /// 契約の無いパッケージを、存在していても 403 で返す（在庫を漏らさない
    /// ための仕様）。「無い」と書くと、買ったのに出ない人が問い合わせ先を
    /// 見失う。
    /// </summary>
    public sealed class UslogApiException : Exception
    {
        public int StatusCode { get; }
        public string ErrorCode { get; }

        /// <summary>ログインし直せば直る種類の失敗か。</summary>
        public bool NeedsLogin => StatusCode == 401;

        public UslogApiException(int statusCode, string errorCode, string message, Exception inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }

        public static UslogApiException From(int status, string errorCode, string serverMessage)
        {
            switch (status)
            {
                case 401:
                    return new UslogApiException(status, errorCode,
                        "トークンが失効しています。もう一度ログインしてください。");

                case 403 when errorCode == "account_disabled":
                    return new UslogApiException(status, errorCode,
                        "このアカウントは利用停止中です。運営にお問い合わせください。");

                case 403:
                    return new UslogApiException(status, errorCode,
                        "このパッケージの契約がありません。購入直後の場合は 1 分ほど待ってから再読み込みしてください。");

                case 404:
                    return new UslogApiException(status, errorCode,
                        "このレジストリに Editor 用の API がありません。レジストリ側の更新が必要です。");

                case 503:
                    return new UslogApiException(status, errorCode,
                        "レジストリが一時的に応答できません。しばらく待ってからやり直してください。");

                default:
                    return new UslogApiException(status, errorCode,
                        string.IsNullOrEmpty(serverMessage)
                            ? $"レジストリがエラーを返しました (HTTP {status})"
                            : $"レジストリがエラーを返しました (HTTP {status}): {serverMessage}");
            }
        }
    }

    /// <summary>
    /// レジストリの Editor 向け API を叩く。
    ///
    /// UnityWebRequest ではなく HttpClient を使っているのは、この層を
    /// Unity から切り離しておくため。おかげでロジックを素の .NET で
    /// テストできる（Unity を起動しないと 1 行も確かめられない状態にしない）。
    /// </summary>
    public sealed class UslogApiClient
    {
        public const string ApiPath = "/-/uslog/api/v1";

        private static readonly HttpClient Http = CreateHttpClient();

        private readonly string _registryUrl;

        public UslogApiClient(string registryUrl)
        {
            _registryUrl = NormalizeRegistryUrl(registryUrl);
        }

        public string RegistryUrl => _registryUrl;

        /// <summary>
        /// 末尾スラッシュを落とす。.upmconfig.toml と manifest.json の URL は
        /// 一字一句一致していないと Unity が認証を付けないので、
        /// 入口で 1 つの形に揃えておく。
        /// </summary>
        public static string NormalizeRegistryUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            return url.Trim().TrimEnd('/');
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };

            var client = new HttpClient(handler)
            {
                // 個々の呼び出しは CancellationToken で切る。
                // ここを短くすると大きな tarball の取得が落ちる。
                Timeout = TimeSpan.FromMinutes(10),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("USLOG-Package-Manager-Editor");
            return client;
        }

        // ------------------------------------------------------------ API

        public async Task<UslogAccount> GetAccountAsync(string token, CancellationToken cancel = default)
        {
            var json = await GetJsonAsync($"{_registryUrl}{ApiPath}/me", token, cancel).ConfigureAwait(false);
            return UslogAccount.FromJson(json);
        }

        public async Task<UslogListing> GetListingAsync(string token, CancellationToken cancel = default)
        {
            var json = await GetJsonAsync($"{_registryUrl}{ApiPath}/vpm/index.json", token, cancel).ConfigureAwait(false);
            return UslogListing.FromJson(json);
        }

        /// <summary>ブラウザ連携で受け取ったコードを、トークンに引き換える。</summary>
        public async Task<string> ExchangeAsync(string code, string verifier, CancellationToken cancel = default)
        {
            var payload = JsonValue.NewObject()
                .Set("code", code)
                .Set("verifier", verifier);

            using (var request = new HttpRequestMessage(HttpMethod.Post, $"{_registryUrl}{ApiPath}/editor/exchange"))
            {
                request.Content = new StringContent(payload.ToJson(false), Encoding.UTF8, "application/json");

                using (var response = await Http.SendAsync(request, cancel).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw ErrorFrom(response.StatusCode, body);

                    var json = ParseOrThrow(body);
                    var token = json["token"].AsString;
                    if (string.IsNullOrEmpty(token))
                    {
                        throw new UslogApiException(0, "malformed",
                            "レジストリの応答にトークンが入っていません。");
                    }
                    return token;
                }
            }
        }

        /// <summary>tarball を取る。progress は 0..1（長さが分からなければ呼ばれない）。</summary>
        public async Task<byte[]> DownloadAsync(
            string url,
            string token,
            IProgress<float> progress = null,
            CancellationToken cancel = default)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                Authorize(request, token);

                using (var response = await Http
                           .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel)
                           .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await SafeReadAsync(response).ConfigureAwait(false);
                        throw ErrorFrom(response.StatusCode, body);
                    }

                    var total = response.Content.Headers.ContentLength ?? -1L;

                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        var chunk = new byte[64 * 1024];
                        long received = 0;

                        while (true)
                        {
                            var read = await source.ReadAsync(chunk, 0, chunk.Length, cancel).ConfigureAwait(false);
                            if (read <= 0) break;

                            buffer.Write(chunk, 0, read);
                            received += read;

                            if (total > 0) progress?.Report((float)((double)received / total));
                        }

                        return buffer.ToArray();
                    }
                }
            }
        }

        // ------------------------------------------------------------ 下回り

        private async Task<JsonValue> GetJsonAsync(string url, string token, CancellationToken cancel)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                Authorize(request, token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using (var response = await Http.SendAsync(request, cancel).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw ErrorFrom(response.StatusCode, body);
                    return ParseOrThrow(body);
                }
            }
        }

        private static void Authorize(HttpRequestMessage request, string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new UslogApiException(401, "no_token", "ログインしていません。");
            }

            // Verdaccio の legacy(AES) トークンをそのまま Bearer に載せる。
            // .upmconfig.toml に貼るのと同じ値。
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        private static JsonValue ParseOrThrow(string body)
        {
            if (JsonValue.TryParse(body, out var json)) return json;

            throw new UslogApiException(0, "malformed",
                "レジストリの応答を解釈できませんでした。URL が正しいか確認してください。");
        }

        private static UslogApiException ErrorFrom(HttpStatusCode status, string body)
        {
            var code = string.Empty;
            var message = string.Empty;

            if (!string.IsNullOrEmpty(body) && JsonValue.TryParse(body, out var json) && json.IsObject)
            {
                code = json["error"].AsString ?? string.Empty;
                message = json["message"].AsString ?? string.Empty;
            }

            return UslogApiException.From((int)status, code, message);
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
