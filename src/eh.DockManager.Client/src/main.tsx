// File: src/eh.DockManager.Client/src/main.tsx
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './index.css';

const container = document.getElementById('root');
if (!container) {
  throw new Error('Root element #root not found.');
}

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>
);

// Installable-PWA plumbing. The worker is a pass-through (see public/sw.js) so a deploy never
// serves a stale shell; it exists only to satisfy the browser's installability check.
if ('serviceWorker' in navigator) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => {
      /* Installability is a nice-to-have; never break the app over it. */
    });
  });
}
