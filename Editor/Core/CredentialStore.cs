using System;
using System.IO;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// トークンの置き場。
    ///
    /// プロジェクトの中には置かない。トークンは人に紐付いていて、
    /// 誰がいつ何を取得したかの記録もトークン単位で残る。プロジェクトに
    /// 入れると、リポジトリに入って共有されてしまう。
    ///
    /// 置き場所は .upmconfig.toml と同じ「ユーザーのホーム」。同じ性質の
    /// ものを 2 か所に散らさないため。
    /// </summary>
    public sealed class CredentialStore
    {
        public const string DirectoryName = ".uslog";
        public const string FileName = "upm-credentials.json";

        private readonly string _path;

        public CredentialStore(string path = null)
        {
            _path = path ?? DefaultPath();
        }

        public string Path => _path;

        public static string DefaultPath()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (string.IsNullOrEmpty(home))
            {
                // UserProfile が空になる環境（一部の CI）向けの逃げ道
                home = Environment.GetEnvironmentVariable("HOME")
                       ?? Environment.GetEnvironmentVariable("USERPROFILE")
                       ?? System.IO.Path.GetTempPath();
            }

            return System.IO.Path.Combine(home, DirectoryName, FileName);
        }

        public sealed class Credential
        {
            public string RegistryUrl { get; set; }
            public string Token { get; set; }
            public string Email { get; set; }
            public string SavedAt { get; set; }
        }

        public Credential Load(string registryUrl)
        {
            var key = UslogApiClient.NormalizeRegistryUrl(registryUrl);
            if (string.IsNullOrEmpty(key)) return null;

            var root = ReadFile();
            var entry = root["registries"][key];
            if (!entry.IsObject) return null;

            var token = entry["token"].AsString;
            if (string.IsNullOrEmpty(token)) return null;

            return new Credential
            {
                RegistryUrl = key,
                Token = token,
                Email = entry["email"].AsString,
                SavedAt = entry["savedAt"].AsString,
            };
        }

        public void Save(string registryUrl, string token, string email)
        {
            var key = UslogApiClient.NormalizeRegistryUrl(registryUrl);
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("レジストリ URL が空です", nameof(registryUrl));
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("トークンが空です", nameof(token));

            var root = ReadFile();
            var registries = root["registries"];
            if (!registries.IsObject)
            {
                registries = JsonValue.NewObject();
                root.Set("registries", registries);
            }

            registries.Set(key, JsonValue.NewObject()
                .Set("token", token)
                .Set("email", email ?? string.Empty)
                .Set("savedAt", DateTime.UtcNow.ToString("o")));

            WriteFile(root);
        }

        public void Clear(string registryUrl)
        {
            var key = UslogApiClient.NormalizeRegistryUrl(registryUrl);
            var root = ReadFile();
            var registries = root["registries"];

            if (registries.IsObject && registries.Remove(key)) WriteFile(root);
        }

        private JsonValue ReadFile()
        {
            try
            {
                if (!File.Exists(_path)) return JsonValue.NewObject();

                var text = File.ReadAllText(_path);
                if (JsonValue.TryParse(text, out var json) && json.IsObject) return json;
            }
            catch (IOException)
            {
                // 読めないだけなら、未ログインとして扱えばやり直せる
            }
            catch (UnauthorizedAccessException)
            {
            }

            return JsonValue.NewObject();
        }

        private void WriteFile(JsonValue root)
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // 書き途中で落ちても、元のファイルを壊さないようにする
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, root.ToJson());

            if (File.Exists(_path)) File.Delete(_path);
            File.Move(temporary, _path);
        }
    }
}
