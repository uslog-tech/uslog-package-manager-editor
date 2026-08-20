using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Uslog.PackageManager.Editor.Tests
{
    public class VpmManifestTests
    {
        private string _project;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "uslog-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_project, "Packages"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_project)) Directory.Delete(_project, recursive: true);
        }

        private void WriteManifest(string json)
        {
            File.WriteAllText(VpmManifest.PathFor(_project), json);
        }

        [Test]
        public void 無ければ空の形で作る()
        {
            var manifest = VpmManifest.Load(_project);

            Assert.IsTrue(manifest.Dependencies.IsObject);
            Assert.IsTrue(manifest.Locked.IsObject);
        }

        [Test]
        public void dependencies_と_locked_の両方に書く()
        {
            // 片方だけ書くと、VCC の Resolve で消されたり戻されたりする。
            var manifest = VpmManifest.Load(_project);
            manifest.Add("com.uslog.example", "1.2.3", new Dictionary<string, string> { { "com.vrchat.base", "3.x" } });

            Assert.AreEqual("1.2.3", manifest.Dependencies["com.uslog.example"]["version"].AsString);
            Assert.AreEqual("1.2.3", manifest.LockedVersion("com.uslog.example"));
            Assert.AreEqual("3.x", manifest.Locked["com.uslog.example"]["dependencies"]["com.vrchat.base"].AsString);
        }

        [Test]
        public void 他のツールが書いたキーを消さない()
        {
            WriteManifest(@"{
  ""dependencies"": { ""com.vrchat.base"": { ""version"": ""3.4.0"" } },
  ""locked"": { ""com.vrchat.base"": { ""version"": ""3.4.0"", ""dependencies"": {} } },
  ""somethingElse"": { ""keep"": true }
}");

            var manifest = VpmManifest.Load(_project);
            manifest.Add("com.uslog.example", "1.0.0", null);
            manifest.Save();

            var written = JsonValue.Parse(File.ReadAllText(VpmManifest.PathFor(_project)));

            Assert.IsTrue(written["somethingElse"]["keep"].AsBool);
            Assert.AreEqual("3.4.0", written["locked"]["com.vrchat.base"]["version"].AsString);
            Assert.AreEqual("1.0.0", written["locked"]["com.uslog.example"]["version"].AsString);
        }

        [Test]
        public void Remove_で両方から消える()
        {
            var manifest = VpmManifest.Load(_project);
            manifest.Add("com.uslog.example", "1.0.0", null);
            manifest.Remove("com.uslog.example");

            Assert.IsFalse(manifest.IsDependency("com.uslog.example"));
            Assert.IsNull(manifest.LockedVersion("com.uslog.example"));
        }

        [Test]
        public void 壊れた_manifest_は上書きせずに例外にする()
        {
            // 黙って書き潰すと、利用者の依存が全部消えたように見える。
            WriteManifest("{ これは JSON ではない ");

            Assert.Throws<IOException>(() => VpmManifest.Load(_project));
        }

        [Test]
        public void 保存したファイルは改行で終わる()
        {
            var manifest = VpmManifest.Load(_project);
            manifest.Add("com.uslog.example", "1.0.0", null);
            manifest.Save();

            StringAssert.EndsWith("\n", File.ReadAllText(VpmManifest.PathFor(_project)));
        }
    }

    public class UpmConfigWriterTests
    {
        private string _temp;

        [SetUp]
        public void SetUp()
        {
            _temp = Path.Combine(Path.GetTempPath(), "uslog-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        }

        [Test]
        public void 新規なら丸ごと書く()
        {
            var path = Path.Combine(_temp, ".upmconfig.toml");

            UpmConfigWriter.WriteUpmConfig(path, "https://private-upm.uslog.tech/", "tok", "me@example.com");

            var text = File.ReadAllText(path);
            StringAssert.Contains("[npmAuth.\"https://private-upm.uslog.tech\"]", text);
            StringAssert.Contains("token = \"tok\"", text);
            // これが無いと「一覧は見えるのに取得だけ失敗する」状態になる。
            StringAssert.Contains("alwaysAuth = true", text);
        }

        [Test]
        public void 他のレジストリの設定は残す()
        {
            var path = Path.Combine(_temp, ".upmconfig.toml");
            File.WriteAllText(path,
                "[npmAuth.\"https://other.example\"]\n" +
                "token = \"other\"\n" +
                "alwaysAuth = true\n");

            UpmConfigWriter.WriteUpmConfig(path, "https://private-upm.uslog.tech", "tok", "me@example.com");

            var text = File.ReadAllText(path);
            StringAssert.Contains("[npmAuth.\"https://other.example\"]", text);
            StringAssert.Contains("token = \"other\"", text);
            StringAssert.Contains("[npmAuth.\"https://private-upm.uslog.tech\"]", text);
        }

        [Test]
        public void 同じレジストリの古い記述は差し替える()
        {
            var path = Path.Combine(_temp, ".upmconfig.toml");
            File.WriteAllText(path,
                "[npmAuth.\"https://private-upm.uslog.tech\"]\n" +
                "token = \"OLD\"\n" +
                "email = \"old@example.com\"\n" +
                "alwaysAuth = true\n" +
                "\n" +
                "[npmAuth.\"https://other.example\"]\n" +
                "token = \"other\"\n");

            UpmConfigWriter.WriteUpmConfig(path, "https://private-upm.uslog.tech", "NEW", "new@example.com");

            var text = File.ReadAllText(path);
            StringAssert.DoesNotContain("OLD", text);
            StringAssert.Contains("token = \"NEW\"", text);
            StringAssert.Contains("token = \"other\"", text);
        }

        [Test]
        public void 上書き前に控えを残す()
        {
            var path = Path.Combine(_temp, ".upmconfig.toml");
            File.WriteAllText(path, "[npmAuth.\"https://other.example\"]\ntoken = \"other\"\n");

            UpmConfigWriter.WriteUpmConfig(path, "https://private-upm.uslog.tech", "tok", null);

            Assert.IsTrue(File.Exists(path + ".bak"));
            StringAssert.Contains("other", File.ReadAllText(path + ".bak"));
        }

        [Test]
        public void 末尾スラッシュの有無で別扱いにしない()
        {
            // ここがずれると Unity が認証を付けずに取得へ行く。
            var path = Path.Combine(_temp, ".upmconfig.toml");

            UpmConfigWriter.WriteUpmConfig(path, "https://private-upm.uslog.tech/", "a", null);
            UpmConfigWriter.WriteUpmConfig(path, "https://private-upm.uslog.tech", "b", null);

            var text = File.ReadAllText(path);
            StringAssert.DoesNotContain("\"a\"", text);
            Assert.AreEqual(1, CountOccurrences(text, "[npmAuth."));
        }

        [Test]
        public void scopedRegistries_を足しても他の記述を消さない()
        {
            var manifestPath = Path.Combine(_temp, "Packages", "manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            File.WriteAllText(manifestPath, @"{
  ""dependencies"": { ""com.unity.ide.rider"": ""3.0.0"" },
  ""scopedRegistries"": [ { ""name"": ""Other"", ""url"": ""https://other.example"", ""scopes"": [""com.other""] } ]
}");

            UpmConfigWriter.WriteScopedRegistry(manifestPath, "https://private-upm.uslog.tech", "USLOG", new[] { "com.uslog" });

            var written = JsonValue.Parse(File.ReadAllText(manifestPath));

            Assert.AreEqual("3.0.0", written["dependencies"]["com.unity.ide.rider"].AsString);
            Assert.AreEqual(2, written["scopedRegistries"].Count);
            Assert.AreEqual("https://other.example", written["scopedRegistries"][0]["url"].AsString);
            Assert.AreEqual("https://private-upm.uslog.tech", written["scopedRegistries"][1]["url"].AsString);
            Assert.AreEqual("com.uslog", written["scopedRegistries"][1]["scopes"][0].AsString);
        }

        [Test]
        public void 同じ_URL_の_scopedRegistries_は増やさずに更新する()
        {
            var manifestPath = Path.Combine(_temp, "Packages", "manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            File.WriteAllText(manifestPath,
                @"{""scopedRegistries"":[{""name"":""old"",""url"":""https://private-upm.uslog.tech/"",""scopes"":[""x""]}]}");

            UpmConfigWriter.WriteScopedRegistry(manifestPath, "https://private-upm.uslog.tech", "USLOG", new[] { "com.uslog" });

            var written = JsonValue.Parse(File.ReadAllText(manifestPath));

            Assert.AreEqual(1, written["scopedRegistries"].Count);
            Assert.AreEqual("USLOG", written["scopedRegistries"][0]["name"].AsString);
            Assert.AreEqual("com.uslog", written["scopedRegistries"][0]["scopes"][0].AsString);
        }

        [Test]
        public void 壊れた_manifest_は書き潰さない()
        {
            var manifestPath = Path.Combine(_temp, "Packages", "manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            File.WriteAllText(manifestPath, "{ 壊れている");

            Assert.Throws<IOException>(() =>
                UpmConfigWriter.WriteScopedRegistry(manifestPath, "https://private-upm.uslog.tech"));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }
    }
}
