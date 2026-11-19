import { useState, type ImgHTMLAttributes, type ReactNode } from 'react'

/**
 * Art that may not arrive: a picture that fails to load gives way to `fallback` (the place's own plate: a title, an
 * initial, an icon), never the browser's broken-image glyph. A new `src` tries again.
 */
export function ArtImg({ src, fallback = null, alt = '', ...rest }: Omit<ImgHTMLAttributes<HTMLImageElement>, 'src'> & {
  src: string | null | undefined
  fallback?: ReactNode
}) {
  const [failed, setFailed] = useState<string | null>(null)
  if (!src || failed === src) return <>{fallback}</>
  return <img {...rest} src={src} alt={alt} onError={event => { setFailed(src); rest.onError?.(event) }} />
}
