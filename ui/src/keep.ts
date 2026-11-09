import { useCallback, useReducer } from 'react'

const kept = new Map<string, unknown>()

/**
 * State that outlives its component. A period far from the screen lets go of its tables and charts; the filters,
 * sorting, page and chart choices made in it are kept here under the period's key, so they are all there again when
 * the person scrolls back (or switches between Statements and Analytics).
 */
export function useKept<T>(key: string, initial: T): [T, (value: T) => void] {
  const [, redraw] = useReducer((n: number) => n + 1, 0)
  const value = kept.has(key) ? (kept.get(key) as T) : initial
  const set = useCallback((v: T) => { kept.set(key, v); redraw() }, [key])
  return [value, set]
}
