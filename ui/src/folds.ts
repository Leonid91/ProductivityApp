import { useCallback, useSyncExternalStore } from 'react'

/**
 * What is folded in the periods: each period (month, year...), each bank in it, the "All banks" part, and each table.
 * Keys start with "p:" (periods, banks, all banks) or "t:" (tables). Periods and banks are folded until opened; tables
 * are open (the transfers folded when the setting says so) until folded. "Fold everything" and "Unfold everything" set
 * every period and bank at once (unfolding opens every table too) and forget what was opened or folded one by one.
 */
let periodsAll: boolean | null = null
let tablesAll: boolean | null = null
const one = new Map<string, boolean>()
const listeners = new Set<() => void>()
let version = 0

function changed() {
  version++
  listeners.forEach((l) => l())
}

const subscribe = (l: () => void) => { listeners.add(l); return () => { listeners.delete(l) } }
const snapshot = () => version

/** Open or folded: set one by one, else by the last "everything", else as it starts. */
export function isOpen(key: string, byDefault: boolean): boolean {
  const set = one.get(key)
  if (set !== undefined) return set
  const all = key.startsWith('t:') ? tablesAll : periodsAll
  return all ?? byDefault
}

export function setOpen(key: string, open: boolean) {
  one.set(key, open)
  changed()
}

/** Unfolds (or folds) every period, bank and table. */
export function setEverything(open: boolean) {
  periodsAll = open
  tablesAll = open ? true : null
  one.clear()
  changed()
}

/** Something of a period is unfolded: the button offers to fold everything. */
export function anythingOpen(): boolean {
  if (periodsAll) return true
  for (const [k, v] of one) if (v && k.startsWith('p:')) return true
  return false
}

/** One thing's fold, drawn again when it changes. */
export function useFold(key: string, byDefault: boolean): [boolean, (open: boolean) => void] {
  useSyncExternalStore(subscribe, snapshot)
  const set = useCallback((open: boolean) => setOpen(key, open), [key])
  return [isOpen(key, byDefault), set]
}

/** Whether anything of the periods is unfolded (the button's label). */
export function useAnythingOpen(): boolean {
  useSyncExternalStore(subscribe, snapshot)
  return anythingOpen()
}

/** A click on a fold's heading folds or unfolds it, unless it ended a selection of its text. */
export const clickedNotSelected = () => (window.getSelection()?.toString() ?? '') === ''
