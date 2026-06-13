/* Shared primitives. */

const Hat = ({ size = 220, strokeWidth = 6 }) => (
  <svg
    viewBox="0 0 120 120"
    width={size}
    height={size}
    fill="none"
    stroke="currentColor"
    strokeWidth={strokeWidth}
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-label="Eternal Launcher mark"
  >
    {/* Hat shape */}
    <ellipse cx="60" cy="92" rx="48" ry="6" />
    <line x1="14" y1="92" x2="106" y2="92" />
    <line x1="30" y1="92" x2="30" y2="34" />
    <line x1="90" y1="92" x2="90" y2="34" />
    <ellipse cx="60" cy="34" rx="30" ry="6" />
    <line x1="30" y1="78" x2="90" y2="78" />
  </svg>
);

/* Logo stack. */
const LogoStack = ({ active, size = 220, glow = true, className = '' }) => {
  const themes = window.ETERNAL_THEMES;
  return (
    <div
      className={`logo-stack ${className}`}
      style={{ width: size, height: size }}
    >
      {themes.map(t => (
        <img
          key={t.id}
          src={t.logo}
          alt={t.name + ' launcher logo'}
          draggable={false}
          data-active={t.id === active ? 'true' : 'false'}
          style={{
            filter: glow
              ? `drop-shadow(0 0 10px ${t.swatches[0]}AA) drop-shadow(0 0 30px ${t.swatches[0]}66) drop-shadow(0 0 60px ${t.swatches[0]}44)`
              : 'none',
          }}
        />
      ))}
    </div>
  );
};

const Pulse = () => <span className="pulse" />;

/* Reveal helper. */
const Reveal = ({ children, delay = 0, as = 'div', className = '', ...rest }) => {
  const ref = React.useRef(null);
  React.useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const io = new IntersectionObserver(
      (entries) => {
        for (const e of entries) {
          if (e.isIntersecting) {
            el.setAttribute('data-visible', 'true');
            io.unobserve(el);
          }
        }
      },
      { threshold: 0.12, rootMargin: '0px 0px -40px 0px' }
    );
    io.observe(el);
    return () => io.disconnect();
  }, []);
  const Tag = as;
  return (
    <Tag
      ref={ref}
      className={`reveal ${className}`}
      data-visible="false"
      data-delay={delay}
      {...rest}
    >
      {children}
    </Tag>
  );
};

Object.assign(window, { Hat, LogoStack, Pulse, Reveal });
