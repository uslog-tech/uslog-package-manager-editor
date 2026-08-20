using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// npm の tarball（.tgz）を展開する。
    ///
    /// VPM の配布物は zip だが、このレジストリは npm レジストリなので
    /// 流れてくるのは gzip + tar。.NET Standard に tar は無いので自前で読む。
    /// 外部パッケージを足さないのは、VRChat SDK と依存がぶつかると
    /// そもそも入らないプロジェクトが出るため。
    ///
    /// 展開先の外へ書き出させないことがいちばん大事。tar は名前に
    /// "../" も絶対パスもシンボリックリンクも入れられる。
    /// </summary>
    public static class TarGzReader
    {
        private const int BlockSize = 512;

        /// <summary>npm の tarball は必ずこの 1 段の下に入っている。</summary>
        public const string NpmRootPrefix = "package/";

        public sealed class TarEntry
        {
            public string Name { get; internal set; }
            public long Size { get; internal set; }
            public bool IsDirectory { get; internal set; }
        }

        /// <summary>
        /// 展開する。戻り値は書き出したファイルの相対パス（区切りは '/'）。
        /// 既存の内容は呼び出し側で消しておくこと（ここでは消さない）。
        /// </summary>
        public static IReadOnlyList<string> Extract(Stream tgzStream, string destinationDirectory, string stripPrefix = NpmRootPrefix)
        {
            if (tgzStream == null) throw new ArgumentNullException(nameof(tgzStream));
            if (string.IsNullOrEmpty(destinationDirectory)) throw new ArgumentException("展開先が空です", nameof(destinationDirectory));

            var root = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(root);

            var written = new List<string>();

            using (var gzip = new GZipStream(tgzStream, CompressionMode.Decompress, leaveOpen: true))
            {
                foreach (var entry in ReadEntries(gzip, out var payload))
                {
                    var relative = Strip(entry.Name, stripPrefix);

                    if (relative == null)
                    {
                        // 剥がす接頭辞の外にあるもの（PaxHeaders など）は捨てる
                        payload.Skip(entry.Size);
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(ResolveSafe(root, relative));
                        payload.Skip(entry.Size);
                        continue;
                    }

                    var full = ResolveSafe(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));

                    using (var file = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        payload.CopyTo(file, entry.Size);
                    }

                    written.Add(relative);
                }
            }

            return written;
        }

        /// <summary>中身を書き出さずに一覧だけ見る。検証とテスト用。</summary>
        public static IReadOnlyList<TarEntry> List(Stream tgzStream)
        {
            var entries = new List<TarEntry>();

            using (var gzip = new GZipStream(tgzStream, CompressionMode.Decompress, leaveOpen: true))
            {
                foreach (var entry in ReadEntries(gzip, out var payload))
                {
                    entries.Add(entry);
                    payload.Skip(entry.Size);
                }
            }

            return entries;
        }

        // ------------------------------------------------------------ 中身

        /// <summary>
        /// tar のブロック読み。エントリを 1 つ返すたびに、本体は呼び出し側が
        /// Skip か CopyTo で必ず読み切ること（読み残すと次の位置がずれる）。
        /// </summary>
        private static IEnumerable<TarEntry> ReadEntries(Stream stream, out PayloadReader payload)
        {
            var reader = new PayloadReader(stream);
            payload = reader;
            return Iterate(reader);
        }

        private static IEnumerable<TarEntry> Iterate(PayloadReader reader)
        {
            var header = new byte[BlockSize];
            var emptyBlocks = 0;

            // GNU / pax の拡張ヘッダで上書きされる名前
            string pendingLongName = null;

            while (true)
            {
                if (!reader.TryReadBlock(header)) yield break;

                if (IsAllZero(header))
                {
                    // 終端は空ブロック 2 つ。1 つだけなら壊れた tar として扱う。
                    emptyBlocks++;
                    if (emptyBlocks >= 2) yield break;
                    continue;
                }

                emptyBlocks = 0;

                var size = ParseSize(header, 124, 12);
                var typeFlag = (char)header[156];
                var name = ReadName(header);

                if (pendingLongName != null)
                {
                    name = pendingLongName;
                    pendingLongName = null;
                }

                // GNU の長い名前。次のエントリの名前が本体に入っている。
                if (typeFlag == 'L')
                {
                    pendingLongName = TrimNul(Encoding.UTF8.GetString(reader.ReadAll(size)));
                    continue;
                }

                // pax ヘッダ。path= だけ拾って、残りは捨てる。
                if (typeFlag == 'x' || typeFlag == 'X' || typeFlag == 'g')
                {
                    var pax = Encoding.UTF8.GetString(reader.ReadAll(size));
                    var path = ReadPaxPath(pax);
                    if (path != null) pendingLongName = path;
                    continue;
                }

                // シンボリックリンク / ハードリンク / デバイスは受け取らない。
                // リンクは展開先の外を指せるので、通すと安全性の話が
                // パス検査だけでは閉じなくなる。
                if (typeFlag == '1' || typeFlag == '2' || typeFlag == '3' ||
                    typeFlag == '4' || typeFlag == '6')
                {
                    reader.Skip(size);
                    continue;
                }

                var isDirectory = typeFlag == '5' || name.EndsWith("/", StringComparison.Ordinal);

                yield return new TarEntry
                {
                    Name = name,
                    Size = isDirectory ? 0 : size,
                    IsDirectory = isDirectory,
                };
            }
        }

        private sealed class PayloadReader
        {
            private readonly Stream _stream;

            public PayloadReader(Stream stream)
            {
                _stream = stream;
            }

            public bool TryReadBlock(byte[] block)
            {
                var read = 0;
                while (read < BlockSize)
                {
                    var n = _stream.Read(block, read, BlockSize - read);
                    if (n <= 0) return read == 0 ? false : throw new InvalidDataException("tar のヘッダが途中で終わっています");
                    read += n;
                }
                return true;
            }

            /// <summary>本体を読み飛ばす。512 の倍数までの詰め物も一緒に。</summary>
            public void Skip(long size)
            {
                CopyTo(null, size);
            }

            public byte[] ReadAll(long size)
            {
                using (var buffer = new MemoryStream())
                {
                    CopyTo(buffer, size);
                    return buffer.ToArray();
                }
            }

            public void CopyTo(Stream destination, long size)
            {
                var buffer = new byte[64 * 1024];
                var remaining = size;

                while (remaining > 0)
                {
                    var want = (int)Math.Min(buffer.Length, remaining);
                    var read = _stream.Read(buffer, 0, want);
                    if (read <= 0) throw new InvalidDataException("tar の本体が途中で終わっています");

                    destination?.Write(buffer, 0, read);
                    remaining -= read;
                }

                // ブロック境界まで進める
                var padding = (int)((BlockSize - (size % BlockSize)) % BlockSize);
                while (padding > 0)
                {
                    var read = _stream.Read(buffer, 0, padding);
                    if (read <= 0) break;
                    padding -= read;
                }
            }
        }

        // ------------------------------------------------------------ 名前

        private static string ReadName(byte[] header)
        {
            var name = TrimNul(Encoding.UTF8.GetString(header, 0, 100));

            // ustar は長い名前を prefix と name に割る
            var magic = Encoding.ASCII.GetString(header, 257, 5);
            if (magic == "ustar")
            {
                var prefix = TrimNul(Encoding.UTF8.GetString(header, 345, 155));
                if (prefix.Length > 0) name = prefix + "/" + name;
            }

            return name.Replace('\\', '/');
        }

        private static string ReadPaxPath(string pax)
        {
            // "<長さ> path=<値>\n" の並び
            foreach (var line in pax.Split('\n'))
            {
                var space = line.IndexOf(' ');
                if (space < 0) continue;

                var pair = line.Substring(space + 1);
                if (pair.StartsWith("path=", StringComparison.Ordinal))
                {
                    return pair.Substring("path=".Length).Replace('\\', '/');
                }
            }
            return null;
        }

        /// <summary>接頭辞を剥がす。外にあるものは null を返して捨てさせる。</summary>
        internal static string Strip(string name, string prefix)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var normalized = name.Replace('\\', '/').TrimStart('/');
            if (normalized.Length == 0) return null;

            // "./" は落とす
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized.Substring(2);

            if (!string.IsNullOrEmpty(prefix))
            {
                if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) return null;
                normalized = normalized.Substring(prefix.Length);
            }

            normalized = normalized.TrimEnd('/');
            return normalized.Length == 0 ? null : normalized;
        }

        /// <summary>
        /// 展開先の中に収まることを確かめてから絶対パスにする。
        /// "../" も絶対パスも、ここで必ず弾く。
        /// </summary>
        internal static string ResolveSafe(string root, string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Contains(":"))
                throw new InvalidDataException($"展開先の外を指しています: {relative}");

            var rootFull = Path.GetFullPath(root);
            var combined = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));

            var withSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            if (!combined.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"展開先の外を指しています: {relative}");

            return combined;
        }

        // ------------------------------------------------------------ 小物

        private static bool IsAllZero(byte[] block)
        {
            foreach (var b in block)
            {
                if (b != 0) return false;
            }
            return true;
        }

        private static string TrimNul(string value)
        {
            var nul = value.IndexOf('\0');
            return (nul >= 0 ? value.Substring(0, nul) : value).Trim();
        }

        private static long ParseSize(byte[] header, int offset, int length)
        {
            // GNU の base-256（先頭ビットが立っている）。8GB 超のときだけ出る。
            if ((header[offset] & 0x80) != 0)
            {
                long value = header[offset] & 0x7f;
                for (var i = 1; i < length; i++) value = (value << 8) | header[offset + i];
                return value;
            }

            var text = TrimNul(Encoding.ASCII.GetString(header, offset, length));
            if (text.Length == 0) return 0;

            long result = 0;
            foreach (var c in text)
            {
                if (c < '0' || c > '7')
                {
                    // 8 進として読めない値。壊れた tar を無理に読み進めない。
                    throw new InvalidDataException(
                        $"tar のサイズ欄を解釈できません: '{text}'");
                }
                result = (result * 8) + (c - '0');
            }

            if (result < 0)
                throw new InvalidDataException("tar のサイズが負です");

            return result;
        }
    }
}
