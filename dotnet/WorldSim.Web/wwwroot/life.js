'use strict';
/* Life mode (人生体験モード) for the web client. The mode decides what the player may do (the C# side
   switches the actor to a resident); the camera is a separate, purely visual choice. Every action is a
   command executed by WorldSim.Core — nothing here changes the world by itself. Loaded after app.js. */

const ACTIVITY = { Idle: '自由', Sleeping: '睡眠中', Resting: '休憩中', Working: '仕事中', Studying: '職業訓練中', Travelling: '移動中' };
const SCHEDULE = { Home: '自宅', Commute: '通勤中', Work: '仕事', Shopping: '買い物', School: '学校' };
const FIXTURE_COLORS = { bed: 0x7fa7c9, fridge: 0xdfe6ea, sofa: 0xc98fd1, counter: 0xe8893a, workstation: 0x63bba3, jobboard: 0xe3b341, classroom: 0xb5c46a };
const EDU = ['義務教育', '高校・専門', '大学'];
const WALK_SPEED = 4.5, RUN_SPEED = 11, EYE = 1.6;

const Life = {
  mode: 'creator', cam: 'third', state: null, lastState: 0, camDist: 7, pos: null, room: null, roomKey: '',
  travel: null, panelKey: '', show: { jobs: false, homes: false }, avatar: null, inner: null,

  /** True while a person is being lived as and the life UI is shown. */
  on() { return this.mode === 'life' && !!this.state?.active; },

  init() {
    $$('[data-mode]').forEach((b) => b.addEventListener('click', () => this.setMode(b.dataset.mode)));
    $$('[data-lcam]').forEach((b) => b.addEventListener('click', () => this.setCam(b.dataset.lcam)));
    $('#lifeCard').addEventListener('click', (e) => {
      const b = e.target.closest('[data-act]');
      if (b) this.action(b.dataset.act, b.dataset.arg);
    });
    addEventListener('keydown', (e) => {
      if (this.mode !== 'life' || e.target.closest('input, textarea, select')) return;
      if (e.code === 'KeyE') this.interact();
      if (e.code === 'KeyV') this.setCam({ first: 'third', third: 'overhead', overhead: 'first' }[this.cam]);
    });
    this.avatar = makeFigure(0xe8893a, 1.0);
    this.avatar.visible = false;
    R.scene.add(this.avatar);
    this.inner = makeInterior();
    this.refresh();
    this.syncUi();
    addEventListener('resize', () => this.fitPanel());
  },

  setMode(mode) {
    if (mode === this.mode) return;
    this.mode = mode;
    api('SetMode', mode);
    cancelPending();
    setTool('inspect');
    if (mode === 'life') {
      // Living runs at the life pace (1 game hour ≈ 20 s) unless the world is paused.
      if (S.status && S.status.speed !== 0) setSpeed(4);
      this.refresh();
      if (this.state?.active) this.placeCamera();
      showTab('life');
      this.foldOnPhone();
    } else {
      if (S.status?.speed === 4) setSpeed(1);
      if (this.state?.active && !this.state.inside) { Cam.target.set(this.state.x, heightAt(this.state.x, this.state.z), this.state.z); Cam.dist = 160; }
      Cam.setMode('overhead');
      if (S.tab === 'life') showTab('info');
    }
    this.syncUi();
  },

  setCam(cam) {
    this.cam = cam;
    $$('[data-lcam]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.lcam === cam)));
    if (cam === 'overhead') { Cam.pitch = 1.0; Cam.dist = this.state?.inside ? 30 : 120; }
    else Cam.lookPitch = cam === 'first' ? 0 : -0.25;
  },

  /** Shows or hides the creator tools and life widgets for the current mode. */
  syncUi() {
    const life = this.mode === 'life', on = this.on();
    $$('[data-mode]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.mode === this.mode)));
    document.body.classList.toggle('life', life);
    $('#tools').hidden = life;
    $('#drawer').hidden = true;
    $('#overlays').hidden = life;
    $('#camSeg').hidden = on;
    $('#lifeCamSeg').hidden = !on;
    $('#lifeCard').hidden = !on;
    $$('[data-tab]').forEach((b) => { if (b.dataset.creator !== undefined) b.hidden = life; });
    $('[data-tab="life"]').hidden = false;
    $('#stick').hidden = !(isTouch && (on || S.cam !== 'overhead'));
    $('#updown').hidden = on || !(isTouch && S.cam === 'free');
    this.avatar.visible = !!this.state?.active && !this.state.inside && !(on && this.cam === 'first');
    if (!on) { $('#lifeCard').innerHTML = ''; this.cardHead = this.cardBody = null; }
    this.fitPanel();
  },

  /** Phones: the notebook sits just under the (wrapping) status strip and starts folded while living. */
  fitPanel() {
    const panel = $('#panel');
    panel.style.top = this.mode === 'life' && innerWidth <= 760 ? Math.round($('#hud').getBoundingClientRect().bottom + 6) + 'px' : '';
  },

  foldOnPhone() {
    if (innerWidth > 760 || !this.on()) return;
    $('#panel').classList.add('collapsed');
    $('#collapse').textContent = '▴';
  },

  refresh() {
    this.state = japi('LifeState');
    const s = this.state;
    if (!s.active) { this.pos = null; this.room = null; return; }
    // Every step is executed on the C# side at once, so the server position is always the current one.
    this.pos = s.inside ? { x: s.roomX, z: s.roomZ, inside: s.inside } : { x: s.x, z: s.z, inside: 0 };
    const key = s.inside ? s.inside + ':' + s.room.id : '';
    if (key !== this.roomKey) {
      this.roomKey = key;
      this.room = s.inside ? s.room : null;
      if (this.room) {
        buildInterior(this.inner, this.room);
        Cam.lookYaw = Math.PI; // face into the room from the door
      } else if (s.activity !== 'Travelling') Cam.lookYaw = s.heading + Math.PI;
    }
    if (s.inside) updateOccupants(this.inner, s.occupants || [], this.room);
  },

  /** Puts the life camera behind the person (used when entering the mode). */
  placeCamera() {
    const s = this.state;
    if (!s?.active) return;
    Cam.lookYaw = s.inside ? Math.PI : s.heading + Math.PI;
    this.setCam(this.cam);
  },

  /** Called every frame by app.js. Returns true when life mode drew the camera itself. */
  frame(dt, st, t) {
    if (st.player !== (this.state?.id || 0) || t - this.lastState > 250) {
      this.lastState = t;
      const wasActive = !!this.state?.active;
      this.refresh();
      if (wasActive !== !!this.state.active) { this.syncUi(); if (this.state.active && this.mode === 'life') this.placeCamera(); }
      this.renderCard(st);
      if (S.tab === 'life') this.maybeRenderPanel();
    }
    const s = this.state;
    if (!s?.active) { this.avatar.visible = false; return false; }
    const outdoor = this.outdoorPos(st);
    this.avatar.visible = !s.inside && !(this.mode === 'life' && this.cam === 'first');
    if (outdoor) {
      this.avatar.position.set(outdoor.x, heightAt(outdoor.x, outdoor.z), outdoor.z);
      this.avatar.rotation.y = s.activity === 'Travelling' ? outdoor.h : Cam.lookYaw + Math.PI;
      if (this.mode !== 'life') this.avatar.rotation.y = s.heading;
      this.avatar.scale.setScalar(this.mode === 'life' ? 1 : 6); // visible from above in creator mode
    }
    if (this.mode !== 'life') return false;
    this.walk(dt);
    this.camera(outdoor);
    return true;
  },

  /** Where the person is drawn outside: the server position, or a point along the trip while travelling. */
  outdoorPos(st) {
    const s = this.state;
    if (s.inside) return null;
    if (s.activity !== 'Travelling') { this.travel = null; return { x: this.pos.x, z: this.pos.z }; }
    if (!this.travel || this.travel.to !== s.travelTo?.id) {
      const door = japi('DoorOf', s.travelTo?.id || 0);
      this.travel = { to: s.travelTo?.id, fromX: s.x, fromZ: s.z, toX: door.x, toZ: door.z, start: st.tick + st.progress, arrive: s.arrive };
    }
    const tr = this.travel, now = st.tick + st.progress;
    const k = Math.max(0, Math.min(1, (now - tr.start) / Math.max(0.01, tr.arrive - tr.start)));
    return { x: tr.fromX + (tr.toX - tr.fromX) * k, z: tr.fromZ + (tr.toZ - tr.fromZ) * k, h: Math.atan2(tr.toX - tr.fromX, tr.toZ - tr.fromZ) };
  },

  /** Keyboard / joystick walking. Each step is a WalkCommand; a refused step is not taken. */
  walk(dt) {
    const s = this.state;
    if (s.activity === 'Travelling' || !this.pos) return;
    const kb = Keys;
    const mx = (kb.KeyD || kb.ArrowRight ? 1 : 0) - (kb.KeyA || kb.ArrowLeft ? 1 : 0) + Cam.move.x;
    const mz = (kb.KeyW || kb.ArrowUp ? 1 : 0) - (kb.KeyS || kb.ArrowDown ? 1 : 0) + Cam.move.z;
    if (Math.abs(mx) < 0.05 && Math.abs(mz) < 0.05) return;
    const len = Math.max(1, Math.hypot(mx, mz));
    const sp = ((kb.ShiftLeft || kb.ShiftRight) && !s.inside ? RUN_SPEED : WALK_SPEED) * dt / len;
    const yaw = this.cam === 'overhead' ? Cam.yaw : Cam.lookYaw;
    const fx = -Math.sin(yaw), fz = -Math.cos(yaw);
    const dx = (fx * mz - fz * mx) * sp, dz = (fz * mz + fx * mx) * sp;
    const h = Math.atan2(dx, dz);
    const p = this.pos;
    // Try the full step, then slide along each axis so walls and water do not stop the walk dead.
    for (const [nx, nz] of [[p.x + dx, p.z + dz], [p.x + dx, p.z], [p.x, p.z + dz]]) {
      const r = japi('LifeExecute', JSON.stringify({ type: 'walk', x: nx, z: nz, h }));
      if (r.ok) {
        if (s.inside) { p.x = Math.max(0.3, Math.min(this.room.width - 0.3, nx)); p.z = Math.max(0.3, Math.min(this.room.depth - 0.3, nz)); }
        else { p.x = nx; p.z = nz; }
        if (s.activity !== 'Idle') s.activity = 'Idle';
        return;
      }
    }
  },

  camera(outdoor) {
    const s = this.state, cam = R.camera, inside = !!s.inside;
    const px = inside ? this.pos.x : outdoor.x, pz = inside ? this.pos.z : outdoor.z;
    const ground = inside ? 0 : Math.max(S.meta.seaLevel, heightAt(px, pz));
    const ox = inside ? this.inner.origin.x : 0, oz = inside ? this.inner.origin.z : 0;
    if (inside) this.inner.player.position.set(ox + px, 0, oz + pz), this.inner.player.rotation.y = Cam.lookYaw + Math.PI;
    this.inner.player.visible = inside && this.cam !== 'first';
    if (this.cam === 'overhead') {
      const cp = Math.cos(Cam.pitch), d = inside ? Math.min(Cam.dist, 40) : Cam.dist;
      cam.position.set(ox + px + Math.sin(Cam.yaw) * cp * d, ground + Math.sin(Cam.pitch) * d, oz + pz + Math.cos(Cam.yaw) * cp * d);
      cam.lookAt(ox + px, ground, oz + pz);
      return;
    }
    if (this.cam === 'first') {
      cam.position.set(ox + px, ground + EYE, oz + pz);
    } else {
      const d = inside ? Math.min(this.camDist, 4.5) : this.camDist;
      const back = new THREE.Vector3(Math.sin(Cam.lookYaw) * Math.cos(Cam.lookPitch), -Math.sin(Cam.lookPitch), Math.cos(Cam.lookYaw) * Math.cos(Cam.lookPitch));
      cam.position.set(ox + px + back.x * d, ground + EYE + 0.4 + back.y * d, oz + pz + back.z * d);
      if (!inside) cam.position.y = Math.max(cam.position.y, heightAt(cam.position.x, cam.position.z) + 0.5);
      else {
        // Keep the camera inside the room so walls never block the view.
        cam.position.x = Math.max(ox + 0.3, Math.min(ox + this.room.width - 0.3, cam.position.x));
        cam.position.z = Math.max(oz + 0.3, Math.min(oz + this.room.depth - 0.3, cam.position.z));
        cam.position.y = Math.max(0.4, Math.min(2.8, cam.position.y));
      }
    }
    cam.rotation.set(Cam.lookPitch, Cam.lookYaw, 0, 'YXZ');
  },

  /** The interior is its own small scene; outdoors the world scene is drawn. */
  scene() { return this.mode === 'life' && this.state?.active && this.state.inside ? this.inner.scene : null; },

  zoom(f) {
    if (this.cam === 'overhead') Cam.dist = Math.max(15, Math.min(600, Cam.dist * f));
    else this.camDist = Math.max(2.5, Math.min(40, this.camDist * f));
  },

  /** A tap on the map while living: offer to go to the tapped building. */
  tap(cell) {
    if (!this.on() || this.state.inside) return;
    const info = japi('Inspect', cell.x, cell.z);
    const b = info?.building;
    if (!b) { hideBar(); return; }
    showBar(`<b>${esc(b.name)}</b><br><small>道路を通って移動します（ゲーム内の移動時間がかかります）</small>`, 'ここへ行く', true,
      () => { this.action('travel', b.id); hideBar(); }, false);
  },

  /** Enter / exit / use whatever is nearest (key E). */
  interact() {
    const s = this.state;
    if (!s?.active) return;
    if (!s.inside && s.door && !s.door.denied) return this.action('enter', s.door.id);
    if (s.inside) {
      const f = this.nearFixture();
      if (f) return this.action('fixture', f.type);
      return this.action('exit');
    }
  },

  nearFixture() {
    if (!this.room || !this.pos) return null;
    let best = null, bd = 1.6;
    for (const f of this.room.fixtures) {
      const dx = Math.max(f.x - this.pos.x, 0, this.pos.x - (f.x + f.w)), dz = Math.max(f.z - this.pos.z, 0, this.pos.z - (f.z + f.d));
      const d = Math.hypot(dx, dz);
      if (d < bd) { bd = d; best = f; }
    }
    return best;
  },

  run(cmd) {
    const r = japi('LifeExecute', JSON.stringify(cmd));
    toast(r.msg || (r.ok ? '完了' : '失敗'), !r.ok);
    this.refresh();
    this.renderCard(S.status);
    this.panelKey = '';
    if (S.tab === 'life') renderPanel();
    return r;
  },

  action(act, arg) {
    const id = arg !== undefined ? +arg : 0;
    switch (act) {
      case 'enter': return this.run({ type: 'enter', id });
      case 'exit': return this.run({ type: 'exit' });
      case 'stop': return this.run({ type: 'stop' });
      case 'travel': return this.run({ type: 'travel', id });
      case 'buy': return this.run({ type: 'buy', res: arg, qty: 1 });
      case 'talk': return this.run({ type: 'talk', id });
      case 'apply': return this.run({ type: 'apply', id });
      case 'move': return this.run({ type: 'move', id });
      case 'quit': return showBar('<b>今の仕事を辞めますか？</b>', '辞める', true, () => { hideBar(); this.run({ type: 'quit' }); }, false);
      case 'possess': {
        const r = this.run({ type: 'possess', id });
        if (r.ok) { this.syncUi(); this.placeCamera(); this.foldOnPhone(); }
        return;
      }
      case 'release': {
        return showBar('<b>この人物の操作をやめますか？</b><br><small>人物は街の住民として暮らし続けます</small>', 'やめる', true,
          () => { hideBar(); this.run({ type: 'release' }); this.syncUi(); }, false);
      }
      case 'fixture': {
        if (arg === 'counter') {
          if (!this.state.shop) return toast('ここでは商品を売っていません', true);
          this.show.shop = true;
          return this.renderCard(S.status);
        }
        if (arg === 'jobboard') { this.show.jobs = true; return showTab('life'); }
        return this.run({ type: 'use', fixture: arg });
      }
      case 'places': this.show.places = !this.show.places; this.places = null; return this.renderCard(S.status);
      case 'closeShop': this.show.shop = false; return this.renderCard(S.status);
    }
  },

  /** The always-visible status card (bottom left). */
  renderCard(st) {
    const s = this.state;
    if (!this.on() || !st) return;
    const bar = (v) => `<span class="meter ${v < 0.25 ? 'bad' : v < 0.5 ? 'warn' : ''}"><i style="width:${Math.round(v * 100)}%"></i></span>`;
    const where = s.activity === 'Travelling' ? `${esc(s.travelTo?.name || '')}へ移動中（到着 ${fmtTick(s.arrive)}）`
      : s.inside ? esc(s.room.building) + (s.home && s.inside === s.home.id ? '（自宅）' : s.job && s.inside === s.job.id ? '（職場）' : '') : '屋外';
    const acts = [];
    if (s.activity !== 'Idle' && s.activity !== 'Travelling') acts.push(`<button class="btn" data-act="stop">${ACTIVITY[s.activity]}をやめる</button>`);
    if (!s.inside && s.door) acts.push(s.door.denied
      ? `<button class="btn" disabled title="${esc(s.door.denied)}">${esc(s.door.name)}：${esc(s.door.denied)}</button>`
      : `<button class="btn primary" data-act="enter" data-arg="${s.door.id}">${esc(s.door.name)}に入る <kbd>E</kbd></button>`);
    if (s.inside) {
      const near = this.nearFixture();
      for (const f of s.room.fixtures)
        if (!acts.some((a) => a.includes(`data-arg="${f.type}"`)))
          acts.push(`<button class="btn${near && near.type === f.type ? ' primary' : ''}" data-act="fixture" data-arg="${f.type}">${esc(f.label)}</button>`);
      acts.push(`<button class="btn" data-act="exit">外に出る</button>`);
    }
    if (s.activity !== 'Travelling') acts.push(`<button class="btn" data-act="places">${this.show.places ? '閉じる' : '行き先'}</button>`);
    let extra = '';
    if (this.show.places && s.activity !== 'Travelling') {
      if (!this.places || performance.now() - this.places.t > 2000) this.places = { t: performance.now(), list: japi('Places') };
      const places = this.places.list;
      extra = `<div class="picker">${places.map((p) => `<button class="btn" data-act="travel" data-arg="${p.id}">${esc(p.label)}<small>${esc(p.name)}・${p.minutes < 0 ? '道路なし' : '約' + Math.max(1, Math.round(p.minutes)) + '分'}</small></button>`).join('')}</div>`;
    }
    if (this.show.shop && s.shop) {
      extra = `<div class="picker">${s.shop.map((i) => `<button class="btn" data-act="buy" data-arg="${i.res}" ${i.stock < 1 ? 'disabled' : ''}>${esc(i.name)} 1${esc(i.unit)}<small>${yen(i.price)}・在庫 ${i.stock}</small></button>`).join('')}<button class="btn" data-act="closeShop">閉じる</button></div>`;
    } else if (!s.shop) this.show.shop = false;
    const people = s.inside && s.occupants?.length ? `<div class="people">${s.occupants.slice(0, 6).map((o) => `<button class="btn" data-act="talk" data-arg="${o.id}">${esc(o.name)}と話す<small>${esc(o.job)}・${SCHEDULE[o.activity] || ''}</small></button>`).join('')}</div>` : '';
    const card = $('#lifeCard');
    if (!card.firstElementChild) card.innerHTML = '<div class="lcHead"></div><div class="lcBody"></div>';
    // Only rewrite what changed, so a button is never replaced under the finger.
    const head = `<header><b>${esc(s.name)}</b><span>${s.age}歳・${esc(s.job ? s.job.name : s.retired ? '退職' : '無職')}</span><span class="num">${esc(st.clock)}</span></header>
      <div class="needs">
        <label>所持金 <b class="num">${yen(s.cash)}</b></label>
        <label>空腹 ${bar(s.hunger)}</label><label>体力 ${bar(s.energy)}</label>
        <label>${esc(ACTIVITY[s.activity])}・${where}</label>
      </div>`;
    const body = `<div class="acts">${acts.join('')}</div>${extra}${people}`;
    if (head !== this.cardHead) { this.cardHead = head; card.firstElementChild.innerHTML = head; }
    if (body !== this.cardBody) { this.cardBody = body; card.lastElementChild.innerHTML = body; }
  },

  maybeRenderPanel() {
    const s = this.state;
    const key = s.active ? [s.id, s.job?.id, s.home?.id, s.inside, s.activity, Math.floor(s.cash / 1000), s.education, s.level, S.status?.days].join(':') : 'none:' + Math.floor(S.status?.days || 0);
    if (key === this.panelKey) return;
    this.panelKey = key;
    renderPanel();
  },

  /** The 人生 tab: choose a person, or everything about the one being lived. */
  renderPanel(el) {
    const s = this.state || japi('LifeState');
    if (!s.active) return this.renderChooser(el);
    const job = s.job ? `${esc(s.job.name)}（役職${s.level}・勤続${s.tenure}日）` : s.retired ? '退職' : '無職';
    const today = s.workday ? `勤務日・今日の勤務 ${s.worked.toFixed(0)}時間（8時間で満額）` : '休日';
    el.innerHTML = `<h2>${esc(s.name)}（${s.age}歳）</h2>
      ${dl([
        ['所持金', yen(s.cash)], ['仕事', job], ['日給', s.wage ? yen(s.wage) : '—'], ['今日', today],
        ['欠勤', s.absence ? `<span style="color:var(--warn)">${s.absence}日（3日を超えると解雇）</span>` : '0日'],
        ['住まい', s.home ? esc(s.home.name) : 'なし'], ['通勤', s.commute ? `${s.commute.toFixed(0)}分` : '—'],
        ['学歴', EDU[s.education] || s.education], ['職業訓練', s.education < 2 ? `${s.study}／${s.studyNeeded}時間` : '修了'],
        ['食料（自宅）', `${s.pantry}食分`], ['満足度', pct(s.happiness)],
      ])}
      <p class="empty" style="margin-top:8px">世界の時間は止まらず、他の住民もそれぞれの予定で暮らしています。給料は実際に働いた時間で支払われ、食事は店で買った食料からとります。</p>
      <h4>同じ住まいの人・知り合い</h4>${this.peopleList([...(s.family || []), ...(s.friends || []).filter((f) => !(s.family || []).some((x) => x.id === f.id))])}
      <h4>仕事を探す</h4>${this.show.jobs ? this.jobsList() : '<button class="btn" id="lfJobs">求人を見る</button>'}
      ${s.job ? '<div class="row" style="margin-top:8px"><button class="btn danger" data-life="quit">仕事を辞める</button></div>' : ''}
      <h4>住まいを探す</h4>${this.show.homes ? this.homesList() : '<button class="btn" id="lfHomes">空き住宅を見る</button>'}
      <h4>最近の出来事</h4>${this.logList()}
      <h4>操作</h4><div class="row"><button class="btn" data-life="release">この人物の操作をやめる</button></div>
      <p class="empty" style="margin-top:6px">操作をやめても人物は住民として暮らし続けます。世界を編集するには上の「世界創造」に切り替えてください（人物はそのまま残ります）。</p>`;
    this.bindPanel(el);
  },

  peopleList(list) {
    if (!list.length) return '<p class="empty">まだいません。同じ建物にいる人と話すと知り合いになります。</p>';
    return list.map((p) => `<div class="news"><b>${esc(p.name)}</b> ${p.age}歳・${esc(p.job)}・${esc(SCHEDULE[p.activity] || '')}
      <span class="pill">${p.affinity > 0 ? '親しさ ' + Math.round(p.affinity * 100) : '同じ住まい'}</span>
      <button class="btn" data-life="talk" data-id="${p.id}" style="padding:3px 8px;margin-left:4px">話す</button></div>`).join('');
  },

  jobsList() {
    const jobs = japi('Jobs');
    if (!jobs.length) return '<p class="empty">いま募集している職場はありません。日を置くと企業が人を募集します。</p>';
    return jobs.slice(0, 25).map((j) => `<div class="news"><b>${esc(j.name)}</b> 日給 ${yen(j.wage)}・欠員${j.openings}・${j.commute < 0 ? '' : '通勤' + j.commute.toFixed(0) + '分'}
      ${j.problem ? `<br><small style="color:var(--warn)">${esc(j.problem)}</small>` : `<button class="btn" data-life="apply" data-id="${j.id}" style="padding:3px 8px;margin-left:4px">応募</button>`}</div>`).join('');
  },

  homesList() {
    const homes = japi('Homes');
    if (!homes.length) return '<p class="empty">空いている住宅がありません。</p>';
    return `<p class="empty">引っ越し費用 50,000円</p>` + homes.map((h) => `<div class="news"><b>${esc(h.name)} #${h.id}</b> 空き${h.free}・${h.commute < 0 ? '' : '通勤' + h.commute.toFixed(0) + '分'}
      <button class="btn" data-life="move" data-id="${h.id}" style="padding:3px 8px;margin-left:4px">引っ越す</button></div>`).join('');
  },

  logList() {
    const log = japi('LifeLog');
    if (!log.length) return '<p class="empty">まだ出来事はありません。</p>';
    return log.slice(0, 15).map(([t, k, m]) => `<div class="news"><span class="t">${fmtTick(t)}</span><span class="k">${esc(k)}</span>${esc(m)}</div>`).join('');
  },

  renderChooser(el) {
    const c = Cam.target;
    const list = japi('Candidates', c.x, c.z);
    el.innerHTML = `<h2>人生体験モード</h2>
      <p class="empty">街の住民ひとりとして暮らします。世界の時間は進み続け、他の住民もそれぞれの予定で暮らします。この間は世界を編集できません（「世界創造」にいつでも戻れます）。</p>
      <h4>新しい住民として始める</h4>
      <div class="row"><label>年齢 <select id="lfAge">${[18, 22, 25, 30, 40, 50, 60].map((a) => `<option ${a === 22 ? 'selected' : ''}>${a}</option>`).join('')}</select></label>
      <label>学歴 <select id="lfEdu">${EDU.map((e, i) => `<option value="${i}" ${i === 1 ? 'selected' : ''}>${e}</option>`).join('')}</select></label>
      <button class="btn primary" id="lfCreate">この条件で始める</button></div>
      <p class="empty" style="font-size:12px">空いている住宅に入居し、所持金30万円（外部から持ち込み）で始まります。仕事は自分で探します。</p>
      <h4>街の住民を選ぶ（画面中央に近い順）</h4>
      ${list.length ? list.map((p) => `<div class="news"><b>${esc(p.name)}</b> ${p.age}歳・${esc(p.job)}・${EDU[p.education] || ''}・${yen(p.cash)}
        <button class="btn" data-life="possess" data-id="${p.id}" style="padding:3px 8px;margin-left:4px">この人になる</button></div>`).join('') : '<p class="empty">住民がいません。</p>'}`;
    this.bindPanel(el);
    $('#lfCreate')?.addEventListener('click', () => {
      const r = this.run({ type: 'create', age: +$('#lfAge').value, education: +$('#lfEdu').value });
      if (r.ok) { this.syncUi(); this.placeCamera(); this.foldOnPhone(); }
    });
  },

  bindPanel(el) {
    el.querySelectorAll('[data-life]').forEach((b) => b.addEventListener('click', () => this.action(b.dataset.life, b.dataset.id)));
    el.querySelector('#lfJobs')?.addEventListener('click', () => { this.show.jobs = true; renderPanel(); });
    el.querySelector('#lfHomes')?.addEventListener('click', () => { this.show.homes = true; renderPanel(); });
  },
};

function fmtTick(t) {
  const day = Math.floor(t / 24), h = Math.floor(t % 24), m = Math.floor((t % 1) * 60);
  return `${day + 1}日 ${h}:${String(m).padStart(2, '0')}`;
}

/* ------------------------------------------------------------------ figures and interiors */

function makeFigure(color, scale) {
  const g = new THREE.Group();
  const mat = new THREE.MeshLambertMaterial({ color });
  const body = new THREE.Mesh(new THREE.CylinderGeometry(0.25, 0.3, 1.1, 10), mat);
  body.position.y = 0.75;
  const head = new THREE.Mesh(new THREE.SphereGeometry(0.2, 12, 10), new THREE.MeshLambertMaterial({ color: 0xf0d2b0 }));
  head.position.y = 1.5;
  const nose = new THREE.Mesh(new THREE.BoxGeometry(0.1, 0.08, 0.12), mat); // shows which way the figure faces
  nose.position.set(0, 1.5, 0.2);
  g.add(body, head, nose);
  g.scale.setScalar(scale);
  return g;
}

function makeInterior() {
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x2b2f33);
  scene.add(new THREE.HemisphereLight(0xfff6e8, 0x40372c, 0.95));
  const sun = new THREE.DirectionalLight(0xffffff, 0.45);
  sun.position.set(3, 10, 6);
  scene.add(sun);
  const room = new THREE.Group();
  scene.add(room);
  const player = makeFigure(0xe8893a, 1);
  scene.add(player);
  return { scene, room, player, origin: new THREE.Vector3(0, 0, 0), people: new THREE.Group(), key: '' };
}

function labelSprite(text) {
  const c = document.createElement('canvas');
  const ctx = c.getContext('2d');
  ctx.font = '500 34px "Zen Kaku Gothic New", sans-serif';
  const w = Math.ceil(ctx.measureText(text).width) + 28;
  c.width = w; c.height = 54;
  ctx.font = '500 34px "Zen Kaku Gothic New", sans-serif';
  ctx.fillStyle = 'rgba(15,26,34,.85)';
  ctx.fillRect(0, 0, w, 54);
  ctx.fillStyle = '#ece6da';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, 14, 28);
  const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: new THREE.CanvasTexture(c), depthTest: false }));
  s.scale.set(w / 180, 0.3, 1);
  return s;
}

