export function isDiscordSnowflake(id: string | null | undefined): id is string {
  return typeof id === 'string' && /^\d{1,20}$/.test(id)
}
