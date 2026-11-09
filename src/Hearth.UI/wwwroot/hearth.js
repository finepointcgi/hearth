// Inside the Hearth phone app (its web view adds "HearthApp" to the user agent).
if (navigator.userAgent.includes("HearthApp")) document.documentElement.classList.add("in-app");

// Theme: chosen in Settings → Appearance and saved in Hearth's settings. The last one applied is cached on this
// device so the next start paints in the right colours before Blazor connects (this file loads in <head>).
const hearthTheme = (function () {
  const KEY = "hearth-theme-v2";
  const root = document.documentElement;
  const media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: light)") : null;
  let current = null;
  let applied = [];

  // Readable text on a custom accent: dark text on light colours, white on dark ones.
  function textOn(hex) {
    const n = parseInt(hex.slice(1), 16);
    const lin = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
    const l = 0.2126 * lin(n >> 16 & 255) + 0.7152 * lin(n >> 8 & 255) + 0.0722 * lin(n & 255);
    return l > 0.4 ? "#111111" : "#ffffff";
  }

  function apply(t) {
    current = t;
    const mode = t.mode === "light" || t.mode === "dark" ? t.mode : (media && media.matches ? "light" : "dark");
    root.dataset.theme = mode;
    root.dataset.palette = t.palette || "hearth";
    for (const name of applied) root.style.removeProperty(name);
    applied = [];
    const vars = Object.assign({}, (mode === "light" ? t.light : t.dark) || {});
    if (t.accent && /^#[0-9a-f]{6}$/i.test(t.accent)) {
      vars["--accent"] = t.accent;
      vars["--accent-text"] = textOn(t.accent);
      // Links and focus rings need to stand out from the page: nudge the accent towards the text colour.
      vars["--brand"] = `color-mix(in srgb, ${t.accent} 78%, ${mode === "light" ? "#000" : "#fff"})`;
    }
    for (const [name, value] of Object.entries(vars)) {
      if (!/^--[a-z0-9-]+$/.test(name)) continue;
      root.style.setProperty(name, value);
      applied.push(name);
    }
    syncMud();
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = getComputedStyle(root).getPropertyValue("--bg").trim() || meta.content;
  }

  // MudBlazor mixes some colours as rgba(var(--mud-palette-x-rgb), alpha): give it "r,g,b" versions of the theme's
  // colours (hearth.css points the rest of MudBlazor's palette straight at Hearth's variables).
  const MUD_RGB = { primary: "--accent", secondary: "--brand", tertiary: "--ok", info: "--brand", success: "--ok",
    warning: "--warn", error: "--danger", dark: "--text", "text-primary": "--text", divider: "--line" };
  let ctx = null;
  function rgbOf(value) {
    try {
      ctx = ctx || document.createElement("canvas").getContext("2d");
      ctx.fillStyle = "#000"; ctx.fillStyle = value;
      const c = ctx.fillStyle;
      if (c[0] === "#") { const n = parseInt(c.slice(1, 7), 16); return `${n >> 16 & 255},${n >> 8 & 255},${n & 255}`; }
      const m = c.match(/[\d.]+/g);
      return m ? m.slice(0, 3).map((x) => Math.round(+x)).join(",") : null;
    } catch { return null; }
  }
  function syncMud() {
    const css = getComputedStyle(root);
    for (const [name, v] of Object.entries(MUD_RGB)) {
      const rgb = rgbOf(css.getPropertyValue(v).trim());
      if (rgb) root.style.setProperty(`--mud-palette-${name}-rgb`, rgb);
    }
  }

  if (media) {
    const onChange = () => { if (current && current.mode !== "light" && current.mode !== "dark") apply(current); else syncMud(); };
    if (media.addEventListener) media.addEventListener("change", onChange); else if (media.addListener) media.addListener(onChange);
  }

  // Before anything paints: the cached theme, else an older per-device light/dark choice.
  try {
    const cached = localStorage.getItem(KEY);
    if (cached) apply(JSON.parse(cached));
    else {
      const old = localStorage.getItem("hearth-theme") || localStorage.getItem("locus-theme");
      if (old === "light" || old === "dark") root.dataset.theme = old;
    }
  } catch { /* storage blocked: defaults until settings load */ }
  if (!current) syncMud();

  return {
    apply(t) {
      apply(t);
      try { localStorage.setItem(KEY, JSON.stringify(t)); } catch { /* per-session only */ }
    },
    isDark() { return root.dataset.theme !== "light"; },
    // The light/dark choice older versions kept on this device (carried into settings once).
    legacy() {
      try { return localStorage.getItem("hearth-theme") || localStorage.getItem("locus-theme") || ""; } catch { return ""; }
    },
  };
})();

