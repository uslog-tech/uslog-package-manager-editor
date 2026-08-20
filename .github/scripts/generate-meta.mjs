#!/usr/bin/env node
// 足りない .meta を作る。
//
// Unity に任せると、開いた人ごとに新しい GUID が振られる。zip で配ると
// 利用者ごとに GUID が変わり、アセットの参照が切れる。だからリポジトリに
// .meta を持つ。既にあるものは触らない（GUID を変えないため）。
//
//   node .github/scripts/generate-meta.mjs

import { readdir, writeFile, access } from 'node:fs/promises';
import { join, extname } from 'node:path';
import { randomBytes } from 'node:crypto';

const IGNORED = new Set(['.git', '.github', 'build', 'Library', 'Temp', 'out', 'node_modules']);
const NEEDS_META = ['.cs', '.asmdef', '.json', '.md'];

async function exists(path) {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

function guid() {
  return randomBytes(16).toString('hex');
}

function folderMeta() {
  return `fileFormatVersion: 2
guid: ${guid()}
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
`;
}

function scriptMeta() {
  return `fileFormatVersion: 2
guid: ${guid()}
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData:
  assetBundleName:
  assetBundleVariant:
`;
}

function asmdefMeta() {
  return `fileFormatVersion: 2
guid: ${guid()}
AssemblyDefinitionImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
`;
}

function textMeta() {
  return `fileFormatVersion: 2
guid: ${guid()}
TextScriptImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
`;
}

function metaFor(path, isDirectory) {
  if (isDirectory) return folderMeta();

  switch (extname(path)) {
    case '.cs':
      return scriptMeta();
    case '.asmdef':
      return asmdefMeta();
    default:
      return textMeta();
  }
}

let created = 0;

async function walk(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (IGNORED.has(entry.name)) continue;
    if (entry.name.startsWith('.')) continue;
    if (entry.name.endsWith('~')) continue;
    if (entry.name.endsWith('.meta')) continue;

    const path = join(directory, entry.name);
    const wanted = entry.isDirectory() || NEEDS_META.includes(extname(entry.name));

    if (wanted && !(await exists(`${path}.meta`))) {
      await writeFile(`${path}.meta`, metaFor(path, entry.isDirectory()));
      created++;
      console.log(`generate-meta: ${path}.meta`);
    }

    if (entry.isDirectory()) await walk(path);
  }
}

await walk('.');
console.log(created === 0 ? 'generate-meta: 足りない .meta はありません' : `generate-meta: ${created} 件作りました`);
