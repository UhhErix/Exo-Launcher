/**
 * Play and Stop are held per game, not for every game: a press on a game that is starting or closing is refused and says why;
 * a press on any other game goes through. The table, every row:
 *
 *   held as   | Play                            | Stop
 *   nothing   | goes                            | goes
 *   starting  | "<title> is already starting."  | "<title> is still starting."
 *   closing   | "<title> is closing."           | "<title> is already closing."
 */
export type GameHold = 'starting' | 'closing'
export type Press = 'play' | 'stop'

export function pressOutcome(held: GameHold | undefined, press: Press, title: string): { go: true } | { go: false, say: string } {
  if (!held) return { go: true }
  if (held === 'starting') return { go: false, say: press === 'play' ? `${title} is already starting.` : `${title} is still starting.` }
  return { go: false, say: press === 'play' ? `${title} is closing.` : `${title} is already closing.` }
}

/** The holds after one game's changes; the same map is returned when nothing changed. */
export function withHold(holds: ReadonlyMap<string, GameHold>, gameId: string, held: GameHold | null): ReadonlyMap<string, GameHold> {
  if ((holds.get(gameId) ?? null) === held) return holds
  const next = new Map(holds)
  if (held) next.set(gameId, held)
  else next.delete(gameId)
  return next
}
