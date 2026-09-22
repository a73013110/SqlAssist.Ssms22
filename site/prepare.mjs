import { cp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { dirname, resolve, relative, extname } from 'node:path';
import { fileURLToPath } from 'node:url';

const site = dirname(fileURLToPath(import.meta.url));
const root = resolve(site, '..');
const output = resolve(site, '.build');
const repo = 'https://github.com/a73013110/SqlAssist.Ssms22/blob/master/';

// 只清理由本工具產生的固定目錄；README 與 docs 仍是文件的唯一來源。
await rm(output, { recursive: true, force: true });
await mkdir(resolve(output, 'public'), { recursive: true });
await cp(resolve(site, '.vitepress'), resolve(output, '.vitepress'), { recursive: true });
for (const folder of ['images', 'demos']) {
  await cp(resolve(root, 'docs', folder), resolve(output, 'public', folder), {
    recursive: true, filter: source => !source.endsWith('.md')
  });
}
await cp(resolve(root, 'docs/images/SqlAssist.ico'), resolve(output, 'public/favicon.ico'));
await writeFile(resolve(output, 'public/robots.txt'), 'User-agent: *\nAllow: /\nSitemap: https://a73013110.github.io/SqlAssist.Ssms22/sitemap.xml\n');

function link(url, source) {
  if (/^(?:[a-z][a-z\d+.-]*:|\/|#)/i.test(url)) return url;
  const [path, suffix = ''] = url.split(/(?=[?#])/s, 2);
  const target = relative(root, resolve(dirname(source), path)).replaceAll('\\', '/');
  if (target === 'README.md') return '/en' + suffix;
  if (target === 'README.zh-TW.md') return '/' + suffix;
  if (/^docs\/(images|demos)\//.test(target) && extname(path) !== '.md') return '/' + target.slice(5) + suffix;
  if (target.startsWith('docs/') && extname(path) === '.md') return '/guide/' + target.slice(5, -3) + suffix;
  return repo + target + suffix;
}

async function page(source, destination) {
  let text = await readFile(source, 'utf8');
  // 程式碼範例中的相對路徑不可改成網站 URL。
  text = text.split(/(^\s*```[^\n]*\n[\s\S]*?^\s*```\s*$)/m).map((part, i) => i % 2 ? part : part
    .replace(/(\]\()([^\s)]+)(\))/g, (_, before, url, after) => before + link(url, source) + after)
    .replace(/((?:src|href)=")([^"]+)(")/g, (_, before, url, after) => before + link(url, source) + after)
  ).join('');
  if (destination === 'en.md') text = '---\nlang: en\n---\n\n' + text;
  const target = resolve(output, destination);
  await mkdir(dirname(target), { recursive: true });
  await writeFile(target, text);
}

async function guides(folder) {
  for (const item of await readdir(folder, { withFileTypes: true })) {
    const source = resolve(folder, item.name);
    if (item.isDirectory()) await guides(source);
    else if (item.name.endsWith('.md')) await page(source, 'guide/' + relative(resolve(root, 'docs'), source));
  }
}
await guides(resolve(root, 'docs'));
await page(resolve(root, 'README.zh-TW.md'), 'index.md');
await page(resolve(root, 'README.md'), 'en.md');
