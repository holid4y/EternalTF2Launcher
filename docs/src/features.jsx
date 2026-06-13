/* Feature cards and bubble. */

const { Reveal } = window;

const FeatureCard = ({ f, onHover, onLeave }) => (
  <div
    className="feature-card"
    data-feat={f.key}
    onMouseEnter={(e) => onHover(f, e.currentTarget)}
    onMouseLeave={onLeave}
  >
    <span className="feat-tag">{f.tag}</span>
    <h3 className="feat-title">{f.title}</h3>
    <p className="feat-blurb">{f.blurb}</p>
    <div className="feat-meta">
      {f.meta.map((m, i) => <span key={i} className="pill">{m}</span>)}
    </div>
  </div>
);

const Features = () => {
  const [hovered, setHovered] = React.useState(null);
  const [lastShown, setLastShown] = React.useState(null);
  const [place, setPlace]     = React.useState({ x: 0, y: 0 });

  const bubbleW = 340;
  const bubbleH = 280;

  // Bubble placement.
  const placeNextToCard = (cardEl) => {
    if (!cardEl) return { x: 0, y: 0 };
    const margin = 14;
    const gap = 14;
    const r = cardEl.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    // Right of the card.
    let x = r.right + gap;
    let y = r.top;

    // Flip left if needed.
    if (x + bubbleW + margin > vw) x = r.left - gap - bubbleW;
    // Center under the card if needed.
    if (x < margin) {
      x = Math.max(margin, Math.min(vw - bubbleW - margin, r.left + r.width / 2 - bubbleW / 2));
      y = r.bottom + gap;
    }

    // Keep inside the viewport.
    if (y + bubbleH + margin > vh) y = vh - bubbleH - margin;
    y = Math.max(margin, y);
    return { x, y };
  };

  const onHover = (f, cardEl) => {
    setHovered(f);
    setLastShown(f);
    setPlace(placeNextToCard(cardEl));
  };
  const onLeave = () => setHovered(null);

  // Update on scroll or resize.
  React.useEffect(() => {
    if (!hovered) return;
    const recompute = () => {
      const el = document.querySelector(`.feature-card[data-feat='${hovered.key}']`);
      if (el) setPlace(placeNextToCard(el));
    };
    window.addEventListener('scroll', recompute, { passive: true });
    window.addEventListener('resize', recompute);
    return () => {
      window.removeEventListener('scroll', recompute);
      window.removeEventListener('resize', recompute);
    };
  }, [hovered]);

  const features = window.ETERNAL_FEATURES;

  return (
    <section className="section" id="features">
      <div className="wrap">
        <Reveal className="section-head">
          <span className="tag">FEATURES</span>
          <h2>Hover any keyword.<br /><em>Get the whole story.</em></h2>
          <p>
            Eternal is built around one goal: full control over TF2's cvars & launch options,
            plus painless mod compatibility on Casual servers.
          </p>
        </Reveal>

        <div className="feature-grid">
          {features.map((f, i) => (
            <Reveal key={f.key} delay={Math.min(6, (i % 3) + 1)}>
              <FeatureCard f={f} onHover={onHover} onLeave={onLeave} />
            </Reveal>
          ))}
        </div>
      </div>

      {/* Bubble stays outside the grid. */}
      <div
        className="bubble"
        data-open={hovered ? 'true' : 'false'}
        style={{ left: place.x + 'px', top: place.y + 'px', width: bubbleW + 'px' }}
      >
        {lastShown && (
          <>
            <div className="bubble-tag">{lastShown.tag}</div>
            <h4 className="bubble-title">{lastShown.title}</h4>
            <p className="bubble-body" dangerouslySetInnerHTML={{ __html: lastShown.body }} />
          </>
        )}
      </div>
    </section>
  );
};

window.Features = Features;
