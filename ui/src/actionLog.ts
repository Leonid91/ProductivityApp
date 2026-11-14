// Writes everything done in the window to the log (Logs page), as raw data: the event's type and properties, the element
// it reached (its path, attributes and text) and what it changed. Clicks, double, right and middle clicks, every key
// pressed, changed fields, scrolls, drags, drops, copy, cut and paste, pages opened, messages shown, errors and console
// errors of the page. Entries go to the engine in small batches, and always before a request they caused, so the log
// keeps their order.
import { onBeforeSend, send } from './bridge'

type Level = 'info' | 'warning' | 'error'
type Source = 'you' | 'app'
interface Entry { at: number; level: Level; source: Source; action: string; detail: string }

const queue: Entry[] = []
let timer = 0
let page = 'periods'

/** detail: text as it is, anything else as JSON. */
export function record(action: string, detail: unknown = '', source: Source = 'you', level: Level = 'info') {
  queue.push({ at: Date.now(), level, source, action, detail: typeof detail === 'string' ? detail : json(detail) })
  if (!timer) timer = window.setTimeout(flush, 150)
}

function json(value: unknown): string {
  try {
    return JSON.stringify(value, (_, v) => (v instanceof Error ? { name: v.name, message: v.message, stack: v.stack } : typeof v === 'bigint' ? String(v) : v))
  } catch (e) {
    return String(value) + ' (not written as JSON: ' + String(e) + ')'
  }
}

function flush() {
  window.clearTimeout(timer)
  timer = 0
  if (queue.length === 0) return
  send('logs.write', { entries: queue.splice(0) })
}

onBeforeSend(flush)

/** A page opened: written, and named in every entry after it. */
export function pageOpened(to: string, view: string | null) {
  record('page', { from: page, to, view }, 'app')
  page = to
}

const control = 'button,a,[role=button],[role=tab],[role=menuitem],[role=menuitemcheckbox],[role=option],[role=checkbox],[role=switch],[role=radio],[role=slider],[role=combobox],[role=gridcell],input,select,textarea,label,summary'

/** The element's place in the page, as a CSS path from the body. */
function path(el: Element): string {
  const parts: string[] = []
  for (let e: Element | null = el; e && e !== document.body && e !== document.documentElement; e = e.parentElement) {
    let part = e.tagName.toLowerCase()
    if (e.id) part += '#' + e.id
    const classes = Array.from(e.classList).filter((c) => !/^(css-|Mui[A-Za-z]+-(focusVisible|focused))/.test(c))
    if (classes.length) part += '.' + classes.join('.')
    const parent = e.parentElement
    if (parent) {
      const same = Array.from(parent.children).filter((c) => c.tagName === e!.tagName)
      if (same.length > 1) part += `:nth-of-type(${same.indexOf(e) + 1})`
    }
    parts.unshift(part)
  }
  return parts.join(' > ')
}

/**
 * An element as data: tag, path, every attribute, its text and its value. Nothing that needs the page laid out (its
 * place on the screen): reading it would make the browser lay out the whole page in the middle of its own work.
 */
function element(el: Element | null, withText = true): Record<string, unknown> | null {
  if (!el) return null
  const attributes: Record<string, string> = {}
  for (const a of Array.from(el.attributes)) if (a.name !== 'class' && a.name !== 'style') attributes[a.name] = a.value
  const out: Record<string, unknown> = { tag: el.tagName.toLowerCase(), path: path(el), attributes }
  // The text of a control or a row; for anything else only its own text (never the whole page under a blank area).
  if (withText) {
    out.text = el.matches(control) || el.matches('tr, td, th, li, [role=row], h1, h2, h3, h4, h5, h6, p, span')
      ? el.textContent ?? ''
      : Array.from(el.childNodes).filter((n) => n.nodeType === Node.TEXT_NODE).map((n) => n.textContent).join('')
  }
  if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) {
    out.value = el.value
    if (el instanceof HTMLInputElement && (el.type === 'checkbox' || el.type === 'radio')) out.checked = el.checked
    if (el instanceof HTMLInputElement && el.type === 'file') out.files = files(el.files)
  }
  return out
}

function files(list: FileList | null | undefined) {
  return Array.from(list ?? []).map((f) => ({ name: f.name, size: f.size, type: f.type, lastModified: new Date(f.lastModified).toISOString() }))
}

/** Where the element is: its control, row, dialog and menu, and the page. */
function target(t: EventTarget | null): Record<string, unknown> {
  const el = t instanceof Element ? t : t instanceof Node ? t.parentElement : null
  if (!el) return { target: t === window ? 'window' : t === document ? 'document' : String(t), page }
  const ctl = el.closest(control)
  const row = el.closest('tr, [role=row]')
  const dialog = el.closest('[role=dialog]')
  const menu = el.closest('[role=menu], [role=listbox]')
  return {
    page,
    target: element(el),
    control: ctl && ctl !== el ? element(ctl) : undefined,
    row: row && row !== el && row !== ctl ? row.textContent : undefined,
    dialog: dialog ? dialog.querySelector('h2, .MuiDialogTitle-root')?.textContent ?? '' : undefined,
    menu: menu ? element(menu, false) : undefined,
  }
}

const modifiers = (e: MouseEvent | KeyboardEvent) => ({ ctrlKey: e.ctrlKey, shiftKey: e.shiftKey, altKey: e.altKey, metaKey: e.metaKey })

function mouse(e: MouseEvent) {
  return {
    type: e.type, isTrusted: e.isTrusted, button: e.button, buttons: e.buttons, detail: e.detail,
    clientX: e.clientX, clientY: e.clientY, screenX: e.screenX, screenY: e.screenY, ...modifiers(e),
    pointerType: (e as PointerEvent).pointerType, timeStamp: e.timeStamp, ...target(e.target),
  }
}

