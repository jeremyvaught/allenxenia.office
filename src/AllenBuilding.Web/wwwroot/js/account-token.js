// Keep invitation/reset credentials out of request URLs and access logs.
const tokenField = document.querySelector('[data-account-token]');
if (tokenField && location.hash) {
  tokenField.value = new URLSearchParams(location.hash.slice(1)).get('token') ?? '';
  history.replaceState(null, '', location.pathname + location.search);
}
