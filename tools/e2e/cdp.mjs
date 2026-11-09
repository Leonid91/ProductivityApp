// Minimal Chrome DevTools Protocol client (Node 22+ has WebSocket built in).
import { spawn } from 'node:child_process'
import { writeFileSync } from 'node:fs'

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

export async function launch(exe, args, port) {
  // Debug builds use the .NET runtime of the SDK that built them; never let Windows prompt to install one.
  const env = { ...process.env, DOTNET_DISABLE_GUI_ERRORS: '1' }
  if (process.env.E2E_DOTNET_ROOT) env.DOTNET_ROOT = process.env.E2E_DOTNET_ROOT
  const child = spawn(exe, [...args, '--cdp-port', String(port)], { stdio: 'ignore', detached: false, env })
  let exited = null
  child.on('exit', (code) => { exited = code })
  let target
  for (let i = 0; i < 120 && !target; i++) {
    await sleep(500)
    if (exited !== null) throw new Error(`The app stopped at launch (exit code ${exited}).`)
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json()
      target = list.find((t) => t.type === 'page' && t.url.startsWith('https://finances.localhost'))
    } catch { /* not up yet */ }
  }
  if (!target) { child.kill(); throw new Error('The app did not start.') }
  const ws = new WebSocket(target.webSocketDebuggerUrl)
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject })
  let id = 0
  const pending = new Map()
  const consoleErrors = []
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data)
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id)
      pending.delete(msg.id)
      msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result)
    } else if (msg.method === 'Runtime.exceptionThrown') {
      consoleErrors.push(msg.params.exceptionDetails?.exception?.description ?? msg.params.exceptionDetails?.text)
    } else if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error') {
      consoleErrors.push(msg.params.args.map((a) => a.value ?? a.description).join(' '))
    }
  }
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const n = ++id
    pending.set(n, { resolve, reject })
    ws.send(JSON.stringify({ id: n, method, params }))
  })
  await send('Runtime.enable')
  await send('Page.enable')

  const page = {
    child,
    consoleErrors,
    send,
    async eval(expression) {
      const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
      if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text)
      return r.result.value
    },
    async waitFor(expression, timeout = 20000, label = expression) {
      const t0 = Date.now()
      while (Date.now() - t0 < timeout) {
        const v = await page.eval(`(() => { try { return ${expression} } catch { return false } })()`)
        if (v) return v
        await sleep(150)
      }
      throw new Error('Timed out waiting for: ' + label)
    },
    /** Clicks the first visible element whose text matches exactly (or contains, with partial); scrolls to it only when it is off screen, like a person. */
    async clickText(text, { selector = 'button, [role=button], [role=menuitem], [role=option], li, a', partial = false, index = 0 } = {}) {
      const ok = await page.eval(`(() => {
        const want = ${JSON.stringify(text)}
        const els = [...document.querySelectorAll(${JSON.stringify(selector)})].filter((e) => {
          const t = (e.innerText || e.textContent || '').trim()
          const r = e.getBoundingClientRect()
          return r.width > 0 && r.height > 0 && (${partial} ? t.includes(want) : t === want)
        })
        const el = els[${index}]
        if (!el) return false
        { const v = el.getBoundingClientRect(); if (v.top < 0 || v.bottom > innerHeight) el.scrollIntoView({ block: 'center' }) }
        el.click()
        return true
      })()`)
      if (!ok) throw new Error('Nothing to click with text: ' + text)
      await sleep(250)
    },
    /** A real mouse press and release at the element's centre (some controls open on mouse down). */
    async mouseClick(selector, index = 0) {
      const c = await page.eval(`(() => { const el = document.querySelectorAll(${JSON.stringify(selector)})[${index}]; if (!el) return null; { const v = el.getBoundingClientRect(); if (v.top < 0 || v.bottom > innerHeight) el.scrollIntoView({ block: 'center' }) }; const r = el.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 } })()`)
      if (!c) throw new Error('Nothing to click: ' + selector)
      await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: c.x, y: c.y })
      await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: c.x, y: c.y, button: 'left', clickCount: 1 })
      await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: c.x, y: c.y, button: 'left', clickCount: 1 })
      await sleep(300)
    },
    async click(selector, index = 0) {
      const ok = await page.eval(`(() => { const el = document.querySelectorAll(${JSON.stringify(selector)})[${index}]; if (!el) return false; { const v = el.getBoundingClientRect(); if (v.top < 0 || v.bottom > innerHeight) el.scrollIntoView({ block: 'center' }) }; el.click(); return true })()`)
      if (!ok) throw new Error('Nothing to click: ' + selector)
      await sleep(250)
    },
    async shot(file, { full = false } = {}) {
      await sleep(400)
      const params = { format: 'png', captureBeyondViewport: false }
      const r = await send('Page.captureScreenshot', params)
      writeFileSync(file, Buffer.from(r.data, 'base64'))
      void full
    },
    /** Calls the engine directly, the same way the interface does. */
    async engine(method, params = {}) {
      return page.eval(`new Promise((resolve, reject) => {
        const id = 'e2e-' + Math.random().toString(36).slice(2)
        const on = (e) => { if (e.data && e.data.id === id) { window.chrome.webview.removeEventListener('message', on); e.data.ok ? resolve(e.data.result) : reject(new Error(e.data.error.message)) } }
        window.chrome.webview.addEventListener('message', on)
        window.chrome.webview.postMessage({ id, method: ${JSON.stringify(method)}, params: ${JSON.stringify(params)} })
      })`)
    },
    async scrollMain(y) {
      await page.eval(`document.querySelector('main').scrollTo({ top: ${y} })`)
      await sleep(500)
      await page.settle()
    },
    /** Waits until every period on screen has its data (none still drawn as a placeholder). */
    async settle(timeout = 15000) {
      await page.waitFor(`(() => { const m = document.querySelector('main').getBoundingClientRect(); return [...document.querySelectorAll('section[data-period]')]
        .filter((s) => { const r = s.getBoundingClientRect(); return r.bottom > m.top && r.top < m.bottom })
        .every((s) => !s.querySelector('.MuiSkeleton-root')) })()`, timeout, 'periods on screen loaded').catch(() => undefined)
      await sleep(200)
    },
    async close() {
      try { await page.engine('window.close') } catch { /* already closing */ }
      await sleep(800)
      try { child.kill() } catch { /* gone */ }
      ws.close()
    },
  }
  return page
}
