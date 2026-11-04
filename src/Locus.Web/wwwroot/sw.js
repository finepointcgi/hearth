// Minimal service worker so Locus can be installed as an app. Locus is live (it talks to your server),
// so pages are always fetched from the network; only the offline notice is served from cache.
const OFFLINE = new Response(
  "<html><body style='background:#0a0a0a;color:#ececec;font-family:sans-serif;display:grid;place-items:center;height:100vh;margin:0'>" +
  "<div style='text-align:center'><h2>◎ Locus is out of reach</h2><p>Check that your computer is on and that you're on your network (or Tailscale).</p></div></body></html>",
  { headers: { "Content-Type": "text/html" } });
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", e => e.waitUntil(self.clients.claim()));
self.addEventListener("fetch", e => {
  if (e.request.mode === "navigate") e.respondWith(fetch(e.request).catch(() => OFFLINE.clone()));
});
