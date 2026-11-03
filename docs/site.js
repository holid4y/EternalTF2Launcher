const root = document.documentElement;
const logos = document.querySelectorAll("[data-logo]");
const favicon = document.querySelector('link[rel="icon"]');
const tiles = document.querySelectorAll("[data-pick]");
const heroShot = document.querySelector("[data-shot]");
const calm = matchMedia("(prefers-reduced-motion: reduce)");

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

function pick(tile, event) {
  const id = tile.dataset.pick;
  const apply = () => {
    showTheme(id);
    try {
      localStorage.setItem("eternal-theme", id);
    } catch {
      // private windows may refuse storage; the theme still applies for this visit
    }
  };
  if (!document.startViewTransition || calm.matches || root.dataset.theme === id) return apply();

  // A keyboard press has no pointer position, so the new colours spread from the tile's middle.
  const box = tile.getBoundingClientRect();
  const x = event.detail ? event.clientX : box.left + box.width / 2;
  const y = event.detail ? event.clientY : box.top + box.height / 2;
  const reach = Math.hypot(Math.max(x, innerWidth - x), Math.max(y, innerHeight - y));
  root.classList.add("is-switching");
  const switching = document.startViewTransition(apply);
  switching.ready.then(() => {
    root.animate(
      { clipPath: [`circle(0 at ${x}px ${y}px)`, `circle(${reach}px at ${x}px ${y}px)`] },
      { duration: 750, easing: "cubic-bezier(0.76, 0, 0.24, 1)", pseudoElement: "::view-transition-new(root)" },
    );
  });
  switching.finished.finally(() => root.classList.remove("is-switching"));
}

for (const tile of tiles) tile.addEventListener("click", (event) => pick(tile, event));
showTheme(root.dataset.theme || "mann-co");

// Headings rise word by word, so each word gets its own mask.
function splitWords(heading) {
  let n = 0;
  for (const node of [...heading.childNodes]) {
    if (node.nodeType !== Node.TEXT_NODE) continue;
    const words = document.createDocumentFragment();
    for (const part of node.textContent.split(/(\s+)/)) {
      if (!part) continue;
      if (!part.trim()) {
        words.append(part);
        continue;
      }
      const mask = document.createElement("span");
      const word = document.createElement("span");
      mask.className = "word";
      word.textContent = part;
      word.style.setProperty("--w", n++);
      mask.append(word);
      words.append(mask);
    }
    node.replaceWith(words);
  }
  heading.classList.add("split");
}
document.querySelectorAll(".hero h1, h2[data-reveal]").forEach(splitWords);

// The code and install slips fill in line by line once they land.
for (const code of document.querySelectorAll(".slip-code code")) {
  code.replaceChildren(...code.textContent.split("\n").map((text) => {
    const line = document.createElement("span");
    line.className = "ln";
    line.style.display = "block";
    line.textContent = text || " ";
    return line;
  }));
}
for (const slip of document.querySelectorAll(".slip")) {
  slip.querySelectorAll("h3, li, .ln, .slip-end").forEach((el, k) => el.style.setProperty("--k", Math.min(k, 18)));
}

for (const figure of document.querySelectorAll("figure[data-reveal]")) {
  if (figure.querySelector(".shot")) figure.classList.add("shutter");
}
for (const tile of tiles) tile.setAttribute("data-reveal", "");

// Parts of a section come in one after another, once.
for (const section of document.querySelectorAll("section, footer")) {
  section.querySelectorAll("[data-reveal]").forEach((el, i) => el.style.setProperty("--i", Math.min(i, 8)));
}

// Once a part has arrived it drops the reveal styles, so its own hover and press timings apply again.
function settle(el) {
  el.addEventListener("transitionend", function done(event) {
    if (event.target !== el || (event.propertyName !== "transform" && event.propertyName !== "clip-path")) return;
    el.removeEventListener("transitionend", done);
    el.removeAttribute("data-reveal");
  });
}

// The top margin is effectively unbounded, so a part that a fast flick or a jump carries straight past
// the screen still counts as reached and comes in.
const reveal = new IntersectionObserver((entries) => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    settle(entry.target);
    entry.target.classList.add("is-in");
    reveal.unobserve(entry.target);
  }
}, { rootMargin: "100000px 0px -10% 0px" });
document.querySelectorAll("[data-reveal]").forEach((el) => reveal.observe(el));

// The band only runs while it can be seen.
const marquee = document.querySelector(".marquee");
new IntersectionObserver(([entry]) => marquee.classList.toggle("is-paused", !entry.isIntersecting)).observe(marquee);

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
