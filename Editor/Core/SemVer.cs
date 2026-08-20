using System;
using System.Globalization;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// semver 2.0.0 のうち、比較に必要な部分だけ。
    ///
    /// 「最新はどれか」「更新があるか」を決めるのに使う。ここを文字列比較で
    /// 済ませると 1.10.0 が 1.9.0 より古いことになり、更新が出てこない。
    ///
    /// 範囲指定（^1.2.3 など）は解釈しない。私有レジストリの取得は
    /// 版を明示して行うので、解決の曖昧さを持ち込まないほうがよい。
    /// </summary>
    public readonly struct SemVer : IComparable<SemVer>, IEquatable<SemVer>
    {
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }

        /// <summary>ハイフン以降。無ければ空文字。</summary>
        public string Prerelease { get; }

        /// <summary>解釈できなかった版。比較では常にいちばん下に来る。</summary>
        public bool IsValid { get; }

        public string Raw { get; }

        private SemVer(int major, int minor, int patch, string prerelease, string raw, bool valid)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Prerelease = prerelease ?? string.Empty;
            Raw = raw ?? string.Empty;
            IsValid = valid;
        }

        public bool IsPrerelease => Prerelease.Length > 0;

        public static SemVer Invalid(string raw) => new SemVer(0, 0, 0, null, raw, false);

        public static SemVer Parse(string text)
        {
            return TryParse(text, out var version) ? version : Invalid(text);
        }

        public static bool TryParse(string text, out SemVer version)
        {
            version = Invalid(text);
            if (string.IsNullOrWhiteSpace(text)) return false;

            var raw = text.Trim();
            var body = raw;

            // ビルドメタデータは比較に影響しない（semver 2.0.0 §10）
            var plus = body.IndexOf('+');
            if (plus >= 0) body = body.Substring(0, plus);

            var prerelease = string.Empty;
            var dash = body.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = body.Substring(dash + 1);
                body = body.Substring(0, dash);
            }

            var parts = body.Split('.');
            if (parts.Length < 1 || parts.Length > 3) return false;

            var numbers = new int[3];
            for (var i = 0; i < 3; i++)
            {
                if (i >= parts.Length)
                {
                    // "1" や "1.2" も通す。Unity のパッケージには実在する。
                    numbers[i] = 0;
                    continue;
                }

                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    return false;
                numbers[i] = n;
            }

            version = new SemVer(numbers[0], numbers[1], numbers[2], prerelease, raw, true);
            return true;
        }

        public int CompareTo(SemVer other)
        {
            // 読めなかった版は下に沈める。上に来ると「最新」に選ばれてしまう。
            if (!IsValid || !other.IsValid)
            {
                if (IsValid) return 1;
                if (other.IsValid) return -1;
                return string.CompareOrdinal(Raw, other.Raw);
            }

            if (Major != other.Major) return Major.CompareTo(other.Major);
            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
            if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

            // 数字が同じなら、prerelease が付いているほうが古い
            if (Prerelease.Length == 0 && other.Prerelease.Length == 0) return 0;
            if (Prerelease.Length == 0) return 1;
            if (other.Prerelease.Length == 0) return -1;

            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        private static int ComparePrerelease(string a, string b)
        {
            var left = a.Split('.');
            var right = b.Split('.');
            var max = Math.Max(left.Length, right.Length);

            for (var i = 0; i < max; i++)
            {
                // 識別子が少ないほうが小さい（1.0.0-alpha < 1.0.0-alpha.1）
                if (i >= left.Length) return -1;
                if (i >= right.Length) return 1;

                var ln = int.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var li);
                var rn = int.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ri);

                if (ln && rn)
                {
                    if (li != ri) return li.CompareTo(ri);
                    continue;
                }

                // 数値の識別子は文字列の識別子より小さい
                if (ln) return -1;
                if (rn) return 1;

                var cmp = string.CompareOrdinal(left[i], right[i]);
                if (cmp != 0) return cmp < 0 ? -1 : 1;
            }

            return 0;
        }

        public bool Equals(SemVer other) => CompareTo(other) == 0;

        public override bool Equals(object obj) => obj is SemVer other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Major;
                hash = (hash * 397) ^ Minor;
                hash = (hash * 397) ^ Patch;
                hash = (hash * 397) ^ Prerelease.GetHashCode();
                return hash;
            }
        }

        public override string ToString() => Raw;

        public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
        public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
        public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
        public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
        public static bool operator ==(SemVer a, SemVer b) => a.CompareTo(b) == 0;
        public static bool operator !=(SemVer a, SemVer b) => a.CompareTo(b) != 0;
    }
}
