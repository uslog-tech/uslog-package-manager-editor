using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;

namespace Uslog.PackageManager.Editor.Tests
{
    public class TarGzReaderTests
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
        public void npm_の_tarball_を展開して_package_を剥がす()
        {
            var tgz = BuildTarGz(
                ("package/package.json", "{\"name\":\"tech.uslog.example\"}"),
                ("package/Runtime/Thing.cs", "// hello"));

            var written = TarGzReader.Extract(new MemoryStream(tgz), _temp);

            Assert.AreEqual(2, written.Count);
            Assert.IsTrue(File.Exists(Path.Combine(_temp, "package.json")));
            Assert.IsTrue(File.Exists(Path.Combine(_temp, "Runtime", "Thing.cs")));
            Assert.AreEqual("// hello", File.ReadAllText(Path.Combine(_temp, "Runtime", "Thing.cs")));
        }

        [Test]
        public void 接頭辞の外にあるものは捨てる()
        {
            var tgz = BuildTarGz(
                ("package/keep.txt", "keep"),
                ("elsewhere/drop.txt", "drop"));

            TarGzReader.Extract(new MemoryStream(tgz), _temp);

            Assert.IsTrue(File.Exists(Path.Combine(_temp, "keep.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(_temp, "drop.txt")));
        }

        [Test]
        public void 展開先の外を指す名前を拒む()
        {
            // tar の名前には ".." も絶対パスも入れられる。ここを通すと
            // パッケージを 1 つ取得しただけで、任意の場所に書き込まれる。
            Assert.Throws<InvalidDataException>(() =>
                TarGzReader.ResolveSafe(_temp, "../escaped.txt"));

            Assert.Throws<InvalidDataException>(() =>
                TarGzReader.ResolveSafe(_temp, "sub/../../escaped.txt"));

            Assert.Throws<InvalidDataException>(() =>
                TarGzReader.ResolveSafe(_temp, "/etc/passwd"));
        }

        [Test]
        public void 素直な相対パスは通す()
        {
            var resolved = TarGzReader.ResolveSafe(_temp, "Runtime/Thing.cs");

            StringAssert.StartsWith(Path.GetFullPath(_temp), resolved);
            StringAssert.EndsWith("Thing.cs", resolved);
        }

        [Test]
        public void Strip_は接頭辞の外を_null_にする()
        {
            Assert.AreEqual("a/b.txt", TarGzReader.Strip("package/a/b.txt", "package/"));
            Assert.AreEqual("a/b.txt", TarGzReader.Strip("./package/a/b.txt", "package/"));
            Assert.IsNull(TarGzReader.Strip("other/a.txt", "package/"));
            Assert.IsNull(TarGzReader.Strip("package/", "package/"));
            Assert.IsNull(TarGzReader.Strip("", "package/"));
            Assert.IsNull(TarGzReader.Strip(null, "package/"));
        }

        [Test]
        public void ディレクトリのエントリでも落ちない()
        {
            var tgz = BuildTarGz(
                ("package/Runtime/", null),
                ("package/Runtime/Thing.cs", "x"));

            TarGzReader.Extract(new MemoryStream(tgz), _temp);

            Assert.IsTrue(Directory.Exists(Path.Combine(_temp, "Runtime")));
            Assert.IsTrue(File.Exists(Path.Combine(_temp, "Runtime", "Thing.cs")));
        }

        [Test]
        public void _512_の倍数ちょうどの中身でも次のエントリを見失わない()
        {
            // 詰め物の計算を間違えると、ここだけ壊れる。
            var exact = new string('a', 512);
            var tgz = BuildTarGz(
                ("package/exact.txt", exact),
                ("package/after.txt", "after"));

            TarGzReader.Extract(new MemoryStream(tgz), _temp);

            Assert.AreEqual(exact, File.ReadAllText(Path.Combine(_temp, "exact.txt")));
            Assert.AreEqual("after", File.ReadAllText(Path.Combine(_temp, "after.txt")));
        }

        [Test]
        public void 中身を書かずに一覧だけ見られる()
        {
            var tgz = BuildTarGz(("package/a.txt", "12345"));

            var entries = TarGzReader.List(new MemoryStream(tgz));

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("package/a.txt", entries[0].Name);
            Assert.AreEqual(5, entries[0].Size);
        }

        // ------------------------------------------------------------ 組み立て

        /// <summary>テスト用の tar.gz を組む。中身が null ならディレクトリ。</summary>
        private static byte[] BuildTarGz(params (string name, string content)[] entries)
        {
            using (var tar = new MemoryStream())
            {
                foreach (var entry in entries)
                {
                    var isDirectory = entry.content == null;
                    var payload = isDirectory ? System.Array.Empty<byte>() : Encoding.UTF8.GetBytes(entry.content);

                    tar.Write(Header(entry.name, payload.Length, isDirectory), 0, 512);

                    if (payload.Length > 0)
                    {
                        tar.Write(payload, 0, payload.Length);
                        var padding = (512 - (payload.Length % 512)) % 512;
                        tar.Write(new byte[padding], 0, padding);
                    }
                }

                // 終端は空ブロック 2 つ
                tar.Write(new byte[1024], 0, 1024);

                using (var output = new MemoryStream())
                {
                    using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
                    {
                        var bytes = tar.ToArray();
                        gzip.Write(bytes, 0, bytes.Length);
                    }
                    return output.ToArray();
                }
            }
        }

        private static byte[] Header(string name, int size, bool isDirectory)
        {
            var header = new byte[512];

            Put(header, 0, name, 100);
            Put(header, 100, "0000644\0", 8);
            Put(header, 108, "0000000\0", 8);
            Put(header, 116, "0000000\0", 8);
            Put(header, 124, Convert.ToString(size, 8).PadLeft(11, '0') + "\0", 12);
            Put(header, 136, "00000000000\0", 12);
            Put(header, 148, "        ", 8); // チェックサム欄。読み側は見ていない
            header[156] = (byte)(isDirectory ? '5' : '0');
            Put(header, 257, "ustar\0", 6);
            Put(header, 263, "00", 2);

            return header;
        }

        private static void Put(byte[] buffer, int offset, string value, int length)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            var count = Math.Min(bytes.Length, length);
            System.Array.Copy(bytes, 0, buffer, offset, count);
        }
    }
}
