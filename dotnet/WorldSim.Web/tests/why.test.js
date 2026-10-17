const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const http = require('http'), fs = require('fs'), path = require('path');
const root = process.env.WEB_ROOT || require('path').resolve(__dirname, '../../../out/web/wwwroot');
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.wasm': 'application/wasm', '.json': 'application/json', '.dll': 'application/octet-stream', '.dat': 'application/octet-stream', '.css': 'text/css' };
const server = http.createServer((req, res) => {
  let p = decodeURIComponent(req.url.split('?')[0]); if (p === '/') p = '/index.html';
  const f = path.join(root, p);
  if (!fs.existsSync(f)) { res.writeHead(404); return res.end(); }
  res.writeHead(200, { 'Content-Type': types[path.extname(f)] || 'application/octet-stream' });
  fs.createReadStream(f).pipe(res);
}).listen(8765);
const out = process.argv[2] || require('os').tmpdir();
let fails = 0;
const check = (name, ok, extra) => { console.log((ok ? 'PASS ' : 'FAIL ') + name + (extra !== undefined ? '  ' + JSON.stringify(extra) : '')); if (!ok) fails++; };
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || undefined, args: ['--use-gl=swiftshader', '--enable-webgl', '--ignore-gpu-blocklist'] });
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 }, ignoreHTTPSErrors: true });
  // Offline sandboxes: serve three.js from a local copy instead of the CDN.
  if (process.env.THREE_JS) await page.route('**/cdnjs.cloudflare.com/**', (r) => r.fulfill({ path: process.env.THREE_JS, contentType: 'text/javascript' }));
  if (process.env.THREE_JS) await page.route('**/fonts.g*/**', (r) => r.abort());
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error' && !/ERR_FAILED|ERR_TUNNEL/.test(m.text())) errors.push(m.text()); });
  await page.goto('http://localhost:8765/');
  await page.waitForSelector('#hud:not([hidden])', { timeout: 120000 });
  await page.waitForTimeout(800);
  const ev = (s) => page.evaluate(s);
  // Let the town run a while so there is something to explain.
  await ev(`setSpeed(0); api('AdvanceBranch', JSON.parse(api('Branches')).find(b => b.active).id, 45)`); await page.waitForTimeout(500);
  const days = await ev(`S.status.days`);
  check('time advanced', days > 40, days);
  await page.click('[data-why="unemployment"]');
  await page.waitForTimeout(300);
  check('HUD stat opens why tab', await ev(`S.tab === 'why' && $('#panelBody').textContent.includes('変化の原因')`));
  const x = await ev(`JSON.parse(api('Explain','unemployment',30))`);
  check('explanation available', x.ok && x.parts.length > 0, { parts: x.parts.length, explained: x.explained });
  check('unemployment fully explained', Math.abs(x.change) < 1e-9 || Math.abs(x.explained - 1) < 0.1, x.explained);
  check('rows rendered', await ev(`$$('.whyRow').length`) > 0, await ev(`$$('.whyRow').length`));
  for (const m of ['population', 'treasury', 'householdCash', 'price.food', 'price.goods']) {
    await page.click(`[data-wm="${m}"]`);
    await page.waitForTimeout(150);
    const r = await ev(`JSON.parse(api('Explain','${m}',90))`);
    check('explained: ' + m, r.ok && (Math.abs(r.change) < 1e-6 || Math.abs(r.explained - 1) < 0.1), r.ok && r.explained);
  }
  await page.click('[data-wd="30"]');
  await page.screenshot({ path: out + '/why-desktop.png' });
  // Branch comparison: fork, cut income tax in the fork, run both, compare.
  await ev(`showTab('branches')`);
  await page.fill('#forkName', '減税');
  await page.click('#fork');
  const br = await ev(`JSON.parse(api('Branches'))`);
  const alt = br.find((b) => b.name === '減税');
  await ev(`api('SwitchBranch', ${alt.id}); api('Execute', JSON.stringify({type:'policy', key:'incomeTax', value:0})); api('AdvanceBranch', ${alt.id}, 30); api('AdvanceBranch', ${br.find((b) => b.active).id}, 30)`);
  await ev(`renderPanel()`);
  await page.click(`[data-why-branch="${br.find((b) => b.active).id}"]`);
  await page.waitForTimeout(300);
  await page.click('[data-wm="treasury"]');
  await page.waitForTimeout(300);
  const cmp = await ev(`JSON.parse(api('ExplainBranches','treasury',${br.find((b) => b.active).id}))`);
  check('branch difference explained', cmp.ok && cmp.parts[0].label === '所得税', cmp.ok && cmp.parts.slice(0, 3).map((p) => p.label));
  check('compare view rendered', await ev(`$('#panelBody').textContent.includes('分岐した時点からの違い')`));
  await page.screenshot({ path: out + '/why-branches.png' });
  // A demolition shows up as a cause that points at the company.
  await ev(`api('SwitchBranch', ${br.find((b) => b.active).id}); setSpeed(0)`);
  const co = await ev(`(() => { const list = JSON.parse(api('GetBuildings')); for (const b of list) { const i = JSON.parse(api('Inspect', b[2], b[3])); if (i && i.building && i.building.company && i.building.company.workers >= 5 && i.building.def !== 'power_plant') return { x: b[2], z: b[3], id: b[0] }; } return null; })()`);
  await ev(`api('Execute', JSON.stringify({type:'bulldoze', x:${co.x}, z:${co.z}}))`);
  await ev(`api('AdvanceBranch', ${br.find((b) => b.active).id}, 2)`);
  console.log('co', JSON.stringify(co));
  const u = await ev(`JSON.parse(api('Explain','unemployment',3))`);
  const dem = u.parts.find((p) => p.label === '建物の撤去');
  check('demolition appears with the company', !!dem && dem.agents.some((a) => a.id === co.id), dem || u.parts.map(p => p.label));
  // S2: the new buildings, rent and health are visible.
  check('build list has construction company and clinic', await ev(`$('#drawer').textContent.includes('建設会社') && $('#drawer').textContent.includes('診療所')`));
  await ev(`showTab('policy')`);
  check('rent policy slider', await ev(`$('#panelBody').textContent.includes('家賃')`));
  check('S3 policy switches', await ev(`$('#panelBody').textContent.includes('民間投資') && $('#panelBody').textContent.includes('自治体の自動運営')`));
  const gov = await ev(`JSON.parse(api('Execute', JSON.stringify({type:'policy', key:'autoGovernment', value:1})))`);
  check('city government can be switched on', gov.ok, gov.msg);
  const home = await ev(`(() => { for (const b of JSON.parse(api('GetBuildings'))) { const i = JSON.parse(api('Inspect', b[2], b[3])); if (i && i.building && i.building.housing) return { x: b[2], z: b[3] }; } return null; })()`);
  await ev(`inspectAt({ x: ${home.x}, z: ${home.z} })`);
  await page.waitForTimeout(200);
  check('home shows rent and land value', await ev(`$('#panelBody').textContent.includes('家賃') && $('#panelBody').textContent.includes('地価指数')`));
  const builder = await ev(`(() => { for (const b of JSON.parse(api('GetBuildings'))) if (b[1] === 'construction') return { x: b[2], z: b[3] }; return null; })()`);
  await ev(`inspectAt({ x: ${builder.x}, z: ${builder.z} })`);
  await page.waitForTimeout(200);
  check('construction company shows its order book', await ev(`$('#panelBody').textContent.includes('受注残')`));
  const health = await ev(`JSON.parse(api('Explain','population',30))`);
  check('population explanation available', health.ok, health.parts && health.parts.slice(0, 3).map((p) => p.label));

  // S5-0: the in-browser benchmark runs on a throwaway branch and leaves the player's world alone.
  const before = await ev(`JSON.parse(api('Branches')).map(b => b.id + ':' + b.days).join(',')`);
  await ev(`showTab('about')`);
  await page.click('#benchRun');
  await page.waitForFunction(() => window.__benchResult || /失敗/.test(document.querySelector('#benchOut').textContent), null, { timeout: 120000 });
  const bench = await ev(`window.__benchResult`);
  check('benchmark reports numbers', !!bench && bench.msPerDay > 0 && bench.saveKB > 0 && bench.fps16x > 0, bench);
  await ev(`setSpeed(0)`);
  const after = await ev(`JSON.parse(api('Branches')).map(b => b.id + ':' + b.days).join(',')`);
  check('benchmark leaves branches as they were', before.split(',').length === after.split(',').length, { before, after });

  // "?bench" starts the same measurement on its own.
  await page.goto('http://localhost:8765/?bench');
  await page.waitForFunction(() => window.__benchResult, null, { timeout: 180000 });
  check('?bench runs the measurement', (await ev(`window.__benchResult.msPerDay`)) > 0);
  await ev(`setSpeed(0)`);

  // Phone layout.
  await page.setViewportSize({ width: 390, height: 844 });
  await ev(`Why.open('unemployment', 0)`);
  await page.waitForTimeout(300);
  await page.screenshot({ path: out + '/why-mobile.png' });
  check('no horizontal overflow on phone', !(await ev(`document.documentElement.scrollWidth > innerWidth`)));
  check('no page errors', errors.length === 0, errors.slice(0, 5));
  await browser.close();
  server.close();
  console.log(fails ? `${fails} FAILED` : 'ALL PASSED');
  process.exit(fails ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
