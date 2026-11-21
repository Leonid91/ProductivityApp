// The same driver as cdp.mjs, with the interface in WebKit (the engine of Safari: the macOS window's WKWebView, and of
// WebKitGTK on Linux) instead of WebView2. The real app still runs (off-screen, its Photino window) and does the work:
// each message of the WebKit page goes to the engine through the app's own page, and each answer and event comes back.
// Playwright's WebKit is not part of the repository:
//   E2E_PLAYWRIGHT=<folder of the playwright-core package>  PLAYWRIGHT_BROWSERS_PATH=<its browsers>
import { writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { launch as launchApp, sleep } from './cdp.mjs'

export { sleep }

const retry = async (attempt) => { const t0 = Date.now(); for (;;) { const v = await attempt(); if (v || Date.now() - t0 > 5000) return v; await sleep(100) } }

export async function launch(exe, args, port) {
  if (!process.env.E2E_PLAYWRIGHT) throw new Error('E2E_PLAYWRIGHT is not set (folder of the playwright-core package).')
  const { webkit } = await import(pathToFileURL(join(process.env.E2E_PLAYWRIGHT, 'index.mjs')).href)
  const app = await launchApp(exe, args, port)
  const origin = await app.eval('location.origin')
  // What the engine sends to its own page is kept there for the WebKit page.
  await app.eval(`window.__relay = []; window.chrome.webview.addEventListener('message', (e) => window.__relay.push(typeof e.data === 'string' ? e.data : JSON.stringify(e.data)))`)
  const html = await app.eval(`fetch('/index.html').then((r) => r.text())`)

  const size = args.includes('--size') ? args[args.indexOf('--size') + 1].split('x').map(Number) : [1440, 920]
  const browser = await webkit.launch()
  const context = await browser.newContext({ viewport: { width: size[0], height: size[1] } })
  const wk = await context.newPage()
  const consoleErrors = []
  wk.on('pageerror', (e) => consoleErrors.push(String(e?.stack ?? e)))
  wk.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()) })
  // The page and its files as the app serves them (same address, same content policy), its key replaced by the WebKit one.
  await context.route(origin + '/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/' || path.endsWith('.html')) {
      return route.fulfill({ contentType: 'text/html', body: html.replace(/<meta name="bridge-key" content="[^"]*"/, '<meta name="bridge-key" content="webkit"') })
    }
    const body = await app.eval(`fetch(${JSON.stringify(path)}).then(async (r) => { const b = new Uint8Array(await r.arrayBuffer()); let s = ''; for (let i = 0; i < b.length; i += 32768) s += String.fromCharCode(...b.subarray(i, i + 32768)); return { type: r.headers.get('content-type'), data: btoa(s) } })`)
    return route.fulfill({ contentType: body.type ?? 'application/octet-stream', body: Buffer.from(body.data, 'base64') })
  })
  // Photino's channel in the page (window.external), carried to the app's page and its engine.
  await wk.exposeBinding('__toEngine', async (_, text) => {
    const json = text.slice(text.indexOf('\n') + 1)
    await app.eval(`window.external.sendMessage(document.querySelector('meta[name="bridge-key"]').content + '\\n' + ${JSON.stringify(json)})`)
  })
  await wk.addInitScript(() => {
    const listeners = []
    window.__fromEngine = (text) => listeners.forEach((l) => l(text))
    Object.defineProperty(window, 'external', {
      configurable: true,
      value: { sendMessage: (text) => window.__toEngine(text), receiveMessage: (l) => listeners.push(l) },
    })
  })
  let open = true
  void (async () => {
    while (open) {
      const messages = await app.eval('window.__relay.splice(0)').catch(() => [])
      for (const m of messages) await wk.evaluate((t) => window.__fromEngine?.(t), m).catch(() => undefined)
      await sleep(10)
    }
  })()
  await wk.goto(origin + '/index.html')

  const page = {
    child: app.child,
    consoleErrors,
    browser: browser.version(),
    async eval(expression) {
      return wk.evaluate(expression)
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
    async clickText(text, { selector = 'button, [role=button], [role=menuitem], [role=option], li, a', partial = false, index = 0 } = {}) {
      const ok = await retry(() => page.eval(`(() => {
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
      })()`))
      if (!ok) throw new Error('Nothing to click with text: ' + text)
      await sleep(250)
    },
    async mouseClick(selector, index = 0) {
      const c = await retry(() => page.eval(`(() => { const el = document.querySelectorAll(${JSON.stringify(selector)})[${index}]; if (!el) return null; { const v = el.getBoundingClientRect(); if (v.top < 0 || v.bottom > innerHeight) el.scrollIntoView({ block: 'center' }) }; const r = el.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 } })()`))
      if (!c) throw new Error('Nothing to click: ' + selector)
      await wk.mouse.move(c.x, c.y)
      await wk.mouse.down()
      await wk.mouse.up()
      await sleep(300)
    },
    async click(selector, index = 0) {
      const ok = await retry(() => page.eval(`(() => { const el = document.querySelectorAll(${JSON.stringify(selector)})[${index}]; if (!el) return false; { const v = el.getBoundingClientRect(); if (v.top < 0 || v.bottom > innerHeight) el.scrollIntoView({ block: 'center' }) }; el.click(); return true })()`))
      if (!ok) throw new Error('Nothing to click: ' + selector)
      await sleep(250)
    },
    async shot(file) {
      await sleep(400)
      writeFileSync(file, await wk.screenshot())
    },
    /** The DevTools commands the end-to-end check sends, done the WebKit way. */
    async send(method, params = {}) {
      if (method === 'Input.insertText') return wk.keyboard.insertText(params.text)
      if (method === 'Page.addScriptToEvaluateOnNewDocument') return wk.addInitScript(params.source)
      if (method === 'Input.dispatchMouseEvent') {
        if (params.type === 'mouseMoved') return wk.mouse.move(params.x, params.y)
        if (params.type === 'mousePressed') return wk.mouse.down()
        if (params.type === 'mouseReleased') return wk.mouse.up()
      }
      if (method === 'Input.dispatchKeyEvent' && params.type === 'keyDown') {
        const mods = params.modifiers ?? 0
        const key = (mods & 1 ? 'Alt+' : '') + (mods & 2 ? 'Control+' : '') + (mods & 4 ? 'Meta+' : '') + (mods & 8 ? 'Shift+' : '') + params.key
        return wk.keyboard.press(key)
      }
      throw new Error('Not available in the WebKit driver: ' + method + ' ' + JSON.stringify(params))
    },
    /** Calls the engine directly through the WebKit page, the way the interface does. */
    async engine(method, params = {}) {
      return page.eval(`new Promise((resolve, reject) => {
        const id = 'e2e-' + Math.random().toString(36).slice(2)
        window.external.receiveMessage((text) => { const m = JSON.parse(text); if (m && m.id === id) m.ok ? resolve(m.result) : reject(new Error(m.error.message)) })
        window.external.sendMessage('webkit\\n' + JSON.stringify({ id, method: ${JSON.stringify(method)}, params: ${JSON.stringify(params)} }))
      })`)
    },
    async scrollMain(y) {
      await page.eval(`document.querySelector('main').scrollTo({ top: ${y} })`)
      await sleep(500)
      await page.settle()
    },
    async settle(timeout = 15000) {
      await page.waitFor(`(() => { const m = document.querySelector('main').getBoundingClientRect(); return [...document.querySelectorAll('section[data-period]')]
        .filter((s) => { const r = s.getBoundingClientRect(); return r.bottom > m.top && r.top < m.bottom })
        .every((s) => !s.querySelector('.MuiSkeleton-root')) })()`, timeout, 'periods on screen loaded').catch(() => undefined)
      await sleep(200)
    },
    async close() {
      open = false
      await browser.close().catch(() => undefined)
      await app.close()
    },
  }
  return page
}

