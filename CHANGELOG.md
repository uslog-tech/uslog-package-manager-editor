# 変更履歴

このファイルの書式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に、
バージョン番号は [Semantic Versioning](https://semver.org/lang/ja/) に従います。

## [0.1.0] - 2026-08-20

最初のリリース。

### 追加

- `Window > USLOG Package Manager` ウィンドウ
- ブラウザ連携ログイン（loopback + 1 回限りの引き換えコード。平文トークンを URL に載せない）
- 契約しているパッケージの一覧、インストール / 更新 / 削除
- `Packages/vpm-manifest.json` の `dependencies` と `locked` の更新（VCC / ALCOM と同じ持ちかた）
- 「不足を復元」— `vpm-manifest.json` にあるのに `Packages/` に無いものを入れ直す
- 許諾区分（非商用 / 商用 / 個人 / 法人 / 組織内共有）の表示
- 併用モード — `.upmconfig.toml` と `manifest.json` の `scopedRegistries` を書き、再起動を促す
- Unity のグローバル npm キャッシュを開くボタン
- `Preferences > USLOG Package Manager` でレジストリ URL と scopes を設定
