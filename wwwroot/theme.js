(() => {
  function apply(theme) {
    document.documentElement.dataset.theme = theme;
    for (const button of document.querySelectorAll('[data-theme-toggle]')) {
      button.textContent = theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode';
      button.setAttribute('aria-pressed', String(theme === 'dark'));
    }
  }
  function restore() {
    let theme = 'light';
    try { theme = localStorage.getItem('c12-csharp-theme') || 'light'; } catch {}
    apply(theme === 'dark' ? 'dark' : 'light');
  }
  document.addEventListener('click', event => {
    if (!event.target.closest('[data-theme-toggle]')) return;
    const theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    apply(theme);
    try { localStorage.setItem('c12-csharp-theme', theme); } catch {}
  });
  restore();
  if (window.Blazor) Blazor.addEventListener('enhancedload', restore);
  // Interactive rendering can replace a prerendered navigation button.
  new MutationObserver(() => {
    let saved = document.documentElement.dataset.theme || 'light';
    try { saved = localStorage.getItem('c12-csharp-theme') || saved; } catch {}
    const theme = saved === 'dark' ? 'dark' : 'light';
    if (document.documentElement.dataset.theme !== theme ||
        [...document.querySelectorAll('[data-theme-toggle]')].some(button =>
        button.getAttribute('aria-pressed') !== String(theme === 'dark'))) apply(theme);
  }).observe(document, { childList: true, attributes: true, attributeFilter: ['aria-pressed', 'data-theme'], subtree: true });
})();
