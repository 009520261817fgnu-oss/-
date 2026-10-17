'use strict';
/* Performance measurement in the browser (S5-0): the same numbers the CLI benchmark reports, measured on
   this device — WebAssembly time per simulated day, save/load time, memory and the frame rate while
   the game runs at 16×. Started from the 概要 tab. Loaded after app.js. */

const Bench = {
  running: false,

  async run(onProgress) {
    if (this.running) return null;
    this.running = true;
    const say = (m) => onProgress && onProgress(m);
    try {
      const branches = japi('Branches');
      const active = branches.find((b) => b.active);
      // Measure on a copy so the player's world is not advanced.
      say('世界を複製しています…');
      const fork = japi('Fork', '性能計測（自動で削除）');
      if (!fork.ok) throw new Error(fork.msg);
      api('AdvanceBranch', fork.id, 5); // warm-up
      say('シミュレーションを計測しています（60日）…');
      await new Promise((r) => setTimeout(r, 30));
      const blocks = [];
      for (let i = 0; i < 3; i++) {
        const t0 = performance.now();
        api('AdvanceBranch', fork.id, 20);
        blocks.push((performance.now() - t0) / 20);
        await new Promise((r) => setTimeout(r, 0));
      }
      blocks.sort((a, b) => a - b);
      say('保存と読込を計測しています…');
      const t1 = performance.now();
      const save = api('SaveAll');
      const t2 = performance.now();
      api('RemoveBranch', fork.id);
      // Load into a throwaway check: parsing the whole save is what loading costs.
      const t3 = performance.now();
      JSON.parse(save);
      const t4 = performance.now();
      say('描画を計測しています（5秒、16倍速）…');
      const speed = S.status?.speed ?? 0;
      setSpeed(3);
      const fps = await new Promise((res) => {
        let n = 0;
        const t0 = performance.now();
        const f = () => { n++; if (performance.now() - t0 < 5000) requestAnimationFrame(f); else res(n / ((performance.now() - t0) / 1000)); };
        requestAnimationFrame(f);
      });
      setSpeed(speed);
      const st = S.status;
      return {
        device: `${navigator.userAgent}`, screen: `${screen.width}×${screen.height} @${devicePixelRatio}x`,
        population: st.population, msPerDay: +blocks[1].toFixed(1), saveMs: Math.round(t2 - t1), saveKB: Math.round(save.length / 1024),
        parseMs: Math.round(t4 - t3), heapMB: performance.memory ? Math.round(performance.memory.usedJSHeapSize / 1048576) : null,
        fps16x: +fps.toFixed(1), branch: active?.name,
      };
    } finally {
      this.running = false;
    }
  },

  render(el) {
    el.innerHTML = `<h4>性能の計測</h4><p class="empty" style="font-size:12.5px">この端末での速さを測ります（約20秒）。世界の複製で計測するので、遊んでいる世界は進みません。結果はコピーして共有できます。</p>
      <div class="row"><button class="btn" id="benchRun">計測する</button></div><pre id="benchOut" style="white-space:pre-wrap;font-family:var(--num);font-size:11.5px"></pre>`;
    el.querySelector('#benchRun').addEventListener('click', async () => {
      const out = el.querySelector('#benchOut');
      try {
        const r = await this.run((m) => (out.textContent = m));
        if (!r) return;
        out.textContent = [
          `人口 ${r.population}人`, `シミュレーション 1日あたり ${r.msPerDay} ms`, `保存 ${r.saveKB} KB・${r.saveMs} ms（解析 ${r.parseMs} ms）`,
          `メモリ ${r.heapMB ?? '取得不可'} MB`, `描画（16倍速） ${r.fps16x} fps`, `画面 ${r.screen}`, r.device,
        ].join('\n');
        window.__benchResult = r;
      } catch (e) {
        out.textContent = '計測に失敗しました：' + e;
      }
    });
  },
};
