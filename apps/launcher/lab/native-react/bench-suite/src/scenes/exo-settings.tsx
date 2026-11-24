import React, { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { CompSlot, collectComponentMetrics, resetCompStatuses } from './ErrorCard';

const mockHost = {
  addEventListener() {},
  removeEventListener() {},
  postMessage(msg: any) {
    const parsed = typeof msg === 'string' ? JSON.parse(msg) : msg;
    if (parsed?.id) {
      queueMicrotask(() => {
        window.dispatchEvent(new MessageEvent('message', { data: { id: parsed.id, ok: true, result: {} } }));
      });
    }
  },
};
(globalThis as any).chrome = { webview: mockHost };

import { SettingsPanel } from '../../../../../ui/src/components/SettingsPanel';

function ExoSettingsScene() {
  const [section, setSection] = useState<'general' | 'appearance' | 'accounts' | 'performance' | 'privacy'>('general');
  return (
    <div className="w-[1280px] min-h-[800px] bg-black text-white p-4 flex flex-col gap-3">
      <div className="flex items-center justify-between border-b border-zinc-800 pb-2">
        <div className="text-base font-semibold">Exo Launcher Real Room: ui/src/components/SettingsPanel.tsx (Unmodified)</div>
        <div className="text-xs text-zinc-400">Section: {section}</div>
      </div>
      <CompSlot name="ExoSettingsPanel" category="ExoUI" className="flex-1">
        <SettingsPanel
          open={true}
          onClose={() => {}}
          initialSection={section}
          onSectionChange={(s: any) => setSection(s)}
          games={[]}
          onRefreshLibrary={() => {}}
        />
      </CompSlot>
    </div>
  );
}

resetCompStatuses();
createRoot(document.getElementById('root')!).render(<ExoSettingsScene />);

(globalThis as any).__run_interactions = () => {
  const res: Record<string, boolean> = {};
  const btns = document.querySelectorAll('[data-comp="ExoSettingsPanel"] button');
  res.ExoSettingsPanel = btns.length > 0;
  return collectComponentMetrics(res);
};
