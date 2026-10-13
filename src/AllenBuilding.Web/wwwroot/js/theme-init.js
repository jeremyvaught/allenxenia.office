(() => {
  "use strict";
  const key = "allen-office-theme";
  const media = window.matchMedia("(prefers-color-scheme: dark)");
  let choice = "system";
  try {
    const saved = document.cookie.split(";").map(part => part.trim()).find(part => part.startsWith(key + "="))?.slice(key.length + 1);
    if (["system", "light", "dark"].includes(saved)) choice = saved;
    else {
      const stored = localStorage.getItem(key);
      if (stored === "light" || stored === "dark") choice = stored;
    }
  } catch { /* System remains available when storage is restricted. */ }
  const apply = () => {
    document.documentElement.dataset.theme = choice === "system"
      ? (media.matches ? "dark" : "light") : choice;
  };
  apply();
  media.addEventListener("change", () => { if (choice === "system") apply(); });
  window.AllenTheme = {
    get: () => choice,
    set: (value) => {
      if (!["system", "light", "dark"].includes(value)) return;
      choice = value;
      try { document.cookie = key + "=" + value + "; Path=/; Max-Age=31536000; SameSite=Lax" + (location.protocol === "https:" ? "; Secure" : ""); } catch { /* Selection still applies to this page. */ }
      try {
        if (value === "system") localStorage.removeItem(key);
        else localStorage.setItem(key, value);
      } catch { /* Keep the explicit selection for this page session. */ }
      apply();
    }
  };
})();