let lastInput = 0
let pointer: { e: PointerEvent; at: Record<string, unknown> } | null = null
const scrolls = new Map<EventTarget, { timer: number; from: { scrollTop: number; scrollLeft: number } | null; byYou: boolean }>()
/** Where each element's last scroll ended: where the next one starts (null before its first). */
const ended = new WeakMap<object, { scrollTop: number; scrollLeft: number }>()

function scrollMetrics(el: Element) {
  return { scrollTop: el.scrollTop, scrollLeft: el.scrollLeft, scrollHeight: el.scrollHeight, scrollWidth: el.scrollWidth, clientHeight: el.clientHeight, clientWidth: el.clientWidth }
}

let installed = false

/** Starts listening to the whole window (once). */
export function installActionLog() {
  if (installed) return
  installed = true
  const opts = { capture: true, passive: true } as const
  const who = (e: Event): Source => (e.isTrusted ? 'you' : 'app')
  for (const type of ['click', 'dblclick', 'contextmenu', 'auxclick'] as const) {
    document.addEventListener(type, (e) => record(type, mouse(e), who(e)), opts)
  }
  document.addEventListener('keydown', (e) => {
    lastInput = performance.now()
    record('keydown', {
      type: e.type, isTrusted: e.isTrusted, key: e.key, code: e.code, keyCode: e.keyCode, location: e.location, repeat: e.repeat,
      isComposing: e.isComposing, ...modifiers(e), timeStamp: e.timeStamp, ...target(e.target),
    }, who(e))
  }, opts)
  document.addEventListener('change', (e) => record('change', { type: e.type, isTrusted: e.isTrusted, timeStamp: e.timeStamp, ...target(e.target) }, who(e)), opts)
  document.addEventListener('copy', (e) => record('copy', { isTrusted: e.isTrusted, selection: String(document.getSelection() ?? ''), ...target(e.target) }, who(e)), opts)
  document.addEventListener('cut', (e) => record('cut', { isTrusted: e.isTrusted, selection: String(document.getSelection() ?? ''), ...target(e.target) }, who(e)), opts)
  document.addEventListener('paste', (e) => record('paste', {
    isTrusted: e.isTrusted, types: Array.from(e.clipboardData?.types ?? []), text: e.clipboardData?.getData('text') ?? '', files: files(e.clipboardData?.files), ...target(e.target),
  }, who(e)), opts)
  document.addEventListener('dragstart', (e) => record('dragstart', { ...mouse(e), types: Array.from(e.dataTransfer?.types ?? []) }, who(e)), opts)
  document.addEventListener('drop', (e) => record('drop', {
    ...mouse(e), types: Array.from(e.dataTransfer?.types ?? []), dropEffect: e.dataTransfer?.dropEffect, files: files(e.dataTransfer?.files),
  }, who(e)), opts)
  document.addEventListener('pointerdown', (e) => { lastInput = performance.now(); pointer = { e, at: mouse(e) } }, opts)
  document.addEventListener('pointerup', (e) => {
    const p = pointer
    pointer = null
    // A press moved before it was let go: a drag (a slider, a column border, a scrollbar, the window's title bar).
    if (p && Math.hypot(e.clientX - p.e.clientX, e.clientY - p.e.clientY) > 4) {
      record('drag', { from: p.at, to: { clientX: e.clientX, clientY: e.clientY, screenX: e.screenX, screenY: e.screenY, buttons: e.buttons, timeStamp: e.timeStamp, ...target(e.target) } }, who(e))
    }
  }, opts)
  document.addEventListener('wheel', () => { lastInput = performance.now() }, opts)
  // A scroll is written once it stops (where it started and ended); the app's own scrolls too.
  document.addEventListener('scroll', (e) => {
    const t = e.target
    if (!t) return
    const el = t instanceof Document ? document.scrollingElement : t as Element
    if (!el) return
    let s = scrolls.get(t)
    if (!s) {
      s = { timer: 0, from: ended.get(el) ?? null, byYou: false }
      scrolls.set(t, s)
    }
    if (performance.now() - lastInput < 1000) s.byYou = true
    window.clearTimeout(s.timer)
    const started = s
    s.timer = window.setTimeout(() => {
      scrolls.delete(t)
      // Read in the next frame, when the browser lays the page out anyway: never a layout of its own.
      requestAnimationFrame(() => {
        if (!el.isConnected) return
        const metrics = scrollMetrics(el)
        ended.set(el, { scrollTop: metrics.scrollTop, scrollLeft: metrics.scrollLeft })
        record('scroll', { page, target: element(el, false), from: started.from, ...metrics }, started.byYou ? 'you' : 'app')
      })
    }, 400)
  }, opts)
  document.addEventListener('visibilitychange', () => record('visibilitychange', { visibilityState: document.visibilityState, page }, 'app'))
  window.addEventListener('error', (e) => record('error', {
    message: e.message, filename: e.filename, lineno: e.lineno, colno: e.colno, error: e.error, page,
  }, 'app', 'error'))
  window.addEventListener('unhandledrejection', (e) => record('unhandledrejection', { reason: e.reason instanceof Error ? e.reason : json(e.reason), page }, 'app', 'error'))
  // What the page writes as errors and warnings in its console, as written.
  for (const level of ['error', 'warn'] as const) {
    const original = console[level].bind(console)
    console[level] = (...args: unknown[]) => {
      record('console.' + level, { args: args.map((a) => (a instanceof Error ? a : typeof a === 'object' ? a : String(a))), page }, 'app', level === 'error' ? 'error' : 'warning')
      original(...args)
    }
  }
  window.addEventListener('pagehide', flush)
}
