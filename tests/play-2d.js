// 2D版の自動プレイテスト（Playwright）。
// 使い方: LIBS_DIR=<react/react-dom/htm の UMD を置いたフォルダ> FISH=aji|saba|tai MODE=practice|normal|master
//         SCEN=full|sever|damage|handcut|order|spike node tests/play-2d.js [mobile]
// CDN へのアクセスは横取りして LIBS_DIR のファイルを返す（外部ネットワーク不要）。
const { chromium } = require(process.env.PLAYWRIGHT || 'playwright');
const fs = require('fs');
const S = process.env.OUT_DIR || '.';
const L = process.env.LIBS_DIR || '.';
const body = fs.readFileSync(require('path').join(__dirname, '..', 'sanmai-oroshi.html'), 'utf8');
const page_html = `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"></head><body>${body}</body></html>`;
const libs = {
  'react.production.min.js': L + '/react.production.min.js',
  'react-dom.production.min.js': L + '/react-dom.production.min.js',
  'htm.umd.js': L + '/htm.umd.js',
};
const FISH = process.env.FISH || 'aji', MODE = process.env.MODE || 'practice', SCEN = process.env.SCEN || 'full';
const mobile = process.argv[2] === 'mobile';
(async () => {
  const browser = await chromium.launch(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM } : {});
  const ctx = await browser.newContext(mobile ? { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, deviceScaleFactor: 2 } : { viewport: { width: 1280, height: 900 } });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push('pageerror: ' + e.message));
  page.on('console', m => { if (m.type() === 'error' && !m.text().includes('ERR_FAILED')) errors.push('console: ' + m.text()); });
  await page.route('**/*', route => {
    const u = route.request().url();
    if (u.startsWith('http://game.local')) return route.fulfill({ contentType: 'text/html', body: page_html });
    for (const k in libs) if (u.endsWith(k)) return route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(libs[k]) });
    return route.abort();
  });
  await page.goto('http://game.local/');
  const tag = `${mobile ? 'm' : 'd'}-${FISH}-${MODE}-${SCEN}`;
  const shot = async (n) => page.screenshot({ path: `${S}/s2-${tag}-${n}.png`, fullPage: true });
  await page.click('#fish-' + FISH); await page.click('#mode-' + MODE); await page.click('#start');
  async function toScreen(pts) {
    return page.evaluate((pts) => {
      const svg = document.querySelector('.boardbox svg'); const m = svg.getScreenCTM();
      const mir = !!document.querySelector('.boardbox svg g[transform*="scale(-1"]');
      return pts.map(p => { const pt = svg.createSVGPoint(); pt.x = mir ? 800 - p.x : p.x; pt.y = p.y; const q = pt.matrixTransform(m); return { x: q.x, y: q.y }; });
    }, pts);
  }
  async function drag(fishPts, slow = 8) {
    const sp = await toScreen(fishPts);
    await page.evaluate(async ({ sp, slow, mobile }) => {
      const svg = document.querySelector('.boardbox svg');
      const fire = (type, p) => svg.dispatchEvent(new PointerEvent(type, { bubbles: true, cancelable: true, clientX: p.x, clientY: p.y, pointerId: 7, pointerType: mobile ? 'touch' : 'mouse', isPrimary: true, button: 0 }));
      fire('pointerdown', sp[0]);
      for (let i = 1; i < sp.length; i++) { fire('pointermove', sp[i]); await new Promise(r => setTimeout(r, slow)); }
      fire('pointerup', sp[sp.length - 1]);
    }, { sp, slow, mobile });
    await page.waitForTimeout(40);
  }
  async function tapFish(p) { await drag([p, { x: p.x + 0.5, y: p.y }], 2); }
  const geo = (expr, arg) => page.evaluate(({ expr, arg, FISH }) => { const F = window.__sanmaiFish[FISH]; return (new Function('F', 'arg', 'return ' + expr))(F, arg); }, { expr, arg, FISH });
  async function line(key, off = 0, reverse = false, step = 10) {
    return geo(`(() => { const t = F.targets[arg.key]; const pts = []; const lo = Math.min(t.a, t.b), hi = Math.max(t.a, t.b);
      for (let s = lo; s <= hi; s += arg.step) pts.push(t.axis === 'x' ? { x: s, y: t.fn(s) + arg.off } : { x: t.fn(s) + arg.off, y: s });
      let o = pts; if (t.dir === -1) o = pts.slice().reverse(); if (arg.reverse) o = o.slice().reverse(); return o; })()`, { key, off, reverse, step });
  }
  async function spot(key) {
    return geo(`(() => { const B = F.B; const mid = (F.headT.xTop + B.X1) / 2; const k = arg;
      if (k === 'zeigo') return { x: B.X0 + 62, y: B.CY + 6 };
      if (k === 'head') return { x: mid + 10, y: B.CY + 2 };
      if (k === 'gut') return { x: F.anusX - 10, y: B.top(F.anusX) + 0.35 * B.Hd(F.anusX) };
      const up = (x) => B.top(x) + Math.min(24, Math.max(12, 0.22 * B.Hd(x))), lo = (x) => B.bot(x) - Math.min(24, Math.max(12, 0.25 * B.Hv(x)));
      if (k === 'belly') return { x: mid + 20, y: up(mid + 20) };
      if (k === 'back') return { x: mid + 20, y: lo(mid + 20) };
      if (k === 'spine') return { x: mid, y: up(mid) };
      return { x: B.X1 - 26, y: B.CY }; })()`, key);
  }
  async function hand(key) { await page.click('#tool-hand'); const p = await spot(key); await tapFish(p); await page.click('#tool-knife'); }
  const msg = async () => ((await page.textContent('.msg').catch(() => '')) || '').trim().slice(0, 110);
  const fail = async () => ((await page.textContent('#failbox').catch(() => '')) || '').trim().slice(0, 60);
  async function cut(key, off = 0, reverse = false) { await hand(key); await page.click('#tool-knife'); await drag(await line(key, off, reverse)); await page.click('#decide'); console.log(key.padEnd(6), '|', await msg()); }
  async function scrub() {
    await page.click('#tool-scaler');
    const r = await geo(`({ x0: F.scaleFrom - 10, x1: F.B.X1 - 6, CY: F.B.CY, hd: F.B.Hd((F.B.X0+F.B.X1)/2), hv: F.B.Hv((F.B.X0+F.B.X1)/2) })`);
    for (let f = -0.85; f <= 0.86; f += 0.12) {
      const pts = []; for (let x = r.x1; x >= r.x0; x -= 8) pts.push({ x, y: r.CY + (f < 0 ? f * r.hd : f * r.hv) * (x < r.x0 + 60 || x > r.x1 - 80 ? 0.5 : 1) });
      await drag(pts, 1);
    }
    console.log('scale  |', await msg());
  }
  await page.click('#to-prep');
  const tapTool = async (t) => { await page.click('#tool-' + t); await tapFish({ x: 400, y: 200 }); console.log(t.padEnd(6), '|', await msg()); };

  if (SCEN === 'full') {
    await tapTool('cloth');
    await scrub();
    await page.click('#tool-knife');
    if (FISH === 'aji') await cut('zeigo', 2);
    await cut('head', 3); await cut('gut', 1);
    await tapTool('water'); await tapTool('paper');
    await shot('1prep');
    await cut('belly', -2); await cut('back', 3); await cut('spine', 2);
    await shot('2Aoff');
    await page.click('#tool-hand'); await tapFish({ x: 400, y: 190 }); console.log('flip   |', await msg());
    await cut('back', 2); await cut('belly', -3);
    await shot('3Bdraft');
    await cut('spine', -2);
    console.log('TOTAL', await page.textContent('#total').catch(() => 'none'));
    await shot('4result');
  }
  if (SCEN === 'sever') {
    await tapTool('cloth');
    await page.click('#tool-knife'); await hand('head');
    const x = await geo('(F.headT.xTop + F.B.X1) / 2 - 40'); const t = await geo('F.B.top(arg) - 10', x), b = await geo('F.B.bot(arg) + 10', x);
    await drag([{ x, y: t }, { x: x + 5, y: (t + b) / 2 }, { x: x + 8, y: b }]); console.log('draft  |', await page.textContent('#draftinfo').catch(() => '-'));
    await page.click('#decide'); console.log('fail   |', await fail()); await shot('fail');
    await page.click('#fail-undo').catch(() => console.log('(no undo button)')); console.log('undo   |', await msg());
  }
  if (SCEN === 'damage') {
    await tapTool('cloth'); await page.click('#tool-knife');
    await hand('scale');
    const mid = await geo('(F.headT.xTop + F.B.X1) / 2');
    for (let i = 0; i < 4; i++) {
      const y = await geo(`F.B.CY + arg`, (i % 2 ? 1 : -1) * 22 + i * 3);
      await drag([{ x: mid - 150, y: y - 14 }, { x: mid + 40, y: y + 14 }]); await page.click('#decide');
      console.log('gash' + i, '|', (await msg()) || await fail());
    }
    await shot('fail');
  }
  if (SCEN === 'handcut') {
    await tapTool('cloth'); await page.click('#tool-knife');
    await drag(await line('spine', 2)); console.log('draft  |', await page.textContent('#draftinfo').catch(() => '-'));
    await page.click('#decide'); console.log('fail   |', await fail()); await shot('fail');
  }
  if (SCEN === 'spike') {
    await tapTool('cloth'); await page.click('#tool-hand');
    const x = await geo('(F.fins[0].x0 + F.fins[0].x1) / 2'); const y = await geo('F.B.top(arg) + 6', x);
    await tapFish({ x, y }); await page.click('#tool-knife');
    await drag(await line('head', 3)); await page.click('#decide'); console.log('head   |', await page.textContent('.msg'));
  }
  if (SCEN === 'order') {
    // うろこを取らず、頭も落とさず、いきなり三枚おろし。尾も落とす。塩もふる。
    await tapTool('salt');
    await page.click('#tool-knife'); await hand('head');
    await drag(await line('tail')); await page.click('#decide'); console.log('tail   |', await msg());
    await cut('spine', 2);
    await cut('belly', -2); await cut('back', 3); await cut('spine', 2);
    await page.click('#tool-hand'); await tapFish({ x: 400, y: 190 });
    await cut('back', 2); await cut('belly', -3); await cut('spine', -2);
    console.log('TOTAL', await page.textContent('#total').catch(() => 'none'));
    await shot('result');
  }
  console.log('ERRORS:', errors);
  await browser.close();
})();
