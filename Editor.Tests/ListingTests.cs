using NUnit.Framework;

namespace Uslog.PackageManager.Editor.Tests
{
    public class ListingTests
    {
        private const string Sample = @"{
  ""name"": ""USLOG Private Packages"",
  ""id"": ""tech.uslog.private"",
  ""uslogDistType"": ""npm-tgz"",
  ""packages"": {
    ""com.uslog.example"": {
      ""versions"": {
        ""1.9.0"":  { ""name"": ""com.uslog.example"", ""version"": ""1.9.0"",  ""displayName"": ""Example"", ""url"": ""https://r/1.9.0.tgz"" },
        ""1.10.0"": { ""name"": ""com.uslog.example"", ""version"": ""1.10.0"", ""displayName"": ""Example"", ""url"": ""https://r/1.10.0.tgz"",
                      ""vpmDependencies"": { ""com.vrchat.base"": ""3.x"" },
                      ""uslogLicense"": { ""commercial"": true, ""individual"": true } },
        ""2.0.0-rc.1"": { ""name"": ""com.uslog.example"", ""version"": ""2.0.0-rc.1"", ""url"": ""https://r/2.0.0-rc.1.tgz"" }
      }
    },
    ""com.uslog.empty"": { ""versions"": {} }
  }
}";

        [Test]
        public void バージョンは新しい順に並ぶ()
        {
            var listing = UslogListing.FromJson(JsonValue.Parse(Sample));
            var package = listing.Find("com.uslog.example");

            Assert.AreEqual("2.0.0-rc.1", package.Versions[0].VersionText);
            Assert.AreEqual("1.10.0", package.Versions[1].VersionText);
            Assert.AreEqual("1.9.0", package.Versions[2].VersionText);
        }

        [Test]
        public void 既定で出すのは_prerelease_ではない最新()
        {
            // rc を既定にすると、押しただけで検証版が入ってしまう。
            var listing = UslogListing.FromJson(JsonValue.Parse(Sample));

            Assert.AreEqual("1.10.0", listing.Find("com.uslog.example").Latest.VersionText);
        }

        [Test]
        public void 版が_1_つも無いパッケージは一覧に出さない()
        {
            var listing = UslogListing.FromJson(JsonValue.Parse(Sample));

            Assert.IsNull(listing.Find("com.uslog.empty"));
            Assert.AreEqual(1, listing.Packages.Count);
        }

        [Test]
        public void 許諾区分と_VPM_依存を読む()
        {
            var listing = UslogListing.FromJson(JsonValue.Parse(Sample));
            var latest = listing.Find("com.uslog.example").Latest;

            Assert.IsTrue(latest.License.Commercial);
            Assert.IsTrue(latest.License.Individual);
            Assert.IsFalse(latest.License.OrgSharing);
            Assert.AreEqual("3.x", latest.VpmDependencies["com.vrchat.base"]);
        }

        [Test]
        public void 許諾区分が無ければ_null_のまま()
        {
            // 「指定なし」と「全部不可」は違う。埋めてしまうと嘘になる。
            var listing = UslogListing.FromJson(JsonValue.Parse(Sample));

            Assert.IsNull(listing.Find("com.uslog.example").Find("1.9.0").License);
        }

        [Test]
        public void 空の応答でも落ちない()
        {
            var listing = UslogListing.FromJson(JsonValue.Parse("{}"));

            Assert.AreEqual(0, listing.Packages.Count);
            Assert.IsNull(listing.Find("なにか"));
        }

        [Test]
        public void 表示名が無ければ名前を出す()
        {
            var package = UslogPackage.FromJson("com.uslog.bare",
                JsonValue.Parse(@"{""versions"":{""1.0.0"":{""version"":""1.0.0""}}}"));

            Assert.AreEqual("com.uslog.bare", package.Title);
        }

        [Test]
        public void 許諾区分の要約を作れる()
        {
            var license = LicenseFlags.FromJson(JsonValue.Parse(@"{""commercial"":true,""individual"":true}"));

            StringAssert.Contains("商用利用", license.Summary());
            StringAssert.Contains("個人利用", license.Summary());
            StringAssert.DoesNotContain("組織内共有", license.Summary());
        }
    }

    public class LoginFlowTests
    {
        [Test]
        public void 同意画面の_URL_を組み立てる()
        {
            var url = UslogLoginFlow.BuildAuthorizeUrl(
                "https://private-upm.uslog.tech/", 51234, "st-at-e", "cha-llenge", "Unity 2022.3 / Proj");

            StringAssert.StartsWith("https://private-upm.uslog.tech/-/uslog/editor/authorize?", url);
            StringAssert.Contains("port=51234", url);
            StringAssert.Contains("state=st-at-e", url);
            // 空白や / はそのまま載せない
            StringAssert.Contains("label=Unity%202022.3%20%2F%20Proj", url);
        }

        [Test]
        public void challenge_は_verifier_の_sha256_を_base64url_にしたもの()
        {
            // RFC 7636 の S256 と同じ計算。サーバー側と食い違うと必ず失敗する。
            Assert.AreEqual(
                "n4bQgYhMfWWaL-qgxVrQFaO_TxsrC4Is0V1sFbDwCgg",
                UslogLoginFlow.Challenge("test"));
        }

        [Test]
        public void base64url_に_padding_と記号を残さない()
        {
            var encoded = UslogLoginFlow.Base64Url(new byte[] { 251, 255, 190 });

            StringAssert.DoesNotContain("=", encoded);
            StringAssert.DoesNotContain("+", encoded);
            StringAssert.DoesNotContain("/", encoded);
        }

        [Test]
        public void 長さの違う_state_を比較しても落ちない()
        {
            Assert.IsFalse(UslogLoginFlow.FixedTimeEquals("abc", "abcd"));
            Assert.IsFalse(UslogLoginFlow.FixedTimeEquals(null, "abc"));
            Assert.IsTrue(UslogLoginFlow.FixedTimeEquals("abc", "abc"));
        }

        [Test]
        public void コールバックのクエリを読む()
        {
            var query = LoopbackAuthServer.ParseQuery("state=abc&code=x%2Fy&empty=");

            Assert.AreEqual("abc", query["state"]);
            Assert.AreEqual("x/y", query["code"]);
            Assert.AreEqual("", query["empty"]);
        }

        [Test]
        public void クエリが空でも落ちない()
        {
            Assert.AreEqual(0, LoopbackAuthServer.ParseQuery("").Count);
            Assert.AreEqual(0, LoopbackAuthServer.ParseQuery(null).Count);
        }
    }
}