// Settings search: hides cards and options that don't match, highlights the words, and reports how many matched.
const hearthSettingsSearch = (function () {
  let state = null;
  const ITEM_HIDE = "data-search-hidden";

  function textOf(el) {
    let t = el.textContent || "";
    el.querySelectorAll && el.querySelectorAll("[placeholder],[title],[aria-label]").forEach((x) => {
      // Labels on controls count too (icon buttons and empty fields have no text of their own).
      t += " " + (x.getAttribute("aria-label") || "") + " " + (x.getAttribute("title") || "");
    });
    return t.toLowerCase();
  }

  function clear(container) {
    container.querySelectorAll(`[${ITEM_HIDE}]`).forEach((el) => el.removeAttribute(ITEM_HIDE));
    if (window.CSS && CSS.highlights) CSS.highlights.delete("hearth-search");
  }

  function highlight(container, words) {
    if (!(window.CSS && CSS.highlights && window.Highlight)) return;
    const ranges = [];
    const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      if (node.parentElement && node.parentElement.closest(`[${ITEM_HIDE}], option, script, style, .search-empty`)) continue;
      const text = node.nodeValue.toLowerCase();
      for (const w of words) {
        for (let i = text.indexOf(w); i >= 0; i = text.indexOf(w, i + w.length)) {
          const r = new Range(); r.setStart(node, i); r.setEnd(node, i + w.length); ranges.push(r);
        }
      }
    }
    CSS.highlights.set("hearth-search", new Highlight(...ranges));
  }

  function run() {
    if (!state) return 0;
    const { container, words } = state;
    clear(container);
    if (!words.length) return 0;
    const matches = (el) => { const t = textOf(el); return words.every((w) => t.includes(w)); };
    const cards = [...container.querySelectorAll(".card")].filter((c) => !c.parentElement.closest(".card"));
    let shown = 0;
    for (const card of cards) {
      const heading = card.querySelector(":scope > h3, :scope > .row h3, :scope > header h3");
      if (heading && matches(heading)) { shown++; continue; }
      if (!matches(card)) { card.setAttribute(ITEM_HIDE, ""); continue; }
      // Only some of the card matches: keep its title and the options that match.
      shown++;
      for (const child of card.children) {
        if (child.matches("h3, h4, header") || child.contains(heading)) continue;
        if (!matches(child)) child.setAttribute(ITEM_HIDE, "");
      }
    }
    highlight(container, words);
    return shown;
  }

  return {
    // Filters `container` for `query`; keeps filtering cards that load or change later and tells
    // `dotnet.SearchCountChanged(n)` whenever the number of matching cards changes.
    run(container, query, dotnet) {
      if (!container) return 0;
      const words = (query || "").toLowerCase().split(/\s+/).filter(Boolean);
      if (!state || state.container !== container) {
        if (state) state.observer.disconnect();
        let pending = false;
        const observer = new MutationObserver(() => {
          if (pending) return;
          pending = true;
          requestAnimationFrame(() => {
            pending = false;
            if (!state) return;
            const n = run();
            if (n !== state.count) { state.count = n; state.dotnet && state.dotnet.invokeMethodAsync("SearchCountChanged", n).catch(() => {}); }
          });
        });
        observer.observe(container, { childList: true, subtree: true, characterData: true });
        state = { container, observer, words, dotnet, count: -1 };
      }
      state.words = words; state.dotnet = dotnet;
      const n = run();
      state.count = n;
      return n;
    },
    stop() {
      if (!state) return;
      state.observer.disconnect();
      clear(state.container);
      state = null;
    },
  };
})();

