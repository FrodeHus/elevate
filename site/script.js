// Enhance the static page. All feature stories and links remain available without JavaScript.
const picker = document.querySelector(".feature-picker");
const tabs = [...picker.querySelectorAll("[data-feature]")];
const panels = [...document.querySelectorAll("[data-panel]")];
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
let activeTab;

function positionHighlight(animate = false) {
  if (!activeTab || picker.hidden) return;
  picker.classList.toggle("indicator-animated", animate);
  picker.style.setProperty("--tab-x", `${activeTab.offsetLeft}px`);
  picker.style.setProperty("--tab-y", `${activeTab.offsetTop}px`);
  picker.style.setProperty("--tab-width", `${activeTab.offsetWidth}px`);
  picker.style.setProperty("--tab-height", `${activeTab.offsetHeight}px`);
  picker.classList.add("has-indicator");
}

function selectFeature(tab, moveFocus = false) {
  const previousTab = activeTab;
  activeTab = tab;
  for (const item of tabs) {
    const selected = item === tab;
    item.setAttribute("aria-selected", String(selected));
    item.tabIndex = selected ? 0 : -1;
  }
  for (const panel of panels) {
    panel.getAnimations().forEach((animation) => animation.cancel());
    panel.hidden = panel.dataset.panel !== tab.dataset.feature;
    if (
      !panel.hidden &&
      previousTab &&
      previousTab !== tab &&
      !reducedMotion.matches
    ) {
      const direction = tabs.indexOf(tab) > tabs.indexOf(previousTab) ? 1 : -1;
      panel.animate(
        [
          { opacity: 0.7, transform: `translateX(${direction * 8}px)` },
          { opacity: 1, transform: "translateX(0)" },
        ],
        { duration: 280, easing: "cubic-bezier(0.22, 1, 0.36, 1)" },
      );
    }
  }
  positionHighlight(Boolean(previousTab));
  if (moveFocus) tab.focus();
}

picker.setAttribute("role", "tablist");
for (const tab of tabs) {
  tab.setAttribute("role", "tab");
  tab.setAttribute("aria-controls", `feature-${tab.dataset.feature}`);
  tab.addEventListener("click", () => selectFeature(tab));
  tab.addEventListener("keydown", (event) => {
    const index = tabs.indexOf(tab);
    let next;
    if (event.key === "ArrowRight") next = (index + 1) % tabs.length;
    if (event.key === "ArrowLeft")
      next = (index - 1 + tabs.length) % tabs.length;
    if (event.key === "Home") next = 0;
    if (event.key === "End") next = tabs.length - 1;
    if (next === undefined) return;
    event.preventDefault();
    selectFeature(tabs[next], true);
  });
}
for (const panel of panels) {
  panel.setAttribute("role", "tabpanel");
  panel.setAttribute("aria-labelledby", `tab-${panel.dataset.panel}`);
  panel.tabIndex = 0;
}
selectFeature(tabs[0]);
picker.hidden = false;
positionHighlight();
// Keep the highlight aligned when the tabs become a two-row layout or fonts resize.
new ResizeObserver(() => positionHighlight()).observe(picker);
for (const tab of tabs)
  new ResizeObserver(() => positionHighlight()).observe(tab);
reducedMotion.addEventListener("change", () => {
  if (reducedMotion.matches) {
    panels.forEach((panel) =>
      panel.getAnimations().forEach((animation) => animation.cancel()),
    );
  }
});

// Only offer copying when the browser can support it. The command remains selectable.
const copyButton = document.querySelector("[data-copy]");
const copyStatus = document.querySelector("#copy-status");
if (navigator.clipboard && window.isSecureContext) {
  copyButton.hidden = false;
  let resetTimer;
  copyButton.addEventListener("click", async () => {
    clearTimeout(resetTimer);
    try {
      await navigator.clipboard.writeText(
        document.getElementById(copyButton.dataset.copy).textContent,
      );
      copyButton.textContent = "Copied";
      copyStatus.textContent = "Command copied to clipboard.";
    } catch {
      copyButton.textContent = "Select command to copy";
      copyStatus.textContent =
        "Clipboard access is unavailable. Select the command and copy it manually.";
    }
    resetTimer = setTimeout(() => {
      copyButton.textContent = "Copy command";
    }, 3000);
  });
}
