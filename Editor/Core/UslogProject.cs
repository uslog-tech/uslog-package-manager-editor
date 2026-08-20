using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Uslog.PackageManager.Editor
{
    public sealed class InstalledPackage
    {
        public string Name { get; internal set; }
        public string Version { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Path { get; internal set; }

        public string Title => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName;
    }

    /// <summary>
    /// プロジェクト側の状態を見る / 変える。
    ///
    /// VPM の作法に合わせて Packages/ 直下に展開し、
    /// vpm-manifest.json に記録する。UPM の scopedRegistries 経由では
    /// 入れない（そちらは「併用モード」で別に用意する）。
    /// </summary>
    public static class UslogProject
    {
        public const string PackagesFolder = "Packages";

        /// <summary>Unity が触らない作業場所。展開はここで済ませてから移す。</summary>
        private const string StagingFolder = "Temp";

        public static string PackagesPath(string projectRoot)
        {
            return Path.Combine(projectRoot, PackagesFolder);
        }

        public static string PackagePath(string projectRoot, string packageName)
        {
            if (string.IsNullOrEmpty(packageName)) throw new ArgumentException("パッケージ名が空です", nameof(packageName));

            // 名前がそのままフォルダ名になる。区切り文字が入っていたら、
            // Packages/ の外に書き出せてしまう。
            if (packageName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                packageName == "." || packageName == "..")
            {
                throw new ArgumentException($"パッケージ名として扱えません: {packageName}", nameof(packageName));
            }

            return Path.Combine(PackagesPath(projectRoot), packageName);
        }

        /// <summary>Packages/ の下にある embedded パッケージを全部見る。</summary>
        public static IReadOnlyList<InstalledPackage> ScanInstalled(string projectRoot)
        {
            var result = new List<InstalledPackage>();
            var packages = PackagesPath(projectRoot);
            if (!Directory.Exists(packages)) return result;

            foreach (var directory in Directory.GetDirectories(packages))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal)) continue;

                var manifestPath = Path.Combine(directory, "package.json");
                if (!File.Exists(manifestPath)) continue;

                JsonValue manifest;
                try
                {
                    if (!JsonValue.TryParse(File.ReadAllText(manifestPath), out manifest)) continue;
                }
                catch (IOException)
                {
                    continue;
                }

                result.Add(new InstalledPackage
                {
                    Name = manifest["name"].AsString ?? name,
                    Version = manifest["version"].AsString ?? string.Empty,
                    DisplayName = manifest["displayName"].AsString,
                    Path = directory,
                });
            }

            return result;
        }

        public static InstalledPackage FindInstalled(string projectRoot, string packageName)
        {
            return ScanInstalled(projectRoot).FirstOrDefault(p => p.Name == packageName);
        }
    }

    /// <summary>
    /// 取得 → 展開 → 差し替え → vpm-manifest.json の更新。
    ///
    /// 展開先へ直接書かないのは、途中で失敗したときに「半分だけ入った
    /// パッケージ」が残るため。作業場所で組み立ててから、最後に入れ替える。
    /// </summary>
    public sealed class PackageInstaller
    {
        private readonly string _projectRoot;
        private readonly UslogApiClient _client;

        public PackageInstaller(string projectRoot, UslogApiClient client)
        {
            _projectRoot = projectRoot ?? throw new ArgumentNullException(nameof(projectRoot));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<InstalledPackage> InstallAsync(
            UslogPackageVersion version,
            string token,
            IProgress<float> progress = null,
            CancellationToken cancel = default)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            if (string.IsNullOrEmpty(version.Url))
            {
                throw new UslogApiException(0, "no_url",
                    $"{version.Name} {version.VersionText} に取得先の URL がありません。");
            }

            var payload = await _client
                .DownloadAsync(version.Url, token, progress, cancel)
                .ConfigureAwait(false);

            var staging = CreateStagingDirectory();

            try
            {
                using (var stream = new MemoryStream(payload, writable: false))
                {
                    TarGzReader.Extract(stream, staging);
                }

                VerifyExtracted(staging, version);

                var destination = UslogProject.PackagePath(_projectRoot, version.Name);
                ReplaceDirectory(staging, destination);

                var manifest = VpmManifest.Load(_projectRoot);
                manifest.Add(version.Name, version.VersionText, version.VpmDependencies);
                manifest.Save();

                return new InstalledPackage
                {
                    Name = version.Name,
                    Version = version.VersionText,
                    DisplayName = version.DisplayName,
                    Path = destination,
                };
            }
            finally
            {
                DeleteDirectory(staging);
            }
        }

        public void Uninstall(string packageName)
        {
            var path = UslogProject.PackagePath(_projectRoot, packageName);
            DeleteDirectory(path);

            var manifest = VpmManifest.Load(_projectRoot);
            manifest.Remove(packageName);
            manifest.Save();
        }

        /// <summary>
        /// vpm-manifest.json には載っているのに Packages/ に無いもの。
        /// プロジェクトを clone した直後がこの状態になる。
        /// </summary>
        public IReadOnlyList<string> MissingFromDisk()
        {
            var manifest = VpmManifest.Load(_projectRoot);
            var installed = new HashSet<string>(
                UslogProject.ScanInstalled(_projectRoot).Select(p => p.Name),
                StringComparer.Ordinal);

            return manifest.LockedNames.Where(name => !installed.Contains(name)).ToList();
        }

        // ------------------------------------------------------------ 下回り

        /// <summary>
        /// 展開したものが本当に目当てのパッケージか確かめる。
        /// ここを飛ばすと、取り違えたものを Packages/ に置いてしまう。
        /// </summary>
        private static void VerifyExtracted(string staging, UslogPackageVersion version)
        {
            var manifestPath = Path.Combine(staging, "package.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException(
                    $"{version.Name} の中身に package.json がありません。パッケージが壊れている可能性があります。");
            }

            if (!JsonValue.TryParse(File.ReadAllText(manifestPath), out var manifest))
            {
                throw new InvalidDataException($"{version.Name} の package.json を解釈できません。");
            }

            var name = manifest["name"].AsString;
            if (!string.Equals(name, version.Name, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"取得したものの名前が違います（要求: {version.Name} / 中身: {name}）。");
            }
        }

        private string CreateStagingDirectory()
        {
            // プロジェクトの Temp/ に置く。Unity が見ないフォルダで、かつ
            // Packages/ と同じボリュームなので、最後の Move が単なる rename で済む。
            var staging = Path.Combine(_projectRoot, "Temp", "USLOG", Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(staging);
            }
            catch (Exception)
            {
                // プロジェクトが読み取り専用の場所にある、といった場合の逃げ道。
                staging = Path.Combine(Path.GetTempPath(), "uslog-upm", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
            }

            return staging;
        }

        private static void ReplaceDirectory(string source, string destination)
        {
            DeleteDirectory(destination);

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            try
            {
                Directory.Move(source, destination);
            }
            catch (IOException)
            {
                // ボリュームをまたいだ、あるいは Unity がファイルを掴んでいる。
                CopyDirectory(source, destination);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        private static void DeleteDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // 読み取り専用属性が付いていると消せない。落として消し直す。
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch (Exception)
                    {
                        // 属性を落とせないものは、次の Delete でどうせ落ちる
                    }
                }
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
