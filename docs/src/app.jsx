/* App root. */

const { Cursor, Nav, ThemeSwitcher, Hero, Features, Showcase, ThemesShowcase, CTA, Footer } = window;

const Marquee = () => {
  const items = [
    'Casual mods (opt‑in disclosure)',
    'Drag-drop mods',
    '10 live themes',
    '4 built-in presets',
    'autoexec safe',
    'GameBanana integration',
    'Backpack browser',
    'MIT licensed',
    'Portable build',
    '.NET 8 · WPF',
  ];
  // Loop items.
  const loop = [...items, ...items];
  return (
    <div className="marquee" aria-hidden="true">
      <div className="marquee-track">
        {loop.map((t, i) => (
          <span key={i} className="item">
            <span className="dot">◆</span>
            {t}
          </span>
        ))}
      </div>
    </div>
  );
};

const App = () => {
  const [theme, setTheme] = React.useState('classic');

  React.useEffect(() => {
    window.applyTheme(theme);
  }, [theme]);

  return (
    <div className="page">
      <Cursor />
      <Nav theme={theme} />

      <Hero theme={theme} />
      <Features />
      <Showcase />
      <ThemesShowcase active={theme} onPick={setTheme} />
      <Footer theme={theme} />

      <ThemeSwitcher active={theme} onPick={setTheme} />
    </div>
  );
};

ReactDOM.createRoot(document.getElementById('root')).render(<App />);
