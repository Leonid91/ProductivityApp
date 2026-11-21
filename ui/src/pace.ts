import { useEffect, useState } from 'react'
import { flushSync } from 'react-dom'

/**
 * Drawing paced to the screen: everything that appears a piece at a time (the periods coming in, their banks, the
 * lines of their tables, the periods let go of) asks here for its next piece. Pieces are drawn just after a frame is
 * shown, the one nearest to the screen first, only as many as leave the next frame time to be shown on time: however
 * many periods come in together, the screen keeps its frames.
 */
interface Job { el: HTMLElement | null; run: () => void; off: () => void; idle: boolean; turn?: string }
const jobs = new Set<Job>()
/** Kinds of work done a few at a time (see paceTurn): how many are under way, how many may be. */
const under = new Map<string, number>()
const most = new Map<string, number>()
const full = (kind?: string) => kind !== undefined && (under.get(kind) ?? 0) >= (most.get(kind) ?? 1)

/**
 * Where the elements of the pieces waiting are, as the browser tells it whenever one comes onto the screen or leaves
 * it (without being asked: no layout is made for it): how far from the screen.
 */
const places = new WeakMap<Element, { d: number }>()

/** Top of the page down, an element after what is in it. */
function down(x: HTMLElement | null, y: HTMLElement | null): number {
  if (!x || !y || x === y) return 0
  if (x.contains(y)) return 1
  if (y.contains(x)) return -1
  return x.compareDocumentPosition(y) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1
}
const watched = new Map<Element, number>()
let watcher: IntersectionObserver | null = null
function watch(el: HTMLElement) {
  watcher ??= new IntersectionObserver((entries) => {
    for (const e of entries) {
      const r = e.boundingClientRect, v = e.rootBounds
      // Hidden (display: none): last.
      const d = r.width === 0 && r.height === 0 ? Number.MAX_SAFE_INTEGER - 1
        : e.isIntersecting || !v ? 0 : r.bottom < v.top ? v.top - r.bottom : r.top - v.bottom
      places.set(e.target, { d })
    }
  }, { root: el.closest('main') })
  watched.set(el, (watched.get(el) ?? 0) + 1)
  watcher.observe(el)
}
function unwatch(el: HTMLElement) {
  const n = (watched.get(el) ?? 1) - 1
  if (n > 0) { watched.set(el, n); return }
  watched.delete(el)
  watcher?.unobserve(el)
}
let asked = 0
/**
 * Pieces are drawn only in the first part of a frame's time (from its start, in ms): drawn later, they and the
 * drawing of the next frame would not fit before the screen needs it (half its 120 frames a second).
 */
const UNTIL = 5
/** Time the pieces of one frame may take together (in ms). */
const WORK = 1.5
/** When the frame being shown started; frames waited in a row (after a few, one piece is drawn anyway). */
let frameStart = 0
let waited = 0
/**
 * How long after a frame's start its message comes when nothing is in the way: the lowest of the last frames. It is
 * the engine's own delay, not lateness (Chromium's engine: about none; WebKit's, as in the macOS and Linux windows:
 * up to a whole frame and more), so the time of a frame is counted from there.
 */
const gaps = new Float64Array(240).fill(Infinity)
let gapAt = 0
let delay = Infinity
function measured(gap: number) {
  const old = gaps[gapAt]
  gaps[gapAt] = gap
  gapAt = (gapAt + 1) % gaps.length
  if (gap <= delay) delay = gap
  // The lowest one left the window: the lowest of those kept.
  else if (old <= delay) { delay = Infinity; for (const g of gaps) if (g < delay) delay = g }
}
/** When the page was last scrolled. */
let lastScroll = 0
document.addEventListener('scroll', () => { lastScroll = performance.now() }, { capture: true, passive: true })

