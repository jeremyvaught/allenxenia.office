(() => {
  const form = document.querySelector('[data-assistant-form]');
  if (!form) return;
  const send = form.querySelector('[data-assistant-send]');
  const stop = form.querySelector('[data-assistant-stop]');
  const progress = form.querySelector('[data-assistant-progress]');
  let pending;
  stop.addEventListener('click', () => pending?.abort());
  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (pending) return;
    pending = new AbortController(); send.disabled = true; stop.hidden = false;
    progress.textContent = 'Looking for guidance…';
    try {
      const response = await fetch(form.action, {
        method: 'POST', body: new URLSearchParams(new FormData(form)), signal: pending.signal,
        credentials: 'same-origin', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }
      });
      // Only consume the same-origin server-rendered, Razor-escaped result. Never render model HTML.
      const documentResult = new DOMParser().parseFromString(await response.text(), 'text/html');
      const result = documentResult.querySelector('[data-assistant-result]');
      if (!result) throw new Error('Unavailable');
      document.querySelector('[data-assistant-result]').replaceWith(document.importNode(result, true));
      progress.textContent = response.ok ? 'Answer ready.' : 'Try again or use building help.';
    } catch (error) {
      progress.textContent = error.name === 'AbortError' ? 'Stopped. No action was submitted.' : 'Unable to connect. Try again or use building help.';
    } finally { pending = undefined; send.disabled = false; stop.hidden = true; }
  });
})();
