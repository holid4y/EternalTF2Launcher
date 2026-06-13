/* Top nav and theme switcher. */

const Nav = () => (
  <nav className="nav" aria-label="primary">
    <a className="nav-brand" href="#top">
      <span className="dot" />
      <span>ETERNAL</span>
    </a>
    <div className="nav-links">
      <a className="nav-link" href="#features">Features</a>
      <a className="nav-link" href="#themes">Themes</a>
      <a className="nav-link" href="#screens">Screens</a>
    </div>
    <a
      className="nav-cta"
      href="https://github.com/UnusualHatter/EternalLauncher/releases"
      target="_blank" rel="noreferrer"
    >
      Get it
    </a>
  </nav>
);

const ThemeSwitcher = ({ active, onPick }) => {
  const [open, setOpen] = React.useState(false);
  const wrapRef = React.useRef(null);

  React.useEffect(() => {
    const onDoc = (e) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target)) setOpen(false);
    };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, []);

  const themes = window.ETERNAL_THEMES;
  const current = themes.find(t => t.id === active) || themes[0];

  return (
    <div className="theme-switcher" ref={wrapRef}>
      <div className="theme-list" data-open={open ? 'true' : 'false'}>
        {themes.map(t => (
          <button
            key={t.id}
            className="theme-row"
            data-active={t.id === active ? 'true' : 'false'}
            onClick={() => { onPick(t.id); }}
            aria-label={`Switch to ${t.name}`}
          >
            <div className="swatches">
              {t.swatches.map((c, i) => (
                <span key={i} style={{ background: c }} />
              ))}
            </div>
            <div className="meta">
              <b>{t.name}</b>
              <span>{t.sub}</span>
            </div>
            <span className="check">●</span>
          </button>
        ))}
      </div>

      <button
        className="theme-trigger"
        onClick={() => setOpen(o => !o)}
        aria-label="Change theme"
      >
        <span className="ring" />
        <span>THEME · {current.name}</span>
      </button>
    </div>
  );
};

Object.assign(window, { Nav, ThemeSwitcher });
