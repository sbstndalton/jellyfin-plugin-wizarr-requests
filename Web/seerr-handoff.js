/* Native Quick Connect handoff. The user's Jellyfin token never leaves Jellyfin. */
(() => {
  'use strict';
  const scriptUrl = document.currentScript?.src;
  if (!scriptUrl) return;
  const configUrl = new URL('seerr-handoff-config', scriptUrl);
  let config;
  const pending = new Map();
  fetch(configUrl, { credentials: 'same-origin' }).then(r => r.ok ? r.json() : null).then(c => {
    if (c?.enabled && c.url) config = new URL(c.url);
  }).catch(() => {});
  document.addEventListener('click', event => {
    const anchor = event.target.closest?.('a[href]');
    if (!config || !anchor || event.button !== 0 || event.altKey || event.defaultPrevented) return;
    const dest = new URL(anchor.href);
    if (dest.origin !== config.origin || !dest.pathname.startsWith(config.pathname.replace(/\/$/, '') + '/') && dest.pathname !== config.pathname) return;
    const client = window.ApiClient;
    const userId = client?.getCurrentUserId?.();
    if (!userId || !client.accessToken?.()) return;
    const nonce = [...crypto.getRandomValues(new Uint8Array(24))].map(x => x.toString(16).padStart(2, '0')).join('');
    const landing = new URL(config.href.replace(/\/$/, '') + '/jellyfin-handoff/index.html');
    landing.hash = new URLSearchParams({ nonce, return: dest.pathname + dest.search + dest.hash }).toString();
    const child = window.open(landing.href, '_blank');
    if (!child) return; // Keep normal link navigation if popups are blocked.
    event.preventDefault();
    event.stopImmediatePropagation();
    const timer = setTimeout(() => pending.delete(nonce), 120000);
    pending.set(nonce, { child, userId, timer, used: false });
  }, true);
  window.addEventListener('message', async event => {
    const data = event.data;
    if (!config || event.origin !== config.origin || data?.type !== 'wizarr-seerr-quickconnect') return;
    const job = pending.get(data.nonce);
    if (!job || job.used || event.source !== job.child || !/^\d{6}$/.test(data.code)) return;
    job.used = true;
    try {
      const client = window.ApiClient;
      if (!client || client.getCurrentUserId() !== job.userId) throw Error('Profile changed');
      await client.ajax({ type: 'POST', url: client.getUrl('QuickConnect/Authorize', { code: data.code }), dataType: 'json' });
      job.child.postMessage({ type: 'wizarr-seerr-authorized', nonce: data.nonce }, config.origin);
    } catch {
      job.child.postMessage({ type: 'wizarr-seerr-failed', nonce: data.nonce }, config.origin);
    } finally { clearTimeout(job.timer); pending.delete(data.nonce); }
  });
})();
