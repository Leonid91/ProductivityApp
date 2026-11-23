// Speed and memory of the real app on large data: imports the files made by "SampleStatements big" through the
// engine, then scrolls and switches views while counting the frames the interface draws (fps), times how long each
// action takes until the screen is drawn (ms), and measures the memory of the app and its browser processes (MB).
//
//   node tests/Finance.PerformanceTests/perf.mjs <ProductivityApp.exe> <big folder> <output folder>
import { execSync } from 'node:child_process'
import { mkdirSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { launch, sleep } from '../Finance.AppDriver/cdp.mjs'

const [exe, big, out] = process.argv.slice(2)
rmSync(out, { recursive: true, force: true })
mkdirSync(out, { recursive: true })
const p = await launch(exe, ['--offscreen', '--data-dir', join(out, 'data'), '--size', '1440x920'], 9334)
const results = {}
const note = (k, v) => { results[k] = v; console.log(`${k}: ${typeof v === 'object' ? JSON.stringify(v) : v}`) }

// Frames drawn per second while `step` runs once per frame for `seconds`, with the average and the longest frame (ms).
const frames = (step, seconds = 4) => p.eval(`new Promise((done) => {
  const times = []; let last = performance.now(); const end = last + ${seconds * 1000}
  const tick = (now) => { times.push(now - last); last = now; (${step})(); if (now < end) requestAnimationFrame(tick); else {
    const s = [...times].sort((a, b) => a - b)
    done({ fps: Math.round(times.length / ${seconds}), avgMs: +(times.reduce((a, b) => a + b, 0) / times.length).toFixed(1), p95Ms: +s[Math.floor(s.length * 0.95)].toFixed(1), worstMs: +s.at(-1).toFixed(1) }) } }
  requestAnimationFrame(tick)
})`)

// Time from an action to the screen drawn with every period on screen loaded (polled each frame in the page).
const settled = `(() => { const m = document.querySelector('main').getBoundingClientRect(); const s = [...document.querySelectorAll('section[data-period]')]
  .filter((x) => { const r = x.getBoundingClientRect(); return r.bottom > m.top && r.top < m.bottom }); return s.length > 0 && s.every((x) => !x.querySelector('.MuiSkeleton-root')) })()`
const timed = (action, until = settled) => p.eval(`new Promise((done) => {
  const t0 = performance.now(); (${action})()
  let seen = 0
  const tick = () => { if (${until}) { if (++seen === 2) return done(+(performance.now() - t0).toFixed(0)) } else seen = 0; if (performance.now() - t0 > 30000) return done(-1); requestAnimationFrame(tick) }
  requestAnimationFrame(tick)
})`)
// The first element of the selector with this text (the Statements and Analytics of the menu, or of the page before).
const clickIn = (selector, text) => `() => [...document.querySelectorAll(${JSON.stringify(selector)})].find((b) => b.innerText.trim() === ${JSON.stringify(text)}).click()`

// Round trip of one engine call from the interface: the request, the work, the answer read back (average of `times`).
const engineMs = (method, params, times = 5) => p.eval(`(async () => {
  const call = () => new Promise((resolve, reject) => {
    const id = 'perf-' + Math.random().toString(36).slice(2)
    const on = (e) => { if (e.data && e.data.id === id) { window.chrome.webview.removeEventListener('message', on); e.data.ok ? resolve(e.data.result) : reject(new Error(e.data.error.message)) } }
    window.chrome.webview.addEventListener('message', on)
    window.chrome.webview.postMessage({ id, method: ${JSON.stringify(method)}, params: ${JSON.stringify(params)} })
  })
  await call()
  const t0 = performance.now()
  for (let i = 0; i < ${times}; i++) await call()
  return +((performance.now() - t0) / ${times}).toFixed(1)
})()`)

/** Memory of the interface (JavaScript heap) and of every process of the app: the engine and its browser processes. */
const memory = async () => {
  const heap = await p.eval('Math.round(performance.memory.usedJSHeapSize / 1048576)')
  const pid = p.child?.pid
  let engine = 0, total = 0
  if (pid) {
    const ps = `$all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, WorkingSetSize; $ids = @(${pid}); $i = 0; while ($i -lt $ids.Count) { $ids += @($all | Where-Object { $_.ParentProcessId -eq $ids[$i] } | ForEach-Object { $_.ProcessId }); $i++ }; ($all | Where-Object { $ids -contains $_.ProcessId } | ForEach-Object { $_.WorkingSetSize } | Measure-Object -Sum).Sum; ($all | Where-Object { $_.ProcessId -eq ${pid} }).WorkingSetSize`
    const [t, e] = execSync(`powershell -NoProfile -Command "${ps}"`).toString().trim().split(/\s+/).map(Number)
    total = Math.round(t / 1048576); engine = Math.round(e / 1048576)
  }
  return { interfaceHeapMB: heap, engineMB: engine, allProcessesMB: total }
}

try {
  await p.waitFor(`document.querySelector('nav') !== null`)
  for (const f of readdirSync(big).filter((x) => x.endsWith('.csv'))) {
    const d = await p.engine('import.parseFile', { path: join(big, f) })
    await p.engine('import.commit', { draftId: d.id, accountId: null, newAccountName: null, currency: f.includes('Caisse') ? 'EUR' : 'CAD', kind: 'bank', edits: [], bankId: null, newBankName: f.split(' ')[0] })
  }
  // Rates of the European Central Bank, so that the euro bank is converted as for the person.
  await p.engine('rates.update')
  // Start-up: from the page loading to the first periods drawn with their data (timed in the page from its first moment).
  await p.send('Page.enable')
  await p.send('Page.addScriptToEvaluateOnNewDocument', { source: `(() => {
    const tick = () => { const m = document.querySelector('main'); if (m && ${settled.replace(/`/g, '\\`')}) { window.__startupMs = Math.round(performance.now()); return } requestAnimationFrame(tick) }
    requestAnimationFrame(tick)
  })()` })
  const startups = []
  for (let i = 0; i < 3; i++) {
    await p.eval('location.reload()')
    await p.waitFor(`window.__startupMs !== undefined`, 60000, 'periods drawn after start')
    startups.push(await p.eval('window.__startupMs'))
    await sleep(1500)
  }
  note('start-up ms (page load to periods drawn), 3 runs', startups)
  await sleep(1000)
  note('memory at start', await memory())
  const index = await p.engine('periods.index')
  const [y, m] = index.last.split('-').map(Number)
  const lastDay = new Date(y, m, 0).getDate()
  note('engine: month report ms', await engineMs('periods.report', { from: `${index.last}-01`, to: `${index.last}-${lastDay}`, granularity: 'month' }))
  note('engine: year report ms', await engineMs('periods.report', { from: `${y}-01-01`, to: `${y}-12-31`, granularity: 'year' }))
  note('engine: ten years report ms', await engineMs('periods.report', { from: `${y - 9}-01-01`, to: `${y}-12-31`, granularity: 'range' }, 3))
  note('engine: periods index ms', await engineMs('periods.index', {}))
  note('idle', await frames('() => {}', 3))
  note('scrolling months', await frames(`() => document.querySelector('main').scrollBy(0, 60)`, 6))
  note('memory after scrolling months', await memory())
  // The page's width changing every frame (a scroll bar coming and going, the window resized): every period measured again.
  note('width changing', await frames(`() => { const m = document.querySelector('main'); m.style.paddingRight = m.style.paddingRight ? '' : '12px' }`, 4))
  await p.eval(`document.querySelector('main').style.paddingRight = ''`)
  await p.eval(`document.querySelector('main').scrollTo({ top: 0 })`)
  await sleep(1500)
  note('switch to analytics ms', await timed(clickIn('nav button, [data-period-view] button', 'Analytics'), `${settled} && !document.querySelector('[data-loading-charts]') && document.querySelectorAll('main section[data-period] svg').length > 5`))
  await sleep(1000)
  note('switch to statements ms', await timed(clickIn('nav button, [data-period-view] button', 'Statements')))
  await sleep(1000)
  note('switch to year ms', await timed(clickIn('main button', 'Year')))
  note('just after switching to year', await frames('() => {}', 3))
  note('scrolling years', await frames(`() => document.querySelector('main').scrollBy(0, 60)`, 5))
  await p.eval(`document.querySelector('main').scrollTo({ top: 0 })`)
  await sleep(1500)
  note('switch to all time ms', await timed(clickIn('main button', 'All time')))
  note('ten years view', await frames('() => {}', 3))
  note('switch to month ms', await timed(clickIn('main button', 'Month')))
  // The months start folded to their summary: unfolded, their tables are drawn a piece at a time, a line of one is
  // there to be clicked.
  await p.eval(`(() => { const b = document.querySelector('[data-fold-everything]'); if (b && b.innerText.trim() === 'Unfold everything') b.click() })()`)
  await p.waitFor(`document.querySelector('section tbody tr td:nth-child(2) button') !== null`, 20000, 'a line of a table')
  await p.click('section tbody tr td:nth-child(2) button', 0)
  await sleep(400)
  note('category picker scrolling', await frames(`() => document.querySelector('[role=menu]')?.parentElement?.scrollBy(0, 20)`, 3))
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await sleep(400)
  note('open calendar ms', await timed(clickIn('nav button', 'Calendar'), `document.querySelectorAll('main [role=gridcell], main td').length > 20`))
  note('calendar', await frames('() => {}', 3))
  // Statements in the menu (before, Periods: the script measures the builds before and after).
  note('open statements ms', await timed(`() => [...document.querySelectorAll('nav button')].find((b) => ['Statements', 'Periods'].includes(b.innerText.trim())).click()`))
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('Definitions', { selector: 'button' })
  await p.clickText('Brands', { selector: 'main [role=group] button' })
  await sleep(800)
  note('brands list scrolling', await frames(`() => document.querySelector('main').scrollBy(0, 60)`, 3))
  note('memory at end', await memory())
} finally {
  await p.close()
}
writeFileSync(join(out, 'perf.json'), JSON.stringify(results, null, 1))
