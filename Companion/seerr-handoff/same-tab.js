/* Ordinary Seerr -> Jellyfin clicks reuse the current tab. Explicit modifier-clicks remain native. */
(() => {
  'use strict';
  const origin = window.WizarrJellyfinOrigin;
  if (!origin) return;
  window.addEventListener('click', event => {
    const link = event.target.closest?.('a[href]');
    if (!link || event.button !== 0 || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.defaultPrevented) return;
    const target = new URL(link.href, location.href);
    if (target.origin !== origin) return;
    event.preventDefault(); event.stopImmediatePropagation();
    location.assign(target.href);
  }, true);
})();
