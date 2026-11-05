// Inside the Locus phone app (its web view adds "LocusApp" to the user agent).
if (navigator.userAgent.includes("LocusApp")) document.documentElement.classList.add("in-app");

// Theme: "system" (default), "light" or "dark", remembered per device.
(function () {
  try {
    const t = localStorage.getItem("locus-theme");
    if (t === "light" || t === "dark") document.documentElement.dataset.theme = t;
  } catch { /* storage blocked: follow the system */ }
})();

// Small JS helpers shared by every host (MAUI WebView, Photino, browser).
window.locus = {
  getTheme() {
    try { return localStorage.getItem("locus-theme") || "system"; } catch { return "system"; }
  },
  setTheme(t) {
    if (t === "light" || t === "dark") document.documentElement.dataset.theme = t;
    else delete document.documentElement.dataset.theme;
    try { localStorage.setItem("locus-theme", t); } catch { /* per-session only */ }
  },
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
        try { await dotnet.invokeMethodAsync("PasteImage", (f.name || "pasted").replace(/\.\w+$/, "") + ".jpg", await locus.shrinkImage(f, 1536)); }
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
      // Rich previews: HTML/SVG render in a sandboxed frame (no access to Locus), CSV as a table.
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
window.locus.voice = {
  _recorder: null, _chunks: [], _recognition: null, _final: "", _audio: null,

  support() {
    return {
      recorder: !!(navigator.mediaDevices?.getUserMedia && window.MediaRecorder),
      recognition: !!(window.SpeechRecognition || window.webkitSpeechRecognition),
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
window.locus.live = {
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

  // Resolves when playback finishes (or is interrupted).
  speak(text, lang) {
    return new Promise((resolve) => {
      speechSynthesis.cancel();
      const u = new SpeechSynthesisUtterance(text);
      u.lang = lang || navigator.language;
      u.onend = u.onerror = () => resolve();
      this._cancel = () => { speechSynthesis.cancel(); resolve(); };
      speechSynthesis.speak(u);
    });
  },

  play(base64, mime) {
    return new Promise((resolve) => {
      const a = new Audio(`data:${mime};base64,${base64}`);
      this._audio = a;
      a.onended = a.onerror = () => { this._audio = null; resolve(); };
      this._cancel = () => { a.pause(); this._audio = null; resolve(); };
      a.play().catch(() => resolve());
    });
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

window.locus.downloadImage = (dataUrl, name) => {
  const a = document.createElement("a");
  a.href = dataUrl; a.download = name || "image.png";
  document.body.appendChild(a); a.click(); a.remove();
};