/** Floor, four walls with a door gap, and the fixtures from the content data (interiors.json). */
function buildInterior(inner, room) {
  const g = inner.room;
  g.children.slice().forEach((m) => { g.remove(m); m.geometry?.dispose?.(); });
  const W = room.width, D = room.depth, H = 3;
  const floor = new THREE.Mesh(new THREE.BoxGeometry(W, 0.1, D), new THREE.MeshLambertMaterial({ color: room.id === 'home' ? 0x8a6a4a : room.id === 'factory' ? 0x6b6e70 : 0xb9b2a4 }));
  floor.position.set(W / 2, -0.05, D / 2);
  g.add(floor);
  const wallMat = new THREE.MeshLambertMaterial({ color: 0xe4ddd0 });
  const wall = (x, z, w, d) => { const m = new THREE.Mesh(new THREE.BoxGeometry(w, H, d), wallMat); m.position.set(x, H / 2, z); g.add(m); };
  const door = room.doorX, gap = 1.2;
  wall((door - gap / 2) / 2, 0, door - gap / 2, 0.15);
  wall((door + gap / 2 + W) / 2, 0, W - door - gap / 2, 0.15);
  wall(W / 2, D, W, 0.15);
  wall(0, D / 2, 0.15, D);
  wall(W, D / 2, 0.15, D);
  const mark = new THREE.Mesh(new THREE.BoxGeometry(gap, 0.02, 0.8), new THREE.MeshBasicMaterial({ color: 0x63bba3 }));
  mark.position.set(door, 0.02, 0.4);
  g.add(mark);
  const exit = labelSprite('出口');
  exit.position.set(door, 2.4, 0.2);
  g.add(exit);
  for (const f of room.fixtures) {
    const h = f.type === 'bed' ? 0.5 : f.type === 'fridge' ? 1.8 : f.type === 'jobboard' ? 2 : f.type === 'classroom' ? 0.75 : 0.9;
    const m = new THREE.Mesh(new THREE.BoxGeometry(f.w, h, f.d), new THREE.MeshLambertMaterial({ color: FIXTURE_COLORS[f.type] || 0x9fadb5 }));
    m.position.set(f.x + f.w / 2, h / 2, f.z + f.d / 2);
    g.add(m);
    const l = labelSprite(f.label);
    l.position.set(f.x + f.w / 2, h + 0.6, f.z + f.d / 2);
    g.add(l);
  }
  inner.people.clear();
  inner.key = '';
  g.add(inner.people);
}