function frame(now: number) {
  asked = 0
  measured(now - frameStart)
  const start = frameStart + delay
  // The frame was late in being shown, or other work came first: the pieces wait for the next one.
  if (now - start > UNTIL - 1 && waited < 3) { waited++; ask(); return }
  waited = 0
  // Pieces that can wait: only when nothing else waits and the page has not just been scrolled, one a frame.
  const calm = ![...jobs].some((j) => !j.idle) && now - lastScroll > 200
  let idleDone = false
  let work = 0
  // The nearest to the screen first; at the same distance, from the top of the page down (what is drawn in an element
  // before the element's own next piece: a table's lines before the next table, a bank's tables before the next
  // bank). Where each piece is comes from what the browser last told (see places): asking it now would make it lay out
  // the page in the middle of a frame.
  const order = [...jobs].filter((job) => (!job.idle || calm) && !full(job.turn)).map((job) => {
    if (!job.el?.isConnected) return { job, d: Number.MAX_SAFE_INTEGER }
    return { job, d: places.get(job.el)?.d ?? 0 }
  }).sort((a, b) => a.d - b.d || down(a.job.el, b.job.el))
  for (const { job } of order) {
    // Called off meanwhile (a piece drawn before it took it away).
    if (!jobs.has(job) || (job.idle && idleDone) || full(job.turn)) continue
    if (job.idle) idleDone = true
    job.off()
    const t0 = performance.now()
    flushSync(job.run)
    // The next frame draws what the pieces changed, at about the time they took again: a little more each frame.
    work += performance.now() - t0
    if (work > WORK || performance.now() - start > UNTIL) break
  }
  if (jobs.size > 0) ask()
}

// The pieces are drawn just after a frame is shown (a message sent from the frame's start arrives once it is on the
// screen): the page is laid out then, finding where each piece is costs nothing, and the next frame shows them.
const after = new MessageChannel()
after.port1.onmessage = () => frame(performance.now())
function ask() {
  if (!asked) asked = requestAnimationFrame((t) => { frameStart = t; after.port2.postMessage(0) })
}

/**
 * Draws `run` just after a coming frame is shown, in its turn (nearest to the screen first; `idle`: only once nothing
 * else waits). Returns how to call it off.
 */
export function pace(el: HTMLElement | null, run: () => void, idle = false, turn?: string): () => void {
  let done = false
  const off = () => { if (done) return; done = true; jobs.delete(job); if (el) unwatch(el) }
  const job = { el, run, off, idle, turn }
  jobs.add(job)
  if (el) watch(el)
  ask()
  return off
}

/**
 * Work of one kind done `atOnce` at a time, in its turn (nearest to the screen first): `run` starts it once fewer of
 * the kind are under way, and is given how to say it is over (then the next one may start). Returns how to call it
 * off before it starts.
 */
export function paceTurn(el: HTMLElement | null, kind: string, atOnce: number, run: (over: () => void) => void): () => void {
  most.set(kind, atOnce)
  let ended = false
  const over = () => { if (ended) return; ended = true; under.set(kind, (under.get(kind) ?? 1) - 1); ask() }
  return pace(el, () => { under.set(kind, (under.get(kind) ?? 0) + 1); run(over) }, false, kind)
}

/** True while pieces inside `el` are still waiting to be drawn (not counting work waiting for its turn). */
export function pending(el: HTMLElement | null): boolean {
  if (!el) return false
  for (const j of jobs) if (!j.turn && j.el && el.contains(j.el)) return true
  return false
}

/**
 * How many of `n` things to draw: `first` at once, then `each` more per piece, paced (see pace). Starts again when
 * `key` changes. `where` is the element they are drawn in (its place on the screen orders the pieces).
 */
export function useStepper(n: number, key: unknown, where: React.RefObject<HTMLElement | null>, first = 1, each = 1): number {
  const [state, setState] = useState({ key, shown: first })
  // Something else to draw: from the first pieces again, in this same drawing (nothing more is shown before).
  let shown = state.shown
  if (state.key !== key) { shown = first; setState({ key, shown: first }) }
  useEffect(() => {
    if (shown >= n) return
    return pace(where.current, () => setState((s) => ({ key: s.key, shown: Math.min(n, s.shown + each) })))
  }, [shown, n, each, where])
  return Math.min(shown, n)
}

/** What is drawn only near the screen (content-visibility: auto): a section of a period, the lines of a table. */
const UNIT = '[data-section], .p-lines'

