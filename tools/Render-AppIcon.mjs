// 由 docs/images/SqlAssist.Icon.svg 轉出所有被引用的圖示成品；SVG 是唯一來源。
// 用法：node tools/Render-AppIcon.mjs [--modules <含 playwright 的 node_modules>] [--browser <chromium.exe>]
import { createRequire } from 'node:module';
import { readFile, writeFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const value = flag => args.includes(flag) ? args[args.indexOf(flag) + 1] : undefined;
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(value('--modules') ? resolve(value('--modules'), '_resolver.cjs') : import.meta.url);
const { chromium } = require('playwright');

const images = resolve(root, 'docs/images');
const svg = await readFile(resolve(images, 'SqlAssist.Icon.svg'), 'utf8');
const pngs = [
  [512, resolve(images, 'SqlAssist.Icon.512.png')],
  [128, resolve(images, 'SqlAssist.Icon.128.png')],
  [16, resolve(root, 'src/SqlAssist.Ssms22/Resources/SqlAssist.Icon.16.png')],
];
const icoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

const browser = await chromium.launch({ headless: true, executablePath: value('--browser') });
const rendered = new Map();
try {
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  // 每個尺寸各自由向量光柵化，不從大圖縮小，16 px 才不會糊。
  for (const size of new Set([...pngs.map(([size]) => size), ...icoSizes])) {
    await page.setViewportSize({ width: size, height: size });
    await page.setContent(`<style>html,body{margin:0;background:transparent}img{display:block}</style>
      <img width="${size}" height="${size}" src="data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}">`);
    await page.locator('img').evaluate(img => img.decode());
    const png = await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
    // 四角必須透明，SSMS 深淺主題才不會出現暗邊。
    const corner = await page.evaluate(async data => {
      const bitmap = await createImageBitmap(await (await fetch(`data:image/png;base64,${data}`)).blob());
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
      const context = canvas.getContext('2d');
      context.drawImage(bitmap, 0, 0);
      return context.getImageData(0, 0, 1, 1).data[3];
    }, png.toString('base64'));
    if (corner !== 0) throw new Error(`${size} px 左上角不透明（alpha ${corner}）`);
    rendered.set(size, png);
  }
} finally {
  await browser.close();
}

for (const [size, path] of pngs) {
  await writeFile(path, rendered.get(size));
  console.log(`${size} px → ${path}`);
}

// ICO 直接內嵌 PNG 圖層；寬高欄位 0 代表 256。
const frames = icoSizes.map(size => rendered.get(size));
const header = Buffer.alloc(6 + icoSizes.length * 16);
header.writeUInt16LE(0, 0);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(icoSizes.length, 4);
let offset = header.length;
icoSizes.forEach((size, i) => {
  const entry = 6 + i * 16;
  header.writeUInt8(size === 256 ? 0 : size, entry);
  header.writeUInt8(size === 256 ? 0 : size, entry + 1);
  header.writeUInt16LE(1, entry + 4);
  header.writeUInt16LE(32, entry + 6);
  header.writeUInt32LE(frames[i].length, entry + 8);
  header.writeUInt32LE(offset, entry + 12);
  offset += frames[i].length;
});
const ico = resolve(images, 'SqlAssist.ico');
await writeFile(ico, Buffer.concat([header, ...frames]));
console.log(`${icoSizes.join('、')} px → ${ico}`);