/** NPCs that are really in this building right now (from their daily schedule), placed by id. */
function updateOccupants(inner, list, room) {
  const key = list.map((o) => o.id).join(',');
  if (key === inner.key || !room) return;
  inner.key = key;
  inner.people.clear();
  list.forEach((o) => {
    const fig = makeFigure(0x7fa7c9, 1);
    const hash = (o.id * 2654435761) >>> 0;
    fig.position.set(0.8 + ((hash & 1023) / 1023) * (room.width - 1.6), 0, 2 + (((hash >> 10) & 1023) / 1023) * (room.depth - 2.8));
    fig.rotation.y = ((hash >> 20) & 63) / 10;
    const l = labelSprite(o.name);
    l.position.y = 2.1;
    l.scale.multiplyScalar(0.8);
    fig.add(l);
    inner.people.add(fig);
  });
}

/* ------------------------------------------------------------------ gyro and mouse look */

/**
 * Device orientation adds to the look direction the same way dragging or the mouse does: only the
 * change since the last reading is applied, so touch, mouse and gyro can be mixed freely. iOS asks for
 * permission from a tap. Some hosts (an embedded page such as claude.ai) block the sensor entirely.
 */
const Gyro = {
  on: false, last: null, got: false,
  async toggle() {
    if (this.on) return this.stop();
    const DOE = window.DeviceOrientationEvent;
    if (!DOE) return toast('この端末・ブラウザはジャイロに対応していません', true);
    if (typeof DOE.requestPermission === 'function') {
      try {
        if ((await DOE.requestPermission()) !== 'granted') return toast('ジャイロの使用が許可されませんでした', true);
      } catch (e) {
        return toast('この表示環境ではジャイロの許可を求められません（埋め込み表示の制限）。単独のページで開くと使えます', true);
      }
    }
    this.on = true; this.last = null; this.got = false;
    addEventListener('deviceorientation', this.handler);
    $('#gyroBtn').setAttribute('aria-pressed', 'true');
    toast('ジャイロ操作オン：端末を傾けると視点が動きます');
    setTimeout(() => {
      if (this.on && !this.got) { this.stop(); toast('ジャイロの信号が届きません。この表示環境ではセンサーが使えないようです（単独のページで開くと使えます）', true); }
    }, 2000);
  },
  stop() {
    this.on = false;
    removeEventListener('deviceorientation', this.handler);
    $('#gyroBtn').setAttribute('aria-pressed', 'false');
  },
  handler: (e) => Gyro.read(e),
  read(e) {
    if (e.alpha == null || e.beta == null) return;
    this.got = true;
    const orient = ((screen.orientation && screen.orientation.angle) || window.orientation || 0) * Math.PI / 180;
    _gq.setFromEuler(_ge.set(e.beta * Math.PI / 180, e.alpha * Math.PI / 180, -e.gamma * Math.PI / 180, 'YXZ'));
    _gq.multiply(_gq1).multiply(_gq0.setFromAxisAngle(_gz, -orient));
    _ge2.setFromQuaternion(_gq, 'YXZ');
    const cur = { yaw: _ge2.y, pitch: _ge2.x };
    if (this.last) {
      let dy = cur.yaw - this.last.yaw;
      if (dy > Math.PI) dy -= 2 * Math.PI;
      if (dy < -Math.PI) dy += 2 * Math.PI;
      const dp = cur.pitch - this.last.pitch;
      // Ignore jumps (the yaw is unstable when the device lies flat).
      if (Math.abs(dy) < 0.35 && Math.abs(dp) < 0.35) applyLook(dy, dp);
    }
    this.last = cur;
  },
};
const _gq = new THREE.Quaternion(), _gq0 = new THREE.Quaternion(), _gq1 = new THREE.Quaternion(-Math.sqrt(0.5), 0, 0, Math.sqrt(0.5));
const _ge = new THREE.Euler(), _ge2 = new THREE.Euler(), _gz = new THREE.Vector3(0, 0, 1);

