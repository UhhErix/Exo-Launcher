import React from 'react';
import { createRoot } from 'react-dom/client';
import BlurText from '../../../vendor/react-bits/src/ts-tailwind/TextAnimations/BlurText/BlurText';
import ShinyText from '../../../vendor/react-bits/src/ts-tailwind/TextAnimations/ShinyText/ShinyText';
import GradientText from '../../../vendor/react-bits/src/ts-tailwind/TextAnimations/GradientText/GradientText';
import CountUp from '../../../vendor/react-bits/src/ts-tailwind/TextAnimations/CountUp/CountUp';
import DecryptedText from '../../../vendor/react-bits/src/ts-tailwind/TextAnimations/DecryptedText/DecryptedText';
import GlitchText from '../../../vendor/react-bits/src/ts-tailwind/TextAnimations/GlitchText/GlitchText';
import RotatingText from '../../../vendor/react-bits/src/ts-tailwind/TextAnimations/RotatingText/RotatingText';
import TextType from '../../../vendor/react-bits/src/ts-tailwind/TextAnimations/TextType/TextType';
import ClickSpark from '../../../vendor/react-bits/src/ts-tailwind/Animations/ClickSpark/ClickSpark';
import StarBorder from '../../../vendor/react-bits/src/ts-tailwind/Animations/StarBorder/StarBorder';
import FadeContent from '../../../vendor/react-bits/src/ts-tailwind/Animations/FadeContent/FadeContent';
import Magnet from '../../../vendor/react-bits/src/ts-tailwind/Animations/Magnet/Magnet';
import Noise from '../../../vendor/react-bits/src/ts-tailwind/Animations/Noise/Noise';
import SpotlightCard from '../../../vendor/react-bits/src/ts-tailwind/Components/SpotlightCard/SpotlightCard';
import TiltedCard from '../../../vendor/react-bits/src/ts-tailwind/Components/TiltedCard/TiltedCard';
import Counter from '../../../vendor/react-bits/src/ts-tailwind/Components/Counter/Counter';
import { CompSlot, collectComponentMetrics, resetCompStatuses } from './ErrorCard';

