import { useState } from 'react'
import { lookalikeQuestion, openAnyway } from '../../lib/linkGuard'
import { useConfirmInPlace } from './ConfirmButton'

/**
 * A link that asks in place when its address imitates a brand's (DESIGN.md › Confirm): `go` opens it and reports a
 * look-alike back; the link's words become the question (Bad), and a second press opens it anyway. Spread `ref` and
 * `className` on the link, call `click` from its press, and show its words through `ConfirmLabel` with `armed` and `question`.
 */
export function useLinkGuard<T extends HTMLElement = HTMLAnchorElement>(url: string, go: (onLookalike: (brand: string) => void) => void) {
  const [brand, setBrand] = useState('')
  const question = lookalikeQuestion(brand) + '?'
  const ask = useConfirmInPlace<T>(question, () => openAnyway(url), true)
  return {
    ref: ask.ref,
    className: ask.className,
    armed: ask.armed,
    question,
    click: () => {
      if (ask.armed) return ask.press()
      go(found => { setBrand(found); ask.arm() })
    },
  }
}
