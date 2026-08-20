using NUnit.Framework;

namespace Uslog.PackageManager.Editor.Tests
{
    public class SemVerTests
    {
        [Test]
        public void 文字列比較では間違える組を正しく並べる()
        {
            // ここを文字列比較で済ませると 1.10.0 < 1.9.0 になり、更新が出てこない。
            Assert.IsTrue(SemVer.Parse("1.10.0") > SemVer.Parse("1.9.0"));
            Assert.IsTrue(SemVer.Parse("2.0.0") > SemVer.Parse("1.99.99"));
            Assert.IsTrue(SemVer.Parse("1.0.10") > SemVer.Parse("1.0.9"));
        }

        [Test]
        public void prerelease_は同じ数字の正式版より古い()
        {
            Assert.IsTrue(SemVer.Parse("1.0.0") > SemVer.Parse("1.0.0-rc.1"));
            Assert.IsTrue(SemVer.Parse("1.0.0-rc.2") > SemVer.Parse("1.0.0-rc.1"));
            Assert.IsTrue(SemVer.Parse("1.0.0-beta") > SemVer.Parse("1.0.0-alpha"));
            Assert.IsTrue(SemVer.Parse("1.0.0-alpha.1") > SemVer.Parse("1.0.0-alpha"));
        }

        [Test]
        public void 数値の識別子は文字列より小さい()
        {
            Assert.IsTrue(SemVer.Parse("1.0.0-alpha") > SemVer.Parse("1.0.0-1"));
        }

        [Test]
        public void ビルドメタデータは比較に影響しない()
        {
            Assert.AreEqual(0, SemVer.Parse("1.2.3+abc").CompareTo(SemVer.Parse("1.2.3+xyz")));
        }

        [Test]
        public void 桁の足りない版も受け取る()
        {
            Assert.IsTrue(SemVer.TryParse("1", out var one));
            Assert.AreEqual(1, one.Major);
            Assert.AreEqual(0, one.Minor);

            Assert.IsTrue(SemVer.TryParse("1.2", out var oneTwo));
            Assert.AreEqual(2, oneTwo.Minor);
        }

        [Test]
        public void 読めない版はいちばん下に沈める()
        {
            // 上に来ると「最新」に選ばれて、勝手に入ってしまう。
            var broken = SemVer.Parse("なんだこれ");

            Assert.IsFalse(broken.IsValid);
            Assert.IsTrue(SemVer.Parse("0.0.1") > broken);
        }

        [Test]
        public void 空文字も落ちずに無効として扱う()
        {
            Assert.IsFalse(SemVer.Parse("").IsValid);
            Assert.IsFalse(SemVer.Parse(null).IsValid);
        }

        [Test]
        public void Raw_は元の文字列を保つ()
        {
            Assert.AreEqual("1.2.3-rc.1+build", SemVer.Parse("1.2.3-rc.1+build").Raw);
        }

        [Test]
        public void 負の数やマイナス記号だけの版は無効()
        {
            Assert.IsFalse(SemVer.Parse("1.-2.3").IsValid);
            Assert.IsFalse(SemVer.Parse("v1.2.3").IsValid);
        }
    }
}
