const root = document.documentElement;
const logos = document.querySelectorAll("[data-logo]");
const favicon = document.querySelector('link[rel="icon"]');
const swatches = document.querySelectorAll("[data-pick]");

function showTheme(id) {
  root.dataset.theme = id;
  for (const button of swatches) button.setAttribute("aria-pressed", String(button.dataset.pick === id));
  const logo = `assets/logos/${id}.png`;
  for (const img of logos) img.src = logo;
  favicon.href = logo;
}

for (const button of swatches) {
  button.addEventListener("click", () => {
    showTheme(button.dataset.pick);
    try {
      localStorage.setItem("eternal-theme", button.dataset.pick);
    } catch {
      // private windows may refuse storage; the theme still applies for this visit
    }
  });
}
showTheme(root.dataset.theme || "mann-co");

// Parts of a section come in one after another, once.
for (const section of document.querySelectorAll("section, footer")) {
  section.querySelectorAll("[data-reveal]").forEach((el, i) => el.style.setProperty("--i", Math.min(i, 6)));
}
const reveal = new IntersectionObserver((entries) => {
  for (const entry of entries) {
    if (entry.isIntersecting) {
      entry.target.classList.add("is-in");
      reveal.unobserve(entry.target);
    }
  }
}, { rootMargin: "0px 0px -10% 0px", threshold: 0.1 });
document.querySelectorAll("[data-reveal]").forEach((el) => reveal.observe(el));

const hero = document.querySelector(".hero");
requestAnimationFrame(() => requestAnimationFrame(() => {
  hero.classList.add("is-in");
  hero.querySelector(".shutter").classList.add("is-in");
}));
