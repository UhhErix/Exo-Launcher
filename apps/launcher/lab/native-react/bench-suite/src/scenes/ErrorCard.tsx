import React from 'react';

export interface CompStatus {
  name: string;
  category: string;
  mounted: boolean;
  error: string | null;
}

const registry = new Map<string, CompStatus>();

export function recordCompStatus(name: string, category: string, mounted: boolean, error: string | null) {
  registry.set(name, { name, category, mounted, error });
}

export function getCompStatuses(): CompStatus[] {
  return [...registry.values()];
}

export function resetCompStatuses() {
  registry.clear();
}

interface Props {
  name: string;
  category: string;
  children: React.ReactNode;
  className?: string;
}

interface State {
  error: string | null;
}

export class CompSlot extends React.Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(err: any): State {
    return { error: String(err?.message || err) };
  }

  componentDidCatch(err: any) {
    recordCompStatus(this.props.name, this.props.category, false, String(err?.message || err));
  }

  componentDidMount() {
    if (!this.state.error) {
      recordCompStatus(this.props.name, this.props.category, true, null);
    }
  }

  render() {
    return (
      <div
        data-comp={this.props.name}
        data-cat={this.props.category}
        className={`rounded-xl border border-zinc-800 bg-zinc-950/90 p-3 flex flex-col gap-2 overflow-hidden ${this.props.className ?? ''}`}
      >
        <div className="text-[11px] font-medium text-zinc-400 tracking-tight flex items-center justify-between">
          <span>{this.props.name}</span>
          <span className="text-[10px] text-zinc-500">{this.props.category}</span>
        </div>
        {this.state.error ? (
          <div className="text-xs text-red-400 font-mono break-all">ERR: {this.state.error}</div>
        ) : (
          <div className="flex-1 flex flex-col gap-2 justify-center">{this.props.children}</div>
        )}
      </div>
    );
  }
}

export function collectComponentMetrics(interactionResults: Record<string, boolean>) {
  const rows = getCompStatuses().map((s) => {
    const el = document.querySelector(`[data-comp="${s.name}"]`) as HTMLElement | null;
    const rect = el ? el.getBoundingClientRect() : { width: 0, height: 0 };
    const rectOk = rect.width > 10 && rect.height > 10;
    const interacted = s.name in interactionResults ? interactionResults[s.name] : null;
    return {
      ...s,
      rectW: Math.round(rect.width),
      rectH: Math.round(rect.height),
      rectOk,
      interacted,
    };
  });
  const total = rows.length;
  const mounted = rows.filter((r) => r.mounted).length;
  const rectOk = rows.filter((r) => r.mounted && r.rectOk).length;
  const interactionEntries = Object.entries(interactionResults);
  const interactionsPassed = interactionEntries.filter(([, v]) => v).length;
  return {
    total,
    mounted,
    rectOk,
    interactionsTotal: interactionEntries.length,
    interactionsPassed,
    components: rows,
  };
}