function ReactBitsUiGallery() {
  const [count, setCount] = React.useState(144);
  return (
    <div className="w-[1280px] min-h-[800px] bg-[#050508] text-zinc-100 p-4 flex flex-col gap-3">
      <div className="flex items-center justify-between border-b border-zinc-800 pb-2">
        <div className="text-base font-semibold">React Bits Real Gallery: TextAnimations + Animations + Components (Unmodified)</div>
        <div className="text-xs text-zinc-400">Imported directly from DavidHDev/react-bits/src/ts-tailwind</div>
      </div>

      <div className="grid grid-cols-4 gap-3">
        <CompSlot name="BlurText" category="TextAnimations">
          <BlurText text="Native Rust Speed" className="text-lg font-bold text-white" delay={40} />
        </CompSlot>

        <CompSlot name="ShinyText" category="TextAnimations">
          <ShinyText text="Liquid Glass Ready" className="text-lg font-semibold" />
        </CompSlot>

        <CompSlot name="GradientText" category="TextAnimations">
          <GradientText className="text-lg font-bold">Exo Launcher v4</GradientText>
        </CompSlot>

        <CompSlot name="CountUp" category="TextAnimations">
          <div className="text-2xl font-bold text-cyan-400">
            <CountUp from={0} to={240} duration={0.8} /> FPS
          </div>
        </CompSlot>

        <CompSlot name="DecryptedText" category="TextAnimations">
          <DecryptedText text="ZERO WEBVIEW2" animateOn="view" className="text-base font-mono text-emerald-400" />
        </CompSlot>

        <CompSlot name="GlitchText" category="TextAnimations">
          <GlitchText className="text-lg font-bold">GLITCH</GlitchText>
        </CompSlot>

        <CompSlot name="RotatingText" category="TextAnimations">
          <RotatingText
            texts={['Rune', 'Blitz', 'Stylo']}
            mainClassName="px-2 py-1 bg-indigo-600 text-white rounded-lg text-sm font-semibold w-fit"
            rotationInterval={800}
          />
        </CompSlot>

        <CompSlot name="TextType" category="TextAnimations">
          <TextType text={['Compiling shaders...', 'DX12 ready']} typingSpeed={40} className="text-sm font-mono text-amber-300" />
        </CompSlot>

        <CompSlot name="ClickSpark" category="Animations" className="h-36">
          <ClickSpark sparkColor="#38bdf8" sparkSize={12} sparkRadius={22} sparkCount={8}>
            <div id="rb-spark-area" className="h-24 w-full rounded-lg bg-zinc-900 border border-zinc-700 flex items-center justify-center text-xs cursor-pointer">
              Click for Canvas2D sparks
            </div>
          </ClickSpark>
        </CompSlot>

        <CompSlot name="StarBorder" category="Animations" className="h-36">
          <StarBorder color="#60a5fa" speed="4s">
            <span className="text-xs font-medium">StarBorder CTA</span>
          </StarBorder>
        </CompSlot>

        <CompSlot name="FadeContent" category="Animations" className="h-36">
          <FadeContent blur duration={500}>
            <div className="p-3 rounded-lg bg-zinc-900 text-xs">GSAP FadeContent block</div>
          </FadeContent>
        </CompSlot>

        <CompSlot name="Magnet" category="Animations" className="h-36">
          <Magnet padding={40}>
            <button id="rb-magnet-btn" onClick={() => setCount((c) => c + 12)} className="px-3 py-1.5 rounded-lg bg-blue-600 text-xs font-medium">
              Magnet ({count})
            </button>
          </Magnet>
        </CompSlot>

        <CompSlot name="Noise" category="Animations" className="h-40 relative">
          <div className="relative w-full h-28 rounded-lg bg-gradient-to-br from-indigo-950 to-zinc-900 overflow-hidden flex items-center justify-center">
            <Noise patternAlpha={24} />
            <span className="relative z-10 text-xs font-medium">Canvas2D Noise</span>
          </div>
        </CompSlot>

        <CompSlot name="SpotlightCard" category="Components" className="h-40">
          <SpotlightCard className="h-28 p-3 rounded-lg border border-zinc-800 bg-zinc-900/90">
            <div className="text-xs font-semibold">SpotlightCard</div>
            <div className="text-[11px] text-zinc-400 mt-1">Radial cursor glow surface</div>
          </SpotlightCard>
        </CompSlot>

        <CompSlot name="TiltedCard" category="Components" className="h-40">
          <TiltedCard
            imageSrc="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='160' height='100'%3E%3Crect width='160' height='100' fill='%232563eb'/%3E%3C/svg%3E"
            altText="Exo Card"
            captionText="Tilted 3D"
            containerHeight="100px"
            containerWidth="180px"
            imageHeight="100px"
            imageWidth="180px"
          />
        </CompSlot>

        <CompSlot name="Counter" category="Components" className="h-40">
          <div className="flex items-center justify-center h-28">
            <Counter value={count} fontSize={32} textColor="#ffffff" />
          </div>
        </CompSlot>
      </div>
    </div>
  );
}

resetCompStatuses();
createRoot(document.getElementById('root')!).render(<ReactBitsUiGallery />);

(globalThis as any).__trigger_interactions = () => {
  try {
    const area = document.getElementById('rb-spark-area');
    area?.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: 140, clientY: 360 }));
  } catch {}
  try { document.getElementById('rb-magnet-btn')?.click(); } catch {}
};

(globalThis as any).__run_interactions = () => {
  const res: Record<string, boolean> = {};
  res.ClickSpark = Boolean(document.querySelector('[data-comp="ClickSpark"] canvas'));
  res.Magnet = Boolean(document.getElementById('rb-magnet-btn')?.textContent?.includes('156'));
  return collectComponentMetrics(res);
};
