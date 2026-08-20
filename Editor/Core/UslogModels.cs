using System;
using System.Collections.Generic;
using System.Linq;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// 許諾区分。レジストリ側の 5 つの真偽値をそのまま持つ。
    ///
    /// **表示専用。** 取得できるかどうかは契約の有無だけで決まり、ここは
    /// 「取得したものをどう使ってよいか」を利用者に伝えるためのもの。
    /// サーバー側の 005 マイグレーションにも同じことが書いてある。
    /// </summary>
    public sealed class LicenseFlags
    {
        public bool Noncommercial { get; private set; }
        public bool Commercial { get; private set; }
        public bool Individual { get; private set; }
        public bool Corporate { get; private set; }
        public bool OrgSharing { get; private set; }

        public static LicenseFlags FromJson(JsonValue json)
        {
            if (json == null || !json.IsObject) return null;

            return new LicenseFlags
            {
                Noncommercial = json["noncommercial"].AsBool,
                Commercial = json["commercial"].AsBool,
                Individual = json["individual"].AsBool,
                Corporate = json["corporate"].AsBool,
                OrgSharing = json["org_sharing"].AsBool,
            };
        }

        public IEnumerable<KeyValuePair<string, bool>> Rows()
        {
            yield return new KeyValuePair<string, bool>("非商用利用", Noncommercial);
            yield return new KeyValuePair<string, bool>("商用利用", Commercial);
            yield return new KeyValuePair<string, bool>("個人利用", Individual);
            yield return new KeyValuePair<string, bool>("法人利用", Corporate);
            yield return new KeyValuePair<string, bool>("組織内共有", OrgSharing);
        }

        public string Summary()
        {
            var allowed = Rows().Where(r => r.Value).Select(r => r.Key).ToArray();
            return allowed.Length == 0 ? "許諾区分の指定なし" : string.Join(" / ", allowed);
        }
    }

    public sealed class UslogUser
    {
        public string Id { get; private set; }
        public string Email { get; private set; }
        public string DisplayName { get; private set; }

        public string Label => string.IsNullOrEmpty(DisplayName) ? Email : $"{DisplayName} ({Email})";

        public static UslogUser FromJson(JsonValue json)
        {
            if (json == null || !json.IsObject) return null;

            return new UslogUser
            {
                Id = json["id"].AsString,
                Email = json["email"].AsString,
                DisplayName = json["display_name"].AsString,
            };
        }
    }

    /// <summary>GET /-/uslog/api/v1/me の応答。</summary>
    public sealed class UslogAccount
    {
        public string RegistryUrl { get; private set; }
        public UslogUser User { get; private set; }
        public bool CanPublish { get; private set; }

        public static UslogAccount FromJson(JsonValue json)
        {
            return new UslogAccount
            {
                RegistryUrl = json["registry_url"].AsString,
                User = UslogUser.FromJson(json["user"]),
                CanPublish = json["can_publish"].AsBool,
            };
        }
    }

    /// <summary>リスティングの 1 バージョン。中身は package.json + url。</summary>
    public sealed class UslogPackageVersion
    {
        public string Name { get; private set; }
        public string VersionText { get; private set; }
        public SemVer Version { get; private set; }
        public string DisplayName { get; private set; }
        public string Description { get; private set; }
        public string UnityVersion { get; private set; }
        public string Url { get; private set; }
        public string ChangelogUrl { get; private set; }
        public string DocumentationUrl { get; private set; }
        public string AuthorName { get; private set; }
        public LicenseFlags License { get; private set; }

        /// <summary>UPM の依存（Unity 標準の Package Manager が解決する）。</summary>
        public IReadOnlyDictionary<string, string> Dependencies { get; private set; }

        /// <summary>VPM の依存。こちらは我々が解決する。</summary>
        public IReadOnlyDictionary<string, string> VpmDependencies { get; private set; }

        public string Title => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName;

        public static UslogPackageVersion FromJson(string packageName, string version, JsonValue json)
        {
            return new UslogPackageVersion
            {
                Name = json["name"].AsString ?? packageName,
                VersionText = json["version"].AsString ?? version,
                Version = SemVer.Parse(json["version"].AsString ?? version),
                DisplayName = json["displayName"].AsString,
                Description = json["description"].AsString,
                UnityVersion = json["unity"].AsString,
                Url = json["url"].AsString,
                ChangelogUrl = json["changelogUrl"].AsString,
                DocumentationUrl = json["documentationUrl"].AsString,
                AuthorName = json["author"].IsObject ? json["author"]["name"].AsString : json["author"].AsString,
                License = LicenseFlags.FromJson(json["uslogLicense"]),
                Dependencies = ReadMap(json["dependencies"]),
                VpmDependencies = ReadMap(json["vpmDependencies"]),
            };
        }

        private static IReadOnlyDictionary<string, string> ReadMap(JsonValue json)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (json == null || !json.IsObject) return map;

            foreach (var key in json.Keys)
            {
                var text = json[key].AsText;
                if (text != null) map[key] = text;
            }
            return map;
        }
    }

    /// <summary>リスティング上の 1 パッケージ。バージョンは新しい順。</summary>
    public sealed class UslogPackage
    {
        public string Name { get; private set; }
        public IReadOnlyList<UslogPackageVersion> Versions { get; private set; }

        /// <summary>既定で出す版。prerelease しか無ければそれを出す。</summary>
        public UslogPackageVersion Latest =>
            Versions.FirstOrDefault(v => !v.Version.IsPrerelease) ?? Versions.FirstOrDefault();

        public string Title => Latest?.Title ?? Name;

        public static UslogPackage FromJson(string name, JsonValue json)
        {
            var versions = new List<UslogPackageVersion>();
            var versionsJson = json["versions"];

            foreach (var version in versionsJson.Keys)
            {
                versions.Add(UslogPackageVersion.FromJson(name, version, versionsJson[version]));
            }

            // 新しい順。文字列で並べると 1.10.0 が 1.9.0 より前に来る。
            versions.Sort((a, b) => b.Version.CompareTo(a.Version));

            return new UslogPackage { Name = name, Versions = versions };
        }

        public UslogPackageVersion Find(string version)
        {
            return Versions.FirstOrDefault(v => v.VersionText == version);
        }
    }

    /// <summary>GET /-/uslog/api/v1/vpm/index.json の応答。</summary>
    public sealed class UslogListing
    {
        public string Name { get; private set; }
        public string Id { get; private set; }
        public IReadOnlyList<UslogPackage> Packages { get; private set; }

        public static readonly UslogListing Empty = new UslogListing
        {
            Name = string.Empty,
            Id = string.Empty,
            Packages = System.Array.Empty<UslogPackage>(),
        };

        public static UslogListing FromJson(JsonValue json)
        {
            var packages = new List<UslogPackage>();
            var packagesJson = json["packages"];

            foreach (var name in packagesJson.Keys)
            {
                var package = UslogPackage.FromJson(name, packagesJson[name]);
                if (package.Versions.Count > 0) packages.Add(package);
            }

            packages.Sort((a, b) => string.CompareOrdinal(a.Title, b.Title));

            return new UslogListing
            {
                Name = json["name"].AsString,
                Id = json["id"].AsString,
                Packages = packages,
            };
        }

        public UslogPackage Find(string name)
        {
            return Packages.FirstOrDefault(p => p.Name == name);
        }
    }
}