// Small JS helpers shared by every host (MAUI WebView, Photino, browser).
window.hearth = {
  applyTheme(t) { hearthTheme.apply(t); },
  legacyTheme() { return hearthTheme.legacy(); },
  isDark() { return hearthTheme.isDark(); },
  searchSettings(container, query, dotnet) { return hearthSettingsSearch.run(container, query, dotnet); },
  stopSettingsSearch() { hearthSettingsSearch.stop(); },
  initComposer(textarea, dotnet) {
    const resize = () => {
      textarea.style.height = "auto";
      textarea.style.height = Math.min(textarea.scrollHeight, 240) + "px";
    };
    textarea.addEventListener("input", resize);
    textarea.addEventListener("keydown", (e) => {
      // Enter sends, Shift+Enter = newline. On touch devices Enter inserts a newline.
      const touch = window.matchMedia("(pointer: coarse)").matches;
      if (e.key === "Enter" && !e.shiftKey && !e.isComposing && !touch) {
        e.preventDefault();
        dotnet.invokeMethodAsync("SubmitFromJs", textarea.value);
      }
    });
    // Paste images straight into the message (screenshots, copied pictures), shrunk like attached ones.
    textarea.addEventListener("paste", async (e) => {
      const files = [...(e.clipboardData?.items || [])]
        .filter((i) => i.kind === "file" && i.type.startsWith("image/")).map((i) => i.getAsFile()).filter(Boolean);
      if (!files.length) return;
      e.preventDefault();
      for (const f of files) {
        try { await dotnet.invokeMethodAsync("PasteImage", (f.name || "pasted").replace(/\.\w+$/, "") + ".jpg", await hearth.shrinkImage(f, 1536)); }
        catch (err) { console.warn("Couldn't paste image", err); }
      }
    });
    resize();
    if (!window.matchMedia("(pointer: coarse)").matches) textarea.focus();
  },
  async shrinkImage(file, max) {
    const bmp = await createImageBitmap(file);
    const scale = Math.min(1, max / Math.max(bmp.width, bmp.height));
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(bmp.width * scale);
    canvas.height = Math.round(bmp.height * scale);
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "#fff"; // transparent screenshots get a white background in JPEG
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(bmp, 0, 0, canvas.width, canvas.height);
    return canvas.toDataURL("image/jpeg", 0.85).split(",")[1];
  },
  resetComposer(textarea) {
    textarea.value = "";
    textarea.style.height = "auto";
  },
  focus(el) { el?.focus(); },

  afterRender(scroller, force) {
    if (!scroller) return;
    // Stick to the bottom while streaming unless the user scrolled up.
    const nearBottom = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 120;
    if (force || nearBottom) scroller.scrollTop = scroller.scrollHeight;
    this.enhance(scroller);
  },

  enhance(root) {
    root.querySelectorAll(".md pre:not([data-enh])").forEach((pre) => {
      pre.dataset.enh = "1";
      const code = pre.querySelector("code");
      const lang = (code?.className.match(/language-(\S+)/) || [])[1] || "";
      const bar = document.createElement("div");
      bar.className = "code-bar";
      bar.innerHTML = `<span>${lang.replace(/[^\w#+.-]/g, "")}</span><button type="button">Copy</button>`;
      bar.querySelector("button").addEventListener("click", (e) => {
        this.copy(code ? code.innerText : pre.innerText);
        e.target.textContent = "Copied";
        setTimeout(() => (e.target.textContent = "Copy"), 1200);
      });
      pre.prepend(bar);
      // Rich previews: HTML/SVG render in a sandboxed frame (no access to Hearth), CSV as a table.
      const kind = /^(html|svg|xml)$/i.test(lang) ? "page" : /^csv$/i.test(lang) ? "table" : null;
      if (kind && code) {
        const btn = document.createElement("button");
        btn.type = "button";
        btn.textContent = kind === "page" ? "Preview" : "Table";
        bar.insertBefore(btn, bar.lastElementChild);
        let view = null;
        btn.addEventListener("click", () => {
          if (view) { view.remove(); view = null; btn.textContent = kind === "page" ? "Preview" : "Table"; return; }
          btn.textContent = "Hide";
          if (kind === "page") {
            view = document.createElement("iframe");
            view.className = "code-preview";
            view.setAttribute("sandbox", "allow-scripts");
            view.srcdoc = code.innerText;
          } else {
            view = document.createElement("div");
            view.className = "code-preview table";
            view.innerHTML = this.csvTable(code.innerText);
          }
          pre.after(view);
        });
      }
    });
    root.querySelectorAll(".md a:not([data-enh])").forEach((a) => {
      a.dataset.enh = "1";
      const href = a.getAttribute("href") || "";
      if (!/^https?:\/\//i.test(href)) {
        a.removeAttribute("href"); // block javascript:, file:, etc. from model output
      } else {
        a.target = "_blank";
        a.rel = "noopener noreferrer";
      }
    });
  },

  csvTable(text) {
    const esc = (v) => v.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
    const rows = [];
    for (const line of text.trim().split(/\r?\n/).slice(0, 500)) {
      const cells = []; let cur = "", q = false;
      for (let i = 0; i < line.length; i++) {
        const ch = line[i];
        if (q) { if (ch === '"' && line[i + 1] === '"') { cur += '"'; i++; } else if (ch === '"') q = false; else cur += ch; }
        else if (ch === '"') q = true; else if (ch === ",") { cells.push(cur); cur = ""; } else cur += ch;
      }
      cells.push(cur); rows.push(cells);
    }
    if (rows.length === 0) return "";
    const [head, ...body] = rows;
    return `<table><thead><tr>${head.map((h) => `<th>${esc(h)}</th>`).join("")}</tr></thead><tbody>${body.map((r) => `<tr>${r.map((c) => `<td>${esc(c)}</td>`).join("")}</tr>`).join("")}</tbody></table>`;
  },

  async copy(text) {
    try { await navigator.clipboard.writeText(text); }
    catch {
      const t = document.createElement("textarea");
      t.value = text; document.body.appendChild(t); t.select();
      document.execCommand("copy"); t.remove();
    }
  },
};

