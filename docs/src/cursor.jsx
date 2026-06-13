/* Custom cursor. */

const Cursor = () => {
  const dotRef  = React.useRef(null);
  const ringRef = React.useRef(null);
  const state   = React.useRef({ x: 0, y: 0, rx: 0, ry: 0, hot: false });

  React.useEffect(() => {
    const isTouch = matchMedia('(hover: none)').matches;
    if (isTouch) return;

    const onMove = (e) => {
      state.current.x = e.clientX;
      state.current.y = e.clientY;
      if (dotRef.current) {
        dotRef.current.style.transform =
          `translate(${e.clientX}px, ${e.clientY}px) translate(-50%, -50%)`;
      }
    };
    const onOver = (e) => {
      const hot = !!e.target.closest('a, button, .feature-card, .theme-card, .theme-row, .tab, .nav-link');
      if (hot !== state.current.hot) {
        state.current.hot = hot;
        if (ringRef.current) ringRef.current.setAttribute('data-hot', hot ? 'true' : 'false');
      }
    };

    window.addEventListener('mousemove', onMove, { passive: true });
    window.addEventListener('mouseover', onOver, { passive: true });

    // Ring animation.
    let raf;
    const loop = () => {
      const s = state.current;
      s.rx += (s.x - s.rx) * 0.18;
      s.ry += (s.y - s.ry) * 0.18;
      if (ringRef.current) {
        ringRef.current.style.transform =
          `translate(${s.rx}px, ${s.ry}px) translate(-50%, -50%)`;
      }
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);

    return () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseover', onOver);
      cancelAnimationFrame(raf);
    };
  }, []);

  return (
    <>
      <div ref={dotRef}  className="cursor-dot"  />
      <div ref={ringRef} className="cursor-ring" data-hot="false" />
    </>
  );
};

window.Cursor = Cursor;
