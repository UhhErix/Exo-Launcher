import { openMusicSettings } from '../../lib/music'
import { Button } from '../ui/Button'

/** The room's way to Settings › Music, where the service is chosen, wherever the room can't go on with this one (signed out, won't open). */
export function ChangeMusicService() {
  return <Button variant="ghost" onPress={openMusicSettings}>Change music service</Button>
}