// ---------- voice ----------
window.hearth.voice = {
  _recorder: null, _chunks: [], _recognition: null, _final: "", _audio: null,

  support() {
    return {
      recorder: !!(navigator.mediaDevices?.getUserMedia && window.MediaRecorder),
      recognition: !!(window.SpeechRecognition || window.webkitSpeechRecognition),
      // Browsers only allow the microphone on https:// or localhost.
      secure: window.isSecureContext === true,
    };
  },

  async startRecording() {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    const types = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4", "audio/ogg"];
    const mimeType = types.find((t) => MediaRecorder.isTypeSupported?.(t));
    this._chunks = [];
    this._recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined);
    this._recorder.ondataavailable = (e) => e.data.size && this._chunks.push(e.data);
    this._recorder.start();
  },

  stopRecording() {
    return new Promise((resolve) => {
      const rec = this._recorder;
      if (!rec) return resolve(null);
      rec.onstop = async () => {
        rec.stream.getTracks().forEach((t) => t.stop());
        const blob = new Blob(this._chunks, { type: rec.mimeType || "audio/webm" });
        const buf = new Uint8Array(await blob.arrayBuffer());
        let bin = "";
        for (let i = 0; i < buf.length; i += 0x8000) bin += String.fromCharCode(...buf.subarray(i, i + 0x8000));
        this._recorder = null;
        resolve({ base64: btoa(bin), mime: blob.type });
      };
      rec.stop();
    });
  },

  startRecognition(dotnet, lang) {
    const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
    const r = new SR();
    r.lang = lang || navigator.language;
    r.continuous = true;
    r.interimResults = true;
    this._final = "";
    r.onerror = (e) => {
      if (e.error !== "no-speech" && e.error !== "aborted") dotnet.invokeMethodAsync("OnRecognitionError", e.error);
    };
    r.onresult = (e) => {
      let interim = "";
      for (let i = e.resultIndex; i < e.results.length; i++) {
        if (e.results[i].isFinal) this._final += e.results[i][0].transcript;
        else interim += e.results[i][0].transcript;
      }
      dotnet.invokeMethodAsync("OnPartial", (this._final + interim).trim());
    };
    this._recognition = r;
    r.start();
  },

  stopRecognition() {
    return new Promise((resolve) => {
      const r = this._recognition;
      if (!r) return resolve(this._final.trim());
      r.onend = () => resolve(this._final.trim());
      r.stop();
      this._recognition = null;
      setTimeout(() => resolve(this._final.trim()), 1500);
    });
  },

  speak(text, lang) {
    speechSynthesis.cancel();
    const u = new SpeechSynthesisUtterance(text);
    u.lang = lang || navigator.language;
    speechSynthesis.speak(u);
  },

  playAudio(base64, mime) {
    this.stopSpeaking();
    this._audio = new Audio(`data:${mime};base64,${base64}`);
    this._audio.play();
  },

  stopSpeaking() {
    try { speechSynthesis.cancel(); } catch {}
    if (this._audio) { this._audio.pause(); this._audio = null; }
  },
};

