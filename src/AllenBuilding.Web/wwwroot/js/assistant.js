(() => {
  const form = document.querySelector('[data-assistant-form]');
  const send = form?.querySelector('[data-assistant-send]');
  const stop = form?.querySelector('[data-assistant-stop]');
  const progress = form?.querySelector('[data-assistant-progress]');
  let pending;
  let checking = false;
  async function checkAccess() {
    const result = document.querySelector('[data-assistant-result]');
    const membership = result?.dataset.assistantMembership;
    if (!membership || checking || document.hidden) return;
    checking = true;
    try {
      const response = await fetch('/Assistant/Access?membershipId=' + encodeURIComponent(membership),
        { credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok) {
        pending?.abort();
        result.textContent = 'Access changed. Start a new question using your current access.';
        delete result.dataset.assistantMembership;
        if (form) {
          const selection = form.querySelector('[name="membershipId"]');
          selection.value = '';
          for (const option of Array.from(selection.options)) if (option.value) option.remove();
          form.querySelector('[name="question"]').value = '';
          form.querySelector('[name="requestId"]')?.remove();
        }
      }
    } catch { result.textContent = 'Unable to verify access. Start a new question.'; }
    finally { checking = false; }
  }
  window.addEventListener('focus', checkAccess);
  window.addEventListener('pageshow', checkAccess);
  document.addEventListener('visibilitychange', checkAccess);

  if (!form) return;
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
      const conversation = documentResult.querySelector('[name="conversationId"]');
      if (conversation) form.querySelector('[name="conversationId"]').value = conversation.value;
      progress.textContent = response.ok ? 'Answer ready.' : 'Try again or use building help.';
    } catch (error) {
      progress.textContent = error.name === 'AbortError' ? 'Stopped. No action was submitted.' : 'Unable to connect. Try again or use building help.';
    } finally { pending = undefined; send.disabled = false; stop.hidden = true; }
  });
})();
