const GIFT_LINK = /^https?:\/\/(?:www\.)?(?:discord\.gift|(?:(?:ptb|canary)\.)?discord(?:app)?\.com\/gifts)\/([\w-]{8,48})\/?(?:[?#].*)?$/i

/** A Discord gift link's code; null for any other link. */
export const giftCode = (url: string) => GIFT_LINK.exec(url)?.[1] ?? null
