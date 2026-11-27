import { memo, useEffect, useState } from 'react'
import { host, type LauncherSettings } from '../../lib/host'
import { Spinner } from '../ui/Spinner'
import { report } from '../../lib/report'
import { errorText } from '../../lib/errorWords'
import { ChoiceRow, CopyRow, Row, Section, SliderRow, SwitchRow } from './parts'

/** Null when the save took, else why not. */
type SaveSettings = (patch: Partial<LauncherSettings>) => Promise<string | null>

const TEXT_SIZES = [
  { value: 'small', label: 'Small' },
  { value: 'normal', label: 'Normal' },
  { value: 'large', label: 'Large' },
] as const

/** Settings › General: the look, the window's start, and keeping the PC awake for downloads. */
export const GeneralSection = memo(function GeneralSection({ settings, onSave: save }: {
  settings: LauncherSettings | null
  onSave: SaveSettings
}) {
  const disabled = !settings
  return (
    <Section id="general">
      <ChoiceRow id="set-text-size" label="Text size" value={settings?.textSize ?? 'normal'} choices={TEXT_SIZES} disabled={disabled}
        onChange={value => save({ textSize: value })} />

      <SwitchRow id="set-start-with-windows" label="Open Exo when Windows starts" checked={settings?.startWithWindows === true} disabled={disabled}
        onChange={value => save({ startWithWindows: value })} />
    </Section>
  )
})

/** The first row of Settings › Library: what the Library lists. */
export function LibraryToolsRow({ settings, onSave: save, onLibraryChanged }: {
  settings: LauncherSettings | null
  onSave: SaveSettings
  onLibraryChanged: () => void
}) {
  return (
    <Section id="library-view">
      <SwitchRow id="set-tools" label="Show tools and soundtracks" checked={settings?.showLibraryTools === true} disabled={!settings}
        onChange={async value => {
          const error = await save({ showLibraryTools: value })
          if (!error) onLibraryChanged()
          return error
        }} />
    </Section>
  )
}

/** Settings › In game: the achievement banners and the FPS counter. */
export const InGameSection = memo(function InGameSection({ settings, onSave: save }: {
  settings: LauncherSettings | null
  onSave: SaveSettings
}) {
  const disabled = !settings
  return (
    <>
      <AchievementRows settings={settings} disabled={disabled} save={save} />
      <FpsRows settings={settings} disabled={disabled} save={save} />
    </>
  )
})

const OBS_SOURCE = 'http://127.0.0.1:9364/achievements'

const PREVIEWS = ['common', 'rare', 'epic', 'legendary', 'completion'] as const

/**
 * Settings › Achievements: Exo's banners over the game when one unlocks (on unless turned off), their sound's volume, and
 * a preview shown on the screen in front as an unlock shows it, even with banners off; each press shows the next rarity,
 * so every step gets seen. The OBS browser source is the same banners on a loopback page (AchievementSource.cs,
 * its port fixed so the pasted URL keeps working).
 */
function AchievementRows({ settings, disabled, save }: { settings: LauncherSettings | null; disabled: boolean; save: SaveSettings }) {
  const [volumeError, setVolumeError] = useState<string | null>(null)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [shown, setShown] = useState(0)
  const preview = () => {
    setPreviewError(null)
    setShown(n => n + 1)
    host.achievementsPreview(PREVIEWS[shown % PREVIEWS.length]).catch((error: unknown) => {
      report(error, 'Settings.achievementsPreview')
      setPreviewError(errorText(error, 'Could not show the preview banner.'))
    })
  }
  return (
    <Section id="achievements" title="Achievements">
      <SwitchRow id="set-achievement-banners" label="Show achievement pop-ups" checked={settings?.achievementBanners !== false} disabled={disabled}
        onChange={value => save({ achievementBanners: value })} />
      <SliderRow label="Pop-up sound" min={0} max={100} step={5} value={settings?.achievementVolume ?? 60} disabled={disabled} error={volumeError}
        render={volume => volume === 0 ? 'Off' : `${volume}%`} onCommit={volume => void save({ achievementVolume: volume }).then(setVolumeError)} />
      <Row label="Test pop-up" error={previewError}>
        <button type="button" className="exo-set-btn" disabled={disabled} onClick={preview}>Show</button>
      </Row>
      <CopyRow label="OBS overlay URL" text={OBS_SOURCE} />
    </Section>
  )
}

const FPS_POSITIONS = [
  { value: 'left', label: 'Top left' },
  { value: 'middle', label: 'Top middle' },
  { value: 'right', label: 'Top right' },
] as const

const FPS_ITEMS = [
  { id: 'low', label: '1% low' },
  { id: 'frametime', label: 'Frame time' },
  { id: 'gpu', label: 'GPU use' },
  { id: 'gputemp', label: 'GPU temperature' },
  { id: 'cpu', label: 'CPU use' },
  { id: 'vram', label: 'VRAM' },
  { id: 'ram', label: 'RAM' },
  { id: 'battery', label: 'Lowest battery' },
  { id: 'clock', label: 'Time of day' },
] as const

