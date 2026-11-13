import { useEffect, useState } from 'react'
import { flushSync } from 'react-dom'

/**
 * Drawing paced to the screen: everything that appears a piece at a time (the periods coming in, their banks, the
 * lines of their tables, the periods let go of) asks here for its next piece. Pieces are drawn just after a frame is
 * shown, the one nearest to the screen first, only as many as leave the next frame time to be shown on time: however
 * many periods come in together, the screen keeps its frames.
 */
interface Job { el: HTMLElement | null; run: () => void; off: () => void; idle: boolean }
const jobs = new Set<Job>()

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
/** When the page was last scrolled. */
let lastScroll = 0
document.addEventListener('scroll', () => { lastScroll = performance.now() }, { capture: true, passive: true })

function frame(now: number) {
  asked = 0
  // The frame was late in being shown, or other work came first: the pieces wait for the next one.
  if (now - frameStart > UNTIL - 1 && waited < 3) { waited++; ask(); return }
  waited = 0
  // Pieces that can wait (see useRealHeight): only when nothing else waits and the page has not just been scrolled,
  // one a frame.
  const calm = ![...jobs].some((j) => !j.idle) && now - lastScroll > 200
  let idleDone = false
  let work = 0
  // The nearest to the screen first; at the same distance, from the top of the page down (what is drawn in an element
  // before the element's own next piece: a table's lines before the next table, a bank's tables before the next
  // bank). Where each piece is comes from what the browser last told (see places): asking it now would make it lay out
  // the page in the middle of a frame.
  const order = [...jobs].filter((job) => !job.idle || calm).map((job) => {
    if (!job.el?.isConnected) return { job, d: Number.MAX_SAFE_INTEGER }
    return { job, d: places.get(job.el)?.d ?? 0 }
  }).sort((a, b) => a.d - b.d || down(a.job.el, b.job.el))
  for (const { job } of order) {
    // Called off meanwhile (a piece drawn before it took it away).
    if (!jobs.has(job) || (job.idle && idleDone)) continue
    if (job.idle) idleDone = true
    job.off()
    const t0 = performance.now()
    flushSync(job.run)
    // The next frame draws what the pieces changed, at about the time they took again: a little more each frame.
    work += performance.now() - t0
    if (work > WORK || performance.now() - frameStart > UNTIL) break
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
export function pace(el: HTMLElement | null, run: () => void, idle = false): () => void {
  let done = false
  const off = () => { if (done) return; done = true; jobs.delete(job); if (el) unwatch(el) }
  const job = { el, run, off, idle }
  jobs.add(job)
  if (el) watch(el)
  ask()
  return off
}

/** True while pieces inside `el` are still waiting to be drawn. */
export function pending(el: HTMLElement | null): boolean {
  if (!el) return false
  for (const j of jobs) if (j.el && el.contains(j.el)) return true
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

/**
 * The tables of a bank of a period, drawn only near the screen (content-visibility: auto), are given their real height
 * once all of the bank is there (and again when it changes), in its turn and only while nothing else waits: each table
 * drawn for a frame, then left to the browser again, which keeps that height while it is away. The page has its real
 * length (the scrollbar does not change as one scrolls), and a table drawn for the first time above the screen never
 * moves what is on it.
 */
export function useRealHeight(ref: React.RefObject<HTMLElement | null>, content: unknown) {
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const shown: HTMLElement[] = []
    let stop = () => {}
    const hide = () => { for (const x of shown.splice(0)) x.style.contentVisibility = '' }
    // One table at a time (with the sections it is in), drawn for a frame, then left to the browser again, keeping its
    // height.
    const show = (units: HTMLElement[][]) => {
      stop = pace(el, () => {
        hide()
        const next = units.shift()
        if (!next) return
        for (const x of next) { x.style.contentVisibility = 'visible'; shown.push(x) }
        show(units)
      }, true)
    }
    let quiet = 0
    const first = () => {
      if (!el.isConnected) return
      // Its tables still coming in: once they are all there (nothing more asked for, frames in a row).
      quiet = pending(el) ? 0 : quiet + 1
      if (quiet < 3) { stop = pace(el, first, true); return }
      // Each table with the sections it is in (within the bank); a section without one, alone.
      const inner = [...el.querySelectorAll<HTMLElement>('[data-section], .p-lines')]
      const units = inner.filter((x) => !x.querySelector('[data-section], .p-lines')).map((x) => {
        const unit = [x]
        for (let up = x.parentElement; up && up !== el; up = up.parentElement) if (inner.includes(up)) unit.unshift(up)
        return unit
      })
      show(units)
    }
    stop = pace(el, first, true)
    return () => {
      stop()
      for (const x of shown) x.style.contentVisibility = ''
    }
    // Measured again when what it shows changes (older operations of a long period coming in).
  }, [ref, content])
}
