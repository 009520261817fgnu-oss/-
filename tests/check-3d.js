// 3D観察台の自動確認（Playwright）。
// 使い方: THREE_DIR=<three パッケージのフォルダ（build/ と examples/ を含む）> node tests/check-3d.js
// CDN への読み込みは THREE_DIR のファイルに差し替える。BLOCK_THREE=1 で読み込み失敗時のエラー画面を確認する。
const { chromium } = require(process.env.PLAYWRIGHT || 'playwright');
const fs = require('fs'), path = require('path');
const OUT = process.env.OUT_DIR || '.';
const T = process.env.THREE_DIR || '.';
const body = fs.readFileSync(path.join(__dirname, '..', 'sanmai-3d.html'), 'utf8');
const html = `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"></head><body>${body}</body></html>`;
(async () => {
  const browser = await chromium.launch({ ...(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM } : {}), args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
  const results = [];
  const ok = (name, cond, extra = '') => { results.push(`${cond ? 'OK ' : 'NG '} ${name}${extra ? '  ' + extra : ''}`); };
  for (const dev of ['desktop', 'mobile']) {
    const ctx = await browser.newContext(dev === 'mobile' ? { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, deviceScaleFactor: 2 } : { viewport: { width: 1280, height: 860 } });
    const page = await ctx.newPage(); const errors = [];
    // 疑似ポインターイベントには実体のポインターがないため、ポインターキャプチャのエラーはテスト由来として除外する
    page.on('pageerror', (e) => { if (!/PointerCapture/.test(e.message)) errors.push(e.message); });
    page.on('console', (m) => { if (m.type() === 'error' && !m.text().includes('ERR_FAILED')) errors.push(m.text()); });
    await page.route('**/*', (route) => {
      const u = route.request().url();
      if (u.startsWith('http://game.local')) return route.fulfill({ contentType: 'text/html', body: html });
      const m = u.match(/three@0\.170\.0\/(.*)$/);
      if (m && !process.env.BLOCK_THREE) return route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(path.join(T, m[1])) });
      return route.abort();
    });
    const t0 = Date.now();
    await page.goto('http://game.local/');
    if (process.env.BLOCK_THREE) {
      await page.waitForTimeout(16000);
      ok(`${dev}: 読み込み失敗時にエラー画面`, await page.isVisible('#error'), (await page.textContent('#error-text')).slice(0, 40));
      await ctx.close(); continue;
    }
    await page.waitForFunction(() => window.__sanmai3dReady === true, null, { timeout: 20000 });
    ok(`${dev}: 3Dシーンの表示`, !(await page.isVisible('#loading')) && !(await page.isVisible('#error')), `${Date.now() - t0}ms`);
    await page.screenshot({ path: `${OUT}/3d-${dev}-1.png`, fullPage: true });
    const cam = () => page.evaluate(() => { const c = window.__sanmai3d.sm.camera, t = window.__sanmai3d.cam.controls.target; return { x: c.position.x, y: c.position.y, z: c.position.z, d: c.position.distanceTo(t) }; });
    const box = await page.locator('#canvas').boundingBox();
    const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
    const before = await cam();
    // 回転（マウスまたは1本指）
    await page.evaluate(async ({ cx, cy, touch }) => {
      const el = document.getElementById('canvas');
      const f = (type, x, y, id = 1) => el.dispatchEvent(new PointerEvent(type, { bubbles: true, cancelable: true, clientX: x, clientY: y, pointerId: id, pointerType: touch ? 'touch' : 'mouse', isPrimary: id === 1, button: 0, buttons: 1 }));
      f('pointerdown', cx, cy); for (let i = 1; i <= 10; i++) { f('pointermove', cx + i * 12, cy + i * 3); await new Promise((r) => setTimeout(r, 16)); } f('pointerup', cx + 120, cy + 30);
    }, { cx, cy, touch: dev === 'mobile' });
    await page.waitForTimeout(600);
    const rot = await cam();
    ok(`${dev}: ドラッグで回転`, Math.hypot(rot.x - before.x, rot.z - before.z) > 0.3, `(${before.x.toFixed(2)},${before.z.toFixed(2)})→(${rot.x.toFixed(2)},${rot.z.toFixed(2)})`);
    // 拡大縮小（ホイールまたはピンチ）
    if (dev === 'desktop') { await page.mouse.move(cx, cy); await page.mouse.wheel(0, -600); }
    else await page.evaluate(async ({ cx, cy }) => {
      const el = document.getElementById('canvas');
      const f = (type, x, y, id) => el.dispatchEvent(new PointerEvent(type, { bubbles: true, cancelable: true, clientX: x, clientY: y, pointerId: id, pointerType: 'touch', isPrimary: id === 1, button: 0, buttons: 1 }));
      f('pointerdown', cx - 20, cy, 1); f('pointerdown', cx + 20, cy, 2);
      for (let i = 1; i <= 10; i++) { f('pointermove', cx - 20 - i * 9, cy, 1); f('pointermove', cx + 20 + i * 9, cy, 2); await new Promise((r) => setTimeout(r, 16)); }
      f('pointerup', cx - 110, cy, 1); f('pointerup', cx + 110, cy, 2);
    }, { cx, cy });
    await page.waitForTimeout(700);
    const zoom = await cam();
    ok(`${dev}: ${dev === 'desktop' ? 'ホイール' : 'ピンチ'}で拡大`, zoom.d < rot.d - 0.2, `距離 ${rot.d.toFixed(2)}→${zoom.d.toFixed(2)}`);
    // 表示切り替え
    await page.click('#t-skeleton'); await page.click('#t-guides');
    const disp = await page.evaluate(() => ({ sk: window.__sanmai3d.mm.parts.skeleton.visible, gd: window.__sanmai3d.mm.parts.guides.visible, op: window.__sanmai3d.mm.parts.body.material.opacity }));
    ok(`${dev}: 骨格表示・ガイド切り替え`, disp.sk && !disp.gd && disp.op < 1, JSON.stringify(disp));
    await page.click('#view-top'); await page.waitForTimeout(700);
    await page.screenshot({ path: `${OUT}/3d-${dev}-2-top-skeleton.png` });
    // 部位の選択（中央付近をタップ）
    const b2 = await page.locator('#canvas').boundingBox();
    await page.evaluate(({ x, y }) => { const el = document.getElementById('canvas'); ['pointerdown', 'pointerup'].forEach((t) => el.dispatchEvent(new PointerEvent(t, { bubbles: true, clientX: x, clientY: y, pointerId: 3, pointerType: 'mouse', button: 0 }))); }, { x: b2.x + b2.width * 0.5, y: b2.y + b2.height * 0.5 });
    ok(`${dev}: 部位の選択`, (await page.textContent('#part-info')).length > 10, (await page.textContent('#part-info')).slice(0, 30));
    // 魚の切り替えとメモリ（ジオメトリ数が増え続けないこと）
    const g0 = await page.evaluate(() => window.__sanmai3d.sm.renderer.info.memory.geometries);
    for (const f of ['saba', 'tai', 'aji', 'saba', 'tai']) { await page.click('#fish-' + f); await page.waitForTimeout(150); }
    await page.screenshot({ path: `${OUT}/3d-${dev}-3-tai.png` });
    await page.click('#fish-aji'); await page.waitForTimeout(200);
    const g1 = await page.evaluate(() => window.__sanmai3d.sm.renderer.info.memory.geometries);
    ok(`${dev}: 魚の切り替えでジオメトリが増え続けない`, g1 <= g0 + 2, `${g0}→${g1}`);
    // リセット
    await page.click('#reset'); await page.waitForTimeout(800);
    const rs = await page.evaluate(() => { const w = window.__sanmai3d; return { s: w.state.get(), d: w.sm.camera.position.distanceTo(w.cam.presetPos('oblique').pos), p: w.sm.camera.position.toArray().map((v) => +v.toFixed(2)) }; });
    ok(`${dev}: リセットで初期状態`, !rs.s.skeleton && rs.s.guides && rs.d < 0.01, JSON.stringify(rs.p));
    // 画面サイズの変更
    const vs = dev === 'desktop' ? { width: 800, height: 900 } : { width: 844, height: 390 };
    await page.setViewportSize(vs); await page.waitForTimeout(400);
    const sz = await page.evaluate(() => { const c = document.getElementById('canvas'); const s = document.getElementById('stage'); return { cw: c.width, sw: s.clientWidth, dpr: window.__sanmai3d.sm.renderer.getPixelRatio(), aspect: window.__sanmai3d.sm.camera.aspect, ratio: s.clientWidth / s.clientHeight }; });
    ok(`${dev}: 画面サイズ変更に追従`, Math.abs(sz.cw - sz.sw * sz.dpr) <= 2 && Math.abs(sz.aspect - sz.ratio) < 0.01, JSON.stringify(sz));
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    ok(`${dev}: 横スクロールが出ない`, !overflow);
    const perf = await page.evaluate(() => { const i = window.__sanmai3d.sm.renderer.info; return { calls: i.render.calls, tris: i.render.triangles, geos: i.memory.geometries, tex: i.memory.textures, heapMB: performance.memory ? +(performance.memory.usedJSHeapSize / 1048576).toFixed(1) : null }; });
    ok(`${dev}: 描画負荷`, true, JSON.stringify(perf));
    ok(`${dev}: エラーなし`, errors.length === 0, errors.join(' / '));
    await ctx.close();
  }
  console.log(results.join('\n'));
  await browser.close();
})();
