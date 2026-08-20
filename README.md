# USLOG Package Manager

USLOG のプライベートレジストリ（`https://private-upm.uslog.tech`）用の Unity Editor 拡張です。
**ログインから、契約しているパッケージのインストール・更新・削除までを Unity の中で完結させます。**

`.upmconfig.toml` の手書きが要りません。ここが従来いちばん失敗しやすい箇所でした。

| | |
|---|---|
| 対応 Unity | 2022.3 以降 |
| VRChat SDK | **不要**（入っていても入っていなくても動きます） |
| 配布 | VPM リスティング（VCC / ALCOM から追加） |
| ライセンス | MIT |

---

## 導入

VCC / ALCOM の「リポジトリを追加」に、次の URL を入れてください。

```
https://uslog-tech.github.io/uslog-package-manager-editor/index.json
```

追加すると `USLOG Package Manager` がパッケージ一覧に出るので、プロジェクトに入れます。

<details>
<summary>VCC / ALCOM を使わずに入れる</summary>

[Releases](https://github.com/uslog-tech/uslog-package-manager-editor/releases) から zip を取り、
プロジェクトの `Packages/tech.uslog.package-manager/` に展開してください。

</details>

---

## 使いかた

### 1. ウィンドウを開く

`Window > USLOG Package Manager`

### 2. ログインする

「ブラウザでログイン」を押すと既定のブラウザが開きます。GitHub か Discord でログインし、
表示される確認画面で「連携する」を押してください。Unity に戻ると一覧が出ています。

- トークンは自動で発行され、`~/.uslog/upm-credentials.json` に保存されます
- マイページのトークン一覧に `Unity 2022.3 / プロジェクト名` として並ぶので、不要になったらそこから失効させてください
- **このトークンに publish 権限は付きません**

### 3. 入れる・更新する・消す

左の一覧から選び、右の「インストール」を押します。

- 展開先は `Packages/<パッケージ名>/`
- `Packages/vpm-manifest.json` の `dependencies` と `locked` を更新します（VCC / ALCOM と同じ持ちかた）
- 「不足を復元」は、`vpm-manifest.json` に載っているのに `Packages/` に無いものを入れ直します。プロジェクトを clone した直後に使ってください

### 4. 標準の Package Manager でも使いたいとき

フッターの「標準 Package Manager でも使う」を押すと、次の 2 つを書きます。

- `~/.upmconfig.toml`（`%USERPROFILE%\.upmconfig.toml`）
- `Packages/manifest.json` の `scopedRegistries`

既存のファイルは `.bak` として控えを残します。

> **書いたあと Unity の再起動が必要です。** `.upmconfig.toml` は Unity の起動時にしか読まれません。

---

## 困ったとき

| 症状 | 見るところ |
|---|---|
| 「トークンが失効しています」 | もう一度ログインしてください。マイページで失効させた場合もこうなります |
| 「このパッケージの契約がありません」 | 購入直後なら 1 分ほど待って再読み込みしてください（サーバー側で最大 45 秒キャッシュします）。**パッケージが存在しない、という意味ではありません** |
| 一覧が空 | 契約がまだ反映されていないか、そのアカウントに契約がありません。GitHub と Discord で別アカウントになっていないかもマイページで確認してください |
| ブラウザが開かない | ログを見て URL を手でコピーしてください。同意画面から先は同じです |
| Unity 再起動後も標準 PM に出ない | `.upmconfig.toml` と `manifest.json` の URL が一字一句同じか確認してください。末尾スラッシュの有無まで一致が必要です |
| 古いバージョンが取得される | フッターの「キャッシュの場所を開く」から、Unity のグローバル npm キャッシュを消してください |

ログは Unity のコンソールに出ます。通信の失敗はすべて例外として記録しています。

---

## 仕組み

```
Unity Editor
  └ USLOG Package Manager
       │ ① ブラウザ連携ログイン（loopback + 引き換えコード）
       │ ② GET /-/uslog/api/v1/vpm/index.json   (Bearer)
       │ ③ GET /<pkg>/-/<pkg>-<ver>.tgz         (Bearer)
       ↓
     Packages/<pkg>/ に展開 + vpm-manifest.json を更新
```

### なぜ VCC / ALCOM から私有パッケージを直接入れられないのか

VPM のリスティングは **匿名で取得できること**が前提で、配布物は **zip** です。
一方 USLOG のレジストリは、契約を毎リクエスト照合するために Bearer を要求し、
配布物は npm の **tgz** です。この 2 点が噛み合いません。

そこで、

- **私有パッケージ** … この拡張が VPM 形状の JSON を Bearer 付きで読み、tgz を自前で展開する
- **この拡張自体**（無料・公開） … 本物の VPM パッケージ（zip）として GitHub で配る

という二層にしています。リスティング側にも `uslogDistType: "npm-tgz"` と `uslogAuth: "bearer"` を
明記していて、zip でないものに `zipSHA256` は付けていません。

### ログインの往復

平文トークンをリダイレクト URL に載せません（ブラウザ履歴に残るため）。

1. 拡張が `verifier` を作り、その sha256（`challenge`）をブラウザ経由でレジストリに渡す
2. 同意するとレジストリが 1 回限りの**引き換えコード**を `127.0.0.1:<空きポート>` に返す
3. 拡張が `verifier` を添えて交換し、トークン本体を受け取る

戻り先は `127.0.0.1` 固定です。ポート番号だけが外から渡り、サーバー側で `1024-65535` に限っています。

---

## 開発

パッケージのルートがリポジトリのルートです。

```
package.json
Editor/
  Core/     … Unity に依存しない層（JSON, semver, tar.gz, HTTP, ファイル操作）
  UI/       … Unity 依存の層（ウィンドウ、設定、メインスレッドへの受け渡し）
Editor.Tests/
```

**ロジックは `Editor/Core/` に置いてあり、Unity を起動しなくても検証できます。**
`Editor/UI/` は状態を持たない薄い層に留めています。

### テスト

Unity の Test Runner（Edit Mode）で実行します。パッケージ内のテストを走らせるには、
検証用プロジェクトの `Packages/manifest.json` に次を足してください。

```json
{ "testables": ["tech.uslog.package-manager"] }
```

### リリース

`package.json` の `version` を上げて `main` に push すると、GitHub Actions が

1. `v<version>` タグが無ければ zip を作って Release を作成
2. 全リリースから `index.json` を作り直して GitHub Pages に公開

まで行います。タグが既にあれば何もしません（同じ内容を push し直しても壊れません）。

### 外部依存

**ありません。** JSON も semver も tar.gz も自前で読んでいます。
Newtonsoft を足すと VRChat SDK が固定しているバージョンとぶつかることがあり、
「入らないプロジェクト」を作りたくないためです。

---

## 関連

- レジストリ本体: `uslog-tech/uslog-package-manager`（private）
- マイページ: https://private-upm.uslog.tech/-/uslog
