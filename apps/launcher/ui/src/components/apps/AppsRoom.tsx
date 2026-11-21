import { AppsPanel } from './AppsPanel'
import '../../styles/apps.css'
import '../../styles/apps-room.css'

export function AppsRoom({ active }: { active: boolean }) {
  return (
    <main className="exo-apps-room" aria-label="Apps">
      <div className="exo-apps-room-shell">
        <section className="exo-system-panel exo-apps-room-surface" aria-label="Installed apps">
          <AppsPanel active={active} />
        </section>
      </div>
    </main>
  )
}