/**
 * The real height of everything under `ref` drawn only near the screen (see UNIT), kept while it is away: the page has
 * its real length at all times, so scrolling never changes it (the scrollbar stays still under the mouse and the
 * wheel). While what one shows changes (its lines drawn, older operations coming in, a filter, a sort, another width)
 * the browser lays it out with the rest of the page, a little each frame as it changes; once it stops changing it is
 * skipped again away from the screen, at the height it was last laid out at.
 */
export function useRealHeights(ref: React.RefObject<HTMLElement | null>, key: unknown) {
  useEffect(() => {
    const root = ref.current
    if (!root) return
    // The height (of its content) and width each one was last laid out at.
    const known = new WeakMap<Element, { h: number; w: number }>()
    const keep = (x: HTMLElement, h: number, w: number) => {
      known.set(x, { h, w })
      x.style.containIntrinsicHeight = `${h}px`
    }
    // Laid out while it changes: how it was drawn before, the frame it last changed in.
    const open = new Map<HTMLElement, { before: string; at: number }>()
    let frames = 0, asked = 0
    const opened = (x: HTMLElement) => {
      // It and the ones it is in (whose height holds it).
      for (let u: HTMLElement | null = x; u && root.contains(u); u = u.parentElement?.closest<HTMLElement>(UNIT) ?? null) {
        const o = open.get(u)
        if (o) o.at = frames
        else { open.set(u, { before: u.style.contentVisibility, at: frames }); u.style.contentVisibility = 'visible' }
      }
      if (!asked) asked = requestAnimationFrame(close)
    }
    // Skipped again once laid out unchanged for a frame (its size told, see ro) and nothing more is coming in it.
    const close = () => {
      asked = 0
      frames++
      for (const [u, o] of open) {
        if (!u.isConnected) { open.delete(u); continue }
        if (frames - o.at < 2 || !known.has(u) || pending(u)) continue
        u.style.contentVisibility = o.before
        open.delete(u)
      }
      if (open.size > 0) asked = requestAnimationFrame(close)
    }
    // Something changed inside one (the innermost).
    const touched = (n: Node) => {
      const el = n instanceof Element ? n : n.parentElement
      const x = el?.closest<HTMLElement>(UNIT)
      if (x && root.contains(x)) opened(x)
    }
    const mo = new MutationObserver((records) => {
      for (const r of records) {
        // Its own heights written on them.
        if (r.type === 'attributes' && (r.attributeName === 'style' && (r.target as Element).matches(UNIT) || r.attributeName?.startsWith('data-e2e'))) continue
        touched(r.target)
        for (const n of r.addedNodes) {
          if (!(n instanceof HTMLElement)) continue
          if (n.matches(UNIT)) { ro.observe(n); opened(n) }
          for (const x of n.querySelectorAll<HTMLElement>(UNIT)) { ro.observe(x); opened(x) }
        }
      }
    })
    // Laid out (being changed, or near the screen): its height as it is. Skipped with a new width: laid out again.
    const ro = new ResizeObserver((entries) => {
      for (const e of entries) {
        const x = e.target as HTMLElement
        // Taken away, or not shown.
        if (!x.isConnected || e.borderBoxSize[0].inlineSize === 0) continue
        const k = known.get(x)
        const h = e.contentRect.height
        const w = e.borderBoxSize[0].inlineSize
        if (open.has(x) || (k && Math.abs(h - k.h) > 0.01)) keep(x, h, w)
        else if (!k || Math.abs(w - k.w) > 0.5) opened(x)
      }
    })
    for (const x of root.querySelectorAll<HTMLElement>(UNIT)) { ro.observe(x); opened(x) }
    mo.observe(root, { childList: true, subtree: true, characterData: true, attributes: true })
    // Letters drawn with another font once it is read: every one laid out again.
    const fonts = () => { for (const x of root.querySelectorAll<HTMLElement>(UNIT)) opened(x) }
    document.fonts.addEventListener('loadingdone', fonts)
    return () => {
      mo.disconnect(); ro.disconnect()
      cancelAnimationFrame(asked)
      document.fonts.removeEventListener('loadingdone', fonts)
      for (const [u, o] of open) u.style.contentVisibility = o.before
    }
  }, [ref, key])
}
