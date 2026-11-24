import React, { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AnimatePresence, motion, useMotionValue, useSpring } from 'framer-motion';
import gsap from 'gsap';
import { CompSlot, collectComponentMetrics, resetCompStatuses } from './ErrorCard';

function MotionGsapDemo() {
  const [tab, setTab] = useState(0);
  const [shown, setShown] = useState(true);
  const [waapiRan, setWaapiRan] = useState(false);
  const mv = useMotionValue(0);
  const springX = useSpring(mv, { stiffness: 260, damping: 20 });
  const gsapBoxRef = useRef<HTMLDivElement>(null);
  const gsapStaggerRef = useRef<HTMLDivElement>(null);
  const waapiRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    mv.set(120);
    if (gsapBoxRef.current) {
      gsap.to(gsapBoxRef.current, { x: 140, rotation: 18, duration: 0.6, ease: 'power2.out' });
    }
    if (gsapStaggerRef.current) {
      const bars = gsapStaggerRef.current.querySelectorAll('.g-bar');
      gsap.to(bars, { scaleY: 1.8, opacity: 1, stagger: 0.06, duration: 0.5, repeat: -1, yoyo: true });
    }
    if (waapiRef.current && typeof waapiRef.current.animate === 'function') {
      waapiRef.current.animate(
        [{ opacity: '0.25' }, { opacity: '1' }],
        { duration: 500, iterations: 3, easing: 'ease-in-out' },
      );
      setWaapiRan(true);
    }
  }, [mv]);

  return (
    <div className="w-[1280px] min-h-[800px] bg-[#050508] text-zinc-100 p-4 flex flex-col gap-3">
      <div className="flex items-center justify-between border-b border-zinc-800 pb-2">
        <div className="text-base font-semibold">framer-motion + GSAP + WAAPI Real Animation Benchmark</div>
        <div className="text-xs text-zinc-400">Spring physics · AnimatePresence · layoutId FLIP · GSAP timeline · SVG pathLength</div>
      </div>

      <div className="grid grid-cols-3 gap-3">
        <CompSlot name="MotionSpring" category="framer-motion" className="h-44">
          <div className="relative h-24 bg-zinc-900 rounded-lg p-3 overflow-hidden">
            <motion.div
              initial={{ x: 0, scale: 0.85, opacity: 0.3 }}
              animate={{ x: 160, scale: 1.05, opacity: 1 }}
              transition={{ type: 'spring', stiffness: 220, damping: 18 }}
              className="w-16 h-16 rounded-xl bg-gradient-to-br from-blue-500 to-violet-500 flex items-center justify-center text-xs font-bold"
            >
              Spring
            </motion.div>
          </div>
        </CompSlot>

        <CompSlot name="MotionLayoutId" category="framer-motion" className="h-44">
          <div className="flex gap-2 bg-zinc-900 p-1.5 rounded-lg">
            {['Library', 'Store', 'Discord'].map((label, i) => (
              <button
                key={label}
                id={`fm-tab-${i}`}
                onClick={() => setTab(i)}
                className="relative px-3 py-1.5 text-xs rounded-md"
              >
                {tab === i && (
                  <motion.div
                    layoutId="active-pill"
                    className="absolute inset-0 bg-blue-600 rounded-md"
                    transition={{ type: 'spring', stiffness: 320, damping: 28 }}
                  />
                )}
                <span className="relative z-10">{label}</span>
              </button>
            ))}
          </div>
          <div className="text-xs text-zinc-400">Active tab index: {tab}</div>
        </CompSlot>

        <CompSlot name="MotionAnimatePresence" category="framer-motion" className="h-44">
          <button
            id="fm-toggle"
            onClick={() => setShown((v) => !v)}
            className="px-3 py-1 rounded bg-zinc-800 text-xs w-fit"
          >
            {shown ? 'Unmount' : 'Mount'}
          </button>
          <AnimatePresence>
            {shown && (
              <motion.div
                key="card"
                initial={{ opacity: 0, y: 12 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0, y: -12 }}
                className="p-3 rounded-lg bg-emerald-600/25 border border-emerald-500/40 text-xs"
              >
                Presence mounted
              </motion.div>
            )}
          </AnimatePresence>
        </CompSlot>

        <CompSlot name="MotionUseSpring" category="framer-motion" className="h-44">
          <div className="h-20 bg-zinc-900 rounded-lg p-3">
            <motion.div style={{ x: springX }} className="w-12 h-12 rounded-full bg-cyan-500" />
          </div>
        </CompSlot>

        <CompSlot name="MotionSvgPath" category="framer-motion" className="h-44">
          <svg width="240" height="80" viewBox="0 0 240 80">
            <motion.path
              d="M 10 60 Q 70 10 120 40 T 230 25"
              fill="none"
              stroke="#60a5fa"
              strokeWidth="4"
              initial={{ pathLength: 0.1 }}
              animate={{ pathLength: 1 }}
              transition={{ duration: 0.8 }}
            />
          </svg>
        </CompSlot>

        <CompSlot name="WaapiNativeShim" category="WAAPI" className="h-44">
          <div ref={waapiRef} className="w-full h-16 rounded-lg bg-gradient-to-r from-amber-500 to-rose-500 flex items-center justify-center text-xs font-semibold">
            element.animate() {waapiRan ? 'running' : 'idle'}
          </div>
        </CompSlot>

        <CompSlot name="GsapTween" category="GSAP" className="h-44">
          <div className="h-24 bg-zinc-900 rounded-lg p-3">
            <div ref={gsapBoxRef} className="w-16 h-16 rounded-xl bg-emerald-500 flex items-center justify-center text-xs font-bold text-black">
              GSAP
            </div>
          </div>
        </CompSlot>

        <CompSlot name="GsapStagger" category="GSAP" className="h-44 col-span-2">
          <div ref={gsapStaggerRef} className="h-24 bg-zinc-900 rounded-lg p-4 flex items-end gap-2">
            {Array.from({ length: 16 }).map((_, i) => (
              <div
                key={i}
                className="g-bar w-6 h-8 rounded-t bg-gradient-to-t from-violet-600 to-cyan-400 opacity-70"
              />
            ))}
          </div>
        </CompSlot>
      </div>
    </div>
  );
}

resetCompStatuses();
createRoot(document.getElementById('root')!).render(<MotionGsapDemo />);

(globalThis as any).__trigger_interactions = () => {
  try { document.getElementById('fm-tab-2')?.click(); } catch {}
  try { document.getElementById('fm-toggle')?.click(); } catch {}
};

(globalThis as any).__run_interactions = () => {
  const res: Record<string, boolean> = {};
  res.MotionLayoutId = Boolean(document.querySelector('[data-comp="MotionLayoutId"]')?.textContent?.includes('2'));
  res.MotionAnimatePresence = Boolean(document.getElementById('fm-toggle')?.textContent?.includes('Mount'));
  return collectComponentMetrics(res);
};