/**
 * Settings › FPS counter: on or off over games (off until turned on), where it sits and what it adds to the FPS.
 * Its switch has its own call: where Windows says no, turning it on shows
 * Windows' one prompt. Only if that couldn't do it does its detail line ask for a sign-out.
 */
function FpsRows({ settings, disabled, save }: { settings: LauncherSettings | null; disabled: boolean; save: SaveSettings }) {
  const [state, setState] = useState<Awaited<ReturnType<typeof host.fpsCounter>> | null>(null)
  const items = settings?.fpsCounterItems ?? []
  useEffect(() => {
    let live = true
    host.fpsCounter().then(next => { if (live) setState(next) }, (error: unknown) => report(error, 'Settings.fpsCounter'))
    return () => { live = false }
  }, [])
  const fpsOn = state ? state.on : settings?.fpsCounter === true
  return (
    <Section id="fps" title="FPS counter">
      <SwitchRow
        id="set-fps-counter"
        label="Show FPS counter"
        detail={state?.state === 'signout' ? 'Sign out to finish' : undefined}
        checked={fpsOn}
        disabled={disabled}
        onChange={async value => {
          const next = await host.fpsCounter(value)
          setState(next)
          return next.message
        }}
      />
      {fpsOn ? (
        <>
          <ChoiceRow
            id="set-fps-position"
            label="Position"
            value={settings?.fpsCounterPosition ?? 'right'}
            choices={FPS_POSITIONS}
            disabled={disabled}
            onChange={value => save({ fpsCounterPosition: value })}
          />
          {FPS_ITEMS.map(({ id, label }) => (
            <SwitchRow
              key={id}
              id={`set-fps-${id}`}
              label={label}
              checked={items.includes(id)}
              disabled={disabled}
              onChange={value => save({ fpsCounterItems: value ? [...items, id] : items.filter(item => item !== id) })}
            />
          ))}
        </>
      ) : null}
    </Section>
  )
}

/** Download hours as presets: any time, overnight windows, or the workday. */
const HOURS = [
  { value: 'any', label: 'Any time', start: null, end: null },
  { value: '0-8', label: '12–8 AM', start: 0, end: 8 },
  { value: '1-7', label: '1–7 AM', start: 1, end: 7 },
  { value: '9-17', label: '9 AM–5 PM', start: 9, end: 17 },
] as const

/** Settings › Downloads: where games install, when they download, and what updates by itself. Frame generation lives in PC › Graphics only. */
export function DownloadsSection({ settings, disabled, save }: { settings: LauncherSettings | null; disabled: boolean; save: SaveSettings }) {
  const [picking, setPicking] = useState(false)
  const [folderError, setFolderError] = useState<string | null>(null)
  const {
    defaultInstallRoot,
    downloadHoursStart = null,
    downloadHoursEnd = null,
    autoUpdateGames,
    upscalerAntiCheatEverywhere,
  }: Partial<LauncherSettings> = settings ?? {}
  const root = defaultInstallRoot?.trim() || null

  async function browse() {
    if (picking) return
    setPicking(true)
    setFolderError(null)
    try {
      const result = await host.pickFolder('Choose where new games install')
      if (result.ok && result.path) setFolderError(await save({ defaultInstallRoot: result.path }))
      else if (!result.ok && !result.cancelled) setFolderError(errorText(result.message, 'Could not open the folder picker. Try again.'))
    } catch (error) {
      report(error, 'Settings.installFolder')
      setFolderError(errorText(error, 'Could not open the folder picker. Try again.'))
    } finally {
      setPicking(false)
    }
  }

  // Hours set some other way (an older build, the settings file) show as "Set elsewhere", never as Any time.
  const hours: string = HOURS.find(h => h.start === downloadHoursStart && h.end === downloadHoursEnd)?.value ?? 'custom'
  return (
    <Section id="downloads" title="Downloads">
      <Row label="Install folder" detail={root ?? "Exo's Games folder"} error={folderError}>
        {root ? (
          <button type="button" className="exo-set-btn" disabled={disabled || picking} onClick={() => { setFolderError(null); void save({ defaultInstallRoot: null }).then(setFolderError) }}>
            Reset
          </button>
        ) : null}
        <button type="button" className="exo-set-btn" disabled={disabled || picking} onClick={() => void browse()}>
          {picking ? <Spinner label="Choosing a folder" /> : null}Change
        </button>
      </Row>
      <ChoiceRow<string> id="set-download-hours" label="Download hours" value={hours} choices={HOURS} disabled={disabled}
        onChange={value => {
          const pick = HOURS.find(h => h.value === value)
          return pick ? save({ downloadHoursStart: pick.start, downloadHoursEnd: pick.end }) : undefined
        }} />
      <SwitchRow id="set-auto-update" label="Update games automatically" checked={autoUpdateGames !== false} disabled={disabled}
        onChange={value => save({ autoUpdateGames: value })} />
      <SwitchRow id="set-upscalers-anticheat" label="Update DLSS, FSR and XeSS in anti-cheat games" detail="May carry a ban risk. Never touches Riot games or Vanguard."
        checked={upscalerAntiCheatEverywhere === true} disabled={disabled} onChange={value => save({ upscalerAntiCheatEverywhere: value })} />
    </Section>
  )
}
