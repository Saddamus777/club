'use strict';
(() => {
  const key = 'club.theme';
  let theme = 'dark';
  try { if (localStorage.getItem(key) === 'light') theme = 'light'; } catch { /* Theme switching still works without storage. */ }
  const apply = () => {
    document.documentElement.dataset.theme = theme;
    const button = document.querySelector('#theme-toggle');
    if (button) {
      button.textContent = theme === 'dark' ? 'Светлый фон' : 'Чёрный фон';
      button.setAttribute('aria-pressed', String(theme === 'dark'));
    }
  };
  apply(); // Runs before CSS and first paint to avoid a bright flash.
  document.addEventListener('DOMContentLoaded', () => {
    apply();
    document.querySelector('#theme-toggle').addEventListener('click', () => {
      theme = theme === 'dark' ? 'light' : 'dark';
      apply();
      try { localStorage.setItem(key, theme); } catch { /* Retain the choice for this page. */ }
    });
  });
})();
