import { createRequire } from 'node:module';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const args = process.argv.slice(2);
const value = flag => args.includes(flag) ? args[args.indexOf(flag) + 1] : undefined;
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(value('--modules') ? resolve(value('--modules'), '_resolver.cjs') : import.meta.url);
const { chromium } = require('playwright');
const output = resolve(root, 'artifacts/feature-demos');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true, executablePath: value('--browser') });
try {
  const page = await browser.newPage({ viewport: { width: 1100, height: 620 }, deviceScaleFactor: 1, reducedMotion: 'reduce' });
  const failures = [];
  page.on('pageerror', error => failures.push(error.message));
  await page.goto(pathToFileURL(resolve(root, 'docs/demos/feature-demos.html')).href + '?capture=1');
  await page.evaluate(() => document.fonts.ready);
  const catalog = await page.evaluate(() => window.demoCatalog);
  for (const [id, demo] of Object.entries(catalog)) {
    const folder = resolve(output, id);
    await mkdir(folder, { recursive: true });
    for (let i = 0; i < demo.frames.length; i++) {
      await page.evaluate(([name, index]) => window.renderDemoFrame(name, index), [id, i]);
      // 檢查會承載完整 SQL 的區塊；清單摘要則依產品行為允許省略。
      const clipped = await page.locator('.codebox, .preview, .dialog, .filters, .surround-picker').evaluateAll(nodes => nodes.filter(n => n.scrollWidth > n.clientWidth + 2 || n.scrollHeight > n.clientHeight + 2).map(n => n.className));
      if (clipped.length) failures.push(`${id}:${i} 溢位：${clipped.join(', ')}`);
      await page.locator('#stage').screenshot({ path: resolve(folder, `${String(i).padStart(3, '0')}.png`) });
    }
    console.log(`${id}: ${demo.frames.length} 格，${demo.frames.reduce((a, b) => a + b, 0)} ms`);
  }
  await writeFile(resolve(output, 'manifest.json'), JSON.stringify(catalog, null, 2) + '\n');
  if (failures.length) throw new Error(failures.join('\n'));
} finally {
  await browser.close();
}