// ---------- live conversation (hands-free) ----------
// listen() waits for one spoken turn and resolves with { text } (browser recognition) or { base64, mime }
// (recorded clip for a speech-to-text server); null when cancelled or nothing was said for a while.
window.hearth.live = {
  _stream: null, _ctx: null, _cancel: null, _audio: null,

  _toBase64(buf) {
    let bin = "";
    for (let i = 0; i < buf.length; i += 0x8000) bin += String.fromCharCode(...buf.subarray(i, i + 0x8000));
    return btoa(bin);
  },

  async listen(useRecorder, lang, dotnet) {
    this.cancel();
    return useRecorder ? this._listenRecorder(dotnet) : this._listenRecognition(lang, dotnet);
  },

  _listenRecognition(lang, dotnet) {
    return new Promise((resolve) => {
      const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
      if (!SR) return resolve(null);
      const r = new SR();
      r.lang = lang || navigator.language;
      r.continuous = false;
      r.interimResults = true;
      let text = "", done = false;
      const finish = (v) => { if (!done) { done = true; clearTimeout(idle); this._cancel = null; resolve(v); } };
      const idle = setTimeout(() => { try { r.abort(); } catch {} finish(null); }, 45000);
      r.onresult = (e) => {
        let interim = "";
        text = "";
        for (let i = 0; i < e.results.length; i++) {
          if (e.results[i].isFinal) text += e.results[i][0].transcript;
          else interim += e.results[i][0].transcript;
        }
        dotnet?.invokeMethodAsync("OnHeard", (text + interim).trim());
      };
      r.onerror = (e) => { if (e.error !== "no-speech" && e.error !== "aborted") finish({ error: e.error }); };
      r.onend = () => finish(text.trim() ? { text: text.trim() } : null);
      this._cancel = () => { try { r.abort(); } catch {} finish(null); };
      r.start();
    });
  },

  async _listenRecorder(dotnet) {
    if (!this._stream || !this._stream.active)
      this._stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } });
    this._ctx ??= new (window.AudioContext || window.webkitAudioContext)();
    if (this._ctx.state === "suspended") await this._ctx.resume();
    const source = this._ctx.createMediaStreamSource(this._stream);
    const analyser = this._ctx.createAnalyser();
    analyser.fftSize = 1024;
    source.connect(analyser);
    const samples = new Float32Array(analyser.fftSize);
    const types = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4", "audio/ogg"];
    const mimeType = types.find((t) => MediaRecorder.isTypeSupported?.(t));

    return new Promise((resolve) => {
      let rec = null, chunks = [], speaking = false, lastVoice = 0, started = performance.now(), done = false, timer = null;
      let noise = 0.01, lastLevel = 0;
      const cleanup = () => { clearInterval(timer); try { source.disconnect(); } catch {} this._cancel = null; };
      const finish = (v) => { if (!done) { done = true; cleanup(); resolve(v); } };
      const stopAndSend = () => {
        if (!rec) return finish(null);
        rec.onstop = async () => {
          const blob = new Blob(chunks, { type: rec.mimeType || "audio/webm" });
          finish({ base64: this._toBase64(new Uint8Array(await blob.arrayBuffer())), mime: blob.type });
        };
        rec.stop();
      };
      timer = setInterval(() => {
        analyser.getFloatTimeDomainData(samples);
        let sum = 0;
        for (const v of samples) sum += v * v;
        const rms = Math.sqrt(sum / samples.length);
        const now = performance.now();
        if (!speaking) noise = noise * 0.95 + rms * 0.05; // track background level
        const loud = rms > Math.max(0.02, noise * 3);
        if (now - lastLevel > 150) { lastLevel = now; dotnet?.invokeMethodAsync("OnLevel", Math.min(1, rms * 8)); }
        if (loud) {
          lastVoice = now;
          if (!speaking) {
            speaking = true;
            chunks = [];
            rec = new MediaRecorder(this._stream, mimeType ? { mimeType } : undefined);
            rec.ondataavailable = (e) => e.data.size && chunks.push(e.data);
            rec.start();
          }
        }
        if (speaking && (now - lastVoice > 1200 || now - started > 60000)) { stopAndSend(); return; }
        if (!speaking && now - started > 45000) finish(null);
      }, 50);
      this._cancel = () => { if (rec && rec.state !== "inactive") { rec.onstop = null; rec.stop(); } finish(null); };
    });
  },

  // Barge-in: while a reply plays, talking over it stops it (so you can interrupt by just speaking). The first half
  // second sets the bar (the reply leaking into the mic); a voice clearly louder than that for ~0.35 s counts.
  async _watchForVoice(onVoice) {
    try {
      if (!this._stream || !this._stream.active)
        this._stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } });
      this._ctx ??= new (window.AudioContext || window.webkitAudioContext)();
      if (this._ctx.state === "suspended") await this._ctx.resume();
      const source = this._ctx.createMediaStreamSource(this._stream);
      const analyser = this._ctx.createAnalyser();
      analyser.fftSize = 1024;
      source.connect(analyser);
      const samples = new Float32Array(analyser.fftSize);
      const started = performance.now();
      let bar = 0, loudSince = 0;
      const timer = setInterval(() => {
        analyser.getFloatTimeDomainData(samples);
        let sum = 0;
        for (const v of samples) sum += v * v;
        const rms = Math.sqrt(sum / samples.length);
        const now = performance.now();
        if (now - started < 500) { bar = Math.max(bar, rms); return; }
        if (rms > Math.max(0.05, bar * 3)) {
          loudSince ||= now;
          if (now - loudSince > 350) { stop(); onVoice(); }
        } else loudSince = 0;
      }, 50);
      const stop = () => { clearInterval(timer); try { source.disconnect(); } catch {} };
      return stop;
    } catch { return () => {}; }
  },

  // Resolves when playback finishes (or is interrupted, by a tap or by talking over it).
  async speak(text, lang) {
    let unwatch = () => {};
    await new Promise((resolve) => {
      speechSynthesis.cancel();
      const u = new SpeechSynthesisUtterance(text);
      u.lang = lang || navigator.language;
      u.onend = u.onerror = () => resolve();
      this._cancel = () => { speechSynthesis.cancel(); resolve(); };
      speechSynthesis.speak(u);
      this._watchForVoice(() => this.cancel()).then((stop) => (unwatch = stop));
    });
    unwatch();
  },

  async play(base64, mime) {
    let unwatch = () => {};
    await new Promise((resolve) => {
      const a = new Audio(`data:${mime};base64,${base64}`);
      this._audio = a;
      a.onended = a.onerror = () => { this._audio = null; resolve(); };
      this._cancel = () => { a.pause(); this._audio = null; resolve(); };
      a.play().catch(() => resolve());
      this._watchForVoice(() => this.cancel()).then((stop) => (unwatch = stop));
    });
    unwatch();
  },

  cancel() {
    const c = this._cancel;
    this._cancel = null;
    if (c) c();
  },

  end() {
    this.cancel();
    try { speechSynthesis.cancel(); } catch {}
    this._stream?.getTracks().forEach((t) => t.stop());
    this._stream = null;
  },
};

window.hearth.downloadImage = (dataUrl, name) => {
  const a = document.createElement("a");
  a.href = dataUrl; a.download = name || "image.png";
  document.body.appendChild(a); a.click(); a.remove();
};
