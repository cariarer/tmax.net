// Generates THIRD-PARTY-NOTICES.md from the dependencies that actually ship with tmax.net:
//  - npm: the production dependency tree of frontend/ (bundled into wwwroot by Vite)
//  - NuGet: runtime packages of src/Tmax.App (from obj/project.assets.json; run `dotnet build` first)
// License texts come from the packages themselves; NuGet packages rarely contain one, so their
// SPDX expression and copyright line from the .nuspec are listed and the full text of each
// license type is appended once.
//
// Usage (from the repo root): node scripts/generate-third-party-notices.mjs
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

const root = join(import.meta.dirname, '..');
const frontend = join(root, 'frontend');

// ── npm ──────────────────────────────────────────────────────────────
const tree = JSON.parse(execFileSync('npm', ['ls', '--omit=dev', '--all', '--json', '--long'], {
  cwd: frontend, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, shell: process.platform === 'win32',
}));
const npm = new Map();
(function walk(deps) {
  for (const [name, d] of Object.entries(deps ?? {})) {
    if (!d.path || npm.has(`${name}@${d.version}`)) { walk(d.dependencies); continue; }
    const pkg = JSON.parse(readFileSync(join(d.path, 'package.json'), 'utf8'));
    const licenseFile = readdirSync(d.path).find((f) => /^(licen[sc]e|copying)(\.|$)/i.test(f));
    npm.set(`${name}@${d.version}`, {
      name, version: d.version,
      license: typeof pkg.license === 'string' ? pkg.license : pkg.license?.type ?? (pkg.licenses ?? []).map((l) => l.type).join(' OR ') ?? 'UNKNOWN',
      url: typeof pkg.repository === 'string' ? pkg.repository : pkg.repository?.url ?? pkg.homepage ?? '',
      copyright: typeof pkg.author === 'string' ? pkg.author.replace(/\s*<[^>]*>/, '') : pkg.author?.name ?? '',
      text: licenseFile ? readFileSync(join(d.path, licenseFile), 'utf8').trim() : null,
    });
    walk(d.dependencies);
  }
})(tree.dependencies);

