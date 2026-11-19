import { host, type MissingDependency } from '../../lib/host'
import { report } from '../../lib/report'
import { useNotice } from '../../lib/notify'
import { updateWords, type AppUpdate } from './useAppUpdate'
import type { StatusTone } from './useActionStatus'

export type DepPrompt = {
  gameId: string
  action: 'play' | 'install' | 'update'
  deps: MissingDependency[]
  awaitingContinue?: boolean
}

export type ShellNoticesProps = {
  depPrompt: DepPrompt | null
  onInstallDeps: () => void
  onContinueAfterDeps: () => void
  onDismissDeps: () => void
  update: AppUpdate
  /** The update notice hides on the Settings room, which has its own control. */
  showUpdate: boolean
  /** A message that belongs to no open sheet. Only failures read red; a sticky one waits for Dismiss. */
  notice: { message: string; tone: StatusTone; sticky: boolean } | null
  onDismissNotice: () => void
  /** A passing shell notice (a sale, a mouse alert), shown beside the status, never over it. */
  passing?: string | null
  transferFailures?: Array<{ gameId: string; title: string; message: string }>
  onDismissTransferFailure?: (gameId: string) => void
  /** A Game clients read Exo could not answer: says why, with Try again. Never holds the library. */
  clientsProblem?: { text: string; busy: boolean; onRetry: () => void } | null
}

/** What the shell's state has to say, as notices in the one stack (shell/NoticeStack.tsx). Draws nothing itself. */
export function ShellNotices({
  depPrompt, onInstallDeps, onContinueAfterDeps, onDismissDeps,
  update, showUpdate, notice, onDismissNotice, passing = null,
  transferFailures = [], onDismissTransferFailure, clientsProblem = null,
}: ShellNoticesProps) {
  useDepsNotice(depPrompt, onInstallDeps, onContinueAfterDeps, onDismissDeps)
  useUpdateNotice(update, showUpdate)
  useNotice('shell:status', notice && {
    text: notice.message,
    tone: notice.tone === 'bad' ? 'bad' : undefined,
    sticky: notice.sticky,
    onDismiss: onDismissNotice,
  })
  useNotice('shell:clients', clientsProblem && {
    title: 'Game clients', text: clientsProblem.text, tone: 'bad',
    action: { label: clientsProblem.busy ? 'Checking…' : 'Try again', onPress: clientsProblem.onRetry, busy: clientsProblem.busy, disabled: clientsProblem.busy },
  })
  useNotice('shell:passing',Boolean(passing) && passing !== notice?.message && { text: passing ?? '', sticky: false })
  return <>{transferFailures.map(failure => (
    <TransferFailure key={failure.gameId} failure={failure} onDismiss={() => onDismissTransferFailure?.(failure.gameId)} />
  ))}</>
}

function TransferFailure({ failure, onDismiss }: { failure: { gameId: string; title: string; message: string }; onDismiss: () => void }) {
  // A Steam download Steam stopped is resumed in Steam's own Downloads.
  const openSteam = () => host.steamDownloads().catch(cause => report(cause, 'ShellNotices.openSteam'))
  useNotice(`shell:transfer:${failure.gameId}`, {
    title: failure.title, text: failure.message, tone: 'bad', onDismiss,
    action: failure.gameId.startsWith('steam:') ? { label: 'Open Steam', onPress: () => void openSteam() } : undefined,
  })
  return null
}

function useDepsNotice(prompt: DepPrompt | null, onInstall: () => void, onContinue: () => void, onDismiss: () => void) {
  const names = prompt?.deps.map(dep => dep.name).join(', ')
  useNotice('shell:deps', prompt && {
    title: prompt.awaitingContinue ? `Waiting for ${names}` : `Needs ${names}`,
    // Installed in one go, from the makers' own installers; Windows asks once. Only a restart Windows wants makes it wait.
    text: prompt.awaitingContinue ? 'Restart Windows to finish installing, then check again.'
      : prompt.deps.length > 1 ? 'This game needs them to run. Exo installs them in one go.' : 'This game needs it to run. Exo can install it now.',
    // One button. Dismissing it is "Try without": the next Play goes ahead without them (LauncherApp).
    action: prompt.awaitingContinue
      ? { label: 'Check and retry', onPress: onContinue, primary: true }
      : { label: 'Install', onPress: onInstall, primary: true },
    onDismiss,
  })
}

/**
 * An update going in from Settings or What's New, and how far; one that failed, with Try again. Never an offer: a newer
 * release is offered only by the update sheet as Exo opens (it shows its own progress) and by Settings › General.
 */
function useUpdateNotice(update: AppUpdate, shown: boolean) {
  const going = updateWords(update.status, update.percent)
  useNotice('shell:update', shown && !update.sheet && (update.busy || !!update.error) && {
    text: update.busy ? going.words : update.error ?? '',
    tone: update.busy ? undefined : 'bad',
    progress: update.busy ? going.share : undefined,
    action: update.busy ? undefined : { label: 'Try again', onPress: () => void update.install(), primary: true },
    lasting: update.busy,
    onDismiss: update.dismiss,
  })
  // Kept for after a game: said once, plainly, with nothing to press.
  useNotice('shell:update-waiting', shown && !!update.waiting && { text: update.waiting ?? '', onDismiss: update.dismiss })
  // The update before this start didn't take: said once, with nothing to press (that version is not offered again by itself).
  useNotice('shell:update-problem', shown && !!update.problem && { text: update.problem ?? '', tone: 'bad', onDismiss: update.dismissProblem })
}
