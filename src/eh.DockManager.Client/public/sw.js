// File: src/eh.DockManager.Client/public/sw.js
// Minimal service worker: it exists so the app is installable as a PWA and updates immediately.
// It deliberately does NOT cache anything — the control plane is useless offline and a cached
// shell would serve stale asset hashes after a deploy. Requests pass straight through.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);
  // Never touch API, MCP or WebSocket traffic; let the network handle everything.
  if (event.request.method !== 'GET' || url.origin !== self.location.origin) return;
  if (url.pathname.startsWith('/api') || url.pathname.startsWith('/mcp') || url.pathname.startsWith('/ws')) return;
  // No respondWith(): the browser's default network behaviour applies.
});
