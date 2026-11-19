import { memo, useEffect, useState } from 'react'
import { Gift } from '../../brand/icons'
import { host, onHostEvent, type StoreGift } from '../../lib/host'
import type { PlainError } from '../../lib/errorWords'
import { ActionButton, ConfirmButton } from '../ui/ConfirmButton'
import { ErrorNote } from '../ui/ErrorNote'
import { ArtImg } from '../ui/ArtImg'

const keyOf = (gift: StoreGift) => `${gift.store}:${gift.id}`

/**
 * Gifts (DESIGN.md › Library): games someone sent you on Steam or Epic, waiting to be accepted. Shows only while
 * one waits. Accept adds the game to your library (the store is read again, so it appears); Decline asks first,
 * since the sender is refunded. Each button says how it went (its ring, then its check), and then the gift leaves.
 */
export const GiftsShelf = memo(function GiftsShelf() {
  const gifts = useGifts()
  // An answered gift leaves at once, before the store's next read drops it.
  const [answered, setAnswered] = useState<ReadonlySet<string>>(() => new Set())
  const shown = gifts.filter(gift => !answered.has(keyOf(gift)))
  if (shown.length === 0) return null
  return (
    <section className="exo-gifts" aria-labelledby="exo-gifts-title">
      <h2 className="exo-section-title" id="exo-gifts-title">Gifts</h2>
      <ul className="exo-gifts-row">
        {shown.map(gift => (
          <GiftCard key={keyOf(gift)} gift={gift} onDone={() => setAnswered(all => new Set(all).add(keyOf(gift)))} />
        ))}
      </ul>
    </section>
  )
})

function GiftCard({ gift, onDone }: { gift: StoreGift; onDone: () => void }) {
  const [error, setError] = useState<string | null>(null)
  // Each button shows its own work (ActionButton): its ring, then its check, and then the card leaves the shelf (an
  // accepted game shows in the Library); a refusal says why under the gift.
  const answer = (accept: boolean) => () => { setError(null); return host.giftsAnswer(gift.store, gift.id, accept) }
  const failed = (failure: PlainError) => setError(failure.text)
  return (
    <li className="exo-gift">
      <div className="exo-gift-art">
        <ArtImg src={gift.image} draggable={false} loading="lazy" decoding="async" fallback={<Gift size={28} aria-hidden="true" />} />
      </div>
      <div className="exo-gift-text">
        <strong>{gift.title}</strong>
        {gift.sender ? <span>From {gift.sender}</span> : null}
        {gift.message ? <p className="exo-gift-message">{gift.message}</p> : null}
        {error ? <ErrorNote className="exo-gift-line is-bad" message={error} /> : null}
      </div>
      <div className="exo-gift-actions">
        <ConfirmButton className="exo-set-btn" question="Decline? They get a refund" danger why="none" context="GiftsShelf.decline"
          fallback="Exo couldn’t decline the gift. Try again." onConfirm={answer(false)} onFailed={failed} onDone={onDone}>
          Decline
        </ConfirmButton>
        <ActionButton className="exo-set-btn is-primary" why="none" context="GiftsShelf.accept" fallback="Exo couldn’t accept the gift. Try again."
          onPress={answer(true)} onFailed={failed} onDone={onDone}>
          Accept
        </ActionButton>
      </div>
    </li>
  )
}

// What Exo last read, then each read after it (Exo reads both stores every half hour and on Library › Refresh).
function useGifts(): StoreGift[] {
  const [gifts, setGifts] = useState<StoreGift[]>([])
  useEffect(() => {
    let live = true
    host.giftsList().then(result => { if (live) setGifts(result.gifts) }).catch(() => { })
    const off = onHostEvent('gifts.changed', result => setGifts(result.gifts))
    return () => { live = false; off() }
  }, [])
  return gifts
}
