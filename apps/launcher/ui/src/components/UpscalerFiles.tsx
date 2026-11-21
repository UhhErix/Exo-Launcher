import { type DlssStatus } from '../lib/host'
import { trimStop } from '../lib/utils'
import { upscalerLineWord, upscalerView } from '../lib/upscalers'
import { GameLine } from './game/GameLine'

export type UpscalerFilesProps = {
  status?: DlssStatus | null
  scanState?: 'loading' | 'ready' | 'failed'
  /** PC › Graphics › Upscalers, where every game's upscalers are updated and checked again. */
  onOpen: () => void
}

/**
 * The game's upscalers as one row that opens PC › Graphics › Upscalers. It is always there, like every line on the page,
 * and says whether the game's DLSS, FSR and XeSS files are up to date (with the version), how many updates wait, the
 * anti-cheat, why the files could not be read, or that the game has none (upscalerLineWord). A shining word while they
 * are read. Updating happens on that page; the originals and the anti-cheat consent are in Properties.
 */
export function UpscalerFiles({ status, scanState = 'ready', onOpen }: UpscalerFilesProps) {
  const word = scanState === 'loading' ? null : upscalerLineWord(upscalerView(status), scanFailure(scanState, status))
  if (scanState === 'ready' && word?.text === 'Not supported in this game') return null
  return <GameLine name="Upscalers" word={word} onOpen={onOpen} />
}

/** The files could not be read (the install folder is gone, the read failed): why, for the line. */
function scanFailure(scanState: UpscalerFilesProps['scanState'], status: DlssStatus | null | undefined): string | null {
  if (scanState !== 'failed') return null
  return trimStop(status?.message ?? '') || 'Could not check the files'
}
