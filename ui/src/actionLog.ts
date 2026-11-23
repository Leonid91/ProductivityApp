// Writes to the log (Logs page) what the window does that the engine does not see: pages opened, messages shown,
// errors and console errors of the page, as raw data (the actions themselves are written by the engine as it runs
// them). Entries go to the engine in small batches, and always before a request they caused, so the log keeps their
// order.
import { onBeforeSend, send } from './bridge'

type Level = 'debug' | 'info' | 'warning' | 'error'
type Source = 'you' | 'app'
interface Entry { at: number; level: Level; source: Source; action: string; detail: string; stack: string }

const queue: Entry[] = []
let timer = 0
let page = 'statements'

/** detail: text as it is, anything else as JSON. */
export function record(action: string, detail: unknown = '', source: Source = 'you', level: Level = 'info') {
  queue.push({ at: Date.now(), level, source, action, detail: typeof detail === 'string' ? detail : json(detail), stack: callStack() })
  if (!timer) timer = window.setTimeout(flush, 150)
}

/** The interface's call stack where the entry is written (this file's own frames left out). */
function callStack(): string {
  // The browser keeps 10 frames by default: every frame is kept here.
  const v8 = Error as ErrorConstructor & { stackTraceLimit: number }
  const limit = v8.stackTraceLimit
  v8.stackTraceLimit = Infinity
  const stack = new Error().stack ?? ''
  v8.stackTraceLimit = limit
  // The first lines are this file's own (callStack, record): left out.
  const lines = stack.split('\n').slice(1)
  const first = lines.findIndex((l) => !/\b(callStack|record)\b/.test(l))
  return lines.slice(first < 0 ? 0 : first).join('\n')
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

let installed = false

/** Starts listening to the whole window (once). */
export function installActionLog() {
  if (installed) return
  installed = true
  // Errors keep their whole call stack (the browser keeps only the last 10 calls by default).
  ;(Error as ErrorConstructor & { stackTraceLimit: number }).stackTraceLimit = 200
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
