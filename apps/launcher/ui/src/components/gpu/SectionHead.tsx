import { createContext, useContext, type ReactNode } from 'react'
import { createPortal } from 'react-dom'

/** The end of a PC section's title line (PcRoom.tsx › Section), where what the section holds puts its actions. */
export const SectionSlot = createContext<HTMLElement | null>(null)

/**
 * A section's actions on its title line, at its right end (DESIGN.md › PC: a panel's buttons sit at its top, as This
 * PC's chips sit on its name's line). Rendered where the section's own state lives; nothing outside a section.
 */
export function SectionActions({ children }: { children: ReactNode }) {
  const slot = useContext(SectionSlot)
  return slot ? createPortal(children, slot) : null
}
