import { createContext, createElement, useContext, useSyncExternalStore, type ReactNode } from 'react'

/** What every amount reads as while the amounts are hidden (the eye of the menu). */
export const MASK = '•••••'

let hidden = false
const listeners = new Set<() => void>()

/** Whether the amounts are hidden: every amount, share and salary of the pages reads as {@link MASK}. */
export function amountsHidden(): boolean {
  return hidden
}

export function setAmountsHidden(value: boolean): void {
  if (value === hidden) return
  hidden = value
  for (const listener of listeners) listener()
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

const HiddenContext = createContext(false)

/**
 * Tells the whole page when the amounts are hidden or shown. One subscription for the page: a row of a long list that
 * comes into view reads the context, at no cost.
 */
export function AmountsHiddenProvider({ children }: { children: ReactNode }) {
  const value = useSyncExternalStore(subscribe, amountsHidden)
  return createElement(HiddenContext.Provider, { value }, children)
}

/** Draws the component again when the amounts are hidden or shown: every component that writes amounts calls it. */
export function useAmountsHidden(): boolean {
  return useContext(HiddenContext)
}
