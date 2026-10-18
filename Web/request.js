(() => {
  'use strict';
  const root = new URL(document.currentScript.src).pathname.replace(/\/request\.js$/, '');
  const $ = id => document.getElementById(id);
  const wizard = $('wizard');
  let step = 0;
  let privateRequest = null;
  let checking = false;
  const fields = ['name', 'email', 'username', 'note', 'website'];
  const error = text => { $('message').textContent = text; $('message').hidden = !text; };
  async function api(path, options = {}) {
    const response = await fetch(root + path, { credentials: 'omit', cache: 'no-store', ...options });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Unable to complete this action. Please try again.');
    return data;
  }
  function showStep() {
    document.querySelectorAll('[data-step]').forEach(section => { section.hidden = Number(section.dataset.step) !== step; });
    $('previous').hidden = step === 0;
    $('next').textContent = step === 2 ? 'Submit request' : 'Continue';
    $('progress').textContent = `Step ${step + 1} of 3 · ${['Your profile', 'Your note', 'Review'][step]}`;
    if (step === 2) {
      $('review').replaceChildren();
      for (const [id, title] of [['name', 'Name'], ['email', 'Email'], ['username', 'Desired username'], ['note', 'Note']]) {
        const dt = document.createElement('dt'); dt.textContent = title;
        const dd = document.createElement('dd'); dd.textContent = $(id).value.trim();
        $('review').append(dt, dd);
      }
    }
    const section = document.querySelector(`[data-step="${step}"]`);
    const focus = section.querySelector('input,textarea,h2');
    if (focus?.tagName === 'H2') focus.tabIndex = -1;
    focus?.focus();
  }
  $('previous').addEventListener('click', () => { error(''); step--; showStep(); });
  wizard.addEventListener('submit', async event => {
    event.preventDefault(); error('');
    const current = document.querySelector(`[data-step="${step}"]`);
    for (const field of current.querySelectorAll('input,textarea')) if (!field.reportValidity()) return;
    if (step < 2) { step++; showStep(); return; }
    $('next').disabled = true; $('previous').disabled = true; $('next').textContent = 'Submitting…';
    try {
      const profile = Object.fromEntries(fields.map(id => [id, $(id).value.trim()]));
      privateRequest = await api('/requests', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(profile) });
      const fragment = `${privateRequest.id}:${privateRequest.token}`;
      history.replaceState(null, '', `${root}#${fragment}`);
      try { localStorage.setItem('wizarr-request-status', fragment); } catch { /* The private URL still works without storage. */ }
      showStatus();
    } catch (e) { error(e.message); }
    finally { $('next').disabled = false; $('previous').disabled = false; $('next').textContent = 'Submit request'; }
  });
  function showStatus() {
    wizard.hidden = true; $('status-panel').hidden = false; $('heading').textContent = 'Your access request';
    document.querySelector('.intro').hidden = true;
    $('status-link').href = location.href; $('status-link').textContent = location.href;
    checkStatus();
  }
  async function checkStatus() {
    if (!privateRequest || checking) return;
    checking = true; $('refresh').disabled = true;
    try {
      const data = await api(`/requests/${encodeURIComponent(privateRequest.id)}/status`, { headers: { 'X-Request-Token': privateRequest.token } });
      error('');
      const pending = ['pending', 'approving', 'review-required'].includes(data.status);
      $('status-heading').textContent = pending ? 'Waiting for approval' : data.status === 'approved' ? 'You’re approved' : 'Request declined';
      $('status-copy').textContent = pending ? 'Your profile is waiting for the owner’s review. No account or access has been granted.' : data.status === 'approved' ? 'Your personal invitation is ready. Open Wizarr to confirm your username, choose a password, and finish setting up your account.' : 'The owner has declined this request. Contact them directly if you have questions.';
      $('finish').hidden = true;
      if (data.status === 'approved' && data.invitationUrl) {
        const url = new URL(data.invitationUrl);
        if (url.protocol !== 'https:') throw new Error('The invitation address is invalid. Please contact the owner.');
        $('finish').href = url.href; $('finish').hidden = false;
        $('expiry').textContent = data.invitationExpires ? `This invitation is single-use and expires ${new Date(data.invitationExpires).toLocaleString()}.` : 'This invitation is single-use.';
      }
      if (pending && !document.hidden) setTimeout(checkStatus, 30000);
    } catch (e) { error(e.message); }
    finally { checking = false; $('refresh').disabled = false; }
  }
  $('refresh').addEventListener('click', checkStatus);
  $('copy-status').addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(location.href); $('copy-status').textContent = 'Link copied'; }
    catch { $('status-link').focus(); error('Copy the private link shown below.'); }
  });
  document.addEventListener('visibilitychange', () => { if (!document.hidden && privateRequest) checkStatus(); });
  let fragment = location.hash.slice(1);
  if (!fragment) try { fragment = localStorage.getItem('wizarr-request-status') || ''; } catch { /* Optional. */ }
  if (/^[a-f0-9]{32}:[a-f0-9]{64}$/.test(fragment)) {
    const [id, token] = fragment.split(':'); privateRequest = { id, token };
    history.replaceState(null, '', `${root}#${fragment}`); showStatus();
  } else {
    api('/info').then(info => {
      if (!info.enabled) { wizard.hidden = true; error('The server is not accepting new requests right now.'); }
      document.title = `Request access · ${info.serverName}`;
    }).catch(() => { wizard.hidden = true; error('Requests are temporarily unavailable. Please try again later.'); });
  }
})();
