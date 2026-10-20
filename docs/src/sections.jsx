/* Screens, themes, CTA, and footer. */

const { Reveal } = window;

const SCREENS = [
  {
    id: 'home', label: 'Home', num: '01',
    title: 'Home',
    desc: 'TF2 news feed, GameBanana mod drops, a status board for TF2/Steam/GameBanana health, and quick‑open buttons for the TF2 folder, mods folder, autoexec, and config backup.',
    keys: ['News feed', 'Status pings', 'Quick-open buttons', 'One-click launch'],
    media: { kind: 'img', src: 'assets/screen_home.png' },
  },
  {
    id: 'inventory', label: 'Inventory', num: '02',
    title: 'Inventory',
    desc: 'Your full TF2 backpack in one view. Filter by class, type, quality, or rarity.',
    keys: ['Class · Type · Quality · Rarity', 'prices.tf', 'Steam Market', 'Search'],
    media: { kind: 'img', src: 'assets/screen_inventory.png' },
  },
  {
    id: 'mods', label: 'Mods', num: '03',
    title: 'Mod Library',
    desc: 'Your installed mods with GameBanana thumbnails. Drag a file in to install. Toggle the checkbox to enable or disable; the file moves between custom/ and custom/disabled/ instantly.',
    keys: ['.vpk · .zip · .rar · .7z', 'Drag-drop install', 'Atomic toggle', 'GameBanana thumbs'],
    media: { kind: 'img', src: 'assets/screen_mods.png' },
  },
  {
    id: 'settings', label: 'Settings', num: '04',
    title: 'Settings · Live theme swap',
    desc: 'Nine categories of TF2 cvars with tooltips. Profile selector at the top. Launch args synced automatically. Every change writes only to the launcher\u2019s managed block in your autoexec.cfg.',
    keys: ['9 categories', 'Profile selector', 'Launch args sync', 'Managed block only'],
    media: { kind: 'video', src: 'assets/screen_settings.mp4' },
  },
];

const ScreenMedia = ({ m }) => {
  if (m.kind === 'video') {
    return (
      <video
        src={m.src}
        autoPlay
        loop
        muted
        playsInline
      />
    );
  }
  return (
    <img
      src={m.src}
      alt=""
      draggable={false}
    />
  );
};

const Showcase = () => {
  const [active, setActive] = React.useState('home');
  const current = SCREENS.find(s => s.id === active);
  return (
    <section className="section" id="screens">
      <div className="wrap">
        <Reveal className="section-head">
          <span className="tag">SCREENS</span>
          <h2>Four tabs.<br /><em>Everything you need.</em></h2>
          <p>The launcher splits into four focused screens. Click any tab to peek inside.</p>
        </Reveal>

        <div className="showcase-grid">
          <Reveal>
            <div className="screenshot-frame">
              <div className="titlebar">
                <div className="tdots"><span /><span /><span /></div>
                <span>ETERNAL TF2 · {current.label.toUpperCase()}</span>
              </div>
                <div className="body" key={current.id /* Refresh media */}>
                <ScreenMedia m={current.media} />
              </div>
            </div>
          </Reveal>

          <Reveal delay={1} className="showcase-side">
            <div className="tabs">
              {SCREENS.map(s => (
                <button
                  key={s.id}
                  className="tab"
                  data-active={s.id === active ? 'true' : 'false'}
                  onClick={() => setActive(s.id)}
                >
                  <span className="num">{s.num}</span>
                  {s.label}
                </button>
              ))}
            </div>

            <div className="showcase-detail" key={current.id}>
              <h3>{current.title}</h3>
              <p>{current.desc}</p>
              <div className="keys">
                {current.keys.map(k => <span key={k} className="key">{k}</span>)}
              </div>
            </div>
          </Reveal>
        </div>
      </div>
    </section>
  );
};

/* Themes section */

const ThemesShowcase = ({ active, onPick }) => {
  const themes = window.ETERNAL_THEMES;
  return (
    <section className="section" id="themes">
      <div className="wrap">
        <Reveal className="section-head">
          <span className="tag">THEMES</span>
          <h2>Click a theme.<br /><em>The whole page reskins.</em></h2>
          <p>Five of the launcher's ten built‑in themes are wired into this page. Palette, glow, logo, and accents all swap live.</p>
        </Reveal>

        <Reveal>
          <div className="themes-row">
            {themes.map((t, i) => {
              const a = t.swatches[0], b = t.swatches[1] || a, c = t.swatches[2] || '#000';
              const cardBg = t.vars['--bg-2'];
              return (
                <button
                  key={t.id}
                  className="theme-card"
                  data-active={t.id === active ? 'true' : 'false'}
                  onClick={() => onPick(t.id)}
                  style={{
                    background:
                      `radial-gradient(120% 80% at 50% 0%, ${a}44 0%, transparent 60%), ` +
                      `linear-gradient(180deg, ${cardBg} 0%, #000 100%)`,
                    color: '#fff',
                  }}
                >
                  <div>
                    <div className="name">{t.name}</div>
                    <div className="sub">{t.sub}</div>
                  </div>
                  <div className="preview-hat" style={{ filter: `drop-shadow(0 0 20px ${a}AA)` }}>
                    <img src={t.logo} alt="" draggable={false} style={{ width: 72, height: 72, objectFit: 'contain' }} />
                  </div>
                  <div className="swatches">
                    <span style={{ background: a }} />
                    <span style={{ background: b }} />
                    <span style={{ background: c, opacity: 0.7 }} />
                  </div>
                </button>
              );
            })}
          </div>
        </Reveal>

        {/* Specs removed. */}
      </div>
    </section>
  );
};

