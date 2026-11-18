// Channel to the C# engine through the web view's messages: WebView2's on Windows, Photino's on macOS and Linux.

interface WebViewHost {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (e: { data: unknown }) => void): void
}

/** Photino's channel: text both ways. */
interface PhotinoExternal {
  sendMessage(message: string): void
  receiveMessage(listener: (message: string) => void): void
}

interface Reply {
  id?: string
  ok?: boolean
  result?: unknown
  error?: { message: string }
  event?: string
  data?: unknown
}

/**
 * Photino's channel shaped as WebView2's. Each message starts with the key the app wrote into this page, so the engine
 * only hears its own page.
 */
function photinoHost(): WebViewHost | undefined {
  const external = (window as unknown as { external?: Partial<PhotinoExternal> }).external
  const key = document.querySelector<HTMLMetaElement>('meta[name="bridge-key"]')?.content
  if (!key || typeof external?.sendMessage !== 'function' || typeof external.receiveMessage !== 'function') return undefined
  const { sendMessage, receiveMessage } = external as PhotinoExternal
  return {
    postMessage: (message) => sendMessage.call(external, key + '\n' + JSON.stringify(message)),
    addEventListener: (_type, listener) => receiveMessage.call(external, (text) => listener({ data: JSON.parse(text) })),
  }
}

// Photino's page first: it carries the app's key (Photino's WebView2 on Windows also shows WebView2's own channel).
const host: WebViewHost | undefined = photinoHost() ?? (window as unknown as { chrome?: { webview?: WebViewHost } }).chrome?.webview

const pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: Error) => void }>()
const listeners = new Map<string, Set<(data: unknown) => void>>()

host?.addEventListener('message', (e) => {
  const m = e.data as Reply
  if (m.event) {
    listeners.get(m.event)?.forEach((l) => l(m.data))
    return
  }
  if (!m.id) return
  const p = pending.get(m.id)
  if (!p) return
  pending.delete(m.id)
  if (m.ok) p.resolve(m.result)
  else p.reject(new Error(m.error?.message ?? 'Something went wrong.'))
})

let counter = 0
let beforeSend: (() => void) | null = null

/** Called before each request goes out (the log sends what it holds first, so its entries come before what they caused). */
export function onBeforeSend(f: () => void) { beforeSend = f }

/** A message that needs no answer. */
export function send(method: string, params: unknown) {
  host?.postMessage({ id: `${Date.now().toString(36)}-${(counter++).toString(36)}`, method, params })
}

export function invoke<T>(method: string, params?: unknown): Promise<T> {
  if (!host) return Promise.reject(new Error('The app engine is not available.'))
  beforeSend?.()
  const id = `${Date.now().toString(36)}-${(counter++).toString(36)}`
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject })
    host.postMessage({ id, method, params: params ?? {} })
  })
}

export function on<T>(event: string, listener: (data: T) => void): () => void {
  let set = listeners.get(event)
  if (!set) listeners.set(event, (set = new Set()))
  const l = listener as (d: unknown) => void
  set.add(l)
  return () => set.delete(l)
}
