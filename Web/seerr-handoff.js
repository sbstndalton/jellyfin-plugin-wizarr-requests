/* Native Quick Connect runs in a same-site frame before current-tab navigation. */
(() => {
  'use strict';
  const scriptUrl = document.currentScript?.src;
  if (!scriptUrl) return;
  let config, pending;
  fetch(new URL('seerr-handoff-config', scriptUrl), { credentials: 'same-origin' }).then(r => r.ok ? r.json() : null).then(c => { if (c?.enabled && c.url) config = new URL(c.url); }).catch(() => {});
  function finish(job, error) {
    clearTimeout(job.timer); job.frame.remove(); if (pending === job) pending = null;
    if (error) { job.notice.textContent = 'Couldn’t connect to Seerr. '; const retry = document.createElement('a'); retry.href = job.dest.href; retry.dataset.seerrHandoffSkip = 'true'; retry.textContent = 'Open Seerr'; job.notice.append(retry); }
    else { job.notice.remove(); location.assign(job.dest.href); }
  }
  document.addEventListener('click', event => {
    const anchor = event.target.closest?.('a[href]');
    if (!config || !anchor || anchor.dataset.seerrHandoffSkip || event.button !== 0 || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.defaultPrevented) return;
    const dest = new URL(anchor.href);
    if (dest.origin !== config.origin || (!dest.pathname.startsWith(config.pathname.replace(/\/$/, '') + '/') && dest.pathname !== config.pathname)) return;
    const client = window.ApiClient, userId = client?.getCurrentUserId?.();
    if (!userId || !client.accessToken?.()) return;
    event.preventDefault(); event.stopImmediatePropagation(); if (pending) return;
    const nonce = [...crypto.getRandomValues(new Uint8Array(24))].map(x => x.toString(16).padStart(2, '0')).join('');
    const landing = new URL(config.href.replace(/\/$/, '') + '/jellyfin-handoff/index.html?v=1.2.1');
    landing.hash = new URLSearchParams({ nonce, return: dest.pathname + dest.search + dest.hash }).toString();
    const frame = document.createElement('iframe'); frame.hidden = true; frame.title = 'Seerr sign-in'; frame.referrerPolicy = 'no-referrer'; frame.setAttribute('sandbox', 'allow-scripts allow-same-origin'); frame.src = landing.href;
    const notice = document.createElement('div'); notice.setAttribute('role', 'status'); notice.textContent = 'Opening Seerr…'; notice.style.cssText = 'position:fixed;bottom:1rem;left:1rem;z-index:100000;padding:1rem;background:#191d23;color:#f3f5f7;border:1px solid #647185';
    document.body.append(notice, frame);
    const job = { frame, notice, nonce, userId, dest, authorized: false, used: false };
    job.timer = setTimeout(() => finish(job, true), 45000); pending = job;
  }, true);
  window.addEventListener('message', async event => {
    const job = pending, data = event.data;
    if (!config || !job || event.origin !== config.origin || event.source !== job.frame.contentWindow || data?.nonce !== job.nonce) return;
    if (window.ApiClient?.getCurrentUserId?.() !== job.userId) { finish(job, true); return; }
    if (data.type === 'wizarr-seerr-complete' && job.authorized) { finish(job, false); return; }
    if (data.type === 'wizarr-seerr-failed') { finish(job, true); return; }
    if (data.type !== 'wizarr-seerr-quickconnect' || job.used || !/^\d{6}$/.test(data.code)) return;
    job.used = true;
    try {
      const client = window.ApiClient;
      await client.ajax({ type: 'POST', url: client.getUrl('QuickConnect/Authorize', { code: data.code }), dataType: 'json' });
      if (pending !== job || client.getCurrentUserId() !== job.userId) throw Error('Profile changed');
      job.authorized = true; job.frame.contentWindow.postMessage({ type: 'wizarr-seerr-authorized', nonce: data.nonce }, config.origin);
    } catch { if (pending === job) finish(job, true); }
  });
})();
