/** The Discord rail's hover tip, as plain state: the tip, the voice rooms of the server it is for, and the read that
    answers them. Every change that ends a tip (the pointer leaves it or a rail item is clicked) also ends its read, so a
    late answer for a server the pointer has left, or the one you clicked into, is dropped. */

export type RailTipBox = { guildId: string | null; label: string; left: number; top: number }

export type RailTipState<R> = {
  box: RailTipBox | null
  /** Counts each change of tip and server; a read carries the count it started with. */
  ask: number
  rooms: R[]
  /** The server the rooms belong to, so they are never shown under another server's tip. */
  roomsFor: string | null
}

export function emptyRailTip<R>(): RailTipState<R> {
  return { box: null, ask: 0, rooms: [], roomsFor: null }
}

/** Hovering a tip: the same server keeps its read; another server (or a label) starts a new one. */
export function startRailTip<R>(state: RailTipState<R>, box: RailTipBox): RailTipState<R> {
  if (box.guildId !== null && state.box?.guildId === box.guildId) return { ...state, box }
  return { ...state, box, ask: state.ask + 1, rooms: [], roomsFor: null }
}

/** The pointer leaves the rail item, or the item is clicked: the tip and its read end. */
export function endRailTip<R>(state: RailTipState<R>): RailTipState<R> {
  return { ...state, box: null, ask: state.ask + 1, rooms: [], roomsFor: null }
}

/** A read's answer lands only while its own read is still the current one for that server. */
export function answerRailTip<R>(state: RailTipState<R>, ask: number, guildId: string, rooms: R[]): RailTipState<R> {
  if (ask !== state.ask || state.box?.guildId !== guildId) return state
  return { ...state, rooms, roomsFor: guildId }
}

/** The voice rooms the tip shows right now: only those answered for the server it is for. */
export function railTipRooms<R>(state: RailTipState<R>): R[] {
  return state.box?.guildId && state.roomsFor === state.box.guildId ? state.rooms : []
}
