// Captures the wiki's screenshots from a running Hearth web host.
//   node scripts/docs-screenshots.mjs [baseUrl] [outDir] [only-name ...]
// Needs a Chromium/Chrome: set CHROME=/path/to/chrome, otherwise Playwright's cached Chromium or Google Chrome is used.
// Run Hearth against an empty data folder (HEARTH_DATA_DIR) so your own chats don't end up in the docs.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, writeFileSync, mkdtempSync } from 'node:fs';
import { join } from 'node:path';
import { homedir, tmpdir } from 'node:os';

const base = (process.argv[2] ?? 'http://localhost:5293').replace(/\/$/, '');
const out = process.argv[3] ?? 'docs/wiki/images';
// Extra one-off shots can be given as name=/path (e.g. welcome-bots=/welcome).
const extra = process.argv.slice(4).filter(a => a.includes('='));
const only = process.argv.slice(4).map(a => a.split('=')[0]);

// name, path, optional { width, height, full, before: js run before the shot, wait: ms }
const shots = [
  ['welcome', '/welcome'],
  ['chat-empty', '/'],
  ['code-sessions', '/code'],
  ['bots', '/bots'],
  ['bot-editor', '/bots/{firstBot}'],
  ['bot-editor-abilities', '/bots/{firstBot}', { tab: 'Abilities' }],
  ['bot-editor-memory', '/bots/{firstBot}', { tab: 'Memory' }],
  ['bot-editor-routines', '/bots/{firstBot}', { tab: 'Routines' }],
  ['bot-editor-checks', '/bots/{firstBot}', { tab: 'Checks' }],
  ['agents', '/agents'],
  ['dashboard', '/dashboard'],
  ['tasks', '/tasks'],
  ['usage', '/usage'],
  ['skills', '/skills'],
  ['teach', '/teach'],
  ['learned', '/learned'],
  ['documents', '/documents'],
  ['memory', '/memory'],
  ['files', '/files'],
  ['settings', '/settings'],
  ['settings-web', '/settings', { tab: 'Web and images' }],
  ['settings-voice', '/settings', { tab: 'Voice' }],
  ['settings-daily', '/settings', { tab: 'Daily' }],
  ['connections', '/connections'],
  ['connections-chat-apps', '/connections', { tab: 'Chat apps' }],
  ['connections-google', '/connections', { tab: 'Google' }],
  ['connections-email', '/connections', { tab: 'Email' }],
  ['connections-more', '/connections', { tab: 'More' }],
  ['permissions', '/permissions'],
  ['tools', '/tools'],
  ['computer', '/computer'],
  ['backup', '/backup'],
  ['mobile-chat', '/', { width: 390, height: 844, mobile: true }],
];
// name=/path#Button text|Other button clicks those buttons (by their starting text) before the shot.
for (const e of extra) { const [n, rest] = e.split('='); const [p, c] = rest.split('#'); shots.push([n, p, c ? { clicks: c.split('|') } : {}]); }

function findChrome() {
  if (process.env.CHROME) return process.env.CHROME;
  const cache = join(homedir(), 'Library/Caches/ms-playwright');
  if (existsSync(cache)) {
    for (const d of readdirSync(cache).filter(d => /^chromium-\d+$/.test(d)).sort().reverse()) {
      const p = join(cache, d, 'chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing');
      if (existsSync(p)) return p;
    }
  }
  for (const p of ['/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', '/usr/bin/chromium', '/usr/bin/google-chrome'])
    if (existsSync(p)) return p;
  throw new Error('No Chrome found; set CHROME=/path/to/chrome');
}

const port = 9333 + Math.floor(Math.random() * 500);
const chrome = spawn(findChrome(), [
  '--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), 'hearth-shots-'))}`,
  '--hide-scrollbars', '--force-color-profile=srgb', '--no-first-run', 'about:blank',
], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));

let wsUrl;
for (let i = 0; i < 50 && !wsUrl; i++) {
  try { wsUrl = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => t.type === 'page')?.webSocketDebuggerUrl; }
  catch { await sleep(200); }
}
const ws = new WebSocket(wsUrl);
await new Promise(r => ws.addEventListener('open', r, { once: true }));
let id = 0; const pending = new Map();
ws.addEventListener('message', e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } });
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = async expr => (await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;

await send('Page.enable'); await send('Runtime.enable');
mkdirSync(out, { recursive: true });

async function go(url, w = 1440, h = 900, mobile = false) {
  await send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 2, mobile });
  await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: process.env.SCHEME ?? 'dark' }] });
  await send('Page.navigate', { url });
  // Blazor Server: wait for the circuit to render something inside the app root.
  for (let i = 0; i < 60; i++) {
    await sleep(250);
    if (await evaluate(`!!document.querySelector('.app, main, .page, .shell') && document.readyState === 'complete'`)) break;
  }
  await sleep(1200);
}

// Resolve placeholders like {firstBot} from the first link to a bot page.
await go(base + '/');
const firstBot = await evaluate(`([...document.querySelectorAll('a[href*="bot"]')].map(a => a.getAttribute('href')).find(h => /bots?[/][0-9a-f]{8,}$/.test(h)) ?? '').split('/').pop()`);

for (const [name, path, opt = {}] of shots) {
  if (only.length && !only.includes(name)) continue;
  if (path.includes('{firstBot}') && !firstBot) { console.log('skip', name, '(no bots yet)'); continue; }
  const url = base + path.replace('{firstBot}', firstBot);
  await go(url, opt.width, opt.height, opt.mobile);
  for (const text of [...(opt.tab ? [opt.tab] : []), ...(opt.clicks ?? [])]) {
    if (text.startsWith('wait:')) { await sleep(+text.slice(5)); continue; }
    if (text.startsWith('select:')) { // select:VALUE picks that option in the first <select> offering it
      await evaluate(`(() => { const s = [...document.querySelectorAll('select')].find(s => [...s.options].some(o => o.value === ${JSON.stringify(text.slice(7))}));
        if (s) { s.value = ${JSON.stringify(text.slice(7))}; s.dispatchEvent(new Event('change', { bubbles: true })); } })()`);
      await sleep(700); continue;
    }
    const ok = await evaluate(`(() => { const el = [...document.querySelectorAll('button, a, [role=tab], summary, label')]
      .find(b => b.textContent.trim().replace(/\\s+/g, ' ').startsWith(${JSON.stringify(text)})); el?.click(); return !!el; })()`);
    if (!ok) console.log('  (no button starting with', JSON.stringify(text) + ')');
    await sleep(900);
  }
  if (opt.wait) await sleep(opt.wait);
  const params = { format: 'png' };
  if (opt.full) {
    const height = await evaluate(`Math.max(...[...document.querySelectorAll('*')].filter(e => e.scrollHeight > e.clientHeight + 4 && getComputedStyle(e).overflowY !== 'visible').map(e => e.scrollHeight), document.body.scrollHeight)`);
    await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: Math.min(height + 40, 6000), deviceScaleFactor: 1, mobile: false });
    await sleep(800);
  }
  const shot = await send('Page.captureScreenshot', params);
  writeFileSync(join(out, name + '.png'), Buffer.from(shot.result.data, 'base64'));
  console.log('saved', name);
}

ws.close(); chrome.kill();
