document.querySelectorAll("[data-theme-control]").forEach((control) => {
  control.value = window.AllenTheme.get();
  control.addEventListener("change", () => {
    window.AllenTheme.set(control.value);
    document.querySelectorAll("[data-theme-control]").forEach((other) => {
      other.value = window.AllenTheme.get();
    });
  });
});
