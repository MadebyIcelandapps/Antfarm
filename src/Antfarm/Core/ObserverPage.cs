namespace Antfarm.Core;

/// <summary>
/// The page served at the observation window. Kept as one static string so the
/// mod stays a single .tmod with no loose asset files to go missing.
/// </summary>
internal static class ObserverPage
{
    public const string Html =
        """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Antfarm</title>
        <style>
          :root { color-scheme: dark; }
          * { box-sizing: border-box; }
          body {
            margin: 0; padding: 18px;
            background: #0e0f12; color: #d7dae0;
            font: 13px/1.5 ui-monospace, SFMono-Regular, Consolas, monospace;
          }
          h1 { font-size: 15px; font-weight: 600; margin: 0 0 2px; letter-spacing: .04em; }
          .sub { color: #6f7684; margin-bottom: 12px; }
          .bar { display: flex; gap: 8px; align-items: center; margin-bottom: 10px; flex-wrap: wrap; }
          button {
            background: #1a1d23; color: #d7dae0; border: 1px solid #2b2f38;
            border-radius: 5px; padding: 4px 11px; font: inherit; cursor: pointer;
          }
          button:hover { background: #22262e; border-color: #3a3f4a; }
          .hint { color: #565c68; }
          #mapwrap {
            background: #08090b; border: 1px solid #23262d; border-radius: 6px;
            padding: 8px; overflow: hidden;
          }
          canvas {
            display: block; width: 100%; height: auto;
            image-rendering: pixelated;
            cursor: grab; touch-action: none;
          }
          canvas.drag { cursor: grabbing; }
          table { border-collapse: collapse; margin-top: 16px; width: 100%; min-width: 760px; }
          th, td { text-align: right; padding: 4px 9px; border-bottom: 1px solid #1c1f25; }
          th { color: #6f7684; font-weight: 500; }
          th:first-child, td:first-child, th:nth-child(2), td:nth-child(2) { text-align: left; }
          tbody tr { cursor: pointer; }
          tbody tr:hover { background: #16191f; }
          .swatch { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 7px; }
          .totals { margin-top: 14px; color: #6f7684; }
          .totals b { color: #d7dae0; font-weight: 600; }
          .dead { color: #e0714f; }
          .panes { display: flex; gap: 12px; margin-top: 14px; flex-wrap: wrap; }
          .panes > div {
            flex: 1 1 340px; max-height: 210px; overflow-y: auto;
            border: 1px solid #23262d; border-radius: 6px; background: #0a0b0e; padding: 6px 10px;
          }
          #feed div, #legends div { padding: 2px 0; border-bottom: 1px solid #14171c; }
          #feed div:last-child, #legends div:last-child { border-bottom: 0; }
          #legends .hdr { color: #6f7684; border-bottom: 1px solid #23262d; padding-bottom: 4px; }
          #legends .gone { color: #6f7684; }
          #legends .risen { color: #6fc48a; }
          .k-battle { color: #e0714f; }
          .k-strike { color: #e0b64f; }
          .k-tech   { color: #6fc48a; }
          .k-colony { color: #8fa6c4; }
          tr.under-attack td { background: #2a1512; }
        </style>
        </head>
        <body>
          <h1>ANTFARM</h1>
          <div class="sub" id="status">connecting…</div>

          <div class="bar">
            <button id="fit">fit world</button>
            <button id="zin">+</button>
            <button id="zout">&minus;</button>
            <span class="hint" id="zoomlabel"></span>
            <span class="hint">scroll to zoom · drag to pan · click a tribe to jump to it</span>
          </div>

          <div id="mapwrap"><canvas id="map"></canvas></div>

          <div class="bar" id="scrub" style="margin-top:10px">
            <button id="play">&#9654; play history</button>
            <input type="range" id="seek" min="0" max="0" value="0" style="flex:1; min-width:220px">
            <button id="golive">live</button>
            <span class="hint" id="when"></span>
          </div>
          <div class="totals" id="totals"></div>
          <div class="panes">
            <div id="feed"></div>
            <div id="legends"></div>
          </div>
          <table id="tribes">
            <thead><tr>
              <th>tribe</th><th>home</th><th>pop</th><th>cap</th><th>towns</th><th>rooms</th>
              <th>mined</th><th>stored</th><th>bars</th><th>stock</th><th>built</th>
              <th>army</th><th>kills</th><th>lost</th>
            </tr></thead>
            <tbody></tbody>
          </table>

        <script>
        // ---------------------------------------------------------------
        // Rendering is decoupled from fetching.
        //
        // The first version called fetch('/map.bin') from the mousemove
        // handler, so a drag fired sixty network round trips a second and
        // every pixel of movement waited on Cloudflare before drawing. Panning
        // and zooming were unusable.
        //
        // Now the last fetched region is kept in an offscreen canvas. Panning
        // and zooming just blit a sub-rectangle of it, which is instant and
        // never touches the network. A crisper region is fetched once the view
        // stops moving.
        // ---------------------------------------------------------------

        const cv  = document.getElementById('map');
        const ctx = cv.getContext('2d', { alpha: false });

        const cache  = document.createElement('canvas');
        const cctx   = cache.getContext('2d', { alpha: false });
        let cacheRegion = null;          // {x,y,w,h} in tiles

        let world = { w: 0, h: 0 };
        let view  = null;                // {x,y,w,h} in tiles
        let tribes = [];
        let pal32 = null;
        let lastMined = -1, stalls = 0;
        let fetching = false, refetch = false;
        let settleTimer = null;

        const VIEW_W = 1100;             // logical pixels across the visible canvas

        function esc(s) { return String(s).replace(/[&<>]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c])); }
        function clamp(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // Packed little-endian ABGR, so the pixel loop is one 32 bit store per
        // cell instead of four byte stores.
        function pack(r, g, b) { return (255 << 24) | (b << 16) | (g << 8) | r; }

        function buildPalette() {
          const p = new Uint32Array(64);
          p.fill(pack(255, 0, 255));
          p[0] = pack(16, 17, 21);       // open air or natural cave
          p[1] = pack(58, 62, 71);       // untouched rock
          for (const t of tribes) {
            const h = t.colour;
            p[2 + t.id] = pack(parseInt(h.slice(1,3),16), parseInt(h.slice(3,5),16), parseInt(h.slice(5,7),16));
          }
          pal32 = p;
        }

        function fitWorld() {
          if (!world.w) return;
          view = { x: 0, y: 0, w: world.w, h: world.h };
        }

        function clampView() {
          const minW = 48;
          view.w = clamp(view.w, minW, world.w);
          view.h = clamp(view.h, minW * (world.h / world.w), world.h);
          view.x = clamp(view.x, 0, world.w - view.w);
          view.y = clamp(view.y, 0, world.h - view.h);
        }

        // Instant. Blits the cached region, scaled and offset to the view.
        function paint() {
          if (!view) return;

          const W = VIEW_W;
          const H = Math.max(1, Math.round(W * view.h / view.w));

          if (cv.width !== W || cv.height !== H) { cv.width = W; cv.height = H; }
          ctx.imageSmoothingEnabled = false;
          ctx.fillStyle = '#08090b';
          ctx.fillRect(0, 0, W, H);

          if (!cacheRegion || !cache.width) return;

          const sx = (view.x - cacheRegion.x) / cacheRegion.w * cache.width;
          const sy = (view.y - cacheRegion.y) / cacheRegion.h * cache.height;
          const sw = view.w / cacheRegion.w * cache.width;
          const sh = view.h / cacheRegion.h * cache.height;

          ctx.drawImage(cache, sx, sy, sw, sh, 0, 0, W, H);
        }

        // Decode one frame, live or historical, into the offscreen cache. Both
        // sources use the identical byte layout, which is why the recorder and
        // the live map share one renderer on the server.
        function loadFrame(buf) {
          const dv = new DataView(buf);
          const vw = dv.getInt32(0, true), vh = dv.getInt32(4, true);
          const rx = dv.getInt32(8, true), ry = dv.getInt32(12, true);
          const step = dv.getInt32(16, true);
          if (vw <= 0 || vh <= 0) return 0;

          if (!pal32) buildPalette();

          const cells = new Uint8Array(buf, 20);
          if (cache.width !== vw || cache.height !== vh) { cache.width = vw; cache.height = vh; }

          const img = cctx.createImageData(vw, vh);
          const out = new Uint32Array(img.data.buffer);
          for (let i = 0; i < cells.length; i++) out[i] = pal32[cells[i]];
          cctx.putImageData(img, 0, 0);

          cacheRegion = { x: rx, y: ry, w: vw * step, h: vh * step };
          return step;
        }

        async function fetchMap() {
          if (!view || replaying) return;
          if (fetching) { refetch = true; return; }
          fetching = true;

          try {
            const q = '?x=' + Math.round(view.x) + '&y=' + Math.round(view.y) +
                      '&w=' + Math.round(view.w) + '&h=' + Math.round(view.h);
            const step = loadFrame(await (await fetch('/map.bin' + q)).arrayBuffer());
            if (!step) return;

            document.getElementById('zoomlabel').textContent =
              step === 1 ? 'tile detail' : ('1px = ' + step + ' tiles');

            paint();
          } catch (e) {
            /* the periodic refresh will try again */
          } finally {
            fetching = false;
            if (refetch) { refetch = false; setTimeout(fetchMap, 0); }
          }
        }

        // Draw immediately, fetch crisper data once the view settles.
        function touched() {
          paint();
          clearTimeout(settleTimer);
          settleTimer = setTimeout(fetchMap, 160);
        }

        function zoomAt(fx, fy, factor) {
          if (!view) return;
          const tx = view.x + fx * view.w;
          const ty = view.y + fy * view.h;
          const nw = clamp(view.w * factor, 48, world.w);
          const scale = nw / view.w;
          view.w = nw;
          view.h *= scale;
          view.x = tx - fx * view.w;
          view.y = ty - fy * view.h;
          clampView();
          touched();
        }

        cv.addEventListener('wheel', e => {
          e.preventDefault();
          const r = cv.getBoundingClientRect();
          zoomAt((e.clientX - r.left) / r.width, (e.clientY - r.top) / r.height,
                 e.deltaY < 0 ? 0.8 : 1.25);
        }, { passive: false });

        let drag = null;
        cv.addEventListener('pointerdown', e => {
          drag = { x: e.clientX, y: e.clientY };
          cv.setPointerCapture(e.pointerId);
          cv.classList.add('drag');
        });
        cv.addEventListener('pointerup', e => {
          drag = null;
          cv.classList.remove('drag');
          clearTimeout(settleTimer);
          fetchMap();
        });
        cv.addEventListener('pointermove', e => {
          if (!drag || !view) return;
          const r = cv.getBoundingClientRect();
          view.x -= (e.clientX - drag.x) * (view.w / r.width);
          view.y -= (e.clientY - drag.y) * (view.h / r.height);
          drag = { x: e.clientX, y: e.clientY };
          clampView();
          paint();                       // no network while the mouse is down
        });

        document.getElementById('fit').onclick  = () => { fitWorld(); touched(); };
        document.getElementById('zin').onclick  = () => zoomAt(.5, .5, 0.7);
        document.getElementById('zout').onclick = () => zoomAt(.5, .5, 1 / 0.7);

        function jumpTo(t) {
          if (!world.w || !view) return;
          const aspect = view.h / view.w;
          const span = 340;
          view = { x: t.x - span / 2, y: t.y - span * aspect / 2, w: span, h: span * aspect };
          clampView();
          touched();
        }

        // The table is rebuilt from scratch each refresh, which throws away any
        // row the pointer is over. Rows are keyed by tribe id and their cells
        // updated in place instead.
        const rows = new Map();

        function renderTable(list) {
          const tb = document.querySelector('#tribes tbody');

          for (const t of list) {
            let row = rows.get(t.id);
            if (!row) {
              row = document.createElement('tr');
              row.innerHTML = '<td></td>'.repeat(14);
              row.onclick = () => jumpTo(rows.get(t.id).data);
              rows.set(t.id, row);
              tb.appendChild(row);
            }

            // Name, colour and undead state can all change: a tribe that rises
            // keeps its name but turns a sickly green.
            row.cells[0].innerHTML =
              '<span class="swatch" style="background:' + esc(t.colour) + '"></span>' +
              esc(t.name) + (t.undead ? ' <span class="risen">risen</span>' : '') +
              ' <span class="hint">' + esc(t.trait) + '</span>';

            row.data = t;
            row.classList.toggle('under-attack', !!t.threat);

            const v = [null, t.x + ',' + t.y, t.villagers, t.cap, t.settlements, t.rooms,
                       t.mined, t.stored, t.bars, t.stock, t.built,
                       t.armed + '/' + t.soldiers, t.kills, t.losses];
            for (let i = 1; i < v.length; i++) {
              const s = String(v[i]);
              if (row.cells[i].textContent !== s) row.cells[i].textContent = s;
            }
          }
        }

        function render(stats) {
          const first = !world.w;
          tribes = stats.tribes;
          buildPalette();

          if (first && stats.worldW > 0) {
            world = { w: stats.worldW, h: stats.worldH };
            fitWorld();
            fetchMap();
          }

          renderTable(stats.tribes);

          let mined = 0, stored = 0, pop = 0, built = 0, towns = 0;
          for (const t of stats.tribes) {
            mined += t.mined; stored += t.stored; pop += t.villagers;
            built += t.built; towns += t.settlements;
          }

          if (mined === lastMined) stalls++; else stalls = 0;
          lastMined = mined;

          document.getElementById('totals').innerHTML =
            '<b>' + mined.toLocaleString() + '</b> tiles dug &nbsp;·&nbsp; ' +
            '<b>' + stored.toLocaleString() + '</b> items stockpiled &nbsp;·&nbsp; ' +
            '<b>' + built.toLocaleString() + '</b> blocks built &nbsp;·&nbsp; ' +
            '<b>' + pop + '</b> villagers in <b>' + towns + '</b> settlements' +
            (stats.headlessTicks > 0 ? ' &nbsp;·&nbsp; running with nobody online' : '');

          document.getElementById('status').innerHTML = stalls >= 8
            ? '<span class="dead">not digging &mdash; nothing has changed in ' + (stalls * 0.7 | 0) + 's</span>'
            : 'live';
        }

        async function pollStats() {
          try {
            render(await (await fetch('/stats')).json());
          } catch (e) {
            document.getElementById('status').innerHTML =
              '<span class="dead">server not reachable &mdash; is the world running?</span>';
          }
        }

        // The feed. Only new entries are prepended, so the list does not jump
        // around under the pointer while you are reading it.
        let lastEventId = -1;

        async function pollEvents() {
          try {
            const d = await (await fetch('/events')).json();
            const feed = document.getElementById('feed');
            const fresh = d.events.filter(e => e.id > lastEventId).reverse();

            for (const e of fresh) {
              const div = document.createElement('div');
              div.className = 'k-' + e.kind;
              div.textContent = e.text;
              feed.prepend(div);
              lastEventId = Math.max(lastEventId, e.id);
            }

            while (feed.childNodes.length > 120) feed.removeChild(feed.lastChild);
            if (!feed.childNodes.length) feed.innerHTML = '<div class="hint">no events yet</div>';
          } catch (e) { /* next tick */ }
        }

        // ---------------------------------------------------------------
        // History. Frames are whole-world snapshots recorded on the server, so
        // scrubbing loads one into the same cache the live map uses and every
        // pan and zoom keeps working over the past.
        // ---------------------------------------------------------------

        let frames = 0, replaying = false, playing = false, playTimer = null;

        const seek   = document.getElementById('seek');
        const whenEl = document.getElementById('when');

        async function showFrame(i) {
          try {
            const buf = await (await fetch('/timelapse/frame?i=' + i)).arrayBuffer();
            if (loadFrame(buf)) paint();
          } catch (e) { /* skip a bad frame rather than stopping playback */ }
        }

        function setLive() {
          replaying = false;
          playing = false;
          clearInterval(playTimer);
          document.getElementById('play').innerHTML = '&#9654; play history';
          whenEl.textContent = '';
          fetchMap();
        }

        seek.addEventListener('input', () => {
          replaying = true;
          playing = false;
          clearInterval(playTimer);
          document.getElementById('play').innerHTML = '&#9654; play history';
          const i = +seek.value;
          whenEl.textContent = 'frame ' + (i + 1) + ' of ' + frames;
          showFrame(i);
        });

        document.getElementById('golive').onclick = setLive;

        document.getElementById('play').onclick = () => {
          if (playing) { setLive(); return; }
          if (frames < 2) return;

          replaying = true;
          playing = true;
          document.getElementById('play').innerHTML = '&#10073;&#10073; stop';

          // Sample rather than play every frame. A year at fifteen minute
          // intervals is about 35,000 frames, which at 120ms each would be a
          // seventy minute film. Capping the playback at ~300 frames keeps the
          // whole history to roughly forty seconds no matter how long it runs.
          const step = Math.max(1, Math.ceil(frames / 300));

          let i = 0;
          playTimer = setInterval(() => {
            if (!playing) return;
            if (i >= frames) { setLive(); return; }
            seek.value = i;
            whenEl.textContent = 'frame ' + (i + 1) + ' of ' + frames +
                                 (step > 1 ? ' (every ' + step + ')' : '');
            showFrame(i);
            i += step;
          }, 120);
        };

        async function pollTimelapse() {
          try {
            const d = await (await fetch('/timelapse')).json();
            frames = d.frames | 0;
            seek.max = Math.max(0, frames - 1);
            document.getElementById('scrub').style.display = frames > 1 ? 'flex' : 'none';
          } catch (e) { /* next tick */ }
        }

        // Hall of fame. Costs nothing to produce: every villager has been
        // carrying its own record all along, it just had nowhere to be read.
        async function pollLegends() {
          try {
            const d = await (await fetch('/legends')).json();
            const el = document.getElementById('legends');

            let html = '<div class="hdr">hall of fame &mdash; blocks dug</div>';
            for (const l of d.legends) {
              const state = l.undead ? '<span class="risen">risen</span>'
                          : l.alive  ? ''
                          : '<span class="gone">died at depth ' + l.depth + '</span>';
              html += '<div><span class="swatch" style="background:' + esc(l.colour) + '"></span>' +
                      esc(l.name) + ' of ' + esc(l.tribe) + ' &nbsp;<b>' +
                      l.dug.toLocaleString() + '</b> blocks' +
                      (l.kills ? ', ' + l.kills + ' kills' : '') +
                      (state ? ' &nbsp;' + state : '') + '</div>';
            }

            el.innerHTML = html || '<div class="hint">nobody yet</div>';
          } catch (e) { /* next tick */ }
        }

        pollLegends();
        setInterval(pollLegends, 5000);

        pollTimelapse();
        setInterval(pollTimelapse, 30000);

        pollStats();
        pollEvents();
        setInterval(pollStats, 700);
        setInterval(pollEvents, 2500);

        // Refresh the map itself less often, and never mid-drag.
        setInterval(() => { if (!drag) fetchMap(); }, 1500);
        </script>
        </body>
        </html>
        """;
}
