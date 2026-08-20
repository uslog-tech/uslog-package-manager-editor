using System;
using System.Collections.Generic;
using System.IO;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// Packages/vpm-manifest.json の読み書き。
    ///
    /// VCC / ALCOM / vrc-get が同じファイルを見るので、知らないキーは
    /// 消さずにそのまま残す。JSON を読み込んで書き戻すだけで並びや
    /// 項目が変わると、他のツールとの差分が毎回出て気持ち悪いことになる。
    ///
    ///   dependencies … 利用者が「入れたい」と言ったもの
    ///   locked       … 実際に入っているもの（依存も含む）
    ///
    /// VCC はこの 2 つを分けて持つ。片方だけ書くと、Resolve のときに
    /// 消されたり戻されたりする。
    /// </summary>
    public sealed class VpmManifest
    {
        public const string FileName = "vpm-manifest.json";

        private readonly JsonValue _root;

        public string Path { get; }

        private VpmManifest(string path, JsonValue root)
        {
            Path = path;
            _root = root;
        }

        public static string PathFor(string projectRoot)
        {
            return System.IO.Path.Combine(projectRoot, "Packages", FileName);
        }

        public static VpmManifest Load(string projectRoot)
        {
            var path = PathFor(projectRoot);

            JsonValue root = null;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (!JsonValue.TryParse(text, out root) || !root.IsObject)
                {
                    // 壊れたまま上書きすると、利用者の状態を丸ごと消すことになる。
                    throw new IOException($"{FileName} を解釈できませんでした。手で直してからやり直してください: {path}");
                }
            }

            root = root ?? JsonValue.NewObject();

            if (!root["dependencies"].IsObject) root.Set("dependencies", JsonValue.NewObject());
            if (!root["locked"].IsObject) root.Set("locked", JsonValue.NewObject());

            return new VpmManifest(path, root);
        }

        public JsonValue Dependencies => _root["dependencies"];
        public JsonValue Locked => _root["locked"];

        public IEnumerable<string> LockedNames => Locked.Keys;

        public string LockedVersion(string name)
        {
            return Locked[name]["version"].AsString;
        }

        public bool IsDependency(string name) => Dependencies.Has(name);

        /// <summary>
        /// 入れたものを記録する。dependencies と locked の両方を更新する。
        /// </summary>
        public void Add(string name, string version, IReadOnlyDictionary<string, string> vpmDependencies)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("パッケージ名が空です", nameof(name));
            if (string.IsNullOrEmpty(version)) throw new ArgumentException("バージョンが空です", nameof(version));

            Dependencies.Set(name, JsonValue.NewObject().Set("version", version));

            var locked = JsonValue.NewObject().Set("version", version);

            // VCC は locked に依存関係も書く。空でも "dependencies" のキーは残す
            // （他のツールが無いものとして扱わないように）。
            var dependencies = JsonValue.NewObject();
            if (vpmDependencies != null)
            {
                foreach (var pair in vpmDependencies) dependencies.Set(pair.Key, pair.Value);
            }
            locked.Set("dependencies", dependencies);

            Locked.Set(name, locked);
        }

        public void Remove(string name)
        {
            Dependencies.Remove(name);
            Locked.Remove(name);
        }

        public void Save()
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // 末尾の改行を入れておく。git の diff が最終行で汚れないように。
            File.WriteAllText(Path, _root.ToJson() + "\n");
        }

        /// <summary>テスト用。書き出す内容をそのまま見る。</summary>
        internal string ToJson() => _root.ToJson();
    }
}