/* CTA and footer */

const CTA = () => (
  <section className="section section--tight" id="download">
    <div className="wrap">
      <Reveal>
        <div className="cta">
          <div className="cta-content">
            <div>
              <h2>Take control of TF2.<br />Get the launcher.</h2>
              <p>Portable build, no install. Runs next to your existing TF2. Drop it anywhere.</p>
            </div>
            <div className="cta-actions">
              <a className="btn btn--primary" href="https://github.com/holid4y/EternalTF2Launcher/releases/latest" target="_blank" rel="noreferrer">
                <span>↓</span> Download installer <span className="kbd">.exe</span>
              </a>
              <a className="btn btn--ghost" href="https://github.com/holid4y/EternalTF2Launcher/releases/latest" target="_blank" rel="noreferrer">
                <span>◌</span> Portable build (.zip)
              </a>
            </div>
          </div>

          <div className="disclaimer">
            <b>Before you download:</b> Eternal Launcher uses the 
            <a href="https://github.com/cueki/casual-pre-loader" target="_blank" rel="noreferrer" style={{ color: 'var(--accent)', marginLeft: 4 }}>Casual Preloader</a> engine 
            to bake your mods into VPKs so they work legitimately in Casual matchmaking. The launcher runs fully unelevated and starts the game normally through Steam.
            <b> Use at your own discretion.</b>
          </div>
        </div>
      </Reveal>
    </div>
  </section>
);

const Footer = ({ theme }) => {
  const t = (window.ETERNAL_THEMES.find(x => x.id === theme) || window.ETERNAL_THEMES[0]);
  return (
  <footer className="footer">
    <div className="wrap">
      <div className="footer-grid">
        <div className="footer-col footer-brand">
          <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            <img
              src={t.logo}
              alt=""
              draggable={false}
              style={{
                width: 40, height: 40, objectFit: 'contain',
                filter: `drop-shadow(0 0 10px ${t.swatches[0]}AA)`,
              }}
            />
            <span style={{ fontFamily: 'var(--font-display)', fontWeight: 700, fontSize: 18, letterSpacing: '-0.01em' }}>
              Eternal Launcher
            </span>
          </div>
          <p>
            A custom Team Fortress 2 launcher for Windows.
            Built by hat fans, for hat fans. MIT licensed, open source.
          </p>
        </div>

        <div className="footer-col">
          <h4>Project</h4>
          <ul>
            <li><a href="https://github.com/holid4y/EternalTF2Launcher" target="_blank" rel="noreferrer">GitHub repo</a></li>
            <li><a href="https://github.com/holid4y/EternalTF2Launcher/releases/latest" target="_blank" rel="noreferrer">Releases</a></li>
            <li><a href="https://github.com/holid4y/EternalTF2Launcher#readme" target="_blank" rel="noreferrer">README</a></li>
            <li><a href="https://github.com/holid4y/EternalTF2Launcher/blob/main/LICENSE" target="_blank" rel="noreferrer">MIT license</a></li>
          </ul>
        </div>

        <div className="footer-col">
          <h4>Reference</h4>
          <ul>
            <li><a href="#features">Features</a></li>
            <li><a href="#screens">Screens</a></li>
            <li><a href="#themes">Themes</a></li>
          </ul>
        </div>

        <div className="footer-col">
          <h4>Disclaimer</h4>
          <ul>
            <li><span style={{ color: 'var(--muted)' }}>Not affiliated with Valve</span></li>
            <li><span style={{ color: 'var(--muted)' }}>TF2 trademarks belong to Valve</span></li>
            <li><span style={{ color: 'var(--muted)' }}>Use at your own discretion</span></li>
          </ul>
        </div>
      </div>

      <div className="footer-bottom">
        <span>© 2026 holid4y · MIT</span>
        <span>Built with React · Press · for cursor reveal</span>
      </div>
    </div>
  </footer>
  );
};

Object.assign(window, { Showcase, ThemesShowcase, CTA, Footer });
