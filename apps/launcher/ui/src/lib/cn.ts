import { clsx, type ClassValue } from 'clsx'

/**
 * Joins class names. No Tailwind conflict merging: no call site passes conflicting utilities. On its own so a module
 * every Exo app shares (the Exo Profile's) can join class names without lib/utils, which reaches Launcher's host.
 */
export function cn(...inputs: ClassValue[]) {
  return clsx(inputs)
}
