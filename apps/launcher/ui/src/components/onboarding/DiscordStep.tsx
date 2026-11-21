import { useEffect, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { DiscordQrSignIn } from '../discord/DiscordSignIn'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { Row, State } from '../settings/parts'
import { Wait } from '../ui/Wait'

export function DiscordStep() {
  const [signedIn, setSignedIn] = useState<boolean | null>(null)

  useEffect(() => {
    let live = true
    const off = onHostEvent('discord.state', event => { if (event.state === 'ready' || event.state === 'connecting') setSignedIn(true) })
    host.discordStatus().then(status => { if (live) setSignedIn(status.signedIn === true || status.state === 'ready') })
      .catch((error: unknown) => { report(error, 'DiscordStep.status'); if (live) setSignedIn(false) })
    return () => { live = false; off() }
  }, [])

  return signedIn === null ? (
    <Wait label="Checking Discord"><div className="exo-set-list"><Row label="Discord" icon={<svg width="22" height="22" />}><State on>Signed in</State></Row></div></Wait>
  ) : signedIn ? (
    <div className="exo-set-list">
      <Row label="Discord" icon={<CompanyLogo company="discord" size={22} />}><State on>Signed in</State></Row>
    </div>
  ) : (
    <div className="exo-ob-discord">
      <p>Open Discord on your phone, go to You › Scan QR code, and point it here.</p>
      <DiscordQrSignIn />
    </div>
  )
}
