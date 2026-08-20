using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// ブラウザ連携ログインの戻り先。127.0.0.1 だけで待ち受ける。
    ///
    /// HttpListener を使っていないのは、.NET Standard 2.0/2.1 に
    /// System.Net.HttpListener が無いため。Unity の API 互換レベルが
    /// ".NET Standard" のプロジェクトでは参照できず、コンパイルが通らない。
    /// TcpListener は .NET Standard にあるので、必要な範囲の HTTP だけ自前で話す。
    /// </summary>
    public sealed class LoopbackAuthServer : IDisposable
    {
        /// <summary>レジストリがここへ 302 で戻してくる。</summary>
        public const string CallbackPath = "/uslog-auth";

        private const int MaxRequestBytes = 16 * 1024;

        private TcpListener _listener;
        private bool _disposed;

        public int Port { get; private set; }

        /// <summary>ポートは OS に選ばせる。固定すると使用中のときに詰まる。</summary>
        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            if (Port < 1024 || Port > 65535)
            {
                // レジストリ側は 1024-65535 しか受け取らない。ここで気づく。
                Dispose();
                throw new InvalidOperationException($"待ち受けポートが範囲外です: {Port}");
            }
        }

        /// <summary>
        /// 戻ってくるまで待つ。クエリをそのまま返す。
        ///
        /// ブラウザは favicon など関係ないものも取りに来るので、
        /// コールバックのパスに当たるまで受け続ける。
        /// </summary>
        public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(
            TimeSpan timeout,
            CancellationToken cancel = default)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                deadline.CancelAfter(timeout);

                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();

                    TcpClient client;
                    try
                    {
                        // AcceptTcpClientAsync はキャンセルを受け取らない。
                        // 打ち切りは listener を閉じて例外にする。
                        using (deadline.Token.Register(SafeStop))
                        {
                            client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        throw new OperationCanceledException("ログインの待ち受けが閉じられました");
                    }
                    catch (SocketException)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        throw;
                    }

                    using (client)
                    {
                        var target = await ReadRequestTargetAsync(client).ConfigureAwait(false);
                        if (target == null) continue;

                        var split = target.IndexOf('?');
                        var path = split >= 0 ? target.Substring(0, split) : target;

                        if (!string.Equals(path, CallbackPath, StringComparison.Ordinal))
                        {
                            await RespondAsync(client, 404, "<h1>Not found</h1>").ConfigureAwait(false);
                            continue;
                        }

                        var query = ParseQuery(split >= 0 ? target.Substring(split + 1) : string.Empty);
                        var failed = query.ContainsKey("error");

                        await RespondAsync(client, 200, failed ? DeniedPage() : DonePage()).ConfigureAwait(false);
                        return query;
                    }
                }
            }
        }

        private void SafeStop()
        {
            try
            {
                _listener?.Stop();
            }
            catch
            {
                // 既に閉じている
            }
        }

        /// <summary>リクエスト行の「パス + クエリ」だけを取り出す。</summary>
        private static async Task<string> ReadRequestTargetAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var buffer = new byte[MaxRequestBytes];
            var read = 0;

            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer, read, buffer.Length - read).ConfigureAwait(false);
                if (n <= 0) break;
                read += n;

                var text = Encoding.ASCII.GetString(buffer, 0, read);
                var newline = text.IndexOf('\n');
                if (newline < 0) continue;

                // "GET /uslog-auth?code=... HTTP/1.1"
                var line = text.Substring(0, newline).TrimEnd('\r');
                var parts = line.Split(' ');
                return parts.Length >= 2 ? parts[1] : null;
            }

            return null;
        }

        internal static IReadOnlyDictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query)) return result;

            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;

                var eq = pair.IndexOf('=');
                var key = eq >= 0 ? pair.Substring(0, eq) : pair;
                var value = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;

                result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace("+", " "));
            }

            return result;
        }

        private static async Task RespondAsync(TcpClient client, int status, string body)
        {
            var payload = Encoding.UTF8.GetBytes(Page(body));
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {payload.Length}\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n");

            var stream = client.GetStream();
            await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static string Page(string body)
        {
            return
                "<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\">" +
                "<title>USLOG Package Manager</title>" +
                "<style>" +
                "body{font-family:system-ui,-apple-system,'Segoe UI','Hiragino Sans',sans-serif;" +
                "background:#0f1115;color:#e7e9ee;display:flex;min-height:100vh;margin:0;" +
                "align-items:center;justify-content:center;text-align:center}" +
                "main{max-width:26rem;padding:2rem}" +
                "h1{font-size:1.25rem;margin:0 0 .75rem}" +
                "p{color:#a9b0bd;line-height:1.7;margin:0}" +
                "</style></head><body><main>" + body + "</main></body></html>";
        }

        private static string DonePage()
        {
            return
                "<h1>連携が完了しました</h1>" +
                "<p>このタブを閉じて、Unity に戻ってください。</p>";
        }

        private static string DeniedPage()
        {
            return
                "<h1>連携をやめました</h1>" +
                "<p>このタブを閉じて、Unity に戻ってください。もう一度やり直せます。</p>";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SafeStop();
            _listener = null;
        }
    }
}
