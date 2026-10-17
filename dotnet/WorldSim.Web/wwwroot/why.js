'use strict';
/* "Why?" panel (S1-4): splits the change of an indicator into its causes, using the cause log kept by
   WorldSim.Core. Every number here comes from GameApi.Explain / ExplainBranches; nothing is estimated
   in JavaScript. Loaded after app.js. */

const WHY_METRICS = [
  ['unemployment', '失業率', 'up-bad'], ['population', '人口', 'up-good'], ['treasury', '市の資金', 'up-good'],
  ['householdCash', '家計の貯蓄', 'up-good'], ['price.food', '食品の価格', 'up-bad'], ['price.goods', '工業製品の価格', 'up-bad'],
];
const WHY_PERIODS = [[30, '30日'], [90, '90日'], [360, '1年']];

const Why = {
  metric: 'unemployment', days: 90, other: 0,

  init() {
    // The headline numbers open their explanation.
    $$('[data-why]').forEach((el) => {
      el.setAttribute('role', 'button');
      el.setAttribute('tabindex', '0');
      el.title = 'クリックで「なぜ？」（変化の原因）';
      const open = () => this.open(el.dataset.why);
      el.addEventListener('click', open);
      el.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } });
    });
  },

  open(metric, other) {
    this.metric = metric;
    if (other !== undefined) this.other = other;
    showTab('why');
  },

  fmt(v, unit, signed) {
    const s = signed && v > 0 ? '+' : '';
    if (unit === '円') return (signed && v > 0 ? '+' : '') + yen(v);
    if (unit === '人') return s + Math.round(v).toLocaleString('ja-JP') + '人';
    if (unit === 'pt') return s + v.toFixed(1) + (signed ? 'pt' : '%');
    if (unit === '%') return s + v.toFixed(1) + '%';
    return s + v.toFixed(2);
  },

  render(el) {
    const branches = japi('Branches');
    if (this.other && !branches.some((b) => b.id === this.other && !b.active)) this.other = 0;
    const x = this.other ? japi('ExplainBranches', this.metric, this.other) : japi('Explain', this.metric, this.days);
    const sense = (WHY_METRICS.find((m) => m[0] === this.metric) || [])[2];
    const chips = WHY_METRICS.map(([k, n]) => `<button aria-pressed="${k === this.metric}" data-wm="${k}">${n}</button>`).join('');
    const periods = WHY_PERIODS.map(([d, n]) => `<button aria-pressed="${!this.other && d === this.days}" data-wd="${d}">${n}</button>`).join('');
    const others = branches.filter((b) => !b.active);
    const compare = others.length ? `<label style="font-size:12.5px;color:var(--muted)">分岐と比べる
      <select id="whyOther"><option value="0">比べない（期間の変化）</option>${others.map((b) => `<option value="${b.id}" ${b.id === this.other ? 'selected' : ''}>${esc(b.name)}</option>`).join('')}</select></label>` : '';
    let body;
    if (!x.ok) body = '<p class="empty">まだ説明できるだけの記録がありません。時間を進めてください（原因は1日ごとに記録されます）。</p>';
    else {
      const head = this.other
        ? `表示中の分岐は <b>${this.fmt(x.end, x.unit)}</b>、比べる分岐は <b>${this.fmt(x.start, x.unit)}</b>。分岐した時点からの違いを原因別に分けました。`
        : `${x.fromDay}〜${x.toDay}日目：<b>${x.unit === '%' ? esc(x.note || '') : this.fmt(x.start, x.unit) + ' → ' + this.fmt(x.end, x.unit)}</b>（${this.fmt(x.change, x.unit, true)}）`;
      const explained = Math.abs(x.change) > 1e-9 ? `<small class="pill ${Math.abs(x.explained - 1) < 0.1 ? 'good' : 'bad'}">原因で説明できた割合 ${Math.round(x.explained * 100)}%</small>` : '';
      const max = Math.max(1e-9, ...x.parts.map((p) => Math.abs(p.value)));
      const rows = x.parts.slice(0, 14).map((p) => {
        const good = sense === 'up-good' ? p.value > 0 : p.value < 0;
        const w = Math.max(2, Math.round((Math.abs(p.value) / max) * 100));
        const agents = p.agents.length ? `<div class="whyAgents">${p.agents.map((a) => `<span>${esc(a.name)} #${a.id}・${a.count}件${a.x >= 0 ? ` <button class="linkish" data-goto="${a.x},${a.z}">地図で見る</button>` : ''}</span>`).join('')}</div>` : '';
        return `<div class="whyRow"><div class="whyLabel">${esc(p.label)}<b class="num" style="color:var(${good ? '--good' : '--bad'})">${this.fmt(p.value, x.unit, true)}</b></div>
          <div class="whyBar"><i style="width:${w}%;background:var(${good ? '--good' : '--bad'})"></i></div>${agents}</div>`;
      }).join('');
      body = `<p style="margin:0 0 6px;line-height:1.6">${head}</p>${explained}
        <div style="margin-top:10px">${rows || '<p class="empty">この期間に変化はありません。</p>'}</div>
        <p class="empty" style="font-size:12px;margin-top:10px">緑は良い方向、赤は悪い方向への寄与です。値はシミュレーションが記録した実際の出来事（解雇・転入・取引など）の合計です。</p>`;
    }
    el.innerHTML = `<h2>なぜ？ — 変化の原因</h2>
      <div class="series-pick">${chips}</div>
      <div class="row" style="margin-bottom:10px"><div class="seg" role="group" aria-label="期間">${periods}</div>${compare}</div>${body}`;
    el.querySelectorAll('[data-wm]').forEach((b) => b.addEventListener('click', () => { this.metric = b.dataset.wm; renderPanel(); }));
    el.querySelectorAll('[data-wd]').forEach((b) => b.addEventListener('click', () => { this.days = +b.dataset.wd; this.other = 0; renderPanel(); }));
    el.querySelector('#whyOther')?.addEventListener('change', (e) => { this.other = +e.target.value; renderPanel(); });
    el.querySelectorAll('[data-goto]').forEach((b) => b.addEventListener('click', () => {
      const [cx, cz] = b.dataset.goto.split(',').map(Number);
      if (Life.mode === 'life') return toast('世界創造モードで地図を移動できます');
      const cs = S.meta.cellSize;
      Cam.setMode('overhead');
      Cam.target.set(cx * cs, heightAt(cx * cs, cz * cs), cz * cs);
      Cam.dist = Math.min(Cam.dist, 260);
      inspectAt({ x: Math.floor(cx), z: Math.floor(cz) });
    }));
  },
};
