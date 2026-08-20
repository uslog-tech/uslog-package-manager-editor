using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// USLOG Package Manager 本体のウィンドウ。
    ///
    /// 契約しているパッケージを一覧し、Packages/ に入れて
    /// vpm-manifest.json に記録するところまでを、ここで完結させる。
    /// </summary>
    public sealed class UslogPackageManagerWindow : EditorWindow
    {
        private enum Filter
        {
            All,
            Available,
            Installed,
            Updates,
        }

        // --- 状態 ---------------------------------------------------------

        private CredentialStore _credentials;
        private CredentialStore.Credential _credential;
        private UslogApiClient _client;

        private UslogAccount _account;
        private UslogListing _listing = UslogListing.Empty;
        private IReadOnlyList<InstalledPackage> _installed = System.Array.Empty<InstalledPackage>();

        private string _selectedName;
        private Filter _filter = Filter.All;
        private string _search = string.Empty;

        private bool _busy;
        private string _status;
        private string _error;
        private CancellationTokenSource _cancel;

        // --- 部品 ---------------------------------------------------------

        private Label _accountLabel;
        private Button _loginButton;
        private Button _logoutButton;
        private Button _refreshButton;
        private VisualElement _banner;
        private VisualElement _listContainer;
        private VisualElement _detailContainer;
        private VisualElement _filterRow;

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        [MenuItem("Window/USLOG Package Manager", priority = 1500)]
        public static void Open()
        {
            var window = GetWindow<UslogPackageManagerWindow>();
            window.titleContent = new GUIContent("USLOG Packages");
            window.minSize = new Vector2(720, 420);
            window.Show();
        }

        private void OnEnable()
        {
            _credentials = new CredentialStore();
            SyncRegistry();
        }

        /// <summary>
        /// 設定でレジストリを切り替えたときに追随する。ウィンドウを開き直さないと
        /// 反映されないと、検証環境と本番を行き来する人が必ず引っかかる。
        /// </summary>
        private void SyncRegistry()
        {
            var url = UslogSettings.RegistryUrl;
            if (_client != null && _client.RegistryUrl == url) return;

            _client = new UslogApiClient(url);
            _credential = _credentials.Load(url);
            _account = null;
            _listing = UslogListing.Empty;
            _selectedName = null;
        }

        private void OnDisable()
        {
            _cancel?.Cancel();
            _cancel?.Dispose();
            _cancel = null;
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;

            root.Add(BuildHeader());

            _banner = new VisualElement();
            _banner.style.display = DisplayStyle.None;
            root.Add(_banner);

            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.flexGrow = 1;
            root.Add(body);

            body.Add(BuildSidebar());
            body.Add(BuildDetail());

            root.Add(BuildFooter());

            RefreshInstalled();
            RebuildAll();

            // 起動時に自動で通信はしない。ログイン済みなら一覧だけ取りに行く。
            if (HasToken) StartRefresh();
        }

        // ------------------------------------------------------------ 骨組み

        private VisualElement BuildHeader()
        {
            var header = Row();
            header.style.paddingLeft = 12;
            header.style.paddingRight = 12;
            header.style.paddingTop = 8;
            header.style.paddingBottom = 8;
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = LineColor;
            header.style.alignItems = Align.Center;

            var title = new Label("USLOG Package Manager");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 13;
            header.Add(title);

            header.Add(Spacer());

            _accountLabel = new Label(string.Empty);
            _accountLabel.style.marginRight = 8;
            _accountLabel.style.color = MutedColor;
            header.Add(_accountLabel);

            _loginButton = new Button(StartLogin) { text = "ブラウザでログイン" };
            header.Add(_loginButton);

            _refreshButton = new Button(StartRefresh) { text = "再読み込み" };
            header.Add(_refreshButton);

            _logoutButton = new Button(Logout) { text = "ログアウト" };
            header.Add(_logoutButton);

            return header;
        }

        private VisualElement BuildSidebar()
        {
            var sidebar = new VisualElement();
            sidebar.style.width = 300;
            sidebar.style.minWidth = 240;
            sidebar.style.borderRightWidth = 1;
            sidebar.style.borderRightColor = LineColor;
            sidebar.style.flexDirection = FlexDirection.Column;

            var search = new TextField { value = string.Empty };
            search.style.marginTop = 6;
            search.style.marginLeft = 6;
            search.style.marginRight = 6;
            search.RegisterValueChangedCallback(evt =>
            {
                _search = evt.newValue ?? string.Empty;
                RebuildList();
            });
            sidebar.Add(search);

            _filterRow = Row();
            _filterRow.style.flexWrap = Wrap.Wrap;
            _filterRow.style.marginLeft = 4;
            _filterRow.style.marginRight = 4;
            _filterRow.style.marginTop = 4;
            _filterRow.style.marginBottom = 4;
            sidebar.Add(_filterRow);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            sidebar.Add(scroll);

            _listContainer = scroll.contentContainer;

            return sidebar;
        }

        private VisualElement BuildDetail()
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.contentContainer.style.paddingLeft = 16;
            scroll.contentContainer.style.paddingRight = 16;
            scroll.contentContainer.style.paddingTop = 12;
            scroll.contentContainer.style.paddingBottom = 12;

            _detailContainer = scroll.contentContainer;
            return scroll;
        }

        private VisualElement BuildFooter()
        {
            var footer = Row();
            footer.style.paddingLeft = 12;
            footer.style.paddingRight = 12;
            footer.style.paddingTop = 6;
            footer.style.paddingBottom = 6;
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = LineColor;
            footer.style.alignItems = Align.Center;

            footer.Add(new Button(ResolveMissing) { text = "不足を復元" });
            footer.Add(new Button(EnableUpmInterop) { text = "標準 Package Manager でも使う" });

            footer.Add(Spacer());

            var cache = new Button(OpenGlobalCache) { text = "キャッシュの場所を開く" };
            footer.Add(cache);

            var settings = new Button(() => SettingsService.OpenUserPreferences("Preferences/USLOG Package Manager"))
            {
                text = "設定",
            };
            footer.Add(settings);

            return footer;
        }

        // ------------------------------------------------------------ 描画

        private bool HasToken => _credential != null && !string.IsNullOrEmpty(_credential.Token);

        private void RebuildAll()
        {
            RebuildHeader();
            RebuildBanner();
            RebuildFilters();
            RebuildList();
            RebuildDetail();
        }

        private void RebuildHeader()
        {
            if (_accountLabel == null) return;

            var email = _account?.User?.Label ?? _credential?.Email;
            _accountLabel.text = HasToken && !string.IsNullOrEmpty(email) ? email : "未ログイン";

            _loginButton.style.display = HasToken ? DisplayStyle.None : DisplayStyle.Flex;
            _logoutButton.style.display = HasToken ? DisplayStyle.Flex : DisplayStyle.None;
            _refreshButton.SetEnabled(HasToken && !_busy);
            _loginButton.SetEnabled(!_busy);
            _logoutButton.SetEnabled(!_busy);
        }

        private void RebuildBanner()
        {
            if (_banner == null) return;

            _banner.Clear();

            var message = _error ?? _status;
            if (string.IsNullOrEmpty(message))
            {
                _banner.style.display = DisplayStyle.None;
                return;
            }

            _banner.style.display = DisplayStyle.Flex;
            _banner.style.paddingLeft = 12;
            _banner.style.paddingRight = 12;
            _banner.style.paddingTop = 8;
            _banner.style.paddingBottom = 8;
            _banner.style.backgroundColor = _error != null ? ErrorBackground : InfoBackground;

            var label = new Label(message);
            label.style.whiteSpace = WhiteSpace.Normal;
            _banner.Add(label);
        }

        private void RebuildFilters()
        {
            if (_filterRow == null) return;

            _filterRow.Clear();

            AddFilterButton("すべて", Filter.All);
            AddFilterButton("利用できる", Filter.Available);
            AddFilterButton("インストール済み", Filter.Installed);

            var updates = CountUpdates();
            AddFilterButton(updates > 0 ? $"更新あり ({updates})" : "更新あり", Filter.Updates);
        }

        private void AddFilterButton(string text, Filter filter)
        {
            var button = new Button(() =>
            {
                _filter = filter;
                RebuildFilters();
                RebuildList();
            })
            {
                text = text,
            };

            if (_filter == filter) button.style.unityFontStyleAndWeight = FontStyle.Bold;
            _filterRow.Add(button);
        }

        private void RebuildList()
        {
            if (_listContainer == null) return;

            _listContainer.Clear();

            if (!HasToken)
            {
                _listContainer.Add(Hint("ログインすると、契約しているパッケージが表示されます。"));
                return;
            }

            var rows = FilteredPackages().ToList();

            if (rows.Count == 0)
            {
                _listContainer.Add(Hint(
                    _listing.Packages.Count == 0
                        ? "利用できるパッケージがありません。購入直後の場合は 1 分ほど待ってから再読み込みしてください。"
                        : "条件に合うパッケージがありません。"));
                return;
            }

            foreach (var package in rows) _listContainer.Add(BuildRow(package));
        }

        private VisualElement BuildRow(UslogPackage package)
        {
            var row = new VisualElement();
            row.style.paddingLeft = 10;
            row.style.paddingRight = 10;
            row.style.paddingTop = 6;
            row.style.paddingBottom = 6;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = LineColor;

            if (package.Name == _selectedName) row.style.backgroundColor = SelectionColor;

            var title = new Label(package.Title);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(title);

            var installed = FindInstalled(package.Name);
            var latest = package.Latest;

            var state = installed == null
                ? $"未インストール · 最新 {latest?.VersionText}"
                : HasUpdate(package)
                    ? $"{installed.Version} → {latest?.VersionText} に更新できます"
                    : $"インストール済み {installed.Version}";

            var subtitle = new Label(state);
            subtitle.style.fontSize = 11;
            subtitle.style.color = HasUpdate(package) ? AccentColor : MutedColor;
            row.Add(subtitle);

            row.RegisterCallback<MouseDownEvent>(_ =>
            {
                _selectedName = package.Name;
                RebuildList();
                RebuildDetail();
            });

            return row;
        }

        private void RebuildDetail()
        {
            if (_detailContainer == null) return;

            _detailContainer.Clear();

            if (!HasToken)
            {
                _detailContainer.Add(Heading("ようこそ"));
                _detailContainer.Add(Paragraph(
                    "「ブラウザでログイン」を押すと、既定のブラウザが開いて USLOG のログイン画面に移ります。" +
                    "同意すると Unity に戻り、契約しているパッケージが一覧に出ます。"));
                _detailContainer.Add(Paragraph($"レジストリ: {_client.RegistryUrl}"));
                return;
            }

            var package = _listing.Find(_selectedName);
            if (package == null)
            {
                _detailContainer.Add(Heading("パッケージを選んでください"));
                _detailContainer.Add(Paragraph("左の一覧から選ぶと、内容と操作がここに出ます。"));
                return;
            }

            var latest = package.Latest;
            var installed = FindInstalled(package.Name);

            _detailContainer.Add(Heading(package.Title));

            var name = new Label(package.Name);
            name.style.color = MutedColor;
            name.style.marginBottom = 8;
            _detailContainer.Add(name);

            if (!string.IsNullOrEmpty(latest?.Description))
            {
                _detailContainer.Add(Paragraph(latest.Description));
            }

            _detailContainer.Add(KeyValue("最新バージョン", latest?.VersionText ?? "-"));
            _detailContainer.Add(KeyValue("インストール済み", installed?.Version ?? "なし"));
            if (!string.IsNullOrEmpty(latest?.UnityVersion)) _detailContainer.Add(KeyValue("対応 Unity", latest.UnityVersion));
            if (!string.IsNullOrEmpty(latest?.AuthorName)) _detailContainer.Add(KeyValue("作者", latest.AuthorName));

            // 操作
            var actions = Row();
            actions.style.marginTop = 12;
            actions.style.marginBottom = 12;

            if (installed == null)
            {
                actions.Add(new Button(() => StartInstall(latest)) { text = $"インストール ({latest?.VersionText})" });
            }
            else
            {
                if (HasUpdate(package))
                {
                    actions.Add(new Button(() => StartInstall(latest)) { text = $"{latest?.VersionText} に更新" });
                }
                actions.Add(new Button(() => Uninstall(package.Name)) { text = "削除" });
            }

            actions.SetEnabled(!_busy);
            _detailContainer.Add(actions);

            // 版の選択
            if (package.Versions.Count > 1)
            {
                _detailContainer.Add(SubHeading("ほかのバージョン"));

                var versions = Row();
                versions.style.flexWrap = Wrap.Wrap;

                foreach (var version in package.Versions.Take(12))
                {
                    if (version == latest) continue;

                    var button = new Button(() => StartInstall(version)) { text = version.VersionText };
                    button.SetEnabled(!_busy);
                    versions.Add(button);
                }

                _detailContainer.Add(versions);
            }

            // 許諾区分
            if (latest?.License != null)
            {
                _detailContainer.Add(SubHeading("許諾区分"));
                _detailContainer.Add(Paragraph(
                    "取得できるかどうかは契約の有無だけで決まります。ここは「取得したものをどう使ってよいか」です。"));

                foreach (var row in latest.License.Rows())
                {
                    _detailContainer.Add(KeyValue(row.Key, row.Value ? "可" : "不可"));
                }
            }

            if (latest != null && latest.VpmDependencies.Count > 0)
            {
                _detailContainer.Add(SubHeading("VPM の依存"));
                foreach (var dependency in latest.VpmDependencies)
                {
                    _detailContainer.Add(KeyValue(dependency.Key, dependency.Value));
                }
                _detailContainer.Add(Paragraph(
                    "依存の解決は行いません。VCC / ALCOM で先に入れておいてください。"));
            }

            if (!string.IsNullOrEmpty(latest?.DocumentationUrl))
            {
                var open = new Button(() => Application.OpenURL(latest.DocumentationUrl)) { text = "ドキュメントを開く" };
                open.style.marginTop = 8;
                _detailContainer.Add(open);
            }
        }

        // ------------------------------------------------------------ 動作

        private void StartLogin()
        {
            if (_busy) return;

            var label = $"Unity {Application.unityVersion} / {Path.GetFileName(ProjectRoot)}";
            if (label.Length > 60) label = label.Substring(0, 60);

            // Application.OpenURL はメインスレッド専用。await の続きは
            // どのスレッドで動くか分からないので、必ず戻してから呼ぶ。
            Action<string> openBrowser = url => EditorDispatcher.Run(() => Application.OpenURL(url));

            RunAsync("ブラウザでの同意を待っています…", async cancel =>
            {
                var token = await UslogLoginFlow
                    .LoginAsync(_client.RegistryUrl, label, openBrowser, null, cancel)
                    .ConfigureAwait(false);

                var account = await _client.GetAccountAsync(token, cancel).ConfigureAwait(false);
                var listing = await _client.GetListingAsync(token, cancel).ConfigureAwait(false);

                EditorDispatcher.Run(() =>
                {
                    _credentials.Save(_client.RegistryUrl, token, account?.User?.Email);
                    _credential = _credentials.Load(_client.RegistryUrl);
                    _account = account;
                    _listing = listing;
                    _status = "ログインしました。";
                });
            });
        }

        private void Logout()
        {
            if (_busy) return;

            var ok = EditorUtility.DisplayDialog(
                "ログアウト",
                "この PC からトークンを消します。レジストリ側のトークンは失効しません" +
                "（失効させたいときはマイページから行ってください）。",
                "ログアウト",
                "やめる");

            if (!ok) return;

            _credentials.Clear(_client.RegistryUrl);
            _credential = null;
            _account = null;
            _listing = UslogListing.Empty;
            _selectedName = null;
            _status = "ログアウトしました。";
            _error = null;
            RebuildAll();
        }

        private void StartRefresh()
        {
            if (_busy) return;

            SyncRegistry();
            if (!HasToken)
            {
                RebuildAll();
                return;
            }

            RunAsync("一覧を取得しています…", async cancel =>
            {
                var account = await _client.GetAccountAsync(_credential.Token, cancel).ConfigureAwait(false);
                var listing = await _client.GetListingAsync(_credential.Token, cancel).ConfigureAwait(false);

                EditorDispatcher.Run(() =>
                {
                    _account = account;
                    _listing = listing;
                    RefreshInstalled();
                });
            });
        }

        private void StartInstall(UslogPackageVersion version)
        {
            if (_busy || version == null || !HasToken) return;

            // Application.dataPath はメインスレッドからしか読めない。
            // バックグラウンドに入る前に確定させておく。
            var installer = new PackageInstaller(ProjectRoot, _client);

            RunAsync($"{version.Name} {version.VersionText} を取得しています…", async cancel =>
            {
                await installer.InstallAsync(version, _credential.Token, null, cancel).ConfigureAwait(false);

                EditorDispatcher.Run(() =>
                {
                    _status = $"{version.Title} {version.VersionText} を入れました。";
                    RefreshInstalled();
                    AssetDatabase.Refresh();
                });
            });
        }

        private void Uninstall(string packageName)
        {
            if (_busy) return;

            var ok = EditorUtility.DisplayDialog(
                "削除",
                $"{packageName} を Packages/ から削除します。よろしいですか？",
                "削除する",
                "やめる");

            if (!ok) return;

            try
            {
                new PackageInstaller(ProjectRoot, _client).Uninstall(packageName);
                _status = $"{packageName} を削除しました。";
                _error = null;
                RefreshInstalled();
                AssetDatabase.Refresh();
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                Debug.LogException(exception);
            }

            RebuildAll();
        }

        private void ResolveMissing()
        {
            if (_busy) return;

            if (!HasToken)
            {
                _error = "先にログインしてください。";
                RebuildAll();
                return;
            }

            var installer = new PackageInstaller(ProjectRoot, _client);
            var missing = installer.MissingFromDisk();

            if (missing.Count == 0)
            {
                _status = "不足しているパッケージはありません。";
                _error = null;
                RebuildAll();
                return;
            }

            var manifest = VpmManifest.Load(ProjectRoot);

            RunAsync($"{missing.Count} 件を復元しています…", async cancel =>
            {
                var restored = 0;
                var skipped = new List<string>();

                foreach (var name in missing)
                {
                    var wanted = manifest.LockedVersion(name);
                    var package = _listing.Find(name);
                    var version = package?.Find(wanted) ?? package?.Latest;

                    if (version == null)
                    {
                        // USLOG 以外のリスティングから入ったものが混ざっている。
                        // 触らずに置いておく（VCC / ALCOM の担当）。
                        skipped.Add(name);
                        continue;
                    }

                    await installer.InstallAsync(version, _credential.Token, null, cancel).ConfigureAwait(false);
                    restored++;
                }

                EditorDispatcher.Run(() =>
                {
                    _status = skipped.Count == 0
                        ? $"{restored} 件を復元しました。"
                        : $"{restored} 件を復元しました。USLOG の一覧に無いものは触っていません: {string.Join(", ", skipped)}";
                    RefreshInstalled();
                    AssetDatabase.Refresh();
                });
            });
        }

        private void EnableUpmInterop()
        {
            if (!HasToken)
            {
                _error = "先にログインしてください。トークンが無いと .upmconfig.toml を書けません。";
                RebuildAll();
                return;
            }

            var upmConfig = UpmConfigWriter.DefaultUpmConfigPath();

            var ok = EditorUtility.DisplayDialog(
                "標準 Package Manager でも使う",
                $"次の 2 つを書き換えます。\n\n" +
                $"・{upmConfig}\n" +
                $"・{UpmConfigWriter.ManifestPath(ProjectRoot)}\n\n" +
                "既存のファイルは .bak として控えを残します。",
                "書き込む",
                "やめる");

            if (!ok) return;

            try
            {
                UpmConfigWriter.WriteUpmConfig(upmConfig, _client.RegistryUrl, _credential.Token, _credential.Email);
                UpmConfigWriter.WriteScopedRegistry(
                    UpmConfigWriter.ManifestPath(ProjectRoot),
                    _client.RegistryUrl,
                    "USLOG",
                    UslogSettings.Scopes);

                _error = null;
                _status = "書き込みました。Unity を再起動してください。";

                // ここは省略できない。.upmconfig.toml は Unity の起動時にしか
                // 読まれないので、書いただけでは反映されない。
                EditorUtility.DisplayDialog(
                    "Unity の再起動が必要です",
                    ".upmconfig.toml は Unity の起動時にしか読まれません。\n" +
                    "いま書き込んだ内容を標準の Package Manager に反映するには、Unity を再起動してください。",
                    "わかりました");
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                Debug.LogException(exception);
            }

            RebuildAll();
        }

        private void OpenGlobalCache()
        {
            var path = UpmConfigWriter.GlobalNpmCachePath(_client.RegistryUrl);

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                _error = path == null
                    ? "キャッシュの場所を組み立てられませんでした。"
                    : $"まだキャッシュがありません: {path}";
                RebuildAll();
                return;
            }

            EditorUtility.RevealInFinder(path);
        }

        // ------------------------------------------------------------ 補助

        private void RefreshInstalled()
        {
            try
            {
                _installed = UslogProject.ScanInstalled(ProjectRoot);
            }
            catch (Exception exception)
            {
                _installed = System.Array.Empty<InstalledPackage>();
                Debug.LogException(exception);
            }
        }

        private InstalledPackage FindInstalled(string name)
        {
            return _installed.FirstOrDefault(p => p.Name == name);
        }

        private bool HasUpdate(UslogPackage package)
        {
            var installed = FindInstalled(package.Name);
            if (installed == null) return false;

            var latest = package.Latest;
            if (latest == null) return false;

            return latest.Version > SemVer.Parse(installed.Version);
        }

        private int CountUpdates()
        {
            return _listing.Packages.Count(HasUpdate);
        }

        private IEnumerable<UslogPackage> FilteredPackages()
        {
            var packages = _listing.Packages.AsEnumerable();

            switch (_filter)
            {
                case Filter.Available:
                    packages = packages.Where(p => FindInstalled(p.Name) == null);
                    break;
                case Filter.Installed:
                    packages = packages.Where(p => FindInstalled(p.Name) != null);
                    break;
                case Filter.Updates:
                    packages = packages.Where(HasUpdate);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(_search))
            {
                var needle = _search.Trim();
                packages = packages.Where(p =>
                    p.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return packages;
        }

        /// <summary>
        /// 通信を伴う操作の共通の型。失敗はすべてここで文言に変えて表示する。
        /// 例外を握り潰すと「押しても何も起きない」になるので、ログにも必ず出す。
        /// </summary>
        private void RunAsync(string status, Func<CancellationToken, Task> work)
        {
            _busy = true;
            _status = status;
            _error = null;
            RebuildAll();

            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;

            Task.Run(async () =>
            {
                try
                {
                    await work(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    EditorDispatcher.Run(() => _status = "中断しました。");
                }
                catch (UslogLoginFlow.LoginCancelledException exception)
                {
                    EditorDispatcher.Run(() => _error = exception.Message);
                }
                catch (UslogApiException exception)
                {
                    EditorDispatcher.Run(() =>
                    {
                        _error = exception.Message;

                        // トークンが死んでいるなら、押し直せる形にしておく。
                        if (exception.NeedsLogin)
                        {
                            _credentials.Clear(_client.RegistryUrl);
                            _credential = null;
                            _account = null;
                            _listing = UslogListing.Empty;
                        }
                    });
                }
                catch (Exception exception)
                {
                    EditorDispatcher.Run(() =>
                    {
                        _error = exception.Message;
                        Debug.LogException(exception);
                    });
                }
                finally
                {
                    EditorDispatcher.Run(() =>
                    {
                        _busy = false;
                        if (_error != null) _status = null;
                        RebuildAll();
                    });
                }
            });
        }

        // ------------------------------------------------------------ 見た目

        private static Color LineColor => EditorGUIUtility.isProSkin
            ? new Color(1f, 1f, 1f, 0.08f)
            : new Color(0f, 0f, 0f, 0.12f);

        private static Color MutedColor => EditorGUIUtility.isProSkin
            ? new Color(1f, 1f, 1f, 0.55f)
            : new Color(0f, 0f, 0f, 0.55f);

        private static Color AccentColor => EditorGUIUtility.isProSkin
            ? new Color(0.45f, 0.68f, 1f)
            : new Color(0.11f, 0.35f, 0.75f);

        private static Color SelectionColor => EditorGUIUtility.isProSkin
            ? new Color(1f, 1f, 1f, 0.08f)
            : new Color(0f, 0f, 0f, 0.06f);

        private static Color InfoBackground => EditorGUIUtility.isProSkin
            ? new Color(0.20f, 0.28f, 0.38f)
            : new Color(0.86f, 0.91f, 0.97f);

        private static Color ErrorBackground => EditorGUIUtility.isProSkin
            ? new Color(0.38f, 0.20f, 0.20f)
            : new Color(0.98f, 0.87f, 0.87f);

        private static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            return row;
        }

        private static VisualElement Spacer()
        {
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            return spacer;
        }

        private static Label Heading(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.fontSize = 15;
            label.style.marginBottom = 2;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private static Label SubHeading(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 12;
            label.style.marginBottom = 4;
            return label;
        }

        private static Label Paragraph(string text)
        {
            var label = new Label(text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginBottom = 6;
            return label;
        }

        private static Label Hint(string text)
        {
            var label = Paragraph(text);
            label.style.color = MutedColor;
            label.style.marginTop = 12;
            label.style.marginLeft = 10;
            label.style.marginRight = 10;
            return label;
        }

        private static VisualElement KeyValue(string key, string value)
        {
            var row = Row();
            row.style.marginBottom = 2;

            var name = new Label(key);
            name.style.width = 140;
            name.style.color = MutedColor;
            row.Add(name);

            var content = new Label(value);
            content.style.whiteSpace = WhiteSpace.Normal;
            content.style.flexGrow = 1;
            row.Add(content);

            return row;
        }
    }
}
