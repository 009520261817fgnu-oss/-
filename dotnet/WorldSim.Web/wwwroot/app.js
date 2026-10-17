'use strict';
/* WorldSim web client: renders the C# simulation (running in WebAssembly) with Three.js and turns
   pointer/keyboard/touch input into commands. All game rules live in WorldSim.Core. */

const $ = (s) => document.querySelector(s);
const $$ = (s) => Array.from(document.querySelectorAll(s));
const api = (name, ...args) => DotNet.invokeMethod('WorldSim.Web', name, ...args);
const japi = (name, ...args) => JSON.parse(api(name, ...args));

function b64Bytes(b64) {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}
const b64F32 = (b64) => new Float32Array(b64Bytes(b64).buffer);
const yen = (v) => {
  const a = Math.abs(v);
  const s = a >= 1e8 ? (a / 1e8).toFixed(2) + '億' : a >= 1e4 ? Math.round(a / 1e4).toLocaleString('ja-JP') + '万' : Math.round(a).toLocaleString('ja-JP');
  return (v < 0 ? '−' : '') + s + '円';
};
const pct = (v) => (v * 100).toFixed(1) + '%';
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const isTouch = matchMedia('(pointer: coarse)').matches;

const CAT_NAMES = { Residential: '住宅', Commercial: '商業', Industrial: '工業', Agriculture: '農業', Power: '電力', Public: '公共' };
const RES_NAMES = {};
const POLICY = {
  incomeTax: { label: '所得税率', min: 0, max: 0.5, step: 0.01, fmt: pct, help: '住民の賃金にかかる税。上げると税収が増え、消費は減ります。' },
  corporateTax: { label: '法人税率', min: 0, max: 0.6, step: 0.01, fmt: pct, help: '企業の利益にかかる税。' },
  industrySubsidy: { label: '産業補助（1人1日）', min: 0, max: 20000, step: 500, fmt: yen, help: '工業・農業の従業員1人あたり企業へ支給。' },
  unemploymentBenefit: { label: '失業手当（1日）', min: 0, max: 20000, step: 500, fmt: yen, help: '失業者の生活と消費を支えます。' },
  pension: { label: '年金（1日）', min: 0, max: 20000, step: 500, fmt: yen, help: '65歳以上の退職者に支給。' },
  preparedness: { label: '防災投資（1日）', min: 0, max: 20000000, step: 500000, fmt: yen, help: '積み重ねるほど災害の被害が減ります（最大50%）。' },
  repairBudget: { label: '復旧予算の上限（1日）', min: 0, max: 50000000, step: 1000000, fmt: yen, help: '被災した道路・建物の修理に使える上限。' },
  electricityPrice: { label: '電気料金（kWh）', min: 5, max: 100, step: 1, fmt: (v) => v + '円', help: '発電所の収入、企業と家計の支出になります。' },
};
const SERIES = {
  population: ['人口', '人'], unemployment: ['失業率', '%'], treasury: ['市の資金', '円'], avgWage: ['平均賃金', '円'],
  happiness: ['満足度', ''], retailSales: ['小売売上', '円'], wages: ['賃金総額', '円'], taxes: ['税収', '円'],
  imports: ['輸入', '円'], exports: ['輸出', '円'], companyProfit: ['企業利益', '円'], companiesOpen: ['営業中の企業', '社'],
  traffic: ['交通量', '台'], commute: ['平均通勤', '分'], pollution: ['大気汚染', ''], energyDemand: ['電力需要', 'kWh'],
  energySupply: ['電力供給', 'kWh'], research: ['研究ポイント', ''], damaged: ['被災箇所', ''], temperature: ['気温', '℃'],
  'prod.grain': ['穀物生産', ''], 'prod.iron_ore': ['鉄鉱石生産', ''], 'prod.wood': ['木材生産', ''], 'prod.steel': ['鋼材生産', ''],
  'prod.food': ['食品生産', ''], 'prod.goods': ['工業製品生産', ''], 'prod.services': ['サービス', ''],
};
const DAY = new THREE.Color(0x9fc2d9), NIGHT = new THREE.Color(0x4f6d8f);
const LINE_COLORS = ['#e8893a', '#63bba3', '#7fa7c9', '#e3b341', '#c98fd1', '#e2625a', '#b5c46a', '#9fadb5'];

const S = {
  meta: null, defs: {}, status: null, versions: [-1, -1, -1, -1], netVersion: -1, days: -1,
  tool: 'inspect', buildDef: null, overlay: 'none', tab: 'info', cam: 'overhead',
  inspect: null, hover: null, roadStart: null, pending: null, eventsSeen: 0, chartKeys: ['population', 'unemployment'],
  compareKey: 'treasury', heights: null, lastAgents: 0, lastHud: 0, lastPanel: 0,
};

/* ------------------------------------------------------------------ boot */

async function boot() {
  const bar = $('#bootBar');
  let loaded = 0;
  try {
    await Blazor.start({
      // The host reserves names starting with "_", so the runtime is published under framework/.
      loadBootResource(type, name, url) {
        loaded++;
        bar.style.width = Math.min(95, loaded * 2.6) + '%';
        return url.replace('_framework/', 'framework/');
      },
    });
  } catch (e) {
    return bootFail('エンジンの起動に失敗しました。ページを再読み込みしてください。\n' + e);
  }
  bar.style.width = '100%';
  $('#bootMsg').textContent = '世界を生成しています…';
  await new Promise((r) => setTimeout(r, 30));
  // When the page is republished while open, the viewer hands back the last snapshot so play continues.
  const hot = window.claude?.hot;
  const data = await new Promise((resolve) => (hot?.ready ? hot.ready(resolve) : resolve(hot?.data ?? {})));
  try {
    const restored = data?.save ? japi('LoadAll', data.save) : null;
    applyMeta(restored?.ok ? restored.meta : japi('Init', 1));
    hot?.snapshot?.(() => ({ save: api('SaveAll') }));
  } catch (e) {
    return bootFail('世界の生成に失敗しました。\n' + e);
  }
  initScene();
  initUi();
  $('#boot').hidden = true;
  ['#hud', '#tools', '#panel', '#overlays'].forEach((s) => ($(s).hidden = false));
  renderPanel();
  requestAnimationFrame(frame);
}

function bootFail(msg) {
  $('#bootError').hidden = false;
  $('#bootError').textContent = msg;
}

function applyMeta(meta) {
  S.meta = meta;
  S.defs = {};
  meta.buildings.forEach((b) => (S.defs[b.id] = b));
  meta.resources.forEach((r) => (RES_NAMES[r.id] = r.name));
  S.versions = [-1, -1, -1, -1];
  S.netVersion = -1;
  S.days = -1;
  S.eventKey = null;
  S.inspect = null;
  if (R.scene) rebuildTerrainGeometry();
}

/* ------------------------------------------------------------------ 3D scene */

const R = { scene: null };

function initScene() {
  const canvas = $('#view');
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: !isTouch, powerPreference: 'high-performance' });
  renderer.setPixelRatio(Math.min(devicePixelRatio || 1, isTouch ? 1.5 : 2));
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x0e1b24);
  scene.fog = new THREE.Fog(0x0e1b24, 1800, 4200);
  const camera = new THREE.PerspectiveCamera(50, 1, 2, 8000);
  const hemi = new THREE.HemisphereLight(0xdfe8ef, 0x3b3426, 0.75);
  scene.add(hemi);
  const sun = new THREE.DirectionalLight(0xfff1dc, 0.85);
  sun.position.set(-600, 900, -300);
  scene.add(sun);
  Object.assign(R, { renderer, scene, camera, hemi, sun, sky: new THREE.Color(), raycaster: new THREE.Raycaster() });

  const { width, depth, cellSize, seaLevel } = S.meta;
  const W = width * cellSize, D = depth * cellSize;
  const sea = new THREE.Mesh(new THREE.PlaneGeometry(W * 4, D * 4), new THREE.MeshPhongMaterial({ color: 0x2a6f9e, transparent: true, opacity: 0.86, shininess: 70 }));
  sea.rotation.x = -Math.PI / 2;
  sea.position.set(W / 2, seaLevel, D / 2);
  scene.add(sea);
  const flood = new THREE.Mesh(new THREE.PlaneGeometry(W, D), new THREE.MeshPhongMaterial({ color: 0x6b5a3e, transparent: true, opacity: 0.7 }));
  flood.rotation.x = -Math.PI / 2;
  flood.position.set(W / 2, 0, D / 2);
  flood.visible = false;
  scene.add(flood);
  R.flood = flood;

  R.terrain = new THREE.Mesh(new THREE.BufferGeometry(), new THREE.MeshLambertMaterial({ vertexColors: true }));
  scene.add(R.terrain);
  rebuildTerrainGeometry();

  R.buildings = new THREE.InstancedMesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshLambertMaterial(), 3000);
  R.buildings.count = 0;
  R.buildings.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
  scene.add(R.buildings);
  R.roads = new THREE.Mesh(new THREE.BufferGeometry(), new THREE.MeshLambertMaterial({ vertexColors: true, polygonOffset: true, polygonOffsetFactor: -2 }));
  scene.add(R.roads);
  const cone = new THREE.ConeGeometry(1, 1, 5);
  cone.translate(0, 0.5, 0);
  R.trees = new THREE.InstancedMesh(cone, new THREE.MeshLambertMaterial({ color: 0x2f5b2c }), width * depth);
  R.trees.count = 0;
  scene.add(R.trees);
  R.agents = new THREE.Points(new THREE.BufferGeometry(), new THREE.PointsMaterial({ color: 0xffd27a, size: 7, sizeAttenuation: true }));
  R.agents.geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(3 * 600), 3));
  R.agents.geometry.setDrawRange(0, 0);
  scene.add(R.agents);

  R.ghost = new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshBasicMaterial({ color: 0x63bba3, transparent: true, opacity: 0.7, depthWrite: false }));
  R.ghost.visible = false;
  scene.add(R.ghost);
  R.pathGhost = new THREE.InstancedMesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshBasicMaterial({ color: 0x63bba3, transparent: true, opacity: 0.5, depthWrite: false }), 400);
  R.pathGhost.count = 0;
  scene.add(R.pathGhost);
  R.select = new THREE.LineSegments(new THREE.EdgesGeometry(new THREE.BoxGeometry(1, 1, 1)), new THREE.LineBasicMaterial({ color: 0xe8893a }));
  R.select.visible = false;
  scene.add(R.select);

  Cam.reset();
  addEventListener('resize', resize);
  resize();
}