/** Turns the view by radians: the first-person / third-person / free look, or the orbit when overhead. */
function applyLook(dYaw, dPitch) {
  const overhead = Life.mode === 'life' && Life.on() ? Life.cam === 'overhead' : S.cam === 'overhead';
  if (overhead) {
    Cam.yaw += dYaw;
    Cam.pitch = Math.max(0.25, Math.min(1.5, Cam.pitch - dPitch));
  } else {
    Cam.lookYaw += dYaw;
    Cam.lookPitch = Math.max(-1.4, Math.min(1.4, Cam.lookPitch + dPitch));
  }
}

/** Mouse look: double-click the view to capture the mouse (Esc releases it). */
function initMouseLook() {
  const el = R.renderer.domElement;
  el.addEventListener('dblclick', () => {
    const looking = Life.on() || S.cam !== 'overhead';
    if (looking && el.requestPointerLock && document.pointerLockElement !== el) el.requestPointerLock();
  });
  document.addEventListener('pointerlockchange', () => {
    if (document.pointerLockElement === el) toast('マウスで視点を操作中（Escで解除）');
  });
  document.addEventListener('mousemove', (e) => {
    if (document.pointerLockElement !== el) return;
    applyLook(-e.movementX * 0.0025, -e.movementY * 0.0025);
  });
}