// ── NuGet ────────────────────────────────────────────────────────────
const assetsPath = join(root, 'src', 'Tmax.App', 'obj', 'project.assets.json');
if (!existsSync(assetsPath)) throw new Error('Run `dotnet build` first (obj/project.assets.json missing)');
const assets = JSON.parse(readFileSync(assetsPath, 'utf8'));
const nugetRoot = assets.project?.restore?.packagesPath ?? join(homedir(), '.nuget', 'packages');
const nuget = [];
const target = Object.keys(assets.targets).find((t) => !t.includes('/')) ?? Object.keys(assets.targets)[0];
for (const [key, info] of Object.entries(assets.targets[target])) {
  if (info.type !== 'package') continue;
  const [name, version] = key.split('/');
  const dir = join(nugetRoot, name.toLowerCase(), version.toLowerCase());
  const spec = readFileSync(join(dir, readdirSync(dir).find((f) => f.endsWith('.nuspec'))), 'utf8');
  const tag = (t) => spec.match(new RegExp(`<${t}[^>]*>([^<]*)</${t}>`))?.[1]?.trim();
  const licenseFile = readdirSync(dir).find((f) => /^(licen[sc]e|copying)(\.|$)/i.test(f));
  nuget.push({
    name, version,
    license: tag('license') ?? tag('licenseUrl') ?? 'UNKNOWN',
    copyright: tag('copyright')?.replace(/&#xA9;|&copy;/g, '©') ?? '',
    url: tag('projectUrl') ?? '',
    text: licenseFile ? readFileSync(join(dir, licenseFile), 'utf8').trim() : null,
  });
}

// Packages that name no SPDX id (empty field, or a license *file* in the .nuspec): derive it from the text.
const fromText = (text) =>
  /public domain/i.test(text ?? '') ? 'Public Domain' : /^\s*(The )?MIT License/i.test(text ?? '') ? 'MIT' : null;
for (const p of [...npm.values(), ...nuget]) {
  if ((!p.license || /^LICENSE/i.test(p.license) || p.license === 'UNKNOWN') && fromText(p.text)) p.license = `${fromText(p.text)} (per license file)`;
}

// ── Full license texts, taken from shipped packages that carry them ──
const fullTexts = {};
const findText = (re) => [...npm.values()].find((p) => p.text && re.test(p.text))?.text;
fullTexts['Apache-2.0'] = findText(/Apache License\s+Version 2\.0/)?.replace(/^[\s\S]*?(Apache License\s+Version 2\.0)/, '$1');
fullTexts['MPL-2.0'] = findText(/Mozilla Public License Version 2\.0/)?.replace(/^[\s\S]*?(Mozilla Public License Version 2\.0)/, '$1');
const mitBody = findText(/^MIT License/m)?.replace(/^[\s\S]*?(Permission is hereby granted)/, '$1');

// Dual-licensed packages ("A OR B"): we elect Apache-2.0 when offered, so only that text is required.
const elected = (license) => (/\bOR\b/.test(license) && license.includes('Apache-2.0') ? 'Apache-2.0' : license);

// ── Output ───────────────────────────────────────────────────────────
const out = [];
out.push('# Third-party notices', '',
  'tmax.net is licensed under the MIT License (see LICENSE). It is based on',
  '[tmax](https://github.com/InbarR/tmax) by Inbar Rotem and contributors (MIT).', '',
  'The distributed application also contains the following third-party software.',
  'This file is generated by `scripts/generate-third-party-notices.mjs`.', '');

out.push('## .NET packages', '', '| Package | Version | License | Copyright |', '|---|---|---|---|');
for (const p of nuget.sort((a, b) => a.name.localeCompare(b.name)))
  out.push(`| ${p.url ? `[${p.name}](${p.url})` : p.name} | ${p.version} | ${p.license} | ${p.copyright} |`);
out.push('', '## JavaScript packages (bundled into the UI)', '', '| Package | Version | License |', '|---|---|---|');
const npmList = [...npm.values()].sort((a, b) => a.name.localeCompare(b.name));
for (const p of npmList)
  out.push(`| ${p.name} | ${p.version} | ${p.license}${elected(p.license) !== p.license ? ` (used under ${elected(p.license)})` : ''} |`);

out.push('', '## License texts', '');
for (const p of [...nuget, ...npmList].filter((p) => p.text)) {
  out.push(`### ${p.name} ${p.version}`, '', '```', p.text, '```', '');
}
// MIT packages that ship no license file: the MIT text with the author / .nuspec copyright as holder.
const mitWithoutText = [...nuget, ...npmList].filter((p) => !p.text && /MIT/.test(p.license));
const withoutText = [...nuget, ...npmList].filter((p) => !p.text && !/MIT|Apache-2\.0/.test(elected(p.license)));
if (withoutText.length) throw new Error(`No license text for: ${withoutText.map((p) => `${p.name} (${p.license})`).join(', ')}`);
if (mitWithoutText.length && mitBody) {
  out.push('### MIT License (for the packages above that ship no license file)', '',
    ...mitWithoutText.map((p) => {
      const holder = (p.copyright || `the authors of ${p.name}`).replace(/^(copyright\s*)?(©|\(c\))?\s*/i, '');
      return `- ${p.name} ${p.version}: Copyright (c) ${holder}`;
    }), '',
    '```', mitBody, '```', '');
}
for (const [id, text] of Object.entries(fullTexts)) {
  const users = [...nuget, ...npmList].filter((p) => elected(p.license).includes(id)).map((p) => p.name);
  if (!users.length) continue;
  if (!text) throw new Error(`No full text found for ${id}`);
  out.push(`### ${id}`, '', `Applies to: ${users.join(', ')}.`, '', '```', text, '```', '');
}

const unknown = [...nuget, ...npmList].filter((p) => /UNKNOWN/.test(p.license));
if (unknown.length) console.warn('Packages without license info:', unknown.map((p) => p.name).join(', '));
writeFileSync(join(root, 'THIRD-PARTY-NOTICES.md'), out.join('\n'));
console.log(`THIRD-PARTY-NOTICES.md: ${nuget.length} NuGet + ${npm.size} npm packages`);
const byLicense = {};
for (const p of [...nuget, ...npmList]) byLicense[p.license] = (byLicense[p.license] ?? 0) + 1;
console.log(byLicense);
