(() => {
  "use strict";
  const key = "allen-office-theme";
  const media = window.matchMedia("(prefers-color-scheme: dark)");
  let choice = "system";
  try {
    const stored = localStorage.getItem(key);
    if (stored === "light" || stored === "dark") choice = stored;
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
      try {
        if (value === "system") localStorage.removeItem(key);
        else localStorage.setItem(key, value);
      } catch { /* Keep the explicit selection for this page session. */ }
      apply();
    }
  };
})();
