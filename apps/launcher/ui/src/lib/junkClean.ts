// The outcome of a junk clean, as the rows show it (src/components/gpu/JunkRow.tsx, src/components/apps/AppsPanel.tsx).
// A partial clean is both: what went ("Deleted 1.2 GB") and the holder line beside it ("3 files kept. Those files are in use by X.").
//
// How it could fail: a partial clean shows only "Deleted", hiding the programs that hold the files that stayed; a clean
// that deleted nothing reads as success; a failed call reports nothing.

/** A junk.clean answer: a failed call carries only its message. */
export type CleanResult = { ok: boolean; files?: number; bytes?: number; message?: string | null }
export type CleanOutcome = { done?: string; error?: string }

export function cleanOutcome(result: CleanResult, size: (bytes: number) => string): CleanOutcome {
  if (!result.ok) return { error: result.message ?? 'Nothing was deleted. Try again.' }
  if ((result.files ?? 0) > 0) return { done: `Deleted ${size(result.bytes ?? 0) || 'a little'}`, error: result.message ?? undefined }
  return { error: result.message ?? 'Nothing could be deleted: those files are in use.' }
}