function resize() {
  const w = innerWidth, h = innerHeight;
  R.renderer.setSize(w, h, false);
  R.camera.aspect = w / h;
  R.camera.updateProjectionMatrix();
}

function rebuildTerrainGeometry() {
  const { width, depth, cellSize } = S.meta;
  const vx = width + 1, vz = depth + 1;
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(vx * vz * 3), 3));
  g.setAttribute('color', new THREE.BufferAttribute(new Float32Array(vx * vz * 3), 3));
  const idx = new Uint32Array(width * depth * 6);
  let k = 0;
  for (let z = 0; z < depth; z++) for (let x = 0; x < width; x++) {
    const i = z * vx + x;
    idx[k++] = i; idx[k++] = i + vx; idx[k++] = i + 1;
    idx[k++] = i + 1; idx[k++] = i + vx; idx[k++] = i + vx + 1;
  }
  g.setIndex(new THREE.BufferAttribute(idx, 1));
  const pos = g.attributes.position.array;
  for (let z = 0; z < vz; z++) for (let x = 0; x < vx; x++) {
    const i = (z * vx + x) * 3;
    pos[i] = x * cellSize; pos[i + 2] = z * cellSize;
  }
  R.terrain.geometry.dispose();
  R.terrain.geometry = g;
}

function heightAt(wx, wz) {
  const h = S.heights;
  if (!h) return 0;
  const { width, depth, cellSize } = S.meta;
  const fx = Math.max(0, Math.min(width, wx / cellSize)), fz = Math.max(0, Math.min(depth, wz / cellSize));
  const x0 = Math.min(width - 1, Math.floor(fx)), z0 = Math.min(depth - 1, Math.floor(fz));
  const tx = fx - x0, tz = fz - z0, vx = width + 1;
  const a = h[z0 * vx + x0] + (h[z0 * vx + x0 + 1] - h[z0 * vx + x0]) * tx;
  const b = h[(z0 + 1) * vx + x0] + (h[(z0 + 1) * vx + x0 + 1] - h[(z0 + 1) * vx + x0]) * tx;
  return a + (b - a) * tz;
}

const _c = new THREE.Color();
function groundColor(h, slope, sea) {
  const a = h - sea;
  if (a <= 0) return _c.setRGB(0.55, 0.5, 0.38);
  if (a < 1.5) return _c.setRGB(0.84, 0.77, 0.56);
  if (slope > 8) return _c.setRGB(0.5, 0.48, 0.44);
  if (a < 25) return _c.setRGB(0.47 - a * 0.004, 0.66 - a * 0.005, 0.36 - a * 0.003);
  if (a < 55) return _c.setRGB(0.53, 0.52, 0.42);
  if (a < 80) return _c.setRGB(0.6, 0.58, 0.56);
  return _c.setRGB(0.94, 0.94, 0.95);
}

function heat(t) { // 0..1+ → green → amber → red
  t = Math.max(0, Math.min(1.5, t)) / 1.5;
  if (t < 0.5) return _c.setRGB(0.39 + t * 1.0, 0.73, 0.64 - t * 0.9);
  return _c.setRGB(0.89, 0.73 - (t - 0.5) * 1.1, 0.19);
}

function refreshTerrain() {
  S.heights = b64F32(api('GetTerrain'));
  const { width, depth, seaLevel } = S.meta;
  const vx = width + 1, vz = depth + 1, h = S.heights;
  const pos = R.terrain.geometry.attributes.position.array;
  for (let i = 0; i < vx * vz; i++) pos[i * 3 + 1] = h[i];
  R.terrain.geometry.attributes.position.needsUpdate = true;
  R.terrain.geometry.computeVertexNormals();
  R.terrain.geometry.computeBoundingSphere();
  R.terrain.geometry.computeBoundingBox();
  recolorTerrain();
}

function recolorTerrain() {
  const { width, depth, seaLevel } = S.meta;
  const vx = width + 1, vz = depth + 1, h = S.heights;
  const col = R.terrain.geometry.attributes.color.array;
  const ov = (S.overlay === 'pollution' || S.overlay === 'landValue') ? b64F32(api('GetOverlay', S.overlay)) : null;
  for (let z = 0; z < vz; z++) for (let x = 0; x < vx; x++) {
    const i = z * vx + x;
    const n = [h[i], x < width ? h[i + 1] : h[i], z < depth ? h[i + vx] : h[i]];
    const slope = Math.max(Math.abs(n[1] - n[0]), Math.abs(n[2] - n[0]));
    groundColor(h[i], slope, seaLevel);
    if (ov) {
      const cx = Math.min(width - 1, x), cz = Math.min(depth - 1, z);
      const v = ov[cz * width + cx];
      if (S.overlay === 'pollution' && v > 0.02) { const t = Math.min(1, v); _c.lerp(new THREE.Color(0.55, 0.32, 0.6), t * 0.85); }
      if (S.overlay === 'landValue' && v > 0) _c.copy(heat(1.5 - v * 1.5));
    }
    col[i * 3] = _c.r; col[i * 3 + 1] = _c.g; col[i * 3 + 2] = _c.b;
  }
  R.terrain.geometry.attributes.color.needsUpdate = true;
}

const _m = new THREE.Matrix4(), _p = new THREE.Vector3(), _q = new THREE.Quaternion(), _s = new THREE.Vector3();
function refreshBuildings() {
  const list = japi('GetBuildings');
  const cs = S.meta.cellSize;
  S.buildingList = list;
  R.buildings.count = list.length;
  list.forEach((b, i) => {
    const [id, defId, x, z, base, cond, min, state] = b;
    const d = S.defs[defId];
    const bottom = min - 0.6, top = base + d.h * (cond < 0.3 ? 0.55 : 1);
    _p.set((x + d.w / 2) * cs, (bottom + top) / 2, (z + d.d / 2) * cs);
    _s.set(d.w * cs - 1.2, top - bottom, d.d * cs - 1.2);
    _m.compose(_p, _q, _s);
    R.buildings.setMatrixAt(i, _m);
    _c.setHex(d.color);
    if (state === 1) _c.lerp(new THREE.Color(0x555a5e), 0.75);
    else if (state === 2) _c.lerp(new THREE.Color(0xe8893a), 0.45);
    else if (state === 3) _c.multiplyScalar(0.7);
    if (cond < 1) _c.lerp(new THREE.Color(0x4a2a22), (1 - cond) * 0.9);
    R.buildings.setColorAt(i, _c);
  });
  R.buildings.instanceMatrix.needsUpdate = true;
  if (R.buildings.instanceColor) R.buildings.instanceColor.needsUpdate = true;
  updateSelection();
}

