import React from 'react';
import { createRoot } from 'react-dom/client';
import Aurora from '../../../vendor/react-bits/src/ts-tailwind/Backgrounds/Aurora/Aurora';
import Iridescence from '../../../vendor/react-bits/src/ts-tailwind/Backgrounds/Iridescence/Iridescence';
import Waves from '../../../vendor/react-bits/src/ts-tailwind/Backgrounds/Waves/Waves';
import LetterGlitch from '../../../vendor/react-bits/src/ts-tailwind/Backgrounds/LetterGlitch/LetterGlitch';
import DotGrid from '../../../vendor/react-bits/src/ts-tailwind/Backgrounds/DotGrid/DotGrid';
import Particles from '../../../vendor/react-bits/src/ts-tailwind/Backgrounds/Particles/Particles';
import { CompSlot, collectComponentMetrics, resetCompStatuses } from './ErrorCard';

function ReactBitsBackgroundsGallery() {
  return (
    <div className="w-[1280px] min-h-[800px] bg-[#050508] text-zinc-100 p-4 flex flex-col gap-3">
      <div className="flex items-center justify-between border-b border-zinc-800 pb-2">
        <div className="text-base font-semibold">React Bits Real Backgrounds: WebGL2 / OGL / Canvas2D Shaders (Unmodified)</div>
        <div className="text-xs text-zinc-400">Testing Route 4 Shims: WebGL→Naga→wgpu DX12 &amp; Canvas2D→Vello</div>
      </div>

      <div className="grid grid-cols-3 gap-3">
        <CompSlot name="Aurora" category="WebGL2" className="h-[340px]">
          <div className="relative w-full h-[290px] rounded-lg overflow-hidden bg-black">
            <Aurora colorStops={['#3A29FF', '#FF94B4', '#FF3232']} amplitude={1.0} blend={0.5} />
          </div>
        </CompSlot>

        <CompSlot name="Iridescence" category="WebGL2" className="h-[340px]">
          <div className="relative w-full h-[290px] rounded-lg overflow-hidden bg-black">
            <Iridescence color={[0.6, 0.7, 1.0]} speed={1.0} amplitude={0.1} />
          </div>
        </CompSlot>

        <CompSlot name="Particles" category="WebGL-OGL" className="h-[340px]">
          <div className="relative w-full h-[290px] rounded-lg overflow-hidden bg-black">
            <Particles particleCount={120} particleSpread={8} speed={0.1} />
          </div>
        </CompSlot>

        <CompSlot name="Waves" category="Canvas2D" className="h-[340px]">
          <div className="relative w-full h-[290px] rounded-lg overflow-hidden bg-zinc-950">
            <Waves lineColor="#38bdf8" backgroundColor="#07090f" />
          </div>
        </CompSlot>

        <CompSlot name="LetterGlitch" category="Canvas2D" className="h-[340px]">
          <div className="relative w-full h-[290px] rounded-lg overflow-hidden bg-black">
            <LetterGlitch glitchSpeed={50} centerVignette={false} outerVignette={false} smooth />
          </div>
        </CompSlot>

        <CompSlot name="DotGrid" category="Canvas2D+GSAP" className="h-[340px]">
          <div className="relative w-full h-[290px] rounded-lg overflow-hidden bg-zinc-950">
            <DotGrid dotSize={6} gap={22} baseColor="#3f3f46" activeColor="#60a5fa" />
          </div>
        </CompSlot>
      </div>
    </div>
  );
}

resetCompStatuses();
createRoot(document.getElementById('root')!).render(<ReactBitsBackgroundsGallery />);

(globalThis as any).__run_interactions = () => {
  const res: Record<string, boolean> = {};
  for (const name of ['Aurora', 'Iridescence', 'Particles', 'Waves', 'LetterGlitch', 'DotGrid']) {
    const c = document.querySelector(`[data-comp="${name}"] canvas`) as HTMLCanvasElement | null;
    res[name] = Boolean(c && c.width > 0 && c.height > 0);
  }
  return collectComponentMetrics(res);
};
