#!/usr/bin/env node
// 過去の Release を全部見て、VPM のリスティング（index.json）を組み立てる。
//
// 既存の action に任せず自前で書いているのは、リスティングの中身を
// 完全に説明できる状態にしておきたいから。ここが壊れると VCC / ALCOM から
// 何も入らなくなるので、失敗の理由が読めることを優先する。
//
// 使い方:
//   GITHUB_TOKEN=... GITHUB_REPOSITORY=owner/repo PAGES_URL=https://... \
//     node .github/scripts/build-listing.mjs out/
//
// zipSHA256 は release ジョブが package.json アセットに書き込んでいる。
// ここで zip を落とし直して計算しない（リリース数だけ帯域を使うため）。

import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const repository = process.env.GITHUB_REPOSITORY;
const token = process.env.GITHUB_TOKEN;
const pagesUrl = (process.env.PAGES_URL || '').replace(/\/+$/, '');
const outDir = process.argv[2] || 'out';

if (!repository) fatal('GITHUB_REPOSITORY が設定されていません');
if (!pagesUrl) fatal('PAGES_URL が設定されていません');

const here = dirname(fileURLToPath(import.meta.url));
const listingMeta = JSON.parse(await readFile(join(here, '..', 'listing.json'), 'utf8'));

function fatal(message) {
  console.error(`build-listing: ${message}`);
  process.exit(1);
}

async function api(path, accept = 'application/vnd.github+json') {
  const response = await fetch(`https://api.github.com${path}`, {
    headers: {
      accept,
      'user-agent': 'uslog-build-listing',
      ...(token ? { authorization: `Bearer ${token}` } : {}),
      'x-github-api-version': '2022-11-28',
    },
  });

  if (!response.ok) {
    fatal(`GitHub API が ${response.status} を返しました: ${path}\n${await response.text()}`);
  }

  return response;
}

// --- リリースを集める -------------------------------------------------

const releases = [];
for (let page = 1; page <= 10; page++) {
  const batch = await (await api(`/repos/${repository}/releases?per_page=100&page=${page}`)).json();
  releases.push(...batch);
  if (batch.length < 100) break;
}

const packages = {};
let counted = 0;

for (const release of releases) {
  if (release.draft) continue;

  const manifestAsset = release.assets.find((a) => a.name === 'package.json');
  const zipAsset = release.assets.find((a) => a.name.endsWith('.zip'));

  if (!manifestAsset || !zipAsset) {
    // タグだけ手で作ったリリースなど。黙って飛ばすと理由が分からないので出す。
    console.warn(`build-listing: ${release.tag_name} に package.json か zip が無いので飛ばします`);
    continue;
  }

  const manifest = await (
    await api(`/repos/${repository}/releases/assets/${manifestAsset.id}`, 'application/octet-stream')
  ).json();

  if (!manifest.name || !manifest.version) {
    console.warn(`build-listing: ${release.tag_name} の package.json に name / version がありません`);
    continue;
  }

  // url は必ずこの場で付け直す。package.json に書かれたままの値を信じると、
  // タグを打ち直したときに古いリリースの zip を指し続ける。
  manifest.url = zipAsset.browser_download_url;

  packages[manifest.name] ??= { versions: {} };

  if (packages[manifest.name].versions[manifest.version]) {
    console.warn(`build-listing: ${manifest.name} ${manifest.version} が重複しています。先勝ちにします`);
    continue;
  }

  packages[manifest.name].versions[manifest.version] = manifest;
  counted++;
}

if (counted === 0) {
  // 空のリスティングを配ると、VCC 側では「リポジトリはあるが何も無い」に見える。
  // 事故と区別が付かないので、ここで落として気づけるようにする。
  fatal('リリースが 1 つも見つかりませんでした');
}

const listing = {
  name: listingMeta.name,
  id: listingMeta.id,
  url: `${pagesUrl}/index.json`,
  author: listingMeta.author,
  packages,
};

await mkdir(outDir, { recursive: true });
await writeFile(join(outDir, 'index.json'), JSON.stringify(listing, null, 2) + '\n');
await writeFile(join(outDir, 'index.html'), landingPage(listing, listingMeta));

console.log(`build-listing: ${Object.keys(packages).length} パッケージ / ${counted} バージョンを書きました`);

// --- 案内ページ -------------------------------------------------------

function escapeHtml(value) {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function landingPage(listing, meta) {
  const rows = Object.entries(listing.packages)
    .map(([name, entry]) => {
      // リリースは新しい順に並べて入れているので、先頭がいちばん新しい
      const versions = Object.keys(entry.versions);
      const latest = entry.versions[versions[0]];
      return `<tr><td><code>${escapeHtml(name)}</code></td><td>${escapeHtml(latest.displayName || '')}</td><td>${escapeHtml(versions.join(', '))}</td></tr>`;
    })
    .join('\n');

  return `<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escapeHtml(listing.name)} — VPM Listing</title>
<style>
:root{color-scheme:light dark}
body{font-family:system-ui,-apple-system,'Segoe UI','Hiragino Sans',sans-serif;
     max-width:44rem;margin:0 auto;padding:3rem 1.25rem;line-height:1.75}
h1{font-size:1.6rem;margin:0 0 .25rem}
p.lead{color:#6b7280;margin:0 0 2rem}
code{background:rgba(127,127,127,.15);padding:.15em .4em;border-radius:.25rem;font-size:.95em}
pre{background:rgba(127,127,127,.12);padding:1rem;border-radius:.5rem;overflow-x:auto}
table{border-collapse:collapse;width:100%;margin:1rem 0}
th,td{text-align:left;padding:.5rem .75rem;border-bottom:1px solid rgba(127,127,127,.25)}
a.btn{display:inline-block;background:#2f6df6;color:#fff;text-decoration:none;
      padding:.6rem 1.1rem;border-radius:.5rem;font-weight:600}
</style>
</head>
<body>
<h1>${escapeHtml(listing.name)}</h1>
<p class="lead">${escapeHtml(meta.description || '')}</p>

<p><a class="btn" href="vcc://vpm/addRepo?url=${encodeURIComponent(listing.url)}">VCC に追加する</a></p>

<p>ALCOM やコマンドラインからは、この URL を直接登録してください。</p>
<pre>${escapeHtml(listing.url)}</pre>

<h2>収録パッケージ</h2>
<table>
<tr><th>ID</th><th>名前</th><th>バージョン</th></tr>
${rows}
</table>

<p><a href="https://github.com/${escapeHtml(repository)}">GitHub リポジトリ</a></p>
</body>
</html>
`;
}
