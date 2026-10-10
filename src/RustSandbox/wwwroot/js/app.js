/* Rust Sandbox — browser helpers
   - RSM.mapgen  : seeded procedural map preview (approximation of a Rust world, not Facepunch's generator)
   - RSM.liveMap : zoomable live map with heat layer, players and kill markers
   - small helpers used by Blazor components (console scroll, clipboard, hotkeys) */
(function () {
  "use strict";

  // ------------------------------------------------------------------ seeded noise
  function mulberry32(a) {
    return function () {
      a |= 0; a = (a + 0x6D2B79F5) | 0;
      let t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  function makeNoise(rand) {
    const perm = new Uint8Array(512);
    const p = new Uint8Array(256);
    for (let i = 0; i < 256; i++) p[i] = i;
    for (let i = 255; i > 0; i--) { const j = Math.floor(rand() * (i + 1)); const t = p[i]; p[i] = p[j]; p[j] = t; }
    for (let i = 0; i < 512; i++) perm[i] = p[i & 255];
    const grad = new Float32Array(512);
    for (let i = 0; i < 512; i++) grad[i] = rand();
    const fade = t => t * t * (3 - 2 * t);
    function value(x, y) {
      const xi = Math.floor(x), yi = Math.floor(y);
      const xf = x - xi, yf = y - yi;
      const X = xi & 255, Y = yi & 255;
      const a = grad[perm[X + perm[Y]]], b = grad[perm[X + 1 + perm[Y]]];
      const c = grad[perm[X + perm[Y + 1]]], d = grad[perm[X + 1 + perm[Y + 1]]];
      const u = fade(xf), v = fade(yf);
      return (a + (b - a) * u) * (1 - v) + (c + (d - c) * u) * v;
    }
    return function fbm(x, y, octaves) {
      let sum = 0, amp = 0.5, freq = 1, norm = 0;
      for (let o = 0; o < octaves; o++) {
        sum += value(x * freq, y * freq) * amp;
        norm += amp; amp *= 0.5; freq *= 2.03;
      }
      return sum / norm; // 0..1
    };
  }

  const smooth = (a, b, x) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };
  const lerp = (a, b, t) => a + (b - a) * t;
  const mix = (c1, c2, t) => [lerp(c1[0], c2[0], t), lerp(c1[1], c2[1], t), lerp(c1[2], c2[2], t)];

  const PAL = {
    deep: [22, 48, 66], shallow: [48, 98, 112], beach: [201, 185, 138],
    grass: [96, 124, 64], forest: [58, 86, 44], tundra: [124, 126, 88],
    arid: [186, 154, 98], aridDark: [160, 128, 78], snow: [222, 228, 232], snowRock: [178, 188, 194],
    rock: [128, 124, 114], river: [70, 128, 146], road: [214, 200, 168],
  };

  const MONUMENTS = [
    { name: "Launch Site", min: 3000, big: true }, { name: "Military Tunnels", min: 3000, big: true },
    { name: "Airfield", min: 2500, big: true }, { name: "Train Yard", min: 2500 }, { name: "Power Plant", min: 2500 },
    { name: "Water Treatment", min: 2500 }, { name: "The Dome", min: 2000 }, { name: "Sewer Branch", min: 2000 },
    { name: "Satellite Dish", min: 2000 }, { name: "Junkyard", min: 2000 }, { name: "Outpost", min: 1000, safe: true },
    { name: "Bandit Camp", min: 1000, safe: true }, { name: "Harbor", min: 2500, coast: true }, { name: "Harbor", min: 4000, coast: true },
    { name: "Lighthouse", min: 1500, coast: true }, { name: "Lighthouse", min: 3500, coast: true },
    { name: "Fishing Village", min: 1500, coast: true }, { name: "Fishing Village", min: 3000, coast: true },
    { name: "Ferry Terminal", min: 3500, coast: true }, { name: "Arctic Research Base", min: 3500, biome: "snow" },
    { name: "Missile Silo", min: 4000 }, { name: "Gas Station", min: 1000 }, { name: "Gas Station", min: 3000 },
    { name: "Supermarket", min: 1000 }, { name: "Supermarket", min: 3500 }, { name: "Mining Outpost", min: 2500 },
    { name: "Quarry", min: 1500 }, { name: "Quarry", min: 3000 }, { name: "Large Oil Rig", min: 3000, ocean: true },
    { name: "Oil Rig", min: 2000, ocean: true },
  ];

  /** Build the height/biome fields for a seed. Deterministic for (seed, size, res). */
  function generate(seed, size, res) {
    const rand = mulberry32((seed ^ Math.imul(size, 2654435761)) >>> 0);
    const nHeight = makeNoise(rand), nCoast = makeNoise(rand), nBiome = makeNoise(rand), nMoist = makeNoise(rand);
    const scale = 2.2 + size / 1300;          // bigger worlds → more features
    const ox = rand() * 1000, oy = rand() * 1000;
    const h = new Float32Array(res * res);
    const biome = new Uint8Array(res * res);  // 0 temperate, 1 tundra, 2 arid, 3 snow
    const moist = new Float32Array(res * res);
    // landmass shape: a slightly irregular square-ish island like Rust's
    const squareness = 3.2 + rand() * 1.4;
    for (let y = 0; y < res; y++) {
      const v = y / res;
      for (let x = 0; x < res; x++) {
        const u = x / res;
        const dx = Math.abs(u - 0.5) * 2, dy = Math.abs(v - 0.5) * 2;
        const d = Math.pow(Math.pow(dx, squareness) + Math.pow(dy, squareness), 1 / squareness);
        const coast = (nCoast(u * 3 + ox, v * 3 + oy, 4) - 0.5) * 0.38;
        const mask = 1 - smooth(0.7, 0.97, d + coast);
        const base = nHeight(u * scale + ox, v * scale + oy, 6);
        let height = mask * 0.78 + (base - 0.5) * 0.9 - 0.34;
        // occasional ridges
        const ridge = 1 - Math.abs(nHeight(u * scale * 0.6 + 50, v * scale * 0.6 + 50, 4) * 2 - 1);
        height += Math.pow(ridge, 6) * 0.35 * mask;
        const i = y * res + x;
        h[i] = height;
        const jitter = (nBiome(u * 4 + 9, v * 4 + 9, 3) - 0.5) * 0.22;
        const lat = v + jitter;           // v=0 is north
        biome[i] = lat < 0.15 ? 3 : lat < 0.27 ? 1 : lat > 0.68 ? 2 : 0;
        moist[i] = nMoist(u * 6 + 3, v * 6 + 3, 4);
      }
    }
    return { h, biome, moist, res, rand };
  }

  function traceRivers(f, count) {
    const { h, res, rand } = f;
    const rivers = [];
    for (let attempt = 0; attempt < count * 25 && rivers.length < count; attempt++) {
      let x = Math.floor(rand() * res), y = Math.floor(rand() * res);
      if (h[y * res + x] < 0.22) continue;
      const path = [[x, y]], seen = new Set([y * res + x]);
      let reachedSea = false;
      for (let step = 0; step < res * 2; step++) {
        let best = null, bestH = Infinity;
        for (let oy = -1; oy <= 1; oy++) for (let ox2 = -1; ox2 <= 1; ox2++) {
          if (!ox2 && !oy) continue;
          const nx = x + ox2, ny = y + oy;
          if (nx < 0 || ny < 0 || nx >= res || ny >= res) continue;
          const k = ny * res + nx;
          if (seen.has(k)) continue;
          const hv = h[k] + rand() * 0.004;
          if (hv < bestH) { bestH = hv; best = [nx, ny]; }
        }
        if (!best) break;
        [x, y] = best;
        seen.add(y * res + x);
        path.push([x, y]);
        if (h[y * res + x] < 0) { reachedSea = true; break; }
      }
      if (reachedSea && path.length > res * 0.12) rivers.push(path);
    }
    return rivers;
  }

  function landAt(f, u, v) {
    const x = Math.min(f.res - 1, Math.max(0, Math.floor(u * f.res)));
    const y = Math.min(f.res - 1, Math.max(0, Math.floor(v * f.res)));
    const i = y * f.res + x;
    return { h: f.h[i], biome: f.biome[i] };
  }

  function placeMonuments(f, size) {
    const placed = [];
    const minGap = 0.075;
    for (const m of MONUMENTS) {
      if (size < m.min) continue;
      for (let tries = 0; tries < 400; tries++) {
        const u = 0.08 + f.rand() * 0.84, v = 0.08 + f.rand() * 0.84;
        const c = landAt(f, u, v);
        if (m.ocean) { if (c.h > -0.12 || c.h < -0.45) continue; }
        else if (m.coast) { if (c.h < 0.015 || c.h > 0.07) continue; }
        else { if (c.h < 0.08 || c.h > 0.55) continue; }
        if (m.biome === "snow" && c.biome !== 3) continue;
        if (m.safe && c.biome === 3) continue;
        if (placed.some(p => Math.hypot(p.u - u, p.v - v) < (m.big ? minGap * 1.5 : minGap))) continue;
        placed.push({ name: m.name, u, v, ocean: !!m.ocean, safe: !!m.safe });
        break;
      }
    }
    return placed;
  }

  function colorAt(f, i, slopeLight) {
    const hv = f.h[i];
    let c;
    if (hv < 0) {
      c = mix(PAL.shallow, PAL.deep, smooth(-0.02, -0.3, hv));
    } else if (hv < 0.025) {
      c = PAL.beach;
    } else {
      const b = f.biome[i], m = f.moist[i];
      if (b === 3) c = mix(PAL.snow, PAL.snowRock, smooth(0.5, 0.75, hv));
      else if (b === 1) c = mix(PAL.tundra, PAL.forest, smooth(0.5, 0.75, m) * 0.5);
      else if (b === 2) c = mix(PAL.arid, PAL.aridDark, smooth(0.4, 0.7, m));
      else c = mix(PAL.grass, PAL.forest, smooth(0.45, 0.7, m));
      if (b !== 3) c = mix(c, PAL.rock, smooth(0.55, 0.8, hv));
    }
    const k = hv < 0 ? 1 : slopeLight;
    return [c[0] * k, c[1] * k, c[2] * k];
  }

  /**
   * Render a preview map into a canvas. Returns { monuments, landPct }.
   * opts: { labels: bool, rivers: bool, ringRoad: bool, monuments: bool }
   */
  function renderMap(canvas, seed, size, opts) {
    opts = Object.assign({ labels: false, rivers: true, ringRoad: true, monuments: true, resolution: 0 }, opts || {});
    const W = opts.resolution || canvas.width || 512;
    canvas.width = W; canvas.height = W;
    const ctx = canvas.getContext("2d");
    const res = Math.min(W, 640);
    const f = generate(seed | 0, size | 0, res);
    const img = ctx.createImageData(res, res);
    let land = 0;
    for (let y = 0; y < res; y++) {
      for (let x = 0; x < res; x++) {
        const i = y * res + x;
        const hx = f.h[Math.min(res - 1, x + 1) + y * res] - f.h[Math.max(0, x - 1) + y * res];
        const hy = f.h[x + Math.min(res - 1, y + 1) * res] - f.h[x + Math.max(0, y - 1) * res];
        const light = Math.min(1.18, Math.max(0.72, 1 - (hx + hy) * res * 0.06));
        const c = colorAt(f, i, light);
        if (f.h[i] >= 0) land++;
        const o = i * 4;
        img.data[o] = c[0]; img.data[o + 1] = c[1]; img.data[o + 2] = c[2]; img.data[o + 3] = 255;
      }
    }
    if (res === W) ctx.putImageData(img, 0, 0);
    else {
      const tmp = document.createElement("canvas"); tmp.width = res; tmp.height = res;
      tmp.getContext("2d").putImageData(img, 0, 0);
      ctx.imageSmoothingEnabled = true; ctx.imageSmoothingQuality = "high";
      ctx.drawImage(tmp, 0, 0, W, W);
    }
    const s = W / res;

    if (opts.rivers) {
      const rivers = traceRivers(f, Math.round(2 + size / 1500));
      ctx.strokeStyle = `rgb(${PAL.river.join(",")})`;
      ctx.lineCap = "round"; ctx.lineJoin = "round";
      for (const r of rivers) {
        ctx.lineWidth = Math.max(1, W / 260);
        ctx.beginPath();
        r.forEach(([x, y], k) => k ? ctx.lineTo((x + .5) * s, (y + .5) * s) : ctx.moveTo((x + .5) * s, (y + .5) * s));
        ctx.stroke();
      }
    }

    if (opts.ringRoad && size >= 2000) {
      ctx.strokeStyle = `rgba(${PAL.road.join(",")},0.55)`;
      ctx.lineWidth = Math.max(1, W / 320);
      ctx.setLineDash([W / 90, W / 160]);
      ctx.beginPath();
      let drawing = false;
      for (let a = 0; a <= Math.PI * 2 + 0.01; a += 0.01) {
        const wob = Math.sin(a * 5 + seed % 7) * 0.025;
        const r = 0.31 + wob;
        const u = 0.5 + Math.cos(a) * r, v = 0.5 + Math.sin(a) * r * 0.97;
        const onLand = landAt(f, u, v).h > 0.02;
        if (onLand && !drawing) { ctx.moveTo(u * W, v * W); drawing = true; }
        else if (onLand) ctx.lineTo(u * W, v * W);
        else drawing = false;
      }
      ctx.stroke();
      ctx.setLineDash([]);
    }

    const monuments = opts.monuments ? placeMonuments(f, size) : [];
    if (opts.monuments) {
      const r = Math.max(2.5, W / 150);
      for (const m of monuments) {
        const x = m.u * W, y = m.v * W;
        ctx.fillStyle = m.safe ? "#55a892" : m.ocean ? "#6b9bd1" : "#dd6a3f";
        ctx.strokeStyle = "rgba(18,22,26,.9)"; ctx.lineWidth = Math.max(1, r / 2.5);
        ctx.beginPath(); ctx.rect(x - r, y - r, r * 2, r * 2); ctx.fill(); ctx.stroke();
        if (opts.labels) {
          ctx.font = `600 ${Math.max(10, W / 62)}px Barlow, sans-serif`;
          ctx.textBaseline = "middle";
          ctx.lineWidth = 3; ctx.strokeStyle = "rgba(18,22,26,.85)"; ctx.fillStyle = "#f1ece0";
          ctx.strokeText(m.name, x + r * 1.8, y); ctx.fillText(m.name, x + r * 1.8, y);
        }
      }
    }
    return { monuments: monuments.map(m => ({ name: m.name, u: m.u, v: m.v })), landPct: Math.round(land / (res * res) * 100) };
  }

  // ------------------------------------------------------------------ live map
  const HEAT_RAMP = (() => {
    const c = document.createElement("canvas"); c.width = 256; c.height = 1;
    const g = c.getContext("2d");
    const grad = g.createLinearGradient(0, 0, 256, 0);
    grad.addColorStop(0.0, "rgba(85,168,146,0)");
    grad.addColorStop(0.2, "rgba(85,168,146,0.55)");
    grad.addColorStop(0.45, "rgba(227,179,65,0.8)");
    grad.addColorStop(0.75, "rgba(221,106,63,0.92)");
    grad.addColorStop(1.0, "rgba(255,242,214,1)");
    g.fillStyle = grad; g.fillRect(0, 0, 256, 1);
    return g.getImageData(0, 0, 256, 1).data;
  })();

  function columnName(col) { let n = ""; col++; while (col > 0) { const r = (col - 1) % 26; n = String.fromCharCode(65 + r) + n; col = Math.floor((col - 1) / 26); } return n; }

  const maps = new Map();
  let nextId = 1;
  const BASE = 1024; // internal resolution for background and heat layers

  function createLiveMap(container, dotnet) {
    const id = nextId++;
    const canvas = document.createElement("canvas");
    const tip = document.createElement("div"); tip.className = "map-tip";
    const corner = document.createElement("div"); corner.className = "map-corner"; corner.textContent = "Scroll to zoom · drag to pan · double-click to reset";
    container.appendChild(canvas); container.appendChild(tip); container.appendChild(corner);
    const st = {
      id, container, canvas, tip, corner, dotnet,
      size: 4000, seed: 0, bg: document.createElement("canvas"), heat: document.createElement("canvas"),
      monuments: [], players: new Map(), kills: [], showGrid: true, showNames: true, showHeat: true, showKills: true, showMonuments: true,
      zoom: 1, panX: 0, panY: 0, dirty: true, raf: 0, hover: null, focus: null, drag: null,
    };
    st.bg.width = st.bg.height = BASE; st.heat.width = st.heat.height = BASE;

    const ro = new ResizeObserver(() => resize(st));
    ro.observe(container);
    st.ro = ro;

    canvas.addEventListener("wheel", e => {
      e.preventDefault();
      const r = canvas.getBoundingClientRect();
      const mx = (e.clientX - r.left) / r.width, my = (e.clientY - r.top) / r.height;
      const old = st.zoom;
      st.zoom = Math.min(12, Math.max(1, st.zoom * (e.deltaY < 0 ? 1.18 : 1 / 1.18)));
      // keep the point under the cursor fixed
      st.panX = mx - (mx - st.panX) * (st.zoom / old);
      st.panY = my - (my - st.panY) * (st.zoom / old);
      clampPan(st); invalidate(st);
    }, { passive: false });
    canvas.addEventListener("mousedown", e => { st.drag = { x: e.clientX, y: e.clientY, px: st.panX, py: st.panY, moved: false }; });
    window.addEventListener("mouseup", st.onUp = () => { setTimeout(() => { st.drag = null; }, 0); });
    canvas.addEventListener("mousemove", e => {
      const r = canvas.getBoundingClientRect();
      if (st.drag) {
        const dx = (e.clientX - st.drag.x) / r.width, dy = (e.clientY - st.drag.y) / r.height;
        if (Math.abs(dx) + Math.abs(dy) > 0.004) st.drag.moved = true;
        st.panX = st.drag.px + dx; st.panY = st.drag.py + dy; clampPan(st); invalidate(st);
      }
      hover(st, e.clientX - r.left, e.clientY - r.top, r.width);
    });
    canvas.addEventListener("mouseleave", () => { st.tip.style.display = "none"; st.hover = null; invalidate(st); });
    canvas.addEventListener("dblclick", () => { st.zoom = 1; st.panX = 0; st.panY = 0; invalidate(st); });
    canvas.addEventListener("click", () => {
      if (st.drag && st.drag.moved) return;
      if (st.hover && st.hover.player && st.dotnet) st.dotnet.invokeMethodAsync("OnPlayerClicked", st.hover.player.id);
    });

    maps.set(id, st);
    resize(st);
    return id;
  }

  function clampPan(st) {
    const min = 1 - st.zoom;
    st.panX = Math.min(0, Math.max(min, st.panX));
    st.panY = Math.min(0, Math.max(min, st.panY));
  }

  function resize(st) {
    const dpr = window.devicePixelRatio || 1;
    const w = st.container.clientWidth;
    if (!w) return;
    st.canvas.width = Math.round(w * dpr);
    st.canvas.height = Math.round(st.container.clientHeight * dpr);
    invalidate(st);
  }

  function invalidate(st) {
    st.dirty = true;
    if (!st.raf) st.raf = requestAnimationFrame(() => draw(st));
  }

  // The view covers the world plus the map image's ocean border (st.pad metres per side), so oil rigs
  // and other spots out in the sea stay visible on big maps. Generated previews have no border.
  const extent = st => st.size + 2 * (st.pad || 0);
  // world (x east, z north) → 0..1 view coords (u right, v down)
  const toUV = (st, x, z) => { const e = extent(st); return [(x + e / 2) / e, (e / 2 - z) / e]; };
  // 0..1 across the playable world → 0..1 across the view
  const worldToView = (st, t) => ((st.pad || 0) + t * st.size) / extent(st);
  const monumentUV = (st, m) => m.real ? toUV(st, m.x, m.z) : [worldToView(st, m.u), worldToView(st, m.v)];

  function hover(st, px, py, cssW) {
    const s = cssW * st.zoom;
    const u = (px - st.panX * cssW) / s, v = (py - st.panY * cssW) / s;
    if (u < 0 || v < 0 || u > 1 || v > 1) { st.tip.style.display = "none"; return; }
    const e = extent(st), pad = st.pad || 0;
    const wx = u * e - e / 2, wz = e / 2 - v * e;
    const cell = 146.28571;
    const gx = u * e - pad, gy = v * e - pad;
    const grid = gx < 0 || gy < 0 || gx > st.size || gy > st.size ? "Ocean" : columnName(Math.floor(gx / cell)) + Math.floor(gy / cell);
    let best = null, bestD = 12 / s;
    for (const p of st.players.values()) {
      const [pu, pv] = toUV(st, p.x, p.z);
      const d = Math.hypot(pu - u, pv - v);
      if (d < bestD) { bestD = d; best = p; }
    }
    st.hover = { player: best };
    st.tip.style.left = px + "px"; st.tip.style.top = py + "px"; st.tip.style.display = "block";
    st.tip.innerHTML = best
      ? `<b>${escapeHtml(best.name)}</b> · ${Math.round(best.h)} hp<br><span style="color:#7d8a96">${grid} · click for stats</span>`
      : `${grid} <span style="color:#7d8a96">(${Math.round(wx)}, ${Math.round(wz)})</span>`;
    st.canvas.style.cursor = best ? "pointer" : st.drag ? "grabbing" : "crosshair";
    invalidate(st);
  }

  function escapeHtml(s) { return String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])); }

  function setWorld(id, opts) {
    const st = maps.get(id); if (!st) return;
    st.size = opts.size || 4000; st.seed = opts.seed || 0; st.pad = 0;
    // Real monument positions from the server (world metres) beat the generated preview's guesses.
    const real = Array.isArray(opts.monuments) && opts.monuments.length
      ? opts.monuments.map(m => ({ name: m.name, x: m.x, z: m.z, real: true }))
      : null;
    st.monuments = real || [];
    // Generated maps use the base size; real images get resized to their own resolution below.
    if (st.bg.width !== BASE) { st.bg.width = BASE; st.bg.height = BASE; }
    const g = st.bg.getContext("2d");
    g.fillStyle = "#0f1d26"; g.fillRect(0, 0, BASE, BASE);
    if (opts.imageUrl) {
      const img = new Image();
      // No crossOrigin: we only draw the image, never read its pixels, so CORS isn't needed.
      img.onload = () => {
        // The image may include an ocean border; crop it so the island fills the world area exactly.
        // No hand-tuned value: Rust map renders have a 500 px ocean border per side at any scale.
        const auto = img.naturalWidth > 1000 ? Math.round(500 * st.size / (img.naturalWidth - 1000)) : 0;
        const margin = Math.max(0, opts.imageMargin == null ? auto : opts.imageMargin);
        if (st.dotnet && opts.reportImage) st.dotnet.invokeMethodAsync("OnMapImageLoaded", img.naturalWidth, img.naturalHeight).catch(() => { });
        // Show the whole image, ocean border included; positions are converted with the same border.
        st.pad = margin;
        const sx = 0, sy = 0, sw = img.naturalWidth, sh = img.naturalHeight;
        // Keep the image's full detail (capped at 4096 px) so zooming in stays sharp.
        const res = Math.round(Math.min(4096, Math.max(BASE, sw)));
        st.bg.width = res; st.bg.height = res;
        const bg = st.bg.getContext("2d");
        bg.imageSmoothingEnabled = true; bg.imageSmoothingQuality = "high";
        bg.drawImage(img, sx, sy, sw, sh, 0, 0, res, res);
        st.monuments = real || [];
        if (st.lastHeat) setHeat(id, st.lastHeat[0], st.lastHeat[1]);
        invalidate(st);
      };
      img.onerror = () => { const r = renderMap(st.bg, st.seed, st.size, { resolution: BASE, monuments: !real, labels: false }); st.monuments = real || r.monuments; invalidate(st); };
      img.src = opts.imageUrl;
    } else {
      // generating a 1024² map takes a moment; let the UI paint first
      setTimeout(() => {
        const r = renderMap(st.bg, st.seed, st.size, { resolution: BASE, monuments: !real, labels: false });
        st.monuments = real || r.monuments; invalidate(st);
      }, 10);
    }
    invalidate(st);
  }

  /** points: [[x, z, weight], ...] in world metres */
  function setHeat(id, points, opts) {
    const st = maps.get(id); if (!st) return;
    opts = opts || {};
    st.lastHeat = [points, opts];
    const g = st.heat.getContext("2d");
    g.clearRect(0, 0, BASE, BASE);
    if (!points || !points.length) { invalidate(st); return; }
    const radius = Math.max(6, (opts.radiusMeters || 45) / extent(st) * BASE);
    let max = 0; for (const p of points) if (p[2] > max) max = p[2];
    const logMax = Math.log1p(max);
    const intensity = opts.intensity || 0.55;
    for (const p of points) {
      const [u, v] = toUV(st, p[0], p[1]);
      const a = Math.min(1, (Math.log1p(p[2]) / logMax) * intensity + 0.04);
      const grad = g.createRadialGradient(u * BASE, v * BASE, 0, u * BASE, v * BASE, radius);
      grad.addColorStop(0, `rgba(0,0,0,${a})`); grad.addColorStop(1, "rgba(0,0,0,0)");
      g.fillStyle = grad;
      g.fillRect(u * BASE - radius, v * BASE - radius, radius * 2, radius * 2);
    }
    const img = g.getImageData(0, 0, BASE, BASE), d = img.data;
    for (let i = 0; i < d.length; i += 4) {
      const a = d[i + 3];
      if (!a) continue;
      const o = a * 4;
      d[i] = HEAT_RAMP[o]; d[i + 1] = HEAT_RAMP[o + 1]; d[i + 2] = HEAT_RAMP[o + 2]; d[i + 3] = HEAT_RAMP[o + 3];
    }
    g.putImageData(img, 0, 0);
    invalidate(st);
  }

  /** players: [{id,name,x,z,h}], kills: [{x,z,kx,kz,pvp,ageSec,label}] */
  function setLive(id, players, kills) {
    const st = maps.get(id); if (!st) return;
    const now = performance.now();
    const seen = new Set();
    for (const p of players || []) {
      seen.add(p.id);
      const cur = st.players.get(p.id);
      if (cur) { cur.fromX = cur.x; cur.fromZ = cur.z; cur.toX = p.x; cur.toZ = p.z; cur.t0 = now; cur.name = p.name; cur.h = p.h; }
      else st.players.set(p.id, { id: p.id, name: p.name, x: p.x, z: p.z, fromX: p.x, fromZ: p.z, toX: p.x, toZ: p.z, t0: now, h: p.h });
    }
    for (const k of [...st.players.keys()]) if (!seen.has(k)) st.players.delete(k);
    st.kills = kills || [];
    invalidate(st);
  }

  function setOptions(id, opts) {
    const st = maps.get(id); if (!st) return;
    Object.assign(st, opts || {});
    invalidate(st);
  }

  function focusPlayer(id, steamId) {
    const st = maps.get(id); if (!st) return;
    st.focus = steamId || null;
    const p = steamId && st.players.get(steamId);
    if (p) {
      const [u, v] = toUV(st, p.x, p.z);
      st.zoom = Math.max(st.zoom, 3);
      st.panX = 0.5 - u * st.zoom; st.panY = 0.5 - v * st.zoom;
      clampPan(st);
    }
    invalidate(st);
  }

  function draw(st) {
    st.raf = 0;
    const c = st.canvas, ctx = c.getContext("2d");
    const W = c.width, H = c.height;
    const size = Math.min(W, H);
    const s = size * st.zoom;
    const ox = st.panX * size, oy = st.panY * size;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.fillStyle = "#0f1d26"; ctx.fillRect(0, 0, W, H);
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = "high";
    ctx.drawImage(st.bg, ox, oy, s, s);
    if (st.showHeat) ctx.drawImage(st.heat, ox, oy, s, s);
    const px = u => ox + u * s, py = v => oy + v * s;
    const dpr = window.devicePixelRatio || 1;

    // grid
    if (st.showGrid) {
      // The grid covers the playable world only, like the in-game map.
      const cell = 146.28571 / st.size;
      const cells = Math.ceil(1 / cell);
      const w = t => worldToView(st, t);
      ctx.strokeStyle = "rgba(255,255,255,0.09)"; ctx.lineWidth = 1;
      ctx.beginPath();
      for (let i = 0; i <= cells; i++) {
        const t = Math.min(1, i * cell);
        ctx.moveTo(px(w(t)), py(w(0))); ctx.lineTo(px(w(t)), py(w(1)));
        ctx.moveTo(px(w(0)), py(w(t))); ctx.lineTo(px(w(1)), py(w(t)));
      }
      ctx.stroke();
      if (s * st.size / extent(st) / cells > 34 * dpr) {
        ctx.fillStyle = "rgba(255,255,255,0.32)";
        ctx.font = `${10 * dpr}px Barlow, sans-serif`; ctx.textBaseline = "top";
        for (let cx = 0; cx < cells; cx++) for (let cy = 0; cy < cells; cy++) {
          const x = px(w(cx * cell)) + 3 * dpr, y = py(w(cy * cell)) + 2 * dpr;
          if (x > W || y > H || x < -40 || y < -20) continue;
          ctx.fillText(columnName(cx) + cy, x, y);
        }
      }
    }

    // monuments
    if (st.showMonuments && st.monuments.length) {
      ctx.font = `600 ${11 * dpr}px Barlow, sans-serif`; ctx.textBaseline = "middle";
      for (const m of st.monuments) {
        const [mu, mv] = monumentUV(st, m);
        const x = px(mu), y = py(mv);
        if (m.real) {
          ctx.fillStyle = "rgba(241,236,224,.9)"; ctx.strokeStyle = "rgba(18,22,26,.85)"; ctx.lineWidth = 1.5 * dpr;
          ctx.beginPath(); ctx.arc(x, y, 3 * dpr, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
        }
        if (m.real || st.zoom >= 1.6 || W > 700 * dpr) {
          ctx.lineWidth = 3 * dpr; ctx.strokeStyle = "rgba(18,22,26,.8)"; ctx.fillStyle = "rgba(241,236,224,.9)";
          ctx.strokeText(m.name, x + 7 * dpr, y); ctx.fillText(m.name, x + 7 * dpr, y);
        }
      }
    }

    // kills
    if (st.showKills) {
      for (const k of st.kills) {
        const fade = Math.max(0.15, 1 - k.ageSec / 900);
        const [u, v] = toUV(st, k.x, k.z);
        const x = px(u), y = py(v), r = 4 * dpr;
        if (k.pvp && (k.kx || k.kz)) {
          const [ku, kv] = toUV(st, k.kx, k.kz);
          ctx.strokeStyle = `rgba(224,88,76,${0.45 * fade})`; ctx.lineWidth = 1.2 * dpr;
          ctx.setLineDash([3 * dpr, 3 * dpr]);
          ctx.beginPath(); ctx.moveTo(px(ku), py(kv)); ctx.lineTo(x, y); ctx.stroke();
          ctx.setLineDash([]);
        }
        ctx.strokeStyle = k.pvp ? `rgba(224,88,76,${fade})` : `rgba(227,179,65,${fade})`;
        ctx.lineWidth = 2 * dpr;
        ctx.beginPath(); ctx.moveTo(x - r, y - r); ctx.lineTo(x + r, y + r); ctx.moveTo(x + r, y - r); ctx.lineTo(x - r, y + r); ctx.stroke();
      }
    }

    // players (interpolated between updates)
    const now = performance.now();
    let animating = false;
    ctx.font = `600 ${11 * dpr}px Barlow, sans-serif`; ctx.textBaseline = "middle";
    for (const p of st.players.values()) {
      const t = Math.min(1, (now - p.t0) / 1200);
      if (t < 1) animating = true;
      p.x = lerp(p.fromX, p.toX, t); p.z = lerp(p.fromZ, p.toZ, t);
      const [u, v] = toUV(st, p.x, p.z);
      const x = px(u), y = py(v);
      if (x < -20 || y < -20 || x > W + 20 || y > H + 20) continue;
      const focused = st.focus === p.id, hovered = st.hover && st.hover.player === p;
      const r = (focused || hovered ? 6 : 4.2) * dpr;
      ctx.fillStyle = focused ? "#dd6a3f" : p.h < 30 ? "#e0584c" : "#7fd3bd";
      ctx.strokeStyle = "rgba(10,14,17,.9)"; ctx.lineWidth = 2 * dpr;
      ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
      if (focused) { ctx.strokeStyle = "rgba(221,106,63,.5)"; ctx.lineWidth = 2 * dpr; ctx.beginPath(); ctx.arc(x, y, r + 6 * dpr, 0, Math.PI * 2); ctx.stroke(); }
      if (st.showNames && (st.zoom >= 2 || focused || hovered)) {
        ctx.lineWidth = 3 * dpr; ctx.strokeStyle = "rgba(10,14,17,.85)"; ctx.fillStyle = "#f1ece0";
        ctx.strokeText(p.name, x + r + 4 * dpr, y); ctx.fillText(p.name, x + r + 4 * dpr, y);
      }
    }
    if (animating) st.raf = requestAnimationFrame(() => draw(st));
  }

  function disposeLiveMap(id) {
    const st = maps.get(id); if (!st) return;
    st.ro.disconnect();
    window.removeEventListener("mouseup", st.onUp);
    if (st.raf) cancelAnimationFrame(st.raf);
    st.container.innerHTML = "";
    maps.delete(id);
  }

  // ------------------------------------------------------------------ small helpers
  let hotkeyRef = null;
  function onKey(e) {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
      e.preventDefault();
      if (hotkeyRef) hotkeyRef.invokeMethodAsync("OpenPalette");
    }
  }

  window.RSM = {
    mapgen: {
      render: (canvas, seed, size, opts) => renderMap(canvas, seed, size, opts),
      download: (canvas, filename) => {
        const a = document.createElement("a");
        a.href = canvas.toDataURL("image/png"); a.download = filename || "map.png"; a.click();
      },
    },
    liveMap: { create: createLiveMap, setWorld, setHeat, setLive, setOptions, focusPlayer, dispose: disposeLiveMap },
    console: {
      isNearBottom: el => !el || el.scrollHeight - el.scrollTop - el.clientHeight < 60,
      scrollToBottom: el => { if (el) el.scrollTop = el.scrollHeight; },
      // Tab completes commands instead of moving focus away from the console input.
      bindInput: el => {
        if (!el || el._rsmBound) return;
        el._rsmBound = true;
        el.addEventListener("keydown", e => { if (e.key === "Tab" && el.value.trim().length) e.preventDefault(); });
        el.focus();
      },
    },
    focus: el => { if (el) el.focus(); },
    copy: text => navigator.clipboard.writeText(text),
    registerHotkeys: ref => { hotkeyRef = ref; document.removeEventListener("keydown", onKey); document.addEventListener("keydown", onKey); },
    unregisterHotkeys: () => { hotkeyRef = null; document.removeEventListener("keydown", onKey); },
    toggleNav: open => document.querySelector(".shell")?.classList.toggle("nav-open", open),
    _test: { generate, traceRivers, placeMonuments, mulberry32 },
  };
})();
