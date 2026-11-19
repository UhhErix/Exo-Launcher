// Exo Shield on YouTube Music: the player's ad fields go before the page reads them, so no video ad is scheduled.
// It runs in the page itself at document_start (manifest: world MAIN), before YouTube's own code.
// When YouTube renames a field, README.md says how to find the new name.
(() => {
  'use strict';
  // The fields of a player response (/youtubei/v1/player, and the watch page's first one) that schedule ads.
  const AD_FIELDS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams'];

  const clean = (response) => {
    if (response === null || typeof response !== 'object') return;
    for (const field of AD_FIELDS) if (field in response) delete response[field];
  };
  // A player response, one wrapped as { playerResponse }, or a list of either.
  const strip = (value) => {
    if (value === null || typeof value !== 'object') return value;
    for (const item of Array.isArray(value) ? value : [value]) {
      clean(item);
      clean(item?.playerResponse);
    }
    return value;
  };

  // Proxies keep the originals' names and native toString.
  JSON.parse = new Proxy(JSON.parse, { apply: (target, self, args) => strip(Reflect.apply(target, self, args)) });
  const json = Response.prototype.json;
  Response.prototype.json = new Proxy(json, { apply: (target, self, args) => Reflect.apply(target, self, args).then(strip) });
  // The watch page's first response, when the page sets it as a global.
  let initial;
  try {
    Object.defineProperty(window, 'ytInitialPlayerResponse', {
      configurable: true,
      get: () => initial,
      set: (value) => { initial = strip(value); },
    });
  } catch { /* the page defined it first; the parse hooks still apply */ }
})();
