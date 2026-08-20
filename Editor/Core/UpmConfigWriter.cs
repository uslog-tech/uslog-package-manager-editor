using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// Unity 標準の Package Manager でも同じレジストリを使えるようにする「併用モード」。
    ///
    /// 手書きが最大の失敗ポイントだった箇所をそのまま自動化する。
    ///
    ///   ~/.upmconfig.toml          … トークン。**ユーザーのホーム**に置く
    ///   Packages/manifest.json     … scopedRegistries
    ///
    /// URL は両方で一字一句一致していないといけない。末尾スラッシュが
    /// 片方だけ付いていると、Unity は認証を付けずに取りに行って落ちる。
    /// そのため書き込みは必ず正規化した 1 つの文字列から行う。
    ///
    /// 書いても Unity を再起動するまで反映されない。.upmconfig.toml は
    /// 起動時にしか読まれない。呼び出し側は必ず再起動を促すこと。
    /// </summary>
    public static class UpmConfigWriter
    {
        public const string UpmConfigFileName = ".upmconfig.toml";

        public static string DefaultUpmConfigPath()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetEnvironmentVariable("USERPROFILE")
                       ?? Environment.GetEnvironmentVariable("HOME")
                       ?? Path.GetTempPath();
            }
            return Path.Combine(home, UpmConfigFileName);
        }

        // ------------------------------------------------------- .upmconfig.toml

        /// <summary>
        /// 対象の [npmAuth."&lt;url&gt;"] だけを差し替える。他のレジストリの設定は触らない。
        /// </summary>
        public static void WriteUpmConfig(string path, string registryUrl, string token, string email)
        {
            var url = UslogApiClient.NormalizeRegistryUrl(registryUrl);
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("レジストリ URL が空です", nameof(registryUrl));
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("トークンが空です", nameof(token));

            var existing = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

            // 上書きする前に控えを取る。利用者のファイルなので、
            // こちらの読み違いで消してしまうと取り返しがつかない。
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);

            var updated = ReplaceSection(existing, SectionHeader(url), BuildSection(url, token, email));

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, string.Join("\n", updated).TrimEnd('\n') + "\n");
        }

        internal static string SectionHeader(string url) => $"[npmAuth.\"{url}\"]";

        private static IReadOnlyList<string> BuildSection(string url, string token, string email)
        {
            return new[]
            {
                SectionHeader(url),
                $"token = \"{token}\"",
                $"email = \"{email ?? string.Empty}\"",
                // これが無いと「一覧は見えるのに取得だけ失敗する」状態になる。
                "alwaysAuth = true",
            };
        }

        /// <summary>
        /// TOML の該当セクションを丸ごと差し替える。見出しから次の見出しまでを
        /// 1 ブロックとして扱う。TOML を完全に解釈する必要はない。
        /// </summary>
        internal static IReadOnlyList<string> ReplaceSection(
            IReadOnlyList<string> lines,
            string header,
            IReadOnlyList<string> replacement)
        {
            var result = new List<string>();
            var replaced = false;
            var skipping = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (skipping)
                {
                    // 次の見出しが来るまでが、置き換える対象のブロック
                    if (!trimmed.StartsWith("[", StringComparison.Ordinal)) continue;
                    skipping = false;
                }

                if (string.Equals(trimmed, header, StringComparison.Ordinal))
                {
                    result.AddRange(replacement);
                    replaced = true;
                    skipping = true;
                    continue;
                }

                result.Add(line);
            }

            if (!replaced)
            {
                if (result.Count > 0 && result[result.Count - 1].Trim().Length > 0) result.Add(string.Empty);
                result.AddRange(replacement);
            }

            return result;
        }

        // ------------------------------------------------------- manifest.json

        public static string ManifestPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "Packages", "manifest.json");
        }

        /// <summary>
        /// scopedRegistries に USLOG を足す（既にあれば scopes を揃える）。
        /// 他のレジストリの記述は残す。
        /// </summary>
        public static void WriteScopedRegistry(
            string manifestPath,
            string registryUrl,
            string registryName = "USLOG",
            IReadOnlyList<string> scopes = null)
        {
            var url = UslogApiClient.NormalizeRegistryUrl(registryUrl);
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("レジストリ URL が空です", nameof(registryUrl));

            scopes = scopes != null && scopes.Count > 0 ? scopes : new[] { "com.uslog" };

            JsonValue root;
            if (File.Exists(manifestPath))
            {
                if (!JsonValue.TryParse(File.ReadAllText(manifestPath), out root) || !root.IsObject)
                {
                    // JSON が壊れていると Unity は manifest を丸ごと無視する。
                    // こちらで書き潰すと、依存が全部消えたように見える。
                    throw new IOException(
                        $"manifest.json を解釈できませんでした。手で直してからやり直してください: {manifestPath}");
                }
                File.Copy(manifestPath, manifestPath + ".bak", overwrite: true);
            }
            else
            {
                root = JsonValue.NewObject();
            }

            var registries = root["scopedRegistries"];
            if (!registries.IsArray)
            {
                registries = JsonValue.NewArray();
                root.Set("scopedRegistries", registries);
            }

            JsonValue target = null;
            foreach (var entry in registries.Items)
            {
                if (UslogApiClient.NormalizeRegistryUrl(entry["url"].AsString) == url)
                {
                    target = entry;
                    break;
                }
            }

            if (target == null)
            {
                target = JsonValue.NewObject();
                registries.Add(target);
            }

            var scopeArray = JsonValue.NewArray();
            foreach (var scope in scopes) scopeArray.Add(JsonValue.String(scope));

            target.Set("name", registryName);
            target.Set("url", url);
            target.Set("scopes", scopeArray);

            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath) ?? ".");
            File.WriteAllText(manifestPath, root.ToJson() + "\n");
        }

        // ------------------------------------------------------- キャッシュ

        /// <summary>
        /// Unity が持つユーザー単位の npm キャッシュ。
        ///
        /// プロジェクトの Library/ を消しても、ここから供給されると
        /// レジストリに問い合わせが飛ばない。「トークンを失効させたのに
        /// まだ取得できる」の正体はほぼこれ。
        /// </summary>
        public static string GlobalNpmCachePath(string registryUrl)
        {
            var url = UslogApiClient.NormalizeRegistryUrl(registryUrl);
            string host;
            try
            {
                host = new Uri(url).Host;
            }
            catch (UriFormatException)
            {
                return null;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    return Path.Combine(localAppData, "Unity", "cache", "npm", host);

                case PlatformID.MacOSX:
                    return Path.Combine(home, "Library", "Unity", "cache", "npm", host);

                default:
                    // macOS が Unix として報告されることがあるので、実在するほうを返す
                    var mac = Path.Combine(home, "Library", "Unity", "cache", "npm", host);
                    if (Directory.Exists(mac)) return mac;
                    return Path.Combine(home, ".config", "unity3d", "cache", "npm", host);
            }
        }
    }
}
