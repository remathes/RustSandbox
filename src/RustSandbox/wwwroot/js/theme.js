/* Rust Sandbox — themes.
   RSM.theme.set("classic" | "molten" | "ingame" | "toxic", "off" | "subtle" | "strong") switches the look for this browser.
   A browser with no saved choice gets "ingame".
   In "molten", every plate (.panel, .fleet-card, .gauge, .plate) gets bolts in its corners, a staggered lava pulse,
   and now and then a random bolt works itself loose. Sparks drift up the page, and the page can show a
   picture of the user's choosing (kept in this browser only). */
(function () {
  const PLATES = ".panel, .fleet-card, .gauge, .plate";
  const root = document.documentElement;
  let observer = null, looseTimer = 0, scanQueued = false, sparks = null;

  const store = {
    get(key, fallback) { try { return localStorage.getItem(key) || fallback; } catch { return fallback; } },
    set(key, value) { try { localStorage.setItem(key, value); } catch { } },
  };

  const isMolten = () => root.dataset.theme === "molten";
  const motionOk = () => root.dataset.glow !== "off" && !matchMedia("(prefers-reduced-motion: reduce)").matches;

  function boltPlate(plate) {
    if (plate.querySelector(":scope > .bolts")) return;
    const bolts = document.createElement("span");
    bolts.className = "bolts";
    bolts.setAttribute("aria-hidden", "true");
    for (const spot of ["tl", "tr", "bl", "br"]) {
      const b = document.createElement("span");
      b.className = "bolt " + spot;
      b.style.setProperty("--r", Math.round(Math.random() * 60) + "deg");
      bolts.appendChild(b);
    }
    // Each plate pulses on its own beat so the page shimmers instead of blinking together.
    plate.style.setProperty("--pulse-delay", (-Math.random() * 5.5).toFixed(2) + "s");
    plate.style.setProperty("--tex-x", Math.round(Math.random() * 800) + "px");
    plate.style.setProperty("--tex-y", Math.round(Math.random() * 800) + "px");
    plate.appendChild(bolts);
    // A bolt left hanging tightens again when someone touches its plate.
    plate.addEventListener("mouseenter", () => {
      plate.querySelectorAll(":scope > .bolts > .bolt.hanging").forEach(b => {
        b.classList.remove("hanging");
        void b.offsetWidth;
        b.classList.add("loose");
      });
    });
  }

  function scan() {
    scanQueued = false;
    if (!isMolten()) return;
    document.querySelectorAll(PLATES).forEach(boltPlate);
  }

  function queueScan() {
    if (scanQueued) return;
    scanQueued = true;
    requestAnimationFrame(scan);
  }

  function visibleBolts() {
    const h = innerHeight, w = innerWidth;
    return [...document.querySelectorAll(".bolts > .bolt")].filter(b => {
      if (b.classList.contains("loose") || b.classList.contains("hanging")) return false;
      const r = b.getBoundingClientRect();
      return r.width > 0 && r.bottom > 0 && r.top < h && r.right > 0 && r.left < w;
    });
  }

  function loosenOne() {
    if (isMolten() && motionOk() && !document.hidden) {
      const bolts = visibleBolts();
      if (bolts.length) {
        const b = bolts[Math.floor(Math.random() * bolts.length)];
        const hang = Math.random() < 0.05;
        b.classList.add(hang ? "hanging" : "loose");
        if (!hang) b.addEventListener("animationend", () => b.classList.remove("loose"), { once: true });
      }
    }
    looseTimer = setTimeout(loosenOne, 10000 + Math.random() * 15000);
  }

  function ensureEdgeFilter() {
    if (document.getElementById("molten-edge")) return;
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("width", "0");
    svg.setAttribute("height", "0");
    svg.setAttribute("aria-hidden", "true");
    svg.style.position = "absolute";
    svg.innerHTML =
      // Tears the plate edges: big slow wobble plus small chips.
      '<filter id="molten-edge" x="-4%" y="-4%" width="108%" height="108%">' +
      '<feTurbulence type="fractalNoise" baseFrequency="0.022 0.03" numOctaves="4" seed="8" result="n"/>' +
      '<feDisplacementMap in="SourceGraphic" in2="n" scale="11" xChannelSelector="R" yChannelSelector="G"/>' +
      '</filter>';
    document.body.appendChild(svg);
  }

  // ---- sparks: a few embers rising from the bottom of the page
  function startSparks() {
    if (sparks || !motionOk()) return;
    const canvas = document.createElement("canvas");
    canvas.className = "spark-field";
    canvas.setAttribute("aria-hidden", "true");
    document.body.appendChild(canvas);
    const ctx = canvas.getContext("2d");
    const list = [];
    let w = 0, h = 0, frame = 0, last = performance.now();
    const resize = () => {
      w = innerWidth; h = innerHeight;
      canvas.width = w; canvas.height = h;
    };
    const spawn = () => ({
      x: Math.random() * w, y: h + 10,
      vx: (Math.random() - .5) * 20, vy: -(40 + Math.random() * 70),
      life: 0, max: 3 + Math.random() * 5, r: .8 + Math.random() * 1.6, seed: Math.random() * 10,
    });
    const tick = now => {
      frame = requestAnimationFrame(tick);
      if (now - last < 32) return; // ~30 frames a second is plenty for drifting sparks
      const dt = Math.min(.1, (now - last) / 1000);
      last = now;
      if (!document.hidden) {
        const want = Math.round(Math.min(30, w / 50));
        while (list.length < want && Math.random() < .3) list.push(spawn());
        ctx.clearRect(0, 0, w, h);
        ctx.globalCompositeOperation = "lighter";
        for (let i = list.length - 1; i >= 0; i--) {
          const p = list[i];
          p.life += dt;
          p.vx += Math.sin(now / 600 + p.seed) * 14 * dt;
          p.x += p.vx * dt; p.y += p.vy * dt;
          const t = p.life / p.max;
          if (t >= 1 || p.y < -10) { list.splice(i, 1); continue; }
          const a = (1 - t) * (.55 + .45 * Math.sin(now / 90 + p.seed * 7));
          ctx.strokeStyle = `rgba(255,${150 + Math.round(70 * (1 - t))},70,${a.toFixed(3)})`;
          ctx.lineWidth = p.r;
          ctx.beginPath();
          ctx.moveTo(p.x, p.y);
          ctx.lineTo(p.x - p.vx * .06, p.y - p.vy * .06);
          ctx.stroke();
        }
      }
    };
    resize();
    addEventListener("resize", resize);
    frame = requestAnimationFrame(tick);
    sparks = { stop() { cancelAnimationFrame(frame); removeEventListener("resize", resize); canvas.remove(); } };
  }

  function stopSparks() {
    sparks?.stop();
    sparks = null;
  }

  // ---- the page picture
  function applyPhoto() {
    // The picture shows behind the Molten Rust and Rust in-game themes.
    const photo = root.dataset.theme !== "classic" ? store.get("rs-bg", "") : "";
    if (photo) {
      root.style.setProperty("--page-photo", `url("${photo}")`);
      root.dataset.photo = "on";
    } else {
      root.style.removeProperty("--page-photo");
      delete root.dataset.photo;
    }
  }

  // Shrinks the chosen picture so it fits in browser storage. Returns "ok", "none" or "too-big".
  async function pickBackground(input) {
    const file = input?.files?.[0];
    if (!file) return "none";
    const bitmap = await createImageBitmap(file);
    const scale = Math.min(1, 1920 / bitmap.width);
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(bitmap.width * scale);
    canvas.height = Math.round(bitmap.height * scale);
    canvas.getContext("2d").drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close?.();
    input.value = "";
    const url = canvas.toDataURL("image/jpeg", .8);
    try { localStorage.setItem("rs-bg", url); } catch { return "too-big"; }
    applyPhoto();
    return "ok";
  }

  function clearBackground() {
    try { localStorage.removeItem("rs-bg"); } catch { }
    applyPhoto();
  }

  function start() {
    ensureEdgeFilter();
    applyPhoto();
    if (motionOk()) startSparks(); else stopSparks();
    scan();
    if (!observer) {
      observer = new MutationObserver(queueScan);
      observer.observe(document.body, { childList: true, subtree: true });
    }
    if (!looseTimer) looseTimer = setTimeout(loosenOne, 6000 + Math.random() * 6000);
  }

  function stop() {
    observer?.disconnect();
    observer = null;
    clearTimeout(looseTimer);
    looseTimer = 0;
    stopSparks();
    applyPhoto();
    document.querySelectorAll(".bolts").forEach(b => b.remove());
  }

  function apply(theme, glow) {
    root.dataset.theme = theme;
    root.dataset.glow = glow;
    if (theme === "molten") start(); else stop();
    applyPhoto();
  }

  window.RSM = window.RSM || {};
  window.RSM.theme = {
    get: () => ({ theme: ["classic", "molten", "ingame", "toxic"].includes(store.get("rs-theme", "")) ? store.get("rs-theme", "") : "ingame", glow: store.get("rs-glow", "strong"), hasBackground: !!store.get("rs-bg", "") }),
    pickBackground,
    clearBackground,
    set(theme, glow) {
      store.set("rs-theme", theme);
      store.set("rs-glow", glow);
      apply(theme, glow);
    },
  };

  const initial = window.RSM.theme.get();
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", () => apply(initial.theme, initial.glow));
  else apply(initial.theme, initial.glow);
})();
