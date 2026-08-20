#!/usr/bin/env node
// パッケージとして配れる形かどうかを見る。
//
// Unity を起動できない CI でも確かめられることだけを扱う。
// C# のコンパイルはここでは見ていない（Unity のライセンスが要るため）。

import { readFile, readdir, stat } from 'node:fs/promises';
import { join, extname, basename } from 'node:path';

const problems = [];
const notes = [];

function fail(message) {
  problems.push(message);
}

// --- package.json ------------------------------------------------------

const manifest = JSON.parse(await readFile('package.json', 'utf8'));

for (const key of ['name', 'displayName', 'version', 'unity', 'description', 'author']) {
  if (!manifest[key]) fail(`package.json に ${key} がありません`);
}

if (!/^[a-z0-9]+(\.[a-z0-9-]+)+$/.test(manifest.name || '')) {
  fail(`package.json の name が逆ドメイン形式ではありません: ${manifest.name}`);
}

if (!/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$/.test(manifest.version || '')) {
  fail(`package.json の version が semver ではありません: ${manifest.version}`);
}

// --- CHANGELOG ---------------------------------------------------------

const changelog = await readFile('CHANGELOG.md', 'utf8');
if (!changelog.includes(`[${manifest.version}]`)) {
  // 版だけ上げて履歴を書き忘れると、利用者は何が変わったのか分からない。
  fail(`CHANGELOG.md に [${manifest.version}] の項目がありません`);
}

// --- .meta の有無 ------------------------------------------------------
//
// Unity は .meta が無いファイルに毎回新しい GUID を振る。zip で配ると
// 利用者ごとに GUID が変わり、asmdef やアセットの参照が切れる。

const IGNORED_DIRECTORIES = new Set(['.git', '.github', 'build', 'Library', 'Temp', 'out']);

async function walk(directory, files = []) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.name.startsWith('.') && directory === '.') continue;
    if (IGNORED_DIRECTORIES.has(entry.name)) continue;
    if (entry.name.endsWith('~')) continue;

    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push({ path, directory: true });
      await walk(path, files);
    } else {
      files.push({ path, directory: false });
    }
  }
  return files;
}

const needsMeta = ['.cs', '.asmdef', '.json', '.md'];
const entries = await walk('.');
const known = new Set(entries.map((e) => e.path));

for (const entry of entries) {
  if (entry.path.endsWith('.meta')) {
    const target = entry.path.slice(0, -'.meta'.length);
    if (!known.has(target)) fail(`置き去りの .meta があります: ${entry.path}`);
    continue;
  }

  const wanted = entry.directory || needsMeta.includes(extname(entry.path));
  if (!wanted) continue;

  if (!known.has(`${entry.path}.meta`)) fail(`.meta がありません: ${entry.path}`);
}

// --- .meta の GUID が重複していないか ----------------------------------

const guids = new Map();
for (const entry of entries) {
  if (!entry.path.endsWith('.meta')) continue;

  const text = await readFile(entry.path, 'utf8');
  const match = /^guid:\s*([0-9a-f]{32})\s*$/m.exec(text);

  if (!match) {
    fail(`guid を読めません: ${entry.path}`);
    continue;
  }

  if (guids.has(match[1])) {
    // 同じ GUID が 2 つあると、Unity はどちらか一方を無視する。
    fail(`guid が重複しています: ${entry.path} と ${guids.get(match[1])}`);
  }
  guids.set(match[1], entry.path);
}

notes.push(`${guids.size} 件の .meta を確認しました`);

// --- listing の設定 ----------------------------------------------------

const listing = JSON.parse(await readFile('.github/listing.json', 'utf8'));
for (const key of ['name', 'id', 'author']) {
  if (!listing[key]) fail(`.github/listing.json に ${key} がありません`);
}

// --- 結果 --------------------------------------------------------------

for (const note of notes) console.log(`validate: ${note}`);

if (problems.length === 0) {
  console.log(`validate: ${manifest.name} ${manifest.version} は問題ありません`);
  process.exit(0);
}

for (const problem of problems) console.error(`validate: ${problem}`);
process.exit(1);
