// Channel to the C# engine through WebView2 web messages.

interface WebViewHost {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (e: { data: unknown }) => void): void
}

interface Reply {
  id?: string
  ok?: boolean
  result?: unknown
  error?: { message: string }
  event?: string
  data?: unknown
}

const host: WebViewHost | undefined = (window as unknown as { chrome?: { webview?: WebViewHost } }).chrome?.webview

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

export function invoke<T>(method: string, params?: unknown): Promise<T> {
  if (!host) return Promise.reject(new Error('The app engine is not available.'))
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
