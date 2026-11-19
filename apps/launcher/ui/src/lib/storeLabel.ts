/** A store's name as Exo writes it. No imports, so plain-node tests can use it. */
export function storeLabel(store: string): string {
  const map: Record<string, string> = {
    local: 'Local',
    steam: 'Steam',
    epic: 'Epic',
    gog: 'GOG',
    riot: 'Riot',
    xbox: 'Xbox',
    ea: 'EA',
    ubisoft: 'Ubisoft',
    battlenet: 'Battle.net',
    amazon: 'Amazon',
    rockstar: 'Rockstar',
    itch: 'itch.io',
    minecraft: 'Minecraft',
    roblox: 'Roblox',
    paradox: 'Paradox',
    wargaming: 'Wargaming',
    discord: 'Discord',
  }
  return map[store.toLowerCase()] ?? store
}
