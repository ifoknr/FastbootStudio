// Renders the glass icon (tools/icon/glass-dark.svg, glass-light.svg) to the PNGs and the ICO
// the app ships: Assets/icon-themed.png and big_icon.png (the dark glass, 512 px),
// Assets/icon-light.png (the frosted glass of the light theme, 512 px) and icon.ico (16 to
// 256 px, dark glass). Each size is rendered on its own so small sizes stay crisp.
//
// Run from the repository root:  node tools/icon/render_glass.js
// Needs Playwright with Chromium (npm i -D playwright && npx playwright install chromium)
// and Python 3 with Pillow for the ICO.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const root = path.resolve(__dirname, '..', '..');
const sizes = [16, 24, 32, 48, 64, 128, 256];

async function render(browser, svgPath, size) {
  const svg = fs.readFileSync(svgPath, 'utf8');
  const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
  await page.setContent(`<!doctype html><html><body style="margin:0;background:transparent">
    <img src="data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}" width="${size}" height="${size}" style="display:block"></body></html>`);
  const png = await page.screenshot({ omitBackground: true, type: 'png' });
  await page.close();
  return png;
}

(async () => {
  const browser = await chromium.launch();
  const dark = path.join(__dirname, 'glass-dark.svg'), light = path.join(__dirname, 'glass-light.svg');
  fs.writeFileSync(path.join(root, 'Assets', 'icon-themed.png'), await render(browser, dark, 512));
  fs.writeFileSync(path.join(root, 'big_icon.png'), await render(browser, dark, 512));
  fs.writeFileSync(path.join(root, 'Assets', 'icon-light.png'), await render(browser, light, 512));
  const tmp = fs.mkdtempSync(path.join(require('os').tmpdir(), 'fbs-icon-'));
  for (const size of sizes)
    fs.writeFileSync(path.join(tmp, `${size}.png`), await render(browser, dark, size));
  await browser.close();
  // Pillow writes the ICO from the PNGs, one entry per size.
  execFileSync('python3', ['-c', `
import sys
from PIL import Image
sizes = ${JSON.stringify(sizes)}
tmp = sys.argv[1]
images = [Image.open(f"{tmp}/{s}.png").convert("RGBA") for s in sizes]
images[-1].save(sys.argv[2], format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1])
`, tmp, path.join(root, 'icon.ico')]);
  fs.rmSync(tmp, { recursive: true, force: true });
  console.log('icons written');
})();
