/**
 * The width of the left rail on every device's page (the picture, name and facts), from the room's width inside its
 * gutters and the text size. One measure, for two programs: the controller's and audio's pages use it directly
 * (DevicePage) and Exo sends it to Exo Mouse in its frame (DevicesRoom's MouseFrame, as room.rail), so the rail can't
 * drift between devices. It is what Exo Mouse's hero came to at 1080p (about 400px) and no narrower than its readout needs.
 */
export const railWidth = (room: number, scale: number) => Math.round(Math.max(340 * scale, Math.min(560 * scale, room * 0.215)))