function refreshRoads() {
  const roads = b64Bytes(api('GetRoads'));
  const traffic = S.overlay === 'traffic' ? b64F32(api('GetOverlay', 'traffic')) : null;
  const { width, depth, cellSize: cs } = S.meta;
  const h = S.heights, vx = width + 1;
  const pos = [], col = [], idx = [];
  let maxTraffic = 0.05;
  if (traffic) for (let i = 0; i < traffic.length; i++) if (roads[i] === 1 && traffic[i] > maxTraffic) maxTraffic = traffic[i];
  for (let z = 0; z < depth; z++) for (let x = 0; x < width; x++) {
    const r = roads[z * width + x];
    if (!r) continue;
    const n = pos.length / 3;
    const y = (xx, zz) => h[zz * vx + xx] + 0.25;
    pos.push(x * cs, y(x, z), z * cs, (x + 1) * cs, y(x + 1, z), z * cs, x * cs, y(x, z + 1), (z + 1) * cs, (x + 1) * cs, y(x + 1, z + 1), (z + 1) * cs);
    if (r === 2) _c.setRGB(0.62, 0.22, 0.18);
    else if (traffic) roadHeat(traffic[z * width + x], maxTraffic);
    else _c.setRGB(0.27, 0.28, 0.3);
    for (let k = 0; k < 4; k++) col.push(_c.r, _c.g, _c.b);
    idx.push(n, n + 2, n + 1, n + 1, n + 2, n + 3);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  g.setIndex(idx);
  g.computeVertexNormals();
  R.roads.geometry.dispose();
  R.roads.geometry = g;
}

/** Unused roads stay grey; busier roads shade green → amber relative to the busiest; over capacity is red. */
function roadHeat(ratio, max) {
  if (ratio > 1) return _c.setRGB(0.89, 0.25, 0.2);
  const t = Math.min(1, ratio / Math.min(1, max));
  if (t < 0.03) return _c.setRGB(0.3, 0.31, 0.33);
  if (t < 0.5) return _c.setRGB(0.3 + t * 0.5, 0.55 + t * 0.3, 0.4 - t * 0.3);
  return _c.setRGB(0.55 + (t - 0.5) * 0.7, 0.7 - (t - 0.5) * 0.5, 0.25);
}

function refreshTrees() {
  const veg = b64Bytes(api('GetVegetation'));
  const { width, depth, cellSize: cs } = S.meta;
  let n = 0;
  for (let z = 0; z < depth; z++) for (let x = 0; x < width; x++) {
    const v = veg[z * width + x];
    if (!v) continue;
    const hash = ((x * 73856093) ^ (z * 19349663)) >>> 0;
    const wx = (x + 0.5 + ((hash & 255) / 255 - 0.5) * 0.5) * cs, wz = (z + 0.5 + (((hash >> 8) & 255) / 255 - 0.5) * 0.5) * cs;
    const ht = 4 + (v / 255) * 8;
    _p.set(wx, heightAt(wx, wz), wz);
    _s.set(ht * 0.32, ht, ht * 0.32);
    _m.compose(_p, _q, _s);
    R.trees.setMatrixAt(n++, _m);
  }
  R.trees.count = n;
  R.trees.instanceMatrix.needsUpdate = true;
}

function refreshAgents() {
  const pts = b64F32(api('GetAgents', isTouch ? 250 : 500));
  const arr = R.agents.geometry.attributes.position.array;
  const n = Math.min(pts.length / 2, 600);
  for (let i = 0; i < n; i++) {
    const x = pts[i * 2], z = pts[i * 2 + 1];
    arr[i * 3] = x; arr[i * 3 + 1] = heightAt(x, z) + 3; arr[i * 3 + 2] = z;
  }
  R.agents.geometry.attributes.position.needsUpdate = true;
  R.agents.geometry.setDrawRange(0, n);
}

function updateSelection() {
  const b = S.inspect?.building;
  const rec = b && S.buildingList?.find((r) => r[0] === b.id);
  if (!rec) { R.select.visible = false; return; }
  const [, defId, x, z, base, , min] = rec;
  const d = S.defs[defId], cs = S.meta.cellSize;
  R.select.position.set((x + d.w / 2) * cs, (min + base + d.h) / 2, (z + d.d / 2) * cs);
  R.select.scale.set(d.w * cs + 1, base + d.h - min + 1.5, d.d * cs + 1);
  R.select.visible = true;
}

/* ------------------------------------------------------------------ camera */

const Cam = {
  target: new THREE.Vector3(), dist: 520, yaw: 0.6, pitch: 0.95, pos: new THREE.Vector3(), lookYaw: 0, lookPitch: -0.2,
  move: { x: 0, z: 0, y: 0 },
  reset() {
    const { width, depth, cellSize } = S.meta;
    this.target.set(width * cellSize / 2, 20, depth * cellSize / 2);
    this.dist = 560; this.yaw = 0.6; this.pitch = 0.95;
  },
  setMode(mode) {
    if (mode === S.cam) return;
    if (mode !== 'overhead') {
      if (S.cam === 'overhead') {
        this.pos.copy(R.camera.position);
        if (mode === 'walk') this.pos.set(this.target.x, 0, this.target.z);
        this.lookYaw = this.yaw;
        this.lookPitch = mode === 'walk' ? 0 : -this.pitch * 0.8;
      }
    } else {
      const p = R.camera.position;
      this.target.set(p.x, heightAt(p.x, p.z), p.z);
      this.yaw = this.lookYaw;
    }
    S.cam = mode;
    $$('[data-cam]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.cam === mode)));
    $('#stick').hidden = $('#updown').hidden = !(isTouch && mode !== 'overhead');
    if (mode === 'walk') $('#updown').hidden = true;
  },
  /** Drag the ground with the pointer: screen right/down in pixels. */
  pan(dx, dy) {
    const k = this.dist * 0.0018;
    const s = Math.sin(this.yaw), c = Math.cos(this.yaw);
    this.target.x += (-c * dx - s * dy) * k;
    this.target.z += (s * dx - c * dy) * k;
  },
  update(dt) {
    const { width, depth, cellSize, seaLevel } = S.meta;
    const W = width * cellSize, D = depth * cellSize;
    const kb = Keys;
    const mx = (kb.KeyD || kb.ArrowRight ? 1 : 0) - (kb.KeyA || kb.ArrowLeft ? 1 : 0) + this.move.x;
    const mz = (kb.KeyW || kb.ArrowUp ? 1 : 0) - (kb.KeyS || kb.ArrowDown ? 1 : 0) + this.move.z;
    if (S.cam === 'overhead') {
      const sp = this.dist * 1.1 * dt;
      const s = Math.sin(this.yaw), c = Math.cos(this.yaw);
      this.target.x += (c * mx - s * mz) * sp;
      this.target.z += (-s * mx - c * mz) * sp;
      if (kb.KeyQ) this.yaw += 1.4 * dt;
      if (kb.KeyE) this.yaw -= 1.4 * dt;
      if (kb.KeyR) this.dist *= 1 - dt;
      if (kb.KeyF) this.dist *= 1 + dt;
      this.dist = Math.max(40, Math.min(2600, this.dist));
      this.target.x = Math.max(0, Math.min(W, this.target.x));
      this.target.z = Math.max(0, Math.min(D, this.target.z));
      this.target.y += (Math.max(seaLevel, heightAt(this.target.x, this.target.z)) - this.target.y) * Math.min(1, dt * 6);
      const cp = Math.cos(this.pitch);
      R.camera.position.set(this.target.x + Math.sin(this.yaw) * cp * this.dist, this.target.y + Math.sin(this.pitch) * this.dist, this.target.z + Math.cos(this.yaw) * cp * this.dist);
      R.camera.lookAt(this.target);
    } else {
      const walk = S.cam === 'walk';
      const fast = kb.ShiftLeft || kb.ShiftRight;
      const sp = (walk ? (fast ? 30 : 10) : (fast ? 320 : 90)) * dt;
      const fwd = new THREE.Vector3(-Math.sin(this.lookYaw), 0, -Math.cos(this.lookYaw));
      const right = new THREE.Vector3(-fwd.z, 0, fwd.x);
      if (!walk) fwd.y = Math.sin(this.lookPitch) * (mz !== 0 ? 1 : 0);
      this.pos.addScaledVector(fwd, mz * sp).addScaledVector(right, mx * sp);
      const up = (kb.Space ? 1 : 0) - (kb.KeyC ? 1 : 0) + this.move.y;
      if (!walk) this.pos.y += up * sp;
      this.pos.x = Math.max(0, Math.min(W, this.pos.x));
      this.pos.z = Math.max(0, Math.min(D, this.pos.z));
      const ground = Math.max(seaLevel, heightAt(this.pos.x, this.pos.z));
      this.pos.y = walk ? ground + 1.7 : Math.max(ground + 2, this.pos.y);
      R.camera.position.copy(this.pos);
      R.camera.rotation.set(this.lookPitch, this.lookYaw, 0, 'YXZ');
    }
  },
};

const Keys = {};
addEventListener('keydown', (e) => {
  if (e.target.closest('input, textarea, select')) return;
  Keys[e.code] = true;
  if (e.code === 'KeyP') setSpeed(S.status?.speed === 0 ? 1 : 0);
  if (e.code === 'Digit1') setSpeed(1);
  if (e.code === 'Digit2') setSpeed(2);
  if (e.code === 'Digit3') setSpeed(3);
  if (e.code === 'Digit4') setSpeed(4);
  if (e.code === 'KeyV' && Life.mode !== 'life') Cam.setMode({ overhead: 'free', free: 'walk', walk: 'overhead' }[S.cam]);
  if (e.code === 'Escape') { setTool('inspect'); cancelPending(); }
});
addEventListener('keyup', (e) => (Keys[e.code] = false));
addEventListener('blur', () => Object.keys(Keys).forEach((k) => (Keys[k] = false)));

/* ------------------------------------------------------------------ pointer input */

const ptrs = new Map();
let gesture = null; // {kind:'pan'|'rotate'|'look'|'tool'|'pinch', ...}
let brushTimer = 0, flattenHeight = null, lastBrush = null;

function pick(clientX, clientY) {
  const rect = R.renderer.domElement.getBoundingClientRect();
  const v = new THREE.Vector2(((clientX - rect.left) / rect.width) * 2 - 1, -((clientY - rect.top) / rect.height) * 2 + 1);
  R.raycaster.setFromCamera(v, R.camera);
  const hit = R.raycaster.intersectObject(R.terrain)[0];
  if (!hit) return null;
  const cs = S.meta.cellSize;
  const x = Math.floor(hit.point.x / cs), z = Math.floor(hit.point.z / cs);
  if (x < 0 || z < 0 || x >= S.meta.width || z >= S.meta.depth) return null;
  return { x, z, vx: Math.round(hit.point.x / cs), vz: Math.round(hit.point.z / cs), point: hit.point };
}

function initPointer() {
  const el = R.renderer.domElement;
  el.addEventListener('contextmenu', (e) => e.preventDefault());
  el.addEventListener('pointerdown', (e) => {
    el.setPointerCapture(e.pointerId);
    ptrs.set(e.pointerId, { x: e.clientX, y: e.clientY, sx: e.clientX, sy: e.clientY, t: performance.now(), type: e.pointerType, button: e.button });
    if (ptrs.size === 2) { gesture = startPinch(); return; }
    if (ptrs.size > 2) return;
    const look = S.cam !== 'overhead';
    if (Life.on()) { gesture = { kind: Life.cam === 'overhead' ? 'rotate' : 'look', maybeTap: e.button === 0 }; return; }
    if (e.button === 2 || (e.button === 0 && e.altKey)) gesture = { kind: look ? 'look' : 'rotate' };
    else if (e.button === 1) gesture = { kind: 'pan' };
    else if (look && (S.tool === 'inspect' || e.pointerType === 'touch')) gesture = { kind: 'look' };
    else if (S.tool === 'inspect') gesture = { kind: 'pan', maybeTap: true };
    else gesture = { kind: 'tool', maybeTap: true, touch: e.pointerType === 'touch', start: pick(e.clientX, e.clientY) };
    if (gesture.kind === 'tool' && ['raise', 'lower', 'flatten'].includes(S.tool)) startBrush(e);
  });
  el.addEventListener('pointermove', (e) => {
    const p = ptrs.get(e.pointerId);
    if (!p) { hoverAt(e); return; }
    const dx = e.clientX - p.x, dy = e.clientY - p.y;
    p.x = e.clientX; p.y = e.clientY;
    if (Math.hypot(e.clientX - p.sx, e.clientY - p.sy) > 8 && gesture) gesture.maybeTap = false;
    if (!gesture) return;
    if (gesture.kind === 'pinch') return movePinch();
    if (gesture.kind === 'pan') Cam.pan(dx, dy);
    if (gesture.kind === 'rotate') { Cam.yaw -= dx * 0.006; Cam.pitch = Math.max(0.25, Math.min(1.5, Cam.pitch + dy * 0.005)); }
    if (gesture.kind === 'look') { Cam.lookYaw -= dx * 0.005; Cam.lookPitch = Math.max(-1.4, Math.min(1.4, Cam.lookPitch - dy * 0.005)); }
    if (gesture.kind === 'tool' && !gesture.maybeTap) dragTool(e);
  });
  const end = (e) => {
    const p = ptrs.get(e.pointerId);
    ptrs.delete(e.pointerId);
    stopBrush();
    if (!p || !gesture) { if (ptrs.size === 0) gesture = null; return; }
    if (ptrs.size === 0) {
      if (gesture.maybeTap && performance.now() - p.t < 600) tap(e, p.type);
      else if (gesture.kind === 'tool') endDrag(e);
      gesture = null;
    }
  };
  el.addEventListener('pointerup', end);
  el.addEventListener('pointercancel', end);
  el.addEventListener('wheel', (e) => {
    e.preventDefault();
    if (Life.on()) Life.zoom(Math.pow(1.0012, e.deltaY));
    else if (S.cam === 'overhead') Cam.dist = Math.max(40, Math.min(2600, Cam.dist * Math.pow(1.0012, e.deltaY)));
    else Cam.pos.addScaledVector(new THREE.Vector3(-Math.sin(Cam.lookYaw), Math.sin(Cam.lookPitch), -Math.cos(Cam.lookYaw)), -e.deltaY * 0.3);
  }, { passive: false });
}

function startPinch() {
  stopBrush();
  const [a, b] = [...ptrs.values()];
  return { kind: 'pinch', d: Math.hypot(a.x - b.x, a.y - b.y), ang: Math.atan2(b.y - a.y, b.x - a.x), mx: (a.x + b.x) / 2, my: (a.y + b.y) / 2 };
}

function movePinch() {
  const [a, b] = [...ptrs.values()];
  const d = Math.hypot(a.x - b.x, a.y - b.y), ang = Math.atan2(b.y - a.y, b.x - a.x), mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
  if (Life.on()) {
    if (gesture.d > 10) Life.zoom(gesture.d / Math.max(10, d));
  } else if (S.cam === 'overhead') {
    if (gesture.d > 10) Cam.dist = Math.max(40, Math.min(2600, Cam.dist * gesture.d / Math.max(10, d)));
    Cam.yaw += ang - gesture.ang;
    Cam.pan(mx - gesture.mx, my - gesture.my);
  } else {
    Cam.pos.addScaledVector(new THREE.Vector3(-Math.sin(Cam.lookYaw), 0, -Math.cos(Cam.lookYaw)), (d - gesture.d) * 0.6);
  }
  Object.assign(gesture, { d, ang, mx, my });
}

/* Mouse hover: the ghost follows the cursor (touch has no hover; it uses taps and drags). */
function hoverAt(e) {
  if (e.pointerType === 'touch' || Life.mode === 'life') return;
  const h = pick(e.clientX, e.clientY);
  if (!h || (S.hover && h.x === S.hover.x && h.z === S.hover.z)) return;
  S.hover = h;
  if (S.tool === 'build') placeBuild(h, false);
  else if (S.tool === 'road') previewRoad(S.road?.a || h, h, !!S.road?.a);
  else if (S.tool === 'bulldoze') targetBulldoze(h, false);
  else updateGhost(h);
}

function dragTool(e) {
  const c = pick(e.clientX, e.clientY);
  if (!c) return;
  if (brushTimer) { lastBrush = c; return; }
  if (S.tool === 'build') placeBuild(c, false);
  else if (S.tool === 'road' && gesture.start) previewRoad(gesture.start, c, true);
  else if (S.tool === 'bulldoze') targetBulldoze(c, gesture.touch);
}

function endDrag(e) {
  const c = pick(e.clientX, e.clientY);
  if (!c) return;
  if (S.tool === 'build') { placeBuild(c, gesture.touch); if (!gesture.touch) commitBuild(); }
  else if (S.tool === 'road' && gesture.start) {
    previewRoad(gesture.start, c, true);
    if (!gesture.touch) commitRoad();
  }
}

function tap(e, type) {
  const cell = pick(e.clientX, e.clientY);
  if (!cell) return;
  S.hover = cell;
  const touch = type === 'touch';
  if (Life.on()) { Life.tap(cell); return; }
  if (S.tool === 'inspect') { inspectAt(cell); return; }
  if (S.tool === 'build') {
    if (!S.buildDef) { $('#drawer').hidden = false; toast('建てる建物を選んでください'); return; }
    const p = S.place;
    const onGhost = p && ((cell.x >= p.o.x && cell.x < p.o.x + p.def.w && cell.z >= p.o.z && cell.z < p.o.z + p.def.d)
      || (p.src && p.src.x === cell.x && p.src.z === cell.z));
    if (touch) { if (!onGhost) placeBuild(cell, true); return; } // touch: move the ghost, confirm with the bar
    if (onGhost && p.r.ok) { commitBuild(); return; }           // mouse: clicking the (snapped) ghost builds it
    placeBuild(cell, false);
    if (S.place?.r.ok) commitBuild();
    else { placeBuild(cell, true); if (S.place?.r.ok) toast('近くの建設できる場所に移しました。もう一度クリックで建設'); }
  } else if (S.tool === 'road') {
    if (!S.road?.a || S.road.done) { previewRoad(cell, cell, false); toast('終点をタップ（またはドラッグで引く）'); return; }
    previewRoad(S.road.a, cell, true);
    if (!touch) commitRoad();
  } else if (S.tool === 'bulldoze') {
    targetBulldoze(cell, true);
    if (!touch) commitBulldoze();
  }
}

function startBrush(e) {
  const cell = pick(e.clientX, e.clientY);
  if (!cell) return;
  lastBrush = cell;
  flattenHeight = heightAt(cell.vx * S.meta.cellSize, cell.vz * S.meta.cellSize);
  const apply = () => {
    const c = lastBrush;
    if (!c) return;
    const cmd = S.tool === 'flatten' ? { type: 'flatten', x: c.vx, z: c.vz, r: 4, h: flattenHeight } : { type: 'raise', x: c.vx, z: c.vz, r: 3, d: S.tool === 'raise' ? 1.2 : -1.2 };
    exec(cmd, true);
    updateGhost(c);
  };
  apply();
  brushTimer = setInterval(apply, 110);
}
function stopBrush() { if (brushTimer) clearInterval(brushTimer); brushTimer = 0; }

/* ---- building placement ---- */

function originFor(def, cell) { return { x: cell.x - Math.floor((def.w - 1) / 2), z: cell.z - Math.floor((def.d - 1) / 2) }; }

function placeBuild(cell, snap) {
  const def = S.defs[S.buildDef];
  if (!def || !cell) return;
  let o = originFor(def, cell);
  let r = japi('CanPlace', def.id, o.x, o.z);
  if (snap && (!r.ok || r.warn)) {
    const near = snapNear(def, o);
    if (near && (!r.ok || !near.r.warn)) ({ o, r } = near);
  }
  S.place = { def, o, r, src: { x: cell.x, z: cell.z } };
  drawBox(o.x, o.z, def.w, def.d, def.h, r.ok, r.warn);
  if (snap) revealGhost();
  const status = r.ok ? (r.warn ? `⚠ ${r.msg}` : '✓ 建設できます') : snap ? `✕ 近くに空き地がありません（${r.msg}）。町の外側を選ぶか、先に撤去してください` : `✕ ${r.msg}`;
  showBar(`<b>${esc(def.name)}</b> ${yen(def.cost)}<br><small class="${r.ok ? (r.warn ? 'warn' : 'good') : 'bad'}">${esc(status)}</small>`,
    '建設', r.ok, commitBuild, true);
}

/** Nearest buildable origin (searched in C#), preferring spots that touch a road. */
function snapNear(def, o) {
  const f = japi('FindSpot', def.id, o.x, o.z, 20);
  if (!f.found) return null;
  return { o: { x: f.x, z: f.z }, r: japi('CanPlace', def.id, f.x, f.z) };
}

function commitBuild() {
  const p = S.place;
  if (!p || !p.r.ok) { toast(p ? p.r.msg : '場所を選んでください', true); return; }
  const r = exec({ type: 'build', def: p.def.id, x: p.o.x, z: p.o.z });
  if (r.ok) {
    // Offer the next free spot right next to the one just used, so rows of buildings are quick.
    placeBuild({ x: p.o.x + Math.floor((p.def.w - 1) / 2), z: p.o.z + Math.floor((p.def.d - 1) / 2) }, true);
  }
}

/* ---- roads ---- */

function previewRoad(a, b, hasEnd) {
  const r = japi('RoadPreview', a.x, a.z, b.x, b.z);
  S.road = { a, b, r, done: false, hasEnd };
  const cs = S.meta.cellSize;
  R.ghost.visible = false;
  R.pathGhost.material.color.set(r.ok ? 0x63bba3 : 0xe2625a);
  const n = Math.min(400, r.cells.length / 2);
  for (let i = 0; i < n; i++) {
    const x = r.cells[i * 2], z = r.cells[i * 2 + 1];
    _p.set((x + 0.5) * cs, heightAt((x + 0.5) * cs, (z + 0.5) * cs) + 0.8, (z + 0.5) * cs);
    _s.set(cs * 0.9, 1.4, cs * 0.9);
    _m.compose(_p, _q, _s);
    R.pathGhost.setMatrixAt(i, _m);
  }
  R.pathGhost.count = n;
  R.pathGhost.instanceMatrix.needsUpdate = true;
  if (!hasEnd) { showBar('<b>道路</b><br><small>始点を決めました。終点をタップするか、指でなぞって引いてください</small>', '建設', false, commitRoad, false); return; }
  showBar(`<b>道路 ${n}マス</b> ${yen(r.cost)}<br><small class="${r.ok ? 'good' : 'bad'}">${r.ok ? '✓ 建設できます（曲がり角は自動でL字）' : '✕ 建物・水面・急斜面を通ります'}</small>`,
    '建設', r.ok, commitRoad, false);
}

function commitRoad() {
  const rd = S.road;
  if (!rd || !rd.hasEnd) return;
  const r = exec({ type: 'road', x1: rd.a.x, z1: rd.a.z, x2: rd.b.x, z2: rd.b.z });
  if (r.ok) {
    // Continue drawing from the end point, like a pen.
    S.road = { a: rd.b, b: rd.b, done: false, hasEnd: false };
    previewRoad(rd.b, rd.b, false);
  }
}

/* ---- bulldozer ---- */

function targetBulldoze(cell, showConfirm) {
  const info = japi('Inspect', cell.x, cell.z);
  S.bulldoze = { cell, info };
  const b = info?.building;
  if (b) {
    const rec = S.buildingList?.find((x) => x[0] === b.id);
    const d = S.defs[b.def];
    if (rec) drawBox(rec[2], rec[3], d.w, d.d, d.h + 2, true, true);
  } else drawBox(cell.x, cell.z, 1, 1, 3, !!info?.road, true);
  const what = b ? `${b.name} #${b.id}` : info?.road ? '道路 1マス' : null;
  if (showConfirm || isTouch) showBar(what ? `<b>撤去：${esc(what)}</b>${b?.company?.workers ? `<br><small class="warn">従業員 ${b.company.workers}人が職を失います</small>` : b?.residents ? `<br><small class="warn">住民 ${b.residents}人が住まいを失います</small>` : ''}` : '<b>撤去</b><br><small>撤去するものがありません</small>',
    '撤去', !!what, commitBulldoze, false);
}

function commitBulldoze() {
  const t = S.bulldoze;
  if (!t) return;
  const r = exec({ type: 'bulldoze', x: t.cell.x, z: t.cell.z });
  if (r.ok) hideBar();
}

/* ---- ghost & bar ---- */

/** Pans the overhead camera if the ghost ended up off-screen or under a panel. */
function revealGhost() {
  if (S.cam !== 'overhead' || !R.ghost.visible) return;
  const v = R.ghost.position.clone().project(R.camera);
  const sx = (v.x + 1) / 2 * innerWidth, sy = (1 - v.y) / 2 * innerHeight;
  const panel = $('#panel').getBoundingClientRect();
  const blocked = !$('#panel').hidden && !$('#panel').classList.contains('collapsed') && sx > panel.left && sy > panel.top && sy < panel.bottom;
  if (v.z > 1 || sx < 90 || sx > innerWidth - 20 || sy < 80 || sy > innerHeight - 150 || blocked) {
    Cam.target.x = R.ghost.position.x;
    Cam.target.z = R.ghost.position.z;
  }
}

function drawBox(x, z, w, d, h, ok, warn) {
  const cs = S.meta.cellSize;
  R.pathGhost.count = 0;
  const y = heightAt((x + w / 2) * cs, (z + d / 2) * cs);
  R.ghost.position.set((x + w / 2) * cs, y + h / 2, (z + d / 2) * cs);
  R.ghost.scale.set(w * cs, h, d * cs);
  R.ghost.material.color.set(!ok ? 0xe2625a : warn ? 0xe3b341 : 0x63bba3);
  R.ghost.visible = true;
}

function updateGhost(cell) {
  R.ghost.visible = false;
  R.pathGhost.count = 0;
  if (cell && ['raise', 'lower', 'flatten'].includes(S.tool)) drawBox(cell.vx - 3, cell.vz - 3, 6, 6, 1.5, true, S.tool !== 'raise');
}

function showBar(html, okLabel, okEnabled, onOk, withList) {
  S.barAction = onOk;
  $('#confirmText').innerHTML = html;
  $('#confirmOk').textContent = okLabel;
  $('#confirmOk').disabled = !okEnabled;
  $('#confirmList').hidden = !withList;
  $('#confirm').hidden = false;
}
function hideBar() { S.barAction = null; $('#confirm').hidden = true; }
function cancelPending() {
  hideBar();
  S.place = null;
  S.road = null;
  S.bulldoze = null;
  R.ghost.visible = false;
  R.pathGhost.count = 0;
}

function exec(cmd, quiet) {
  const r = japi('Execute', JSON.stringify(cmd));
  if (!quiet || !r.ok) toast(r.msg || (r.ok ? '完了' : '失敗'), !r.ok);
  return r;
}

function inspectAt(cell) {
  S.inspect = japi('Inspect', cell.x, cell.z);
  updateSelection();
  showTab('info');
}

let toastTimer = 0;
function toast(msg, bad) {
  if (!msg) return;
  const t = $('#toast');
  t.textContent = msg;
  t.classList.toggle('bad', !!bad);
  t.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('show'), 2600);
}

/* ------------------------------------------------------------------ UI */

function initUi() {
  initPointer();
  $$('[data-speed]').forEach((b) => b.addEventListener('click', () => setSpeed(+b.dataset.speed)));
  $$('[data-cam]').forEach((b) => b.addEventListener('click', () => Cam.setMode(b.dataset.cam)));
  $$('[data-tool]').forEach((b) => b.addEventListener('click', () => setTool(b.dataset.tool)));
  $$('[data-overlay]').forEach((b) => b.addEventListener('click', () => setOverlay(b.dataset.overlay)));
  $$('[role=tab]').forEach((b) => b.addEventListener('click', () => showTab(b.dataset.tab)));
  $('#collapse').addEventListener('click', () => {
    const c = $('#panel').classList.toggle('collapsed');
    $('#collapse').textContent = c ? '▴' : '▾';
  });
  $('#confirmOk').addEventListener('click', () => S.barAction?.());
  $('#confirmCancel').addEventListener('click', () => { cancelPending(); if (S.tool !== 'inspect') setTool('inspect'); });
  $('#confirmList').addEventListener('click', () => ($('#drawer').hidden = !$('#drawer').hidden));
  initStick();
  Cam.setMode('overhead');
  $$('[data-cam]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.cam === 'overhead')));
  buildDrawer();
  Life.init();
  Why.init();
  initMouseLook();
  $('#gyroBtn').addEventListener('click', () => Gyro.toggle());
}

function initStick() {
  const stick = $('#stick'), knob = stick.querySelector('i');
  const move = (e) => {
    const r = stick.getBoundingClientRect();
    let dx = (e.clientX - r.left - r.width / 2) / (r.width / 2), dy = (e.clientY - r.top - r.height / 2) / (r.height / 2);
    const l = Math.hypot(dx, dy);
    if (l > 1) { dx /= l; dy /= l; }
    Cam.move.x = dx; Cam.move.z = -dy;
    knob.style.transform = `translate(${dx * 36}px, ${dy * 36}px)`;
  };
  stick.addEventListener('pointerdown', (e) => { stick.setPointerCapture(e.pointerId); move(e); });
  stick.addEventListener('pointermove', (e) => { if (stick.hasPointerCapture(e.pointerId)) move(e); });
  const end = () => { Cam.move.x = Cam.move.z = 0; knob.style.transform = ''; };
  stick.addEventListener('pointerup', end);
  stick.addEventListener('pointercancel', end);
  const hold = (id, v) => {
    const b = $(id);
    b.addEventListener('pointerdown', () => (Cam.move.y = v));
    ['pointerup', 'pointercancel', 'pointerleave'].forEach((ev) => b.addEventListener(ev, () => (Cam.move.y = 0)));
  };
  hold('#btnUp', 1); hold('#btnDown', -1);
}

function setSpeed(v) {
  api('SetSpeed', v);
  $$('[data-speed]').forEach((b) => b.setAttribute('aria-pressed', String(+b.dataset.speed === v)));
}

function setTool(tool) {
  S.tool = tool;
  cancelPending();
  $$('[data-tool]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.tool === tool)));
  $('#drawer').hidden = tool !== 'build' || !!S.buildDef;
  if (tool !== 'inspect') { S.inspect = null; R.select.visible = false; }
  // Building needs the map: fold the notebook away while a build tool is active, bring it back for 調査.
  $('#panel').classList.toggle('collapsed', tool !== 'inspect');
  $('#collapse').textContent = tool !== 'inspect' ? '▴' : '▾';
  if (tool === 'build' && S.buildDef) placeBuild(centerCell(), true);
  else if (tool === 'build') toast('建てる建物を選んでください');
  else if (tool === 'road') showBar('<b>道路</b><br><small>始点から終点まで指（マウス）でなぞるか、2か所をタップ</small>', '建設', false, commitRoad, false);
  else if (tool === 'bulldoze') showBar('<b>撤去</b><br><small>撤去したい建物・道路をタップ</small>', '撤去', false, commitBulldoze, false);
  else updateGhost(S.hover);
}

/** The map cell under the middle of the screen (where new ghosts appear). */
function centerCell() {
  const panel = $('#panel');
  const right = panel && !panel.hidden && innerWidth > 760 ? panel.getBoundingClientRect().width : 0;
  return pick((innerWidth - right) / 2, innerHeight / 2) || S.hover;
}

function setOverlay(kind) {
  S.overlay = kind;
  $$('[data-overlay]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.overlay === kind)));
  recolorTerrain();
  refreshRoads();
}

function buildDrawer() {
  const research = japi('GetResearch');
  const done = new Set(research.techs.filter((t) => t.done).map((t) => t.id));
  const techName = Object.fromEntries(research.techs.map((t) => [t.id, t.name]));
  const groups = {};
  S.meta.buildings.forEach((b) => (groups[b.category] ||= []).push(b));
  $('#drawer').innerHTML = Object.entries(groups).map(([cat, list]) => `<h3>${CAT_NAMES[cat] || cat}</h3>` + list.map((b) => {
    const locked = b.requiresTech && !done.has(b.requiresTech);
    const sub = locked ? `要研究：${techName[b.requiresTech]}` : [b.housing ? `住宅 ${b.housing}人` : '', b.jobs ? `雇用 ${b.jobs}人` : '', `${b.w}×${b.d}`].filter(Boolean).join('・');
    return `<button class="bitem" data-def="${b.id}" aria-pressed="${S.buildDef === b.id}" ${locked ? 'disabled' : ''}>
      <span class="swatch" style="background:#${b.color.toString(16).padStart(6, '0')}"></span>
      <span>${esc(b.name)}<small>${esc(sub)}</small></span><span class="cost">${yen(b.cost)}</span></button>`;
  }).join('')).join('');
  $$('#drawer .bitem').forEach((el) => el.addEventListener('click', () => {
    S.buildDef = el.dataset.def;
    $$('#drawer .bitem').forEach((x) => x.setAttribute('aria-pressed', String(x === el)));
    $('#drawer').hidden = true; // get the list out of the way; the bar's 一覧 button brings it back
    placeBuild(centerCell(), true);
    toast(isTouch ? 'タップ・ドラッグで場所を動かし、「建設」で確定' : 'クリックで建設（何度でも続けて置けます）');
  }));
}

function showTab(tab) {
  S.tab = tab;
  $$('[role=tab]').forEach((b) => b.setAttribute('aria-selected', String(b.dataset.tab === tab)));
  $('#panel').classList.remove('collapsed');
  $('#collapse').textContent = '▾';
  renderPanel();
}

function renderPanel() {
  const body = $('#panelBody');
  const fn = { info: renderInfo, analysis: renderAnalysis, charts: renderCharts, policy: renderPolicy, research: renderResearch,
    branches: renderBranches, save: renderSave, disaster: renderDisaster, news: renderNews, about: renderAbout, life: (b) => Life.renderPanel(b), why: (b) => Why.render(b) }[S.tab];
  const scroll = body.scrollTop;
  fn(body);
  body.scrollTop = scroll;
}

function dl(rows) {
  return `<dl class="kv">${rows.filter(Boolean).map(([k, v]) => `<dt>${esc(k)}</dt><dd>${v}</dd>`).join('')}</dl>`;
}

function renderInfo(el) {
  const s = S.status;
  if (S.inspect && S.tool === 'inspect') {
    const c = S.inspect;
    const live = japi('Inspect', c.x, c.z);
    S.inspect = live;
    const b = live.building;
    let html = '';
    if (b) {
      const co = b.company;
      html += `<h2>${esc(b.name)} <span class="pill">#${b.id}</span> ${b.condition < 1 ? `<span class="pill bad">損傷 ${pct(1 - b.condition)}</span>` : ''}</h2>`;
      html += dl([['区分', CAT_NAMES[b.category]], ['状態', b.condition >= 1 ? '良好' : `${pct(b.condition)}（復旧待ち）`], ['道路接続', b.roadAccess ? 'あり' : '<span style="color:var(--bad)">なし</span>']]);
      if (b.housing) {
        html += `<h4>住民</h4>` + dl([['居住 / 定員', `${b.residents} / ${b.housing}`], ['就業者', b.employed], ['子ども・退職者', `${b.children}・${b.retired}`],
          ['平均満足度', pct(b.happiness)], ['平均通勤', `${b.commute.toFixed(1)}分`], ['平均貯蓄', yen(b.savings)]]);
        if (b.sample) {
          const p = b.sample;
          html += `<h4>住民の一人（#${p.id}）</h4>` + dl([['年齢', `${p.age}歳`], ['学歴', ['基礎', '中等', '高等'][p.education]], ['職場', esc(p.job)],
            ['昨日の手取り', yen(p.income)], ['貯蓄', yen(p.savings)], ['満足度', pct(p.happiness)], ['通勤', `${p.commute.toFixed(1)}分`],
            ['食料の充足', pct(p.food)], ['工業製品の充足', pct(p.goods)]]);
        }
      }
      if (co) {
        if (co.contractor) html += `<p class="empty" style="font-size:12.5px">町の建設・維持管理・災害復旧を請け負います。受注残が30日分を超える仕事は市外の業者に流れます。資材（鋼材・木材）を地元や輸入で調達します。</p>`;
        const status = co.closed ? '<span class="pill bad">倒産・休業中</span>' : co.broken ? `<span class="pill accent">設備故障（あと${co.broken}日）</span>` : co.shortage ? '<span class="pill accent">原材料不足</span>' : '<span class="pill good">稼働中</span>';
        html += `<h4>${co.public ? '公共施設の運営' : '企業'}</h4><p>${status}</p>` + dl([
          ['従業員 / 目標 / 上限', `${co.workers} / ${co.target} / ${co.capacity}`], ['賃金（日）', yen(co.wage)],
          co.public ? null : ['資金', yen(co.cash)], co.public ? null : ['利益（平均・日）', yen(co.profitAvg)],
          co.outputCapacity ? ['稼働率', pct(co.output / Math.max(1e-9, co.outputCapacity))] : null,
          co.contractor ? ['受注残（工事）', `${yen(co.backlog)}（${co.workPerDay ? (co.backlog / co.workPerDay).toFixed(1) : '—'}日分）`] : null,
          co.contractor ? ['1日の施工能力', yen(co.workPerDay)] : null,
          ['電力使用', `${Math.round(co.energy).toLocaleString()} kWh`],
          co.techs.length ? ['導入技術', esc(co.techs.join('、'))] : null]);
        const inv = Object.entries(co.inventory);
        if (inv.length) html += `<h4>在庫</h4>` + dl(inv.map(([k, v]) => [RES_NAMES[k] || k, v.toLocaleString()]));
      }
    } else if (live.road) {
      html += `<h2>道路</h2>` + dl([['交通量（台/日）', Math.round(live.road.traffic).toLocaleString()], ['容量', live.road.capacity.toLocaleString()],
        ['混雑度', pct(live.road.traffic / live.road.capacity)], ['状態', live.road.damaged ? '<span style="color:var(--bad)">被災・通行止め</span>' : '通行可']]);
    } else html += `<h2>空き地</h2>`;
    html += `<h4>この場所</h4>` + dl([['座標', `${live.x}, ${live.z}`], ['標高', `${live.height.toFixed(1)} m`], ['大気汚染', live.pollution.toFixed(1)], ['森林', live.forest ? `${Math.round(live.forest / 2.55)}%` : 'なし']]);
    el.innerHTML = html;
    return;
  }
  el.innerHTML = `<h2>${esc(s?.clock || '')}</h2>
    <p class="empty">「調査」ツールで建物や道路をタップすると、住民・企業・在庫・交通量などの詳細がここに表示されます。</p>
    <h4>操作</h4>
    <ul class="plain">
      <li><b>移動</b>：ドラッグ（調査ツール時）／WASD・矢印キー</li>
      <li><b>回転・傾き</b>：右ドラッグ／2本指でひねる／Q・E</li>
      <li><b>拡大縮小</b>：ホイール／ピンチ</li>
      <li><b>カメラ切替</b>：上部の「俯瞰・自由・一人称」／Vキー</li>
      <li><b>時間</b>：P で停止、1・2・3 で速度</li>
      <li><b>地形編集</b>：隆起・掘削・平坦化を選んで押し続ける</li>
    </ul>`;
}

function renderAnalysis(el) {
  const a = japi('Analysis');
  const f = a.forecast, s = a.summary;
  const trend = (k, fmt) => `${fmt(f[k][0])} → <span style="color:${f[k][1] >= f[k][0] ? 'var(--good)' : 'var(--warn)'}">${fmt(f[k][1])}</span>`;
  el.innerHTML = `<h2>都市の診断</h2>` + a.findings.map((x) => `<div class="finding ${x.severity}"><i></i><div><b>${esc(x.title)}</b>${x.detail ? `<p>${esc(x.detail)}</p>` : ''}</div></div>`).join('') +
    `<h4>90日後の予測（直近の傾向から）</h4>` + dl([['人口', trend('population', (v) => Math.round(v).toLocaleString() + '人')], ['市の資金', trend('treasury', yen)],
      ['失業率', trend('unemployment', (v) => v.toFixed(1) + '%')], ['満足度', trend('happiness', (v) => v.toFixed(0) + '%')]]) +
    `<h4>概況</h4>` + dl([['労働力 / 就業者', `${s.labour} / ${s.employed}`], ['求人 / 欠員', `${s.jobs} / ${s.vacancies}`], ['営業中 / 休業中の企業', `${s.companies} / ${s.closed}`],
      ['平均賃金（日）', yen(s.avgWage)], ['平均通勤', `${s.commute.toFixed(1)}分`], ['電力 供給 / 需要', `${Math.round(s.energySupply).toLocaleString()} / ${Math.round(s.energyDemand).toLocaleString()}`],
      ['交通量 / 渋滞マス', `${Math.round(s.traffic).toLocaleString()} / ${s.congested}`], ['災害への強さ', pct(s.resilience)], ['温暖化', `+${s.warming.toFixed(3)}℃`]]) +
    `<h4>市場（昨日）</h4>` + dl(a.market.map((m) => [m.name, `${Math.round(m.price).toLocaleString()}円 <span style="color:var(--muted)">生産${Math.round(m.produced).toLocaleString()}・輸入${Math.round(m.imported).toLocaleString()}</span>`])) +
    `<h4>お金の流れ（昨日）</h4>` + dl(Object.entries(a.flows).filter(([, v]) => v > 0).sort((x, y) => y[1] - x[1]).map(([k, v]) => [FLOW_NAMES[k] || k, yen(v)])) +
    (a.problems.length ? `<h4>整合性チェック</h4><p style="color:var(--bad)">${a.problems.map(esc).join('<br>')}</p>` : `<h4>整合性チェック</h4><p class="empty">お金の保存・雇用と住居の対応・数値の健全性：問題なし</p>`);
}
const FLOW_NAMES = { Wages: '賃金', B2BSales: '企業間取引', Retail: '小売', Freight: '運賃', Imports: '輸入', Exports: '輸出', IncomeTax: '所得税', CorporateTax: '法人税',
  Subsidy: '補助金', Maintenance: '維持費', Construction: '建設費', Energy: '電気代', Fuel: '燃料', Pension: '年金', Migration: '転入・転出', Inheritance: '相続',
  Repair: '修理', TechAdoption: '技術導入', Investment: '投資', Divestment: '撤退', Preparedness: '防災投資', Dividends: '配当', Benefit: '失業手当' };

function renderCharts(el) {
  el.innerHTML = `<h2>推移グラフ</h2><div class="series-pick">${Object.entries(SERIES).map(([k, [n]]) => `<button data-k="${k}" aria-pressed="${S.chartKeys.includes(k)}">${n}</button>`).join('')}</div>
    <div class="row" style="margin-bottom:6px"><label for="range">期間</label><select id="range"><option value="90">90日</option><option value="360">1年</option><option value="1800">5年</option><option value="100000">全期間</option></select></div>
    <div id="charts"></div>`;
  $('#range').value = S.chartRange || '360';
  $('#range').addEventListener('change', (e) => { S.chartRange = e.target.value; drawCharts(); });
  $$('.series-pick button').forEach((b) => b.addEventListener('click', () => {
    const k = b.dataset.k;
    S.chartKeys = S.chartKeys.includes(k) ? S.chartKeys.filter((x) => x !== k) : [...S.chartKeys, k].slice(-4);
    renderCharts(el);
  }));
  drawCharts();
}

function drawCharts() {
  const host = $('#charts');
  if (!host) return;
  if (!S.chartKeys.length) { host.innerHTML = '<p class="empty">表示する項目を選んでください。</p>'; return; }
  const data = japi('Series', S.chartKeys.join(','), +(S.chartRange || 360));
  host.innerHTML = S.chartKeys.map((k, i) => `<h4>${SERIES[k][0]}</h4><canvas class="chart" id="c${i}"></canvas>`).join('');
  S.chartKeys.forEach((k, i) => lineChart($('#c' + i), [{ name: SERIES[k][0], values: data.series[k], color: LINE_COLORS[i] }], data.startDay, SERIES[k][1]));
}

function lineChart(canvas, lines, startDay, unit, marker) {
  const dpr = devicePixelRatio || 1, w = canvas.clientWidth || 320, h = canvas.clientHeight || 170;
  canvas.width = w * dpr; canvas.height = h * dpr;
  const g = canvas.getContext('2d');
  g.scale(dpr, dpr);
  const css = getComputedStyle(document.documentElement);
  const muted = css.getPropertyValue('--muted').trim(), line = css.getPropertyValue('--line').trim();
  const L = 54, Rr = 8, T = 8, B = 20;
  const all = lines.flatMap((l) => l.values).filter((v) => Number.isFinite(v));
  if (!all.length) { g.fillStyle = muted; g.fillText('まだデータがありません（1日経つと記録されます）', 8, 20); return; }
  let lo = Math.min(...all), hi = Math.max(...all);
  if (lo === hi) { lo -= 1; hi += 1; }
  const pad = (hi - lo) * 0.08; lo -= pad; hi += pad;
  if (Math.min(...all) >= 0 && lo < 0) lo = 0;
  const n = Math.max(...lines.map((l) => l.values.length));
  const X = (i) => L + (n <= 1 ? 0 : (i / (n - 1)) * (w - L - Rr)), Y = (v) => T + (1 - (v - lo) / (hi - lo)) * (h - T - B);
  g.font = '10.5px "IBM Plex Mono", monospace';
  g.textBaseline = 'middle';
  for (let k = 0; k <= 3; k++) {
    const v = lo + ((hi - lo) * k) / 3, y = Y(v);
    g.strokeStyle = line; g.beginPath(); g.moveTo(L, y); g.lineTo(w - Rr, y); g.stroke();
    g.fillStyle = muted; g.textAlign = 'right';
    g.fillText(fmtAxis(v, unit), L - 6, y);
  }
  g.textAlign = 'left'; g.textBaseline = 'top';
  g.fillText(`${startDay}日目`, L, h - B + 5);
  g.textAlign = 'right';
  g.fillText(`${startDay + n - 1}日目`, w - Rr, h - B + 5);
  if (marker != null && marker >= 0 && marker < n) {
    g.strokeStyle = muted; g.setLineDash([3, 3]); g.beginPath(); g.moveTo(X(marker), T); g.lineTo(X(marker), h - B); g.stroke(); g.setLineDash([]);
  }
  lines.forEach((l) => {
    g.strokeStyle = l.color; g.lineWidth = 1.8; g.beginPath();
    l.values.forEach((v, i) => (i ? g.lineTo(X(i), Y(v)) : g.moveTo(X(i), Y(v))));
    g.stroke();
    const last = l.values.length - 1;
    if (last >= 0) { g.fillStyle = l.color; g.beginPath(); g.arc(X(last), Y(l.values[last]), 3, 0, 7); g.fill(); }
  });
  g.lineWidth = 1;
}

function fmtAxis(v, unit) {
  if (unit === '円') return yen(v).replace('円', '');
  const a = Math.abs(v);
  return a >= 1e6 ? (v / 1e6).toFixed(1) + 'M' : a >= 1e4 ? (v / 1e3).toFixed(0) + 'k' : a >= 100 ? v.toFixed(0) : v.toFixed(1);
}

function renderPolicy(el) {
  const p = japi('GetPolicy');
  el.innerHTML = `<h2>政策</h2>` + Object.entries(POLICY).map(([k, d]) => `<div class="policy">
    <label for="p_${k}">${d.label}<output id="o_${k}">${d.fmt(p[k])}</output></label>
    <input type="range" id="p_${k}" min="${d.min}" max="${d.max}" step="${d.step}" value="${p[k]}">
    <small>${d.help}</small></div>`).join('');
  Object.entries(POLICY).forEach(([k, d]) => {
    const input = $('#p_' + k);
    input.addEventListener('input', () => ($('#o_' + k).textContent = d.fmt(+input.value)));
    input.addEventListener('change', () => exec({ type: 'policy', key: k, value: +input.value }));
  });
}

function renderResearch(el) {
  const r = japi('GetResearch');
  const name = Object.fromEntries(r.techs.map((t) => [t.id, t.name]));
  el.innerHTML = `<h2>研究</h2><p class="empty">研究所の職員が1日 <span class="num">${r.points.toFixed(1)}</span> ポイントを生み出しています。完了した技術は、市全体に効くものと、企業が費用を払って導入するものがあります。</p>` +
    r.techs.map((t) => {
      const state = t.done ? `<span class="pill good">完了・導入 ${t.adopters}社</span>` : t.current ? `<span class="pill accent">研究中 ${Math.round((t.progress / t.cost) * 100)}%</span>` : t.available ? '' : '<span class="pill">前提が未完了</span>';
      return `<div class="tech ${t.done ? 'done' : ''} ${t.current ? 'current' : ''}"><header><b>${esc(t.name)}</b>${state}</header>
        <small>${esc(t.description)}</small>
        <small>費用 <span class="num">${t.cost.toLocaleString()}</span> pt${t.adoptionCost ? `・企業の導入費 ${yen(t.adoptionCost)}` : '・市全体に即時適用'}${t.prereqs.length ? `・前提：${t.prereqs.map((x) => name[x]).join('、')}` : ''}</small>
        ${!t.done && t.available && !t.current ? `<div><button class="btn" data-tech="${t.id}">これを研究する</button></div>` : ''}</div>`;
    }).join('');
  $$('[data-tech]').forEach((b) => b.addEventListener('click', () => { exec({ type: 'research', tech: b.dataset.tech }); renderResearch(el); }));
}

function renderBranches(el) {
  const list = japi('Branches');
  el.innerHTML = `<h2>歴史分岐</h2><p class="empty">今の世界を丸ごと複製して、別の未来を進められます。各分岐は独立した時間を持ち、表示中の分岐だけが進みます。</p>
    <div class="row"><input id="forkName" placeholder="分岐の名前（例：増税案）" style="flex:1;min-width:0;background:var(--ground);border:1px solid var(--line);border-radius:8px;padding:8px">
    <button class="btn primary" id="fork">ここから分岐</button></div>
    <h4>分岐の一覧</h4>` + list.map((b) => `<div class="tech ${b.active ? 'current' : ''}"><header><b>${esc(b.name)}</b>${b.active ? '<span class="pill accent">表示中</span>' : ''}</header>
      <small>${esc(b.clock)}・人口 ${b.population.toLocaleString()}・資金 ${yen(b.cash)}${b.parent ? `・${b.forkDay}日目に分岐` : ''}</small>
      ${b.active ? '' : `<div class="row"><button class="btn" data-sw="${b.id}">表示する</button><button class="btn" data-why-branch="${b.id}">違いの原因</button><button class="btn" data-adv="${b.id}">裏で30日進める</button><button class="btn danger" data-rm="${b.id}">削除</button></div>`}</div>`).join('') +
    `<h4>分岐の比較</h4><select id="cmpKey">${Object.entries(SERIES).map(([k, [n]]) => `<option value="${k}">${n}</option>`).join('')}</select>
     <canvas class="chart" id="cmpChart" style="margin-top:8px"></canvas><div class="legend" id="cmpLegend"></div>`;
  $('#cmpKey').value = S.compareKey;
  $('#cmpKey').addEventListener('change', (e) => { S.compareKey = e.target.value; drawCompare(list); });
  $('#fork').addEventListener('click', () => {
    const r = japi('Fork', $('#forkName').value.trim());
    toast(r.ok ? '分岐を作成しました' : r.msg, !r.ok);
    renderBranches(el);
  });
  $$('[data-sw]').forEach((b) => b.addEventListener('click', () => { api('SwitchBranch', +b.dataset.sw); S.versions = [-1, -1, -1, -1]; S.inspect = null; setSpeed(0); toast('分岐を切り替えました（停止中）'); renderBranches(el); }));
  $$('[data-why-branch]').forEach((b) => b.addEventListener('click', () => Why.open(Why.metric, +b.dataset.whyBranch)));
  $$('[data-adv]').forEach((b) => b.addEventListener('click', () => { api('AdvanceBranch', +b.dataset.adv, 30); renderBranches(el); }));
  $$('[data-rm]').forEach((b) => b.addEventListener('click', () => { const r = japi('RemoveBranch', +b.dataset.rm); toast(r.msg, !r.ok); renderBranches(el); }));
  drawCompare(list);
}

function drawCompare(list) {
  const data = japi('Compare', S.compareKey);
  const lines = list.map((b, i) => ({ name: b.name, values: data[b.id] || [], color: LINE_COLORS[i % LINE_COLORS.length] }));
  const fork = list.filter((b) => b.parent).map((b) => b.forkDay - 1)[0];
  lineChart($('#cmpChart'), lines, 1, SERIES[S.compareKey][1], fork);
  $('#cmpLegend').innerHTML = lines.map((l) => `<span><i style="background:${l.color}"></i>${esc(l.name)}</span>`).join('');
}

function storage() { try { return window.localStorage; } catch { return null; } }

async function pack(text) {
  if (!window.CompressionStream) return 'raw:' + text;
  const stream = new Blob([text]).stream().pipeThrough(new CompressionStream('gzip'));
  const buf = new Uint8Array(await new Response(stream).arrayBuffer());
  let bin = '';
  for (let i = 0; i < buf.length; i += 0x8000) bin += String.fromCharCode.apply(null, buf.subarray(i, i + 0x8000));
  return 'gz:' + btoa(bin);
}
async function unpack(s) {
  s = s.trim();
  if (s.startsWith('raw:')) return s.slice(4);
  if (s.startsWith('{')) return s;
  if (!s.startsWith('gz:')) throw new Error('形式が違います');
  const stream = new Blob([b64Bytes(s.slice(3))]).stream().pipeThrough(new DecompressionStream('gzip'));
  return await new Response(stream).text();
}

function renderSave(el) {
  const st = storage();
  const slots = [1, 2, 3].map((i) => {
    let info = null;
    try { info = JSON.parse(st?.getItem('worldsim.meta.' + i) || 'null'); } catch { }
    return { i, info };
  });
  el.innerHTML = `<h2>保存とロード</h2><p class="empty">すべての分岐をまとめて保存します。保存先はこの端末のブラウザ内です。別の端末へ移すときは「書き出し」の文字列を使ってください。</p>` +
    slots.map(({ i, info }) => `<div class="tech"><header><b>スロット ${i}</b>${info ? `<span class="pill">${esc(info.size)}</span>` : ''}</header>
      <small>${info ? `${esc(info.clock)}・人口 ${info.population.toLocaleString()}・分岐 ${info.branches}本・保存日時 ${esc(info.when)}` : '空き'}</small>
      <div class="row"><button class="btn primary" data-save="${i}">保存</button>${info ? `<button class="btn" data-load="${i}">ロード</button>` : ''}</div></div>`).join('') +
    `<h4>書き出し・読み込み</h4><div class="row"><button class="btn" id="export">書き出す</button><button class="btn" id="copy">コピー</button><button class="btn" id="import">貼り付けた文字列を読み込む</button></div>
    <textarea id="saveText" placeholder="書き出した文字列がここに表示されます。読み込むときはここに貼り付けてください。" style="margin-top:8px"></textarea>
    <h4>新しい世界</h4><div class="row"><label for="seed">シード</label><input id="seed" type="number" value="${Math.floor(Math.random() * 1000) + 2}" style="width:7em;background:var(--ground);border:1px solid var(--line);border-radius:8px;padding:6px">
    <button class="btn danger" id="newWorld">新しく生成（今の世界は破棄）</button></div>`;
  $$('[data-save]').forEach((b) => b.addEventListener('click', async () => {
    const i = b.dataset.save;
    try {
      const data = await pack(api('SaveAll'));
      const s = S.status;
      st.setItem('worldsim.slot.' + i, data);
      st.setItem('worldsim.meta.' + i, JSON.stringify({ clock: s.clock, population: s.population, branches: japi('Branches').length, when: new Date().toLocaleString('ja-JP'), size: (data.length / 1024).toFixed(0) + 'KB' }));
      toast(`スロット${i}に保存しました`);
    } catch (e) { toast('保存できませんでした（ブラウザの保存領域が使えないか容量不足です）。書き出しをお使いください。', true); }
    renderSave(el);
  }));
  $$('[data-load]').forEach((b) => b.addEventListener('click', async () => {
    try { loadText(await unpack(st.getItem('worldsim.slot.' + b.dataset.load))); } catch (e) { toast('ロードに失敗しました：' + e.message, true); }
  }));
  $('#export').addEventListener('click', async () => { $('#saveText').value = await pack(api('SaveAll')); toast('書き出しました。コピーして保管してください'); });
  $('#copy').addEventListener('click', async () => {
    const t = $('#saveText');
    try { await navigator.clipboard.writeText(t.value); toast('コピーしました'); } catch { t.select(); toast('文字列を選択しました。コピーしてください'); }
  });
  $('#import').addEventListener('click', async () => { try { loadText(await unpack($('#saveText').value)); } catch (e) { toast('読み込めませんでした：' + e.message, true); } });
  $('#newWorld').addEventListener('click', () => {
    applyMeta(japi('Init', +$('#seed').value || 1));
    Cam.reset(); setSpeed(1); buildDrawer(); toast('新しい世界を生成しました'); showTab('info');
  });
}

function loadText(text) {
  const r = japi('LoadAll', text);
  if (!r.ok) { toast('ロードに失敗しました：' + r.msg, true); return; }
  applyMeta(r.meta);
  buildDrawer();
  setSpeed(0);
  toast('ロードしました（停止中）');
  showTab('info');
}

function renderDisaster(el) {
  el.innerHTML = `<h2>災害シナリオ</h2><p class="empty">大雨が続くと洪水、まれに地震が自然に起きます。ここでは災害を起こして、被害と復旧を確かめられます。防災投資と耐震設計で被害は減り、消防・復旧センターと復旧予算で復旧が速くなります。分岐と組み合わせると対策の効果を比較できます。</p>
    <div class="policy"><label for="mag">地震の規模 <output id="magOut">M7.0</output></label><input type="range" id="mag" min="5" max="8.5" step="0.1" value="7"></div>
    <div class="row"><button class="btn danger" id="quake">地震を起こす（震源：画面の中心）</button></div>
    <div class="policy" style="margin-top:14px"><label for="flood">洪水の水位 <output id="floodOut">海面+2.5m</output></label><input type="range" id="flood" min="0.5" max="6" step="0.5" value="2.5"></div>
    <div class="row"><button class="btn danger" id="floodBtn">洪水を起こす</button></div>`;
  $('#mag').addEventListener('input', (e) => ($('#magOut').textContent = 'M' + (+e.target.value).toFixed(1)));
  $('#flood').addEventListener('input', (e) => ($('#floodOut').textContent = `海面+${e.target.value}m`));
  $('#quake').addEventListener('click', () => {
    const cs = S.meta.cellSize, t = S.cam === 'overhead' ? Cam.target : R.camera.position;
    exec({ type: 'disaster', kind: 'earthquake', x: Math.floor(t.x / cs), z: Math.floor(t.z / cs), mag: +$('#mag').value });
  });
  $('#floodBtn').addEventListener('click', () => exec({ type: 'disaster', kind: 'flood', x: 0, z: 0, mag: +$('#flood').value }));
}

function renderNews(el) {
  const ev = japi('GetEvents', 0).events.slice(-120).reverse();
  el.innerHTML = `<h2>ニュース</h2>` + (ev.length ? ev.map(([t, k, m]) => `<div class="news"><span class="t">${Math.floor(t / 24) + 1}日</span><span class="k">${esc(k)}</span>${esc(m)}</div>`).join('') : '<p class="empty">まだ出来事はありません。</p>');
}

function renderAbout(el) {
  el.innerHTML = `<h2>この試作版について</h2>
    <p class="empty">シミュレーション本体は C# で書かれ、Unity 版と同じコードがブラウザの WebAssembly で動いています。1マスは10m、1 tick はゲーム内1時間です。</p>
    <h4>動作する機能</h4><ul class="plain">
    <li><b>世界と地形</b>：自動生成、隆起・掘削・平坦化、森林の伐採・汚染被害・再生</li>
    <li><b>建築と交通</b>：建物の建設・撤去、道路、渋滞と迂回、外部との輸出入</li>
    <li><b>住民</b>：年齢・学歴・住居・職場・通勤・買い物・転職・転居・出生・死亡・移住</li>
    <li><b>企業と製造</b>：原料→中間財→製品の3段階、在庫、故障、倒産と再開、配当</li>
    <li><b>経済と政治</b>：価格・賃金・税・補助・年金・失業手当・電気料金</li>
    <li><b>技術・環境・災害</b>：研究と企業の導入、電力、大気汚染、天候、洪水・地震と復旧</li>
    <li><b>分析と歴史</b>：グラフ、原因診断、90日予測、保存・ロード、歴史分岐と比較</li>
    <li><b>人生体験</b>：住民ひとりとして歩き、建物に入り、働く・買う・食べる・眠る・就職・転居・会話（第1段階）。ジャイロ・マウスで視点操作</li></ul>
    <h4>まだ無い機能（今後）</h4><ul class="plain">
    <li>建物内部の自由な設計・モジュール式建築・自由形状の建築</li><li>鉄道・港・橋・トンネル、複数の都市や国家</li>
    <li>工場内部の設備配置、世帯・家族と人生の節目（結婚・出産など）</li><li>生態系（動物）・進化、外交・安全保障</li>
    <li>AIによる改善提案、マルチプレイ、ユーザー作成コンテンツの読み込み画面</li></ul>`;
}

/* ------------------------------------------------------------------ frame loop */

let lastT = performance.now();
function frame(t) {
  const dt = Math.min(0.1, (t - lastT) / 1000);
  lastT = t;
  let st;
  try { st = japi('Tick', dt); } catch (e) { console.error(e); requestAnimationFrame(frame); return; }
  S.status = st;
  const v = st.versions; // [Terrain, Buildings, Roads, Vegetation]
  if (v[0] !== S.versions[0]) { refreshTerrain(); refreshBuildings(); refreshRoads(); refreshTrees(); }
  else {
    if (v[1] !== S.versions[1]) refreshBuildings();
    if (v[2] !== S.versions[2]) refreshRoads();
    if (v[3] !== S.versions[3]) refreshTrees();
  }
  S.versions = v.slice();
  if (st.days !== S.days) {
    S.days = st.days;
    if (S.overlay !== 'none') { recolorTerrain(); refreshRoads(); }
    if (t - S.lastPanel > 900 && ['analysis', 'charts', 'branches', 'research', 'why'].includes(S.tab)) { S.lastPanel = t; renderPanel(); }
  }
  const eventKey = st.events + ':' + st.lastEventTick;
  if (eventKey !== S.eventKey) {
    const ev = japi('GetEvents', st.events - 1).events.pop();
    if (ev && S.eventKey && /災害|技術|倒産/.test(ev[1])) toast(`${ev[1]}：${ev[2]}`, ev[1] === '災害' || ev[1] === '倒産');
    if (ev && ev[1] === '技術') buildDrawer();
    S.eventKey = eventKey;
  }
  if (t - S.lastAgents > 120) { S.lastAgents = t; refreshAgents(); }
  if (t - S.lastHud > 250) { S.lastHud = t; updateHud(st); if (S.tab === 'info' && S.inspect && t - S.lastPanel > 1000) { S.lastPanel = t; renderPanel(); } }
  // Day/night: sky, fog and light follow the in-game hour.
  const hour = st.hour + st.progress;
  // Night is a bright blue evening: the change of time is visible, but the map stays easy to read.
  const daylight = Math.max(0, Math.sin(((hour - 5.5) / 13) * Math.PI));
  R.sky.copy(NIGHT).lerp(DAY, daylight);
  R.scene.background = R.sky;
  R.scene.fog.color.copy(R.sky);
  R.hemi.intensity = 0.72 + 0.08 * daylight;
  R.hemi.color.setRGB(0.86 + 0.06 * daylight, 0.9 + 0.04 * daylight, 1.0);
  R.sun.intensity = 0.6 + 0.25 * daylight;
  R.flood.visible = st.floodLevel > 0;
  R.flood.position.y = st.floodLevel;
  // Life mode drives the camera itself (and may draw a building interior instead of the map).
  if (!Life.frame(dt, st, t)) Cam.update(dt);
  R.renderer.render(Life.scene() || R.scene, R.camera);
  requestAnimationFrame(frame);
}

function updateHud(st) {
  $('#hDate').textContent = st.clock;
  $('#hBranch').textContent = st.branch + (st.research ? `・研究 ${st.research.name} ${Math.round((st.research.progress / st.research.cost) * 100)}%` : '');
  $('#hCash').textContent = yen(st.cash);
  $('#hCash').style.color = st.cash < 0 ? 'var(--bad)' : '';
  $('#hPop').textContent = st.population.toLocaleString() + '人';
  $('#hUnemp').textContent = pct(st.unemployment);
  $('#hUnemp').style.color = st.unemployment > 0.15 ? 'var(--bad)' : st.unemployment > 0.08 ? 'var(--warn)' : '';
  $('#hHappy').textContent = pct(st.happiness);
  $('#hEnergy').textContent = pct(st.energy);
  $('#hEnergy').style.color = st.energy < 0.999 ? 'var(--bad)' : '';
  $('#hWeather').textContent = `${st.rain > 0.5 ? '雨' : '晴'} ${st.temperature.toFixed(0)}℃`;
  $$('[data-speed]').forEach((b) => b.setAttribute('aria-pressed', String(+b.dataset.speed === st.speed)));
}

boot();
