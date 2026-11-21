// Work that can wait until the window is idle. Safari's web view (the macOS window) and WebKitGTK (Linux) have no
// requestIdleCallback: there it runs once the screen has been drawn.

/** Runs work when the window is idle (at the latest after timeout). Returns what cancels it. */
export function whenIdle(work: () => void, timeout: number): () => void {
  if (typeof window.requestIdleCallback === 'function') {
    const id = window.requestIdleCallback(work, { timeout })
    return () => window.cancelIdleCallback(id)
  }
  let timer = 0
  const frame = window.requestAnimationFrame(() => { timer = window.setTimeout(work, 0) })
  return () => { window.cancelAnimationFrame(frame); window.clearTimeout(timer) }
}
