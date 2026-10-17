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
  check('starts in creator mode', await ev(`!$('#tools').hidden && $('#lifeCamSeg').hidden`));
  await page.click('[data-mode="life"]');
  await page.waitForTimeout(300);
  check('life mode hides creator tools', await ev(`$('#tools').hidden && $('[data-tab="policy"]').hidden && !$('[data-tab="life"]').hidden`));
  check('chooser shown', await ev(`!!$('#lfCreate') && $$('[data-life="possess"]').length > 0`));
  const build = await ev(`JSON.parse(api('Execute', JSON.stringify({type:'road',x1:2,z1:2,x2:5,z2:2})))`);
  check('world editing refused in life mode', !build.ok && /人生体験/.test(build.msg), build.msg);
  await page.screenshot({ path: out + '/life-chooser.png' });
  await page.click('[data-life="possess"]');
  await page.waitForTimeout(500);
  let st = await ev(`Life.state`);
  check('possessed a resident', st.active && !!st.home, { name: st.name, age: st.age, home: st.home });
  check('life card visible', await ev(`!$('#lifeCard').hidden && $('#lifeCard').textContent.includes(Life.state.name)`));
  check('life speed selected', await ev(`S.status.speed`) === 0 || await ev(`S.status.speed`) === 4, await ev(`S.status.speed`));
  await page.screenshot({ path: out + '/life-outdoor-third.png' });
  // Walk with the keyboard.
  const p0 = { x: st.x, z: st.z };
  await page.mouse.move(640, 300);
  await page.keyboard.down('KeyW'); await page.waitForTimeout(900); await page.keyboard.up('KeyW');
  await page.waitForTimeout(350);
  st = await ev(`Life.state`);
  const moved = Math.hypot(st.x - p0.x, st.z - p0.z);
  check('walking moves the person (server position)', moved > 1 && moved < 15, moved.toFixed(2));
  // Go home: travel, then let time pass.
  const homeId = st.home.id;
  let r = await ev(`(() => { const r = JSON.parse(api('LifeExecute', JSON.stringify({type:'travel', id:${homeId}}))); r.act = JSON.parse(api('LifeState')).activity; return r; })()`);
  check('travel home accepted', r.ok, r.msg);
  check('travelling state', r.act === 'Travelling', r.act);
  await ev(`setSpeed(2)`); await page.waitForTimeout(1200); await ev(`setSpeed(0)`);
  await page.waitForTimeout(300);
  st = await ev(`Life.state`);
  check('arrived at home door', st.activity !== 'Travelling' && st.door && st.door.id === homeId, { act: st.activity, door: st.door });
  r = await ev(`(Life.action('enter', ${homeId}), Life.state)`);
  check('entered home', r.inside === homeId && r.room.id === 'home', r.room && r.room.id);
  await page.waitForTimeout(400);
  check('interior scene drawn', await ev(`Life.scene() === Life.inner.scene && Life.inner.room.children.length > 8`));
  await page.screenshot({ path: out + '/life-home-third.png' });
  await ev(`Life.setCam('first')`); await page.waitForTimeout(300);
  await page.screenshot({ path: out + '/life-home-first.png' });
  const before = await ev(`Life.state.hunger`);
  await ev(`Life.action('fixture', 'fridge')`);
  st = await ev(`Life.state`);
  check('eating from fridge raises hunger meter and uses pantry', st.hunger > before && st.pantry === 2, { before, after: st.hunger, pantry: st.pantry });
  await ev(`Life.action('fixture', 'bed')`);
  check('sleeping', (await ev(`Life.state.activity`)) === 'Sleeping');
  check('stop button shown', await ev(`$('#lifeCard').textContent.includes('睡眠中をやめる')`));
  await ev(`Life.action('stop')`);
  // Walk inside the room.
  const r0 = await ev(`[Life.state.roomX, Life.state.roomZ]`);
  await page.keyboard.down('KeyS'); await page.waitForTimeout(500); await page.keyboard.up('KeyS');
  await page.waitForTimeout(300);
  const r1 = await ev(`[Life.state.roomX, Life.state.roomZ]`);
  check('walking inside the room', Math.hypot(r1[0] - r0[0], r1[1] - r0[1]) > 0.5, { r0, r1 });
  await ev(`Life.action('exit')`);
  check('exit to outdoors', (await ev(`Life.state.inside`)) === 0);
  // Shopping.
  const places = await ev(`JSON.parse(api('Places'))`);
  const shop = places.find((p) => p.label === '商店');
  check('places list has a shop', !!shop, places.map((p) => p.label));
  if (shop) {
    await ev(`Life.action('travel', ${shop.id})`);
    await ev(`setSpeed(2)`); await page.waitForTimeout(1200); await ev(`setSpeed(0)`); await page.waitForTimeout(300);
    const s1 = await ev(`Life.state`);
    if (s1.door && s1.door.denied) console.log('INFO shop closed now:', s1.door.denied, await ev(`S.status.clock`));
    else {
      await ev(`Life.action('enter', ${shop.id})`);
      await ev(`Life.action('fixture', 'counter')`);
      await page.waitForTimeout(300);
      check('shop list shown', await ev(`$('#lifeCard').textContent.includes('在庫')`));
      await page.screenshot({ path: out + '/life-shop.png' });
      const cash0 = await ev(`Life.state.cash`);
      const item = await ev(`Life.state.shop.find(i => i.stock >= 1)`);
      if (item) {
        await ev(`Life.action('buy', '${item.res}')`);
        const cash1 = await ev(`Life.state.cash`);
        check('buying costs the shown price', Math.abs(cash0 - cash1 - item.price) <= 1, { cash0, cash1, price: item.price });
      }
    }
  }
  // Panel lists.
  await ev(`showTab('life'); Life.show.jobs = true; renderPanel()`);
  check('life panel renders profile and jobs', await ev(`$('#panelBody').textContent.includes('所持金') && $('#panelBody').textContent.includes('仕事を探す')`));
  // Gyro: synthetic orientation events turn the view.
  await ev(`Life.setCam('first')`);
  await page.click('#gyroBtn');
  const y0 = await ev(`Cam.lookYaw`);
  for (let a = 0; a <= 20; a += 2) await ev(`window.dispatchEvent(Object.assign(new Event('deviceorientation'), {alpha:${a}, beta:80, gamma:0}))`);
  const y1 = await ev(`Cam.lookYaw`);
  check('gyro turns the view', Math.abs(y1 - y0) > 0.2, { y0, y1 });
  check('gyro button on', await ev(`$('#gyroBtn').getAttribute('aria-pressed')`) === 'true');
  await page.click('#gyroBtn');
  // Creator mode again: tools back, person keeps living.
  await page.click('[data-mode="creator"]');
  await page.waitForTimeout(400);
  check('creator mode restores tools, person persists', await ev(`!$('#tools').hidden && Life.state.active`));
  const road = await ev(`JSON.parse(api('Execute', JSON.stringify({type:'road',x1:2,z1:2,x2:3,z2:2})))`);
  check('creator can edit again', road.ok || !/人生体験/.test(road.msg), road.msg);
  // Gyro in creator free camera.
  await page.click('[data-cam="free"]');
  await page.click('#gyroBtn');
  const fy0 = await ev(`Cam.lookYaw`);
  for (let a = 0; a <= 20; a += 2) await ev(`window.dispatchEvent(Object.assign(new Event('deviceorientation'), {alpha:${a}, beta:80, gamma:0}))`);
  check('gyro also works in creator free camera', Math.abs((await ev(`Cam.lookYaw`)) - fy0) > 0.2);
  await page.click('#gyroBtn');
  // Save keeps the person.
  const save = await ev(`api('SaveAll')`);
  const id = await ev(`Life.state.id`);
  await ev(`loadText(${JSON.stringify('X')}.length ? api('SaveAll') : '')`);
  await page.waitForTimeout(300);
  check('save/load keeps the person', (await ev(`(Life.refresh(), Life.state.id)`)) === id);
  // Mobile layout.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.click('[data-mode="life"]');
  await page.waitForTimeout(500);
  await page.screenshot({ path: out + '/life-mobile.png' });
  const overflow = await ev(`document.documentElement.scrollWidth > innerWidth`);
  check('no horizontal overflow on phone', !overflow);
  check('no page errors', errors.length === 0, errors.slice(0, 5));
  await browser.close();
  server.close();
  console.log(fails ? `${fails} FAILED` : 'ALL PASSED');
  process.exit(fails ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
