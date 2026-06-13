/* Hero section. */

const { Hat, LogoStack, Pulse, Reveal } = window;

const HeroParticles = () => {
  const count = 26;
  // Keep particle positions stable.
  const parts = React.useMemo(
    () =>
      Array.from({ length: count }, () => ({
        left: Math.random() * 100,
        delay: Math.random() * 14,
        duration: 10 + Math.random() * 12,
        size: 1 + Math.random() * 2,
      })),
    []
  );
  return (
    <>
      {parts.map((p, i) => (
        <span
          key={i}
          className="particle"
          style={{
            left: p.left + '%',
            bottom: '-10px',
            width: p.size + 'px',
            height: p.size + 'px',
            animationDuration: p.duration + 's',
            animationDelay: '-' + p.delay + 's',
          }}
        />
      ))}
    </>
  );
};

const Hero = ({ theme }) => {
  // Hat parallax.
  const hatRef = React.useRef(null);
  React.useEffect(() => {
    const onMove = (e) => {
      if (!hatRef.current) return;
      const x = (e.clientX / window.innerWidth  - 0.5) * 18;
      const y = (e.clientY / window.innerHeight - 0.5) * 18;
      hatRef.current.style.translate = `${x}px ${y}px`;
    };
    window.addEventListener('mousemove', onMove, { passive: true });
    return () => window.removeEventListener('mousemove', onMove);
  }, []);

  return (
    <section className="hero" id="top">
      <div className="hero-bg">
        <div className="glow-orb a" />
        <div className="glow-orb b" />
        <div className="glow-orb c" />
        <HeroParticles />
      </div>

      <Reveal as="span" className="hero-eyebrow" delay={0}>
        <Pulse />
        Mod Manager · Inventory Viewer · Settings Editor
      </Reveal>

      <div ref={hatRef} className="hat-wrap" style={{ transition: 'translate 200ms var(--ease-out)' }}>
        <LogoStack active={theme} size={220} glow={true} />
      </div>

      <Reveal as="h1" delay={1}>
        ETERNAL{' '}
        <span className="accent">LAUNCHER</span>
      </Reveal>

      <Reveal as="p" className="hero-sub" delay={2}>
        Every useful TF2 setting in one place. Mods that actually load in Casual.
        Ten live themes that restyle the launcher instantly.
      </Reveal>

      <Reveal className="hero-cta" delay={3}>
        <a
          className="btn btn--primary"
          href="https://github.com/UnusualHatter/EternalLauncher/releases"
          target="_blank" rel="noreferrer"
        >
          <span>↓</span>
          Download installer
          <span className="kbd">.exe</span>
        </a>
        <a
          className="btn btn--ghost"
          href="https://github.com/UnusualHatter/EternalLauncher"
          target="_blank" rel="noreferrer"
        >
          <span>★</span>
          View on GitHub
        </a>
      </Reveal>

      <Reveal className="hero-stats" delay={4}>
        <div><span className="dot" /><b>9</b> setting categories</div>
        <div><span className="dot" /><b>10</b> live themes</div>
        <div><span className="dot" /><b>4</b> setting profiles</div>
        <div><span className="dot" /><b>Open</b> source</div>
      </Reveal>
    </section>
  );
};

window.Hero = Hero;
