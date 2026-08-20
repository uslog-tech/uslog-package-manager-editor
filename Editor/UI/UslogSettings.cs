using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// 設定。プロジェクトではなく**利用者ごと**に持つ。
    ///
    /// レジストリ URL をプロジェクト設定にすると、リポジトリに入って
    /// チーム全員に配られる。それ自体は害が無いが、検証環境と本番を
    /// 切り替えたい人がリポジトリを汚さずに切り替えられるほうがよい。
    /// </summary>
    public static class UslogSettings
    {
        public const string DefaultRegistryUrl = "https://private-upm.uslog.tech";

        private const string RegistryUrlKey = "USLOG.PackageManager.RegistryUrl";
        private const string ScopesKey = "USLOG.PackageManager.Scopes";

        public static string RegistryUrl
        {
            get
            {
                var value = EditorPrefs.GetString(RegistryUrlKey, DefaultRegistryUrl);
                return UslogApiClient.NormalizeRegistryUrl(
                    string.IsNullOrWhiteSpace(value) ? DefaultRegistryUrl : value);
            }
            set => EditorPrefs.SetString(RegistryUrlKey, UslogApiClient.NormalizeRegistryUrl(value));
        }

        /// <summary>併用モードで manifest.json に書く scopes。前方一致。</summary>
        public static IReadOnlyList<string> Scopes
        {
            get
            {
                var raw = EditorPrefs.GetString(ScopesKey, "com.uslog");
                var parts = raw.Split(',');
                var result = new List<string>();

                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length > 0) result.Add(trimmed);
                }

                return result.Count > 0 ? result : new List<string> { "com.uslog" };
            }
            set => EditorPrefs.SetString(ScopesKey, string.Join(",", value));
        }

        public static void ResetRegistryUrl() => EditorPrefs.DeleteKey(RegistryUrlKey);

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            return new SettingsProvider("Preferences/USLOG Package Manager", SettingsScope.User)
            {
                label = "USLOG Package Manager",
                keywords = new HashSet<string> { "USLOG", "UPM", "VPM", "registry", "package" },
                guiHandler = _ =>
                {
                    EditorGUILayout.LabelField("レジストリ", EditorStyles.boldLabel);

                    EditorGUI.BeginChangeCheck();
                    var url = EditorGUILayout.TextField("URL", RegistryUrl);
                    if (EditorGUI.EndChangeCheck()) RegistryUrl = url;

                    EditorGUILayout.HelpBox(
                        "末尾のスラッシュは自動で外します。.upmconfig.toml と manifest.json の URL は" +
                        "一字一句同じでないと、Unity が認証を付けずに取得へ行きます。",
                        MessageType.Info);

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("併用モードの scopes", EditorStyles.boldLabel);

                    EditorGUI.BeginChangeCheck();
                    var scopes = EditorGUILayout.TextField("scopes", string.Join(",", Scopes));
                    if (EditorGUI.EndChangeCheck()) Scopes = scopes.Split(',');

                    EditorGUILayout.HelpBox(
                        "Unity 標準の Package Manager でも使うときに manifest.json へ書く値です。" +
                        "前方一致なので com.uslog と書けば com.uslog.* が対象になります。",
                        MessageType.Info);

                    EditorGUILayout.Space();
                    if (GUILayout.Button("既定値に戻す", GUILayout.Width(160)))
                    {
                        ResetRegistryUrl();
                    }
                },
            };
        }
    }
}
