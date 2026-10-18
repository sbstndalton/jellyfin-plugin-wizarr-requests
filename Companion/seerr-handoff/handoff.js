(() => {
  'use strict';
  const status = document.getElementById('status');
  const manual = document.getElementById('manual');
  const embedded = window.parent !== window;
  const openerWindow = embedded ? window.parent : window.opener;
  const fields = new URLSearchParams(location.hash.slice(1));
  const nonce = fields.get('nonce') || '';
  const value = fields.get('return') || '/';
  const dest = new URL(value, location.origin);
  const origin = window.WizarrJellyfinOrigin;
  let finished = false;
  const fail = () => { if (embedded && openerWindow) openerWindow.postMessage({type:'wizarr-seerr-failed',nonce},origin); finished = true; status.textContent = 'We couldn’t connect this profile. You can still sign in with Jellyfin.'; manual.hidden = false; };
  if (dest.origin !== location.origin || !value.startsWith('/') || value.startsWith('//')) { fail(); return; }
  history.replaceState(null, '', location.pathname); // Remove the non-secret handoff state immediately.
  if (!openerWindow || !/^[a-f0-9]{48}$/.test(nonce) || !origin) { fail(); return; }
  const timer = setTimeout(fail, 45000);
  async function api(path, body) {
    const csrf = document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))?.slice(11);
    const r = await fetch('/api/v1/auth/' + path, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', ...(csrf ? { 'X-XSRF-TOKEN': decodeURIComponent(csrf) } : {}) }, body: JSON.stringify(body) });
    if (!r.ok) throw Error('Sign in failed');
    return r.json();
  }
  window.addEventListener('message', async event => {
    if (event.origin !== origin || event.source !== openerWindow || event.data?.nonce !== nonce || finished) return;
    if (event.data.type === 'wizarr-seerr-failed') { clearTimeout(timer); fail(); return; }
    if (event.data.type !== 'wizarr-seerr-authorized') return;
    finished = true;
    try {
      await api('jellyfin/quickconnect/authenticate', { secret });
      clearTimeout(timer);
      if (embedded) openerWindow.postMessage({ type: 'wizarr-seerr-complete', nonce }, origin);
      else { window.opener = null; location.replace(dest.href); }
    } catch { clearTimeout(timer); fail(); }
  });
  let secret;
  api('jellyfin/quickconnect/initiate', {}).then(result => {
    if (finished || !/^\d{6}$/.test(result.code) || !/^[a-f0-9]{8,128}$/i.test(result.secret)) throw Error('Invalid Quick Connect response');
    secret = result.secret;
    openerWindow.postMessage({ type: 'wizarr-seerr-quickconnect', nonce, code: result.code }, origin);
  }).catch(() => { clearTimeout(timer); fail(); });
})();
