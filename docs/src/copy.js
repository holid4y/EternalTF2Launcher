// Feature copy data.
// Rendered as HTML in feature bubbles.

window.ETERNAL_FEATURES = [
  {
    key: 'launch',
    title: 'One‑click launch',
    tag:   'LAUNCH',
    blurb: 'Hit launch, minimise to tray, get out of your way.',
    body:
      "The launcher runs fully <b>unelevated</b> — no UAC prompt, ever. Clicking " +
      "Launch TF2 simply starts the game through Steam " +
      "(<code>steam://rungameid/440</code>) with your launch options, then " +
      "minimises to the system tray. No Steam restart, no memory patching, " +
      "nothing touched. Right‑click the tray icon to restore or quit.",
    meta: ['Fire & forget', 'Tray‑resident', 'No UAC, no patching'],
  },
  {
    key: 'settings',
    title: 'Every cvar, organised',
    tag:   'SETTINGS',
    blurb: 'Nine categories. Schema‑driven UI. Tooltips on every cvar.',
    body:
      "Gameplay, HUD &amp; Quality of Life, Competitive, Performance, Audio, " +
      "Viewmodels, Network, Demo Recording, Advanced. Every setting has a " +
      "description. Changes are written only when they differ from the built‑in " +
      "default, so your managed block stays minimal. The launcher reads your " +
      "existing <code>autoexec.cfg</code> on first launch and pulls in any values " +
      "you already had. It won't overwrite anything outside its own marked block.",
    meta: ['9 categories', 'Schema‑driven', 'Reads your existing cfg'],
  },
  {
    key: 'autoexec',
    title: 'Autoexec, untouched',
    tag:   'AUTOEXEC',
    blurb: 'Writes one marked block. Your hand‑rolled cfg stays exactly yours.',
    body:
      "The launcher owns exactly this:<br>" +
      "<code>// === ETERNAL LAUNCHER MANAGED BLOCK — do not edit between these markers ===</code><br>" +
      "Everything outside those two lines is never read or touched again after " +
      "the initial import. Only non‑default values are written, so the block " +
      "won't balloon over time. DirectX level goes in as a Steam launch option " +
      "(<code>-dxlevel N</code>), never as <code>mat_dxlevel</code> in autoexec, " +
      "which would corrupt <code>video.txt</code> on next startup.",
    meta: ['One marked block', 'Diff‑only writes', 'No video.txt damage'],
  },
  {
    key: 'profiles',
    title: 'Profiles & presets',
    tag:   'PROFILES',
    blurb: 'Four built‑in presets. Save your own. Share as JSON.',
    body:
      "Built‑ins: <b>Competitive</b> (low settings, maximum framerate), " +
      "<b>Max Performance</b> (absolute minimum), <b>Max Quality</b> (everything " +
      "on), <b>Stability</b> (safe DirectX fallback). Each applies both settings " +
      "and the correct DirectX level in one click. Save your current setup as a " +
      "named profile, export it as a JSON file, and share it with friends; " +
      "they can import it directly.",
    meta: ['4 built‑ins', 'JSON import/export', 'DX level bundled'],
  },
  {
    key: 'mods',
    title: 'Preloader mod library',
    tag:   'MODS',
    blurb: 'VPK, folders, ZIP, RAR, 7z. Toggle a mod and it just syncs.',
    body:
      "Drag any of those formats onto the Mods tab to import it into the " +
      "preloader library — VPKs packed inside archives are handled for you. " +
      "There's no Apply button: toggling or importing a mod quietly rebuilds " +
      "the preload in the background, and rapid changes get batched into one " +
      "rebuild. Particle mods are always‑on by design. Right‑click any mod " +
      "card to give it a custom thumbnail with a built‑in 16:9 crop tool.",
    meta: ['.vpk · .zip · .rar · .7z', 'Auto‑sync on change', 'Custom thumbnails'],
  },
  {
    key: 'preloader',
    title: 'Casual mods, the clean way',
    tag:   'PRELOAD',
    blurb: 'Mods baked into VPKs ahead of time using the Casual Preloader engine.',
    body:
      "Mods load in Casual matchmaking because they're <b>baked into " +
      "<code>tf/custom</code> VPKs</b> by an edited build of " +
      "<a href=\"https://github.com/cueki/casual-pre-loader\">cueki's Casual " +
      "Preloader</a> bundled with the launcher. The game starts " +
      "normally through Steam with the modded content already in place. " +
      "Particles, weapon and cosmetic skins, lightwarps, props and bot models " +
      "survive Valve's whitelist; full player‑model swaps generally don't " +
      "due to a Source engine limit.",
    meta: ['VPK baking', 'No runtime patching', 'Persists across launches'],
  },
  {
    key: 'inventory',
    title: 'Backpack browser',
    tag:   'INVENTORY',
    blurb: 'Detects your Steam user. Filters by class, type, quality, rarity.',
    body:
      "Reads your TF2 backpack from Steam Community with a 10‑minute local " +
      "cache. Filter by class, item type, quality, and rarity. Pricing from " +
      "prices.tf and the Steam Market is still work in progress and may be " +
      "missing or wrong. <b>Note:</b> Inventory degrades gracefully if Steam " +
      "isn't running or you're not logged in.",
    meta: ['10‑min cache', 'prices.tf + Market', 'Graceful offline'],
  },
  {
    key: 'feed',
    title: 'Home feed',
    tag:   'FEED',
    blurb: 'TF2 Steam news, GameBanana drops, health indicators.',
    body:
      "The home tab shows the latest TF2 community and official announcements " +
      "pulled from the Steam news feed, plus freshly uploaded mods from " +
      "GameBanana. A status board shows TF2 install health, Steam API " +
      "reachability, GameBanana API, and how many mods are currently loaded. " +
      "Data is cached for 15 minutes; hit Refresh feed to force a pull.",
    meta: ['Steam + GameBanana', 'Health board', '15‑min cache'],
  },
  {
    key: 'themes',
    title: '10 live themes',
    tag:   'THEMES',
    blurb: 'Palette, logo, and window icon swap live. No reload.',
    body:
      "Eternal Classic (dark steel + orange), Australium (aged gold + brass), " +
      "RED, BLU, Carbon, Midnight (deep cyan + ink), Plasma, Toxic (radioactive " +
      "neon), Synthwave (hot pink + neon), Minimal. Switching a theme swaps " +
      "every brush, the logo, and the taskbar icon simultaneously; no restart, " +
      "no flicker.",
    meta: ['10 built‑in', 'Live brush swap', 'Logo + icon together'],
  },
  {
    key: 'dx',
    title: 'DirectX level, the safe way',
    tag:   'DXLEVEL',
    blurb: '-dxlevel as a Steam launch option, never as mat_dxlevel.',
    body:
      "Writing <code>mat_dxlevel</code> in autoexec corrupts <code>video.txt</code> " +
      "on every startup, a well‑known TF2 footgun. Eternal sets DirectX level " +
      "as a Steam launch argument instead, which is the correct method. It's " +
      "tied to presets so switching from Competitive to Max Quality automatically " +
      "sets the right DX level.",
    meta: ['Launch‑option only', 'video.txt safe', 'Bound to presets'],
  },
  {
    key: 'open',
    title: 'Open source',
    tag:   'OSS',
    blurb: '.NET 8, WPF, Windows 10 v2004 or later.',
    body:
      "Built in C# on .NET 8 with WPF. Targets Windows 10 version 2004 or later, " +
      "64‑bit. The portable build bundles the runtime inside the exe, so drop the " +
      "zip anywhere and run <code>LauncherTF2.exe</code>. The installer build is " +
      "framework‑dependent (requires .NET 8 Desktop Runtime), needs no admin " +
      "rights, and installs to <code>%LocalAppData%\\Programs\\Eternal TF2 Launcher</code>. " +
      "MIT licensed, full source on GitHub.",
    meta: ['.NET 8 · WPF', 'Portable or installer', 'MIT'],
  },
];

window.ETERNAL_REQS = [
  { label: 'OS',       value: 'Windows 10 v2004+ · 64‑bit' },
  { label: 'Game',     value: 'TF2 via Steam' },
  { label: 'Runtime',  value: '.NET 8 (bundled in portable)' },
  { label: 'License',  value: 'MIT · open source' },
];
