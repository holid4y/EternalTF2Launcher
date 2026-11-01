const root = document.documentElement;
const logos = document.querySelectorAll("[data-logo]");
const favicon = document.querySelector('link[rel="icon"]');
const tiles = document.querySelectorAll("[data-pick]");
const heroShot = document.querySelector("[data-shot]");

// Picking a theme recolours the page and swaps the hero screenshot to the launcher in that theme.
function showTheme(id) {
  root.dataset.theme = id;
  for (const tile of tiles) tile.setAttribute("aria-pressed", String(tile.dataset.pick === id));
  const logo = `assets/logos/${id}.png`;
  for (const img of logos) img.src = logo;
  favicon.href = logo;
  const shot = `assets/screens/themes/${id}`;
  heroShot.srcset = `${shot}-1x.webp 1366w, ${shot}-2x.webp 2732w`;
  heroShot.src = `${shot}-1x.webp`;
  heroShot.closest("a").href = `${shot}-2x.webp`;
}

for (const tile of tiles) {
  tile.addEventListener("click", () => {
    showTheme(tile.dataset.pick);
    try {
      localStorage.setItem("eternal-theme", tile.dataset.pick);
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

// The Mods story: the held screenshot follows the step in the middle of the screen.
const stage = document.querySelectorAll(".story-stage img");
const steps = document.querySelectorAll(".step");
const follow = new IntersectionObserver((entries) => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    const id = entry.target.dataset.step;
    for (const step of steps) step.classList.toggle("is-on", step === entry.target);
    for (const img of stage) img.classList.toggle("is-on", img.dataset.step === id);
  }
}, { rootMargin: "-45% 0px -45% 0px" });
steps.forEach((step) => follow.observe(step));
steps[0].classList.add("is-on");

const hero = document.querySelector(".hero");
requestAnimationFrame(() => requestAnimationFrame(() => hero.classList.add("is-in")));
