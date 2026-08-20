# USLOG Package Manager

USLOG のプライベートレジストリ（`https://private-upm.uslog.tech`）用の Unity Editor 拡張です。

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
