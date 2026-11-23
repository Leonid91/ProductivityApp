// End-to-end check of the real app: starts the executable off-screen (no window is ever shown), drives the
// interface through the WebView2 debugging port, imports the sample statements through the in-app file
// browser, exercises the main features and saves screenshots.
//
//   node tests/Finance.EndToEndTests/run.mjs <ProductivityApp.exe> <samples folder> <output folder>
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
// E2E_BROWSER=webkit: the interface in WebKit (Safari's engine, as in the macOS and Linux windows), see webkit.mjs.
const { launch, sleep } = await import(process.env.E2E_BROWSER === 'webkit' ? '../Finance.AppDriver/webkit.mjs' : '../Finance.AppDriver/cdp.mjs')

const [exe, samples, out] = process.argv.slice(2)
if (!exe || !samples || !out) { console.error('usage: run.mjs <exe> <samples> <out>'); process.exit(2) }
const data = join(out, 'data')
const reports = join(out, 'reports')
rmSync(out, { recursive: true, force: true })
mkdirSync(reports, { recursive: true })

const checks = []
const check = (name, ok, detail = '') => { checks.push({ name, ok: !!ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`) }
// Every screen photographed is also read: no phrase of the interface is joined to the next by a semicolon.
const semicolons = []
const shot = async (p, name) => {
  // (The raw details of the log's entries are data, not phrases of the interface: left out.)
  const text = await p.eval(`(() => { const hidden = [...document.querySelectorAll('[data-log-detail]')]; hidden.forEach((d) => { d.dataset.e2eDisplay = d.style.display; d.style.display = 'none' }); const t = document.body.innerText; hidden.forEach((d) => { d.style.display = d.dataset.e2eDisplay; delete d.dataset.e2eDisplay }); return t })()`).catch(() => '')
  for (const m of String(text).matchAll(/[A-Za-z0-9)]; [A-Za-z]/g)) semicolons.push(`${name}: ${String(text).slice(Math.max(0, m.index - 30), m.index + 30).replace(/\s+/g, ' ')}`)
  return p.shot(join(out, name))
}
const text = (p) => p.eval('document.body.innerText')
// The page loaded again: waits for the new document (the old one can still answer for a moment after the reload is asked).
const reload = async (p) => {
  await p.eval('window.__oldPage = true; setTimeout(() => location.reload(), 0)')
  await p.waitFor(`!window.__oldPage && document.querySelector('nav') !== null`, 20000, 'the page loaded again')
}
// Analytics has two parts: General (the charts) and Transfers (the money moved, over time and in total).
// A period drawn in full: it appears a piece at a time (the nearest to the screen first), so wait until nothing more
// has come in it for a moment.
const settle = async (p, sel) => {
  let last = -1, same = 0
  for (let i = 0; i < 60 && same < 3; i++) {
    const n = await p.eval(`document.querySelectorAll('${sel} *').length`)
    same = n === last ? same + 1 : 0
    last = n
    await sleep(200)
  }
}
const analyticsPart = async (p, part) => { await p.clickText(part, { selector: '[data-analytics-view] button' }); await sleep(500) }
// An element brought to the middle of the screen, again until the page stops moving (a period being drawn keeps what
// is read in its place, so the page can move under a scroll).
const bringIntoView = async (p, sel) => {
  let last = null
  for (let i = 0; i < 30; i++) {
    const top = await p.eval(`(() => { const e = document.querySelector('${sel}'); if (!e) return null; const r = e.getBoundingClientRect(); if (r.top < 120 || r.bottom > innerHeight - 20) e.scrollIntoView({ block: 'center' }); return Math.round(e.getBoundingClientRect().top) })()`)
    if (top !== null && top === last) return
    last = top
    await sleep(300)
  }
}
// The Logs page: Settings > General, Open logs (not in the left menu).
const openLogs = async (p) => {
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('General', { selector: 'button' })
  await p.waitFor(`!!document.querySelector('[data-open-logs]')`, 10000, 'Open logs')
  await p.click('[data-open-logs]')
}
// In Statements a period starts folded to its summary, and so does each bank in it: a person opens the period with the
// button of its heading, then each bank with its own (its tables open, the transfers folded as the setting says).
const openPeriod = async (p, from, withAll = false) => {
  const sec = `section[data-period="${from}"]`
  // Brought to the top of the screen until it stays there (a list of periods made again can move it).
  const toTop = async () => { for (let i = 0, last = null; i < 30; i++) { const top = await p.eval(`(() => { const s = document.querySelector('${sec}'); if (!s) return null; const t = s.getBoundingClientRect().top; if (t < 150 || t > 400) s.scrollIntoView({ block: 'start' }); return Math.round(s.getBoundingClientRect().top) })()`); if (top !== null && top === last) return; last = top; await sleep(300) } }
  const waitNear = async (cond, label) => { for (let i = 0; i < 100; i++) { if (await p.eval(`(() => { try { return !!(${cond}) } catch { return false } })()`)) return; await toTop(); await sleep(300) } throw new Error('Timed out waiting for: ' + label) }
  await toTop()
  await waitNear(`document.querySelector('${sec} .p-period-head [data-fold]')`, 'period ' + from)
  if (!(await p.eval(`document.querySelector('${sec}').hasAttribute('data-open')`))) await p.click(`${sec} .p-period-head [data-fold]`)
  await waitNear(`document.querySelectorAll('${sec} [data-part-block]:not([data-part-block="all"])').length > 0`, 'banks of ' + from)
  // Banks come in one after the other: each opened as it comes, until no new one comes.
  for (let quiet = 0, i = 0; quiet < 4 && i < 80; i++) {
    const opened = await p.eval(`(() => { const b = [...document.querySelectorAll('${sec} [data-part-block]' + (${withAll} ? '' : ':not([data-part-block="all"])'))].find(b => !b.hasAttribute('data-open') && b.querySelector('[data-part-head] [data-fold]')); if (!b) return false; b.querySelector('[data-part-head] [data-fold]').click(); return true })()`)
    quiet = opened ? 0 : quiet + 1
    await sleep(250)
  }
  await settle(p, sec)
}
// The transfers timeline of a period as drawn, beside what the engine says it holds.
const timelineChecks = async (p, sec, t, label) => {
  const drawn = await p.eval(`(() => { const c = document.querySelector('${sec} [data-transfer-timeline]'); if (!c) return null; const svg = c.querySelector('svg'); const box = svg?.getBoundingClientRect(); const inside = (r) => r.left >= box.left - 1 && r.right <= box.right + 1 && r.top >= box.top - 1 && r.bottom <= box.bottom + 1; const rects = (q) => [...c.querySelectorAll(q)].map(e => e.getBoundingClientRect()); const labels = rects('[data-timeline-label] rect'); const hit = (a, b) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom; let over = 0; for (let i = 0; i < labels.length; i++) for (let j = i + 1; j < labels.length; j++) if (hit(labels[i], labels[j])) over++; return { lanes: [...c.querySelectorAll('[data-timeline-lane]')].map(e => e.dataset.timelineLane), banks: [...c.querySelectorAll('[data-timeline-bank]')].map(e => e.dataset.timelineBank), kinds: [...c.querySelectorAll('[data-timeline-event]')].map(e => e.dataset.timelineEvent), labels: labels.length, over, labelsInside: labels.every(inside), nets: [...c.querySelectorAll('[data-timeline-net]')].map(e => e.textContent), totals: Object.fromEntries([...c.querySelectorAll('[data-total]')].map(e => [e.dataset.total, e.innerText.replace(/\\s+/g, ' ')])), ticks: c.querySelectorAll('[data-timeline-tick]').length, width: box?.width ?? 0, page: document.documentElement.scrollWidth <= window.innerWidth } })()`)
  const cad = (v) => '$' + v.toLocaleString('en-CA', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
  const kinds = t.events.filter((e) => e.from !== e.to).map((e) => e.kind)
  check(`${label}: one line per account in the box of its bank, the outside last`, !!drawn && drawn.lanes.length === t.lanes.length && drawn.banks.join('|') === t.banks.map((b) => b.name).join('|')
    && (!t.lanes.some((l) => l.outside) || drawn.lanes[drawn.lanes.length - 1] === 'outside'), JSON.stringify(drawn?.lanes))
  check(`${label}: every movement drawn, inside a bank, between banks or with the outside`, !!drawn && drawn.kinds.slice().sort().join() === kinds.slice().sort().join() && kinds.length > 0, JSON.stringify(drawn?.kinds))
  check(`${label}: the totals of each kind and what every account gained or gave`, !!drawn && drawn.totals.inside?.includes(cad(t.inside)) && drawn.totals.between?.includes(cad(t.between))
    && drawn.totals.sent?.includes(cad(t.sentOutside)) && drawn.totals.received?.includes(cad(t.receivedOutside)) && (t.lost == null) === (drawn.totals.lost === undefined)
    && drawn.nets.length === t.lanes.filter((l) => !l.outside).length && t.lanes.filter((l) => !l.outside).every((l, i) => drawn.nets[i].includes(cad(Math.abs(l.net)))), JSON.stringify({ totals: drawn?.totals, nets: drawn?.nets }))
  check(`${label}: amounts written beside their movements without covering one another, inside the drawing, time labelled`, !!drawn && drawn.labels > 0 && drawn.over === 0 && drawn.labelsInside && drawn.ticks > 0 && drawn.width > 300 && drawn.page, JSON.stringify(drawn))
  // Pointing at a movement writes it, even one without a label of its own; the others fade.
  const at = await p.eval(`(() => { const c = document.querySelector('${sec} [data-transfer-timeline]'); const es = [...c.querySelectorAll('[data-timeline-event]')]; const labelled = new Set([...c.querySelectorAll('[data-timeline-label]')].map(e => e.dataset.timelineLabel)); const e = es.find((_, i) => !labelled.has(String(i))) ?? es[es.length - 1]; e.scrollIntoView({ block: 'center' }); const r = e.querySelector('[data-timeline-arrow]').getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2, title: e.querySelector('title').textContent, labels: labelled.size, events: es.length } })()`)
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x, y: at.y })
  await sleep(400)
  const hovered = await p.eval(`(() => { const c = document.querySelector('${sec} [data-transfer-timeline]'); const es = [...c.querySelectorAll('[data-timeline-event]')]; return { faded: es.filter(e => e.style.opacity === '0.2').length, labels: [...c.querySelectorAll('[data-timeline-label]')].filter(e => e.style.opacity !== '0.2').map(e => e.textContent) } })()`)
  check(`${label}: a movement pointed at is written, the others fade`, hovered.faded === at.events - 1 && hovered.labels.length === 1 && hovered.labels[0].includes('→') && at.title.includes('→'), JSON.stringify({ at, hovered }))
  // Every transfer ends with an arrow in the stream it reaches; one scale for all: each stream at its widest and each
  // transfer as thick as its money (the smallest are kept a pixel thick).
  const sizes = await p.eval(`(() => { const c = document.querySelector('${sec} [data-transfer-timeline]'); const svg = c.querySelector('svg'); const scale = +svg.dataset.timelineScale; return { scale, arrows: c.querySelectorAll('[data-timeline-event] [data-timeline-arrow]').length, events: c.querySelectorAll('[data-timeline-event]').length,
    lanes: [...c.querySelectorAll('[data-timeline-lane]')].map(l => [+l.dataset.timelineMost, +l.dataset.timelineThick, l.querySelector('[data-timeline-stream]') ? Math.max(0, ...[...l.querySelectorAll('[data-timeline-stream] rect, [data-timeline-stream] path')].map(r => r.getBBox().height)) : 0]),
    moves: [...c.querySelectorAll('[data-timeline-event]')].map(e => [+e.dataset.timelineAmount, +e.dataset.timelineSent]) } })()`)
  const close = (a, b) => Math.abs(a - b) <= Math.max(0.6, 0.01 * Math.abs(b))
  check(`${label}: an arrow on every transfer, streams and transfers as thick as their money`, sizes.scale > 0 && sizes.arrows === sizes.events && sizes.events > 0
    && sizes.lanes.every(([most, thick, drawn]) => close(thick, most * sizes.scale) && drawn <= thick + 0.6) && sizes.lanes.some(([most, thick, drawn]) => most > 0 && close(drawn, thick))
    && sizes.moves.every(([amount, sent]) => close(sent, amount * sizes.scale)), JSON.stringify(sizes))
  return drawn
}

// The first-launch log of this run, written here instead of the user's own one.
const firstLaunchLog = join(out, 'first-launch.txt')
process.env.PRODUCTIVITYAPP_FIRST_LAUNCH_LOG = firstLaunchLog
const p = await launch(exe, ['--offscreen', '--data-dir', data, '--size', '1440x920'], 9333)
// Text read the way it shows on the screen: the banks and tables of a period away from the screen are drawn only when
// they come near (content-visibility: auto), and the browser gives no text (innerText) for what it has not drawn. The
// element read, and what it is in, are drawn for the reading.
const readable = `(() => {
  if (window.__readable) return
  window.__readable = true
  let style = null
  const own = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'innerText')
  Object.defineProperty(HTMLElement.prototype, 'innerText', {
    configurable: true,
    set(v) { own.set.call(this, v) },
    get() {
      if (!style?.isConnected) {
        style = document.createElement('style')
        style.textContent = '[data-e2e-read], [data-e2e-read] *, [data-e2e-up] { content-visibility: visible !important }'
        document.head.append(style)
      }
      this.setAttribute('data-e2e-read', '')
      const up = []
      for (let e = this.parentElement; e; e = e.parentElement) { e.setAttribute('data-e2e-up', ''); up.push(e) }
      try { return own.get.call(this) } finally { this.removeAttribute('data-e2e-read'); for (const e of up) e.removeAttribute('data-e2e-up') }
    },
  })
})()`
await p.send('Page.addScriptToEvaluateOnNewDocument', { source: readable })
await p.eval(readable)
let failed = false
try {
  await p.waitFor(`document.querySelector('nav') !== null`)
  await p.engine('settings.set', { key: 'reportsFolder', value: reports })
  await sleep(600)
  await shot(p, '01-empty.png')
  check('empty state shown', (await text(p)).includes('No operations yet'))

  // ---- import through the in-app file browser
  await p.clickText('Import', { selector: 'nav button' })
  await p.eval(`localStorage.setItem('fileBrowser.lastFolder', ${JSON.stringify(samples)})`)
  await p.clickText('Choose files')
  await p.waitFor(`[...document.querySelectorAll('[role=dialog] li, [role=dialog] [role=button]')].some(e => e.innerText.includes('.pdf'))`, 15000, 'file list')
  await shot(p, '02-file-browser.png')
  await p.clickText('Select all', { selector: '[role=dialog] button' })
  const files = readdirSync(samples).filter((f) => f.endsWith('.pdf') || f.endsWith('.csv'))
  // The messages shown while the files are read (each stays a few seconds): kept for the checks below.
  await p.eval(`(() => { window.__messages = []; new MutationObserver(() => { for (const a of document.querySelectorAll('.MuiAlert-message')) if (!window.__messages.includes(a.innerText)) window.__messages.push(a.innerText) }).observe(document.body, { childList: true, subtree: true, characterData: true }) })()`)
  await p.clickText(`Import ${files.length} files`, { selector: '[role=dialog] button' })
  // Without text recognition on the machine (the macOS/Linux window tested on Windows), a scanned statement cannot be
  // read: it says what to install, and every other file is read.
  const ocr = (await p.engine('app.init')).ocrAvailable
  const scans = ocr ? [] : files.filter((f) => f.includes('(scan)'))
  await p.waitFor(`document.querySelectorAll('[data-draft]').length === ${files.length - scans.length}`, 180000, 'all drafts')
  if (scans.length > 0) {
    await p.waitFor(`window.__messages.some((m) => m.includes(${JSON.stringify(scans[0])}) && m.includes('text recognition is not installed'))`, 20000, 'scan refused').catch(() => false)
    check('a scanned statement with no text recognition says what to install', await p.eval(`window.__messages.some((m) => m.includes(${JSON.stringify(scans[0])}) && m.includes('text recognition is not installed'))`), await p.eval(`window.__messages.join(' | ')`))
    console.log(`SKIP  reading ${scans.join(', ')} — no text recognition on this machine`)
  }
  await sleep(500)
  // A file is read once its bank is chosen: before, no balance check, no status, only the files whose table is not
  // recognised ask for their columns.
  check('nothing read before the bank is chosen', await p.eval(`[...document.querySelectorAll('[data-draft]')].every(d => !/Balanced to the cent|Balance check|Off by|No balances to check/.test(d.innerText))`))
  check('unusual layout asks for columns', await p.eval(`document.querySelector('[data-draft^="Mutuelle"]').innerText.includes('was not recognised')`))
  /** The balance status of each file, taken once its bank is chosen (in the saving loop below). */
  const statuses = []
  const statusOf = (sel) => p.eval(`(() => { const d = document.querySelector('${sel}'); return [d.dataset.draft, d.innerText.includes('Balanced to the cent') ? 'verified' : d.innerText.includes('No operations found') ? 'none' : d.innerText.includes('Off by') ? 'mismatch' : d.innerText.includes('Already imported') ? 'already' : 'other'] })()`)
  check('PDF statements come with a warning', await p.eval(`document.querySelector('[data-pdf-warning]') !== null`))
  await p.scrollMain(0)
  await shot(p, '03-import-drafts.png')

  // ---- column template for the unusual bank: drag the borders on the real page picture
  await p.eval(`document.querySelector('[data-draft^="Mutuelle"]').scrollIntoView({ block: 'center' })`)
  await p.clickText('Set columns', { selector: '[data-draft^="Mutuelle"] button' })
  await p.waitFor(`document.querySelector('[role=dialog] img') !== null`, 20000, 'page picture')
  await sleep(600)
  await shot(p, '04-template-editor-before.png')
  const geo = await p.eval(`(() => { const box = document.querySelector('[role=dialog] img').getBoundingClientRect(); return { left: box.left, width: box.width, top: box.top } })()`)
  const handles = await p.eval(`[...document.querySelectorAll('[role=dialog] [style], [role=dialog] div')].filter(e => getComputedStyle(e).cursor === 'col-resize').map(e => { const r = e.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + 120 } })`)
  const targets = [0.05, 0.10, 0.55, 0.73, 0.86, 0.965]
  for (let i = 0; i < handles.length && i < targets.length; i++) {
    const h = handles[i]
    const x = geo.left + geo.width * targets[i]
    await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: h.x, y: h.y })
    await p.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: h.x, y: h.y, button: 'left', clickCount: 1 })
    for (let s = 1; s <= 6; s++) await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: h.x + ((x - h.x) * s) / 6, y: h.y, button: 'left', buttons: 1 })
    await p.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y: h.y, button: 'left', clickCount: 1 })
    await sleep(120)
  }
  // roles: Ignore | Label | Money out | Money in | Date
  const setRole = async (index, label) => {
    await p.mouseClick('[role=dialog] [role=combobox]', index)
    await p.clickText(label, { selector: '[role=option]' })
    await sleep(200)
  }
  await setRole(0, 'Ignore')
  await setRole(4, 'Date')
  await p.waitFor(`document.querySelector('[role=dialog]').innerText.includes('Balanced to the cent')`, 15000, 'template balances')
  await shot(p, '05-template-editor-after.png')
  check('hand-set columns balance the unusual statement', true)
  // The Mutuelle statement prints no currency: its SEPA operations make it a euro statement, shown in euros.
  const editorAmounts = await p.eval(`[...document.querySelectorAll('[role=dialog] td.MuiTableCell-alignRight')].map(e => e.innerText)`)
  check('column editor: amounts of the euro statement in euros', editorAmounts.length > 0 && editorAmounts.every((t) => t.includes('€') && !t.includes('$')), editorAmounts.slice(0, 3).join(' '))
  await p.clickText('Save', { selector: '[role=dialog] button' })
  await p.waitFor(`!document.querySelector('[role=dialog] img')`, 10000, 'editor closed')
  await sleep(500)
  check('draft re-read with the template', await p.eval(`(() => { const d = document.querySelector('[data-draft^="Mutuelle"]'); return !d.innerText.includes('was not recognised') && d.innerText.includes('Choose the account') })()`))

  // ---- save every draft (PDFs first; the CSVs of the same BNC accounts must then bring nothing new)
  // Nothing may be pre-selected: every file waits for its account, and cannot be saved before.
  check('no account chosen for any file', await p.eval(`[...document.querySelectorAll('[data-draft]')].every(c => c.innerText.includes('Choose the account'))`))
  check('import impossible before choosing', await p.eval(`[...document.querySelectorAll('[data-draft]')].every(c => [...c.querySelectorAll('button')].filter(b => b.innerText.startsWith('Import')).every(b => b.disabled))`))
  // A file is read once its bank is known: no operation and no date format before.
  check('no operation shown before the bank is chosen', await p.eval(`[...document.querySelectorAll('[data-draft]')].every(c => !c.querySelector('table') && !c.querySelector('[data-date-format]'))`))
  // The account (and type) of each file, chosen in the interface as a person would.
  const groupOf = (f) => f.startsWith('BNC ') ? 'BNC ' + (f.includes('highinvacc') ? 'savings' : f.includes('debit') ? 'chequing' : 'card') : f.replace(/ \d{4}-\d{2}.*$/, '')
  const typeOf = (f) => f.includes('highinvacc') ? 'Savings account' : f.startsWith('Fabrikam') || (f.startsWith('BNC ') && !f.includes('debit')) ? 'Credit card' : 'Chequing account'
  const created = new Map()
  let csvNothingNew = 0
  for (let guard = 0; guard < 60; guard++) {
    const left = await p.eval(`document.querySelectorAll('[data-draft]').length`)
    if (left === 0) break
    const file = await p.eval(`(() => { const cards = [...document.querySelectorAll('[data-draft]')]; return (cards.find(c => !c.dataset.draft.endsWith('.csv')) ?? cards[0]).dataset.draft })()`)
    const sel = `[data-draft="${file.replace(/"/g, '\\"').replace(/'/g, "\\'")}"]`
    const group = groupOf(file)
    await p.mouseClick(`${sel} [role=combobox]`, 0)
    if (created.has(group)) {
      await p.clickText(created.get(group), { selector: '[role=option]', partial: true })
    } else {
      await p.clickText('New account', { selector: '[role=option]' })
      await sleep(300)
      // The bank the statement names is proposed; any other goes in as a new bank.
      if (group.startsWith('BNC ')) check('National Bank of Canada proposed for ' + group, await p.eval(`document.querySelector('${sel}').innerText.includes('National Bank of Canada')`))
      if (await p.eval(`document.querySelector('${sel}').innerText.includes('Choose the bank')`)) {
        await p.mouseClick(`${sel} [role=combobox]`, 1)
        await p.clickText('New bank', { selector: '[role=option]' })
        await sleep(300)
        const empty = await p.eval(`(() => { const i = [...document.querySelectorAll('${sel} input')].find(x => x.closest('.MuiFormControl-root')?.innerText.includes('Bank name')); i.focus(); return i.value === '' })()`)
        if (empty) await p.send('Input.insertText', { text: group + ' Bank' })
        await sleep(200)
      }
      await p.mouseClick(`${sel} [role=combobox]`, 2)
      await p.clickText(typeOf(file), { selector: '[role=option]' })
    }
    await p.waitFor(`[...document.querySelector('${sel}').querySelectorAll('button')].some(b => b.innerText.startsWith('Import') && !b.disabled) || document.querySelector('${sel}').innerText.includes('already imported')`, 20000, 'chosen ' + file)
    await sleep(300)
    statuses.push(await statusOf(sel))
    if (file.startsWith('Mutuelle')) {
      const cardText = await p.eval(`document.querySelector('${sel}').innerText`)
      check('import card: balances and amounts of the euro statement in euros', cardText.includes('€') && !cardText.includes('$'))
      await p.eval(`document.querySelector('${sel}').scrollIntoView({ block: 'start' })`)
      await sleep(300)
      await shot(p, '05b-import-card-euros.png')
    }
    // Read with the bank's date format, shown beside the bank: the National Bank of Canada's is the Canadian one.
    if (group === 'BNC chequing' && !created.has(group)) {
      const shown = await p.eval(`(() => { const c = document.querySelector('${sel}'); return [c.querySelector('[data-date-format]')?.value, !!c.querySelector('table'), !!c.querySelector('[data-date-format-mismatch]')] })()`)
      check('file read with its bank\'s date format', shown[0] === 'yearMonthDay' && shown[1] && !shown[2], JSON.stringify(shown))
    }
    const before = (await p.engine('accounts.list')).map((a) => a.id)
    const pick = await p.eval(`(() => {
      const card = document.querySelector('${sel}')
      const buttons = [...card.querySelectorAll('button')]
      const imp = buttons.find(b => b.innerText.startsWith('Import') && !b.disabled)
      if (imp) { imp.click(); return 'import' }
      buttons.find(b => b.innerText === 'Discard' || b.innerText === 'Close').click()
      return card.dataset.draft.endsWith('.csv') && card.innerText.includes('already imported') ? 'nothing-new' : 'discard'
    })()`)
    if (pick === 'nothing-new') csvNothingNew++
    await p.waitFor(`document.querySelectorAll('[data-draft]').length < ${left}`, 20000, 'draft saved')
    if (!created.has(group)) {
      const added = (await p.engine('accounts.list')).find((a) => !before.includes(a.id))
      if (added) created.set(group, added.name)
    }
  }
  check('BNC CSVs after their PDFs bring nothing new', csvNothingNew === 3, `${csvNothingNew}/3`)
  writeFileSync(join(out, 'drafts.json'), JSON.stringify(statuses, null, 1))
  // Read at its bank, every statement balances to the cent, but the card histories (PDF and CSV), which print no
  // balance to check against.
  const verified = statuses.filter((s) => s[1] === 'verified').length
  check('statements read and balanced to the cent', verified === files.length - scans.length - 2, `${verified}/${files.length - scans.length}: ${JSON.stringify(statuses.filter((s) => s[1] !== 'verified'))}`)
  check('BNC card exports read without balances', statuses.filter((s) => s[0].startsWith('BNC 2025-10-01-120000') && s[1] === 'other').length === 2)
  await sleep(600)
  const accounts = await p.engine('accounts.list')
  check('accounts created once per account', accounts.length === 9, accounts.map((a) => a.name).join(', '))
  check('BNC savings account recognised', accounts.some((a) => a.kind === 'savings' && a.name.startsWith('National Bank of Canada')))
  const banks = await p.engine('banks.list')
  check('every imported account has its bank', accounts.every((a) => a.bankId !== null), banks.map((b) => b.name).join(', '))
  await p.scrollMain(0)
  await shot(p, '06-import-history.png')

  // ---- the file's currency is its account's: changing it in the import table changes it everywhere, and flags the bank
  const nbc = banks.find((b) => b.name === 'National Bank of Canada')
  const row = await p.eval(`[...document.querySelectorAll('tbody tr')].findIndex(r => r.innerText.includes('National Bank of Canada') && r.innerText.includes('CAD'))`)
  await p.eval(`[...document.querySelectorAll('tbody tr')][${row}].scrollIntoView({ block: 'center' })`)
  const cur = await p.eval(`(() => { const rows = [...document.querySelectorAll('tbody tr')]; const all = [...document.querySelectorAll('[role=combobox]')]; return all.indexOf([...rows[${row}].querySelectorAll('[role=combobox]')][1]) })()`)
  await p.mouseClick('[role=combobox]', cur)
  await p.clickText('USD', { selector: '[role=option]' })
  await p.waitFor(`document.querySelectorAll('[data-currency-mismatch]').length > 0`, 10000, 'mismatch warning')
  await sleep(400)
  await shot(p, '06b-import-currency-mismatch.png')
  const usd = (await p.engine('accounts.list')).filter((a) => a.currency === 'USD')
  check('currency changed from the import table', usd.length === 1 && usd[0].bankId === nbc.id, usd.map((a) => a.name).join(', '))
  const flagged = await p.eval(`[...document.querySelectorAll('tbody tr')].filter(r => r.querySelector('[data-currency-mismatch]')).every(r => r.innerText.includes('National Bank of Canada'))`)
  check('currency mismatch shown on every file of that bank only', flagged)
  await p.engine('accounts.update', { id: usd[0].id, name: usd[0].name, currency: 'CAD' })

  // ---- rows reordered by dragging their handle (statements table), order kept
  const order = async () => p.eval(`[...document.querySelector('tbody').querySelectorAll('tr')].map(r => r.children[1].innerText)`)
  await p.clickText('Statements', { selector: 'nav button' })
  await p.clickText('Import', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('tbody tr') !== null`, 15000, 'statements list')
  await sleep(300)
  const o1 = await order()
  // Each drag event in its own task, as a real drag delivers them.
  const rowsJs = `[...document.querySelector('tbody').querySelectorAll('tr')]`
  await p.eval(`window.__dt = new DataTransfer(); ${rowsJs}[0].querySelector('[draggable=true]').dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: window.__dt }))`)
  await sleep(150)
  await p.eval(`${rowsJs}[2].dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: window.__dt }))`)
  await sleep(150)
  await p.eval(`${rowsJs}[2].dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: window.__dt }))`)
  await sleep(800)
  const o2 = await order()
  check('row moved by dragging', o2[2] === o1[0] && o2[0] === o1[1], `${o1.slice(0, 3).join(' | ')} -> ${o2.slice(0, 3).join(' | ')}`)
  check('row order saved', JSON.parse((await p.engine('app.init')).settings['order.statements'] ?? '[]').length > 0)

  // ---- periods (the sketch)
  await p.clickText('Statements', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('section h2') !== null`, 20000, 'period title')
  await sleep(1200)
  await shot(p, '07-periods-top.png')
  const title = await p.eval(`document.querySelector('section h2').innerText`)
  check('month title: the month and its year alone', /^[A-Z][a-z]+ \d{4}$/.test(title), title)
  const days = await p.eval(`document.querySelector('section [data-days]')?.innerText`)
  const shownMonth = await p.eval(`document.querySelector('section[data-period]').dataset.period`)
  const monthEnd = new Date(Number(title.slice(-4)), ['January','February','March','April','May','June','July','August','September','October','November','December'].indexOf(title.split(' ')[0]) + 1, 0).getDate()
  const shownReport = await p.engine('periods.report', { from: shownMonth, to: shownMonth.slice(0, 8) + String(monthEnd).padStart(2, '0'), granularity: 'month' })
  check('days of the month shown, then its business days', days === `${monthEnd} days · ${shownReport.businessDays} business days` && shownReport.businessDays > 15 && shownReport.businessDays <= 23, days)
  await p.eval(`(() => { const s = document.querySelector('section'); const t = [...s.querySelectorAll('p, span')].find(e => e.innerText === 'Expenses'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 160 }) })()`)
  await sleep(800)
  await shot(p, '08-periods-totals.png')
  await p.clickText('Analytics', { selector: 'nav button' })
  await sleep(1200)
  await p.eval(`(() => { const s = document.querySelector('section'); const t = s.querySelector('[data-pie]'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 160 }) })()`)
  await sleep(800)
  await shot(p, '08b-periods-charts.png')
  await p.eval(`document.querySelector('main').scrollBy({ top: 420 })`)
  await sleep(600)
  await shot(p, '09-periods-daily-chart.png')
  await p.clickText('Statements', { selector: 'nav button' })
  await sleep(800)

  // Periods start folded to their summary in Statements: Earnings, Expenses, Balance and Expenses / earnings, no table.
  const augSec = 'section[data-period="2025-08-01"]'
  await p.eval(`document.querySelector('${augSec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`!!document.querySelector('${augSec} [data-part-block="all"] [data-totals]')`, 30000, 'August summary')
  const folded = await p.eval(`(() => { const s = document.querySelector('${augSec}'); return { open: s.hasAttribute('data-open'), tables: s.querySelectorAll('table').length, banks: s.querySelectorAll('[data-part-block]:not([data-part-block="all"])').length, stats: [...s.querySelectorAll('[data-part-block="all"] [data-stat]')].map(x => x.dataset.stat) } })()`)
  check('a period starts folded to the summary of every bank together, no table', !folded.open && folded.tables === 0 && folded.banks === 0
    && ['Earnings', 'Expenses', 'Balance', 'Expenses / earnings'].every((x) => folded.stats.includes(x)), JSON.stringify(folded))
  await shot(p, '09a-period-folded.png')
  // transfers, card payments and investments: in their own folded table
  await openPeriod(p, '2025-08-01')
  const mainHasMoves = await p.eval(`[...document.querySelector('${augSec} [data-section="main"] table').querySelectorAll('tbody tr')].some(r => /Credit card payment|Transfer between accounts/.test(r.innerText))`)
  check('transfers kept out of the main table, folded apart', !mainHasMoves && (await p.eval(`(() => { const m = document.querySelector('${augSec} [data-section="movements"]'); return !!m && !m.hasAttribute('data-open') && !m.querySelector('table') })()`)))
  await p.click(`${augSec} [data-section="movements"] [data-fold]`)
  await p.waitFor(`!!document.querySelector('${augSec} [data-movements] tbody tr')`, 10000, 'transfers table open')
  await p.eval(`document.querySelector('${augSec} [data-section="movements"]').scrollIntoView({ block: 'center' })`)
  await sleep(400)
  await shot(p, '09b-transfers-folded-table.png')
  const newest = await p.eval(`(() => { const d = [...document.querySelector('section[data-period="2025-08-01"] [data-section="main"] table').querySelectorAll('tbody tr td:first-child')].map(x => x.innerText); return d.length > 1 && new Date(d[0]) >= new Date(d[d.length - 1]) })()`)
  check('newest first by default', newest)
  // The transfers table has its own filters too: dates, categories and descriptions.
  const mv = 'section[data-period="2025-08-01"] [data-movements]'
  const mvRows = () => p.eval(`[...document.querySelectorAll('${mv} tbody tr')].map(r => [...r.children].map(c => c.innerText))`)
  const mvAll = await mvRows()
  const mvHeaders = await p.eval(`[...document.querySelectorAll('${mv} th button')].map(b => b.innerText.trim())`)
  await p.eval(`[...document.querySelectorAll('${mv} th button')].find(b => b.innerText.trim() === 'Category').click()`)
  await p.waitFor(`document.querySelector('[role=menu]') !== null`, 5000, 'transfers category filter')
  await sleep(250)
  await shot(p, '09b2-transfers-category-filter.png')
  const mvCats = await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].map(l => l.dataset.choice).filter(k => k !== 'all' && k !== 'none')`)
  await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].find(l => l.dataset.choice === 'none').click()`)
  await sleep(250)
  await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].find(l => l.dataset.choice === ${JSON.stringify(mvCats[0])}).click()`)
  await sleep(250)
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await sleep(400)
  const mvOne = await mvRows()
  check('transfers table: date, category, description, tag and amount filters; one category shown', mvHeaders.slice(0, 4).join() === 'Date,Category,Description,Tags' && (mvHeaders[4] ?? '').startsWith('Amount (') && mvCats.length > 1
    && mvOne.length > 0 && mvOne.length < mvAll.length && mvOne.every((r) => r[1] === mvOne[0][1]), `${mvOne.length}/${mvAll.length} rows of ${mvOne[0]?.[1]}`)
  await p.eval(`[...document.querySelectorAll('${mv} th button')].find(b => b.innerText.trim() === 'Category').click()`)
  await p.waitFor(`document.querySelector('[role=menu]') !== null`, 5000, 'transfers category filter')
  await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].find(l => l.dataset.choice === 'all').click()`)
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await sleep(400)
  check('transfers table: every category again', (await mvRows()).length === mvAll.length)

  // Every period fits the window, however many months there are (periods not drawn yet included).
  await p.scrollMain(0)
  check('periods never wider than the window', await p.eval(`(() => { const m = document.querySelector('main'); return m.scrollWidth <= m.clientWidth && [...document.querySelectorAll('section[data-period]')].every(s => s.getBoundingClientRect().right <= m.getBoundingClientRect().right) })()`))
  // Table headers stay under the bar of tabs while the table scrolls (the latest period opened, as a person would).
  const topFrom = await p.eval(`document.querySelector('section[data-period]').dataset.period`)
  await openPeriod(p, topFrom)
  await p.eval(`(() => { const t = document.querySelector('section tbody tr:nth-child(12)'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 200 }) })()`)
  await sleep(300)
  const sticky = await p.eval(`(() => { const th = document.querySelector('section thead th').getBoundingClientRect(); const head = document.querySelector('section thead').closest('[data-part-block]').querySelector('[data-part-head]').getBoundingClientRect(); const bar = document.querySelector('main [class*=MuiToggleButtonGroup]').parentElement.getBoundingClientRect(); return [Math.round(th.top), Math.round(head.bottom), Math.round(head.top), Math.round(bar.bottom)] })()`)
  await shot(p, '09c-sticky-header.png')
  check('table headers stay visible while scrolling, under the bank’s name, itself under the bar', Math.abs(sticky[0] - sticky[1]) <= 2 && Math.abs(sticky[2] - sticky[3]) <= 2, sticky.join(' / '))

  // Second layer under Month / Year / All time / Custom: Statements or Analytics; the totals under both.
  await p.scrollMain(0)
  const sec = 'section[data-period="2025-08-01"]'
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${sec} [data-totals]') !== null`, 20000, 'period loaded')
  await sleep(500)
  check('periods open on their statements, without charts', await p.eval(`document.querySelector('${sec} table') !== null && document.querySelector('${sec} [data-pie]') === null && document.querySelector('${sec} [data-flowchart]') === null`))
  check('each bank starts with its totals, above its tables', await p.eval(`(() => { const b = [...document.querySelectorAll('${sec} [data-part-block]')].find(b => b.querySelector('table')); const t = b.querySelector('[data-totals]'); return !!t && t.getBoundingClientRect().bottom <= b.querySelector('table').getBoundingClientRect().top && !!b.querySelector('[data-stat="Expenses / earnings"]') })()`))
  // The recurring payments come a step after the bank's totals (a period is drawn a piece at a time).
  await p.waitFor(`document.querySelector('${sec} [data-stat="Recurring payments, rent excluded"]') !== null`, 10000, 'recurring payments drawn')
  const ratio = await p.eval(`(() => { const s = document.querySelector('${sec}'); const bg = (l) => getComputedStyle(s.querySelector('[data-stat="' + l + '"]')).backgroundColor; const r = s.querySelector('[data-stat="Expenses / earnings"]'); return { text: r.innerText, rec: s.querySelector('[data-stat="Recurring payments, rent excluded"]').innerText, ratio: bg('Expenses / earnings'), exp: bg('Expenses'), earn: bg('Earnings') } })()`)
  const share = parseInt(ratio.text.match(/(\d+)%/)?.[1] ?? '0', 10)
  check('expenses / earnings red above 100%, green below, without the spent-of-earned line', !ratio.text.includes('spent of') && (share > 100 ? ratio.ratio === ratio.exp : ratio.ratio === ratio.earn), `${share}% ${ratio.ratio}`)
  check('recurring payments card without the line under the amount', ratio.rec.trim().split('\n').filter(Boolean).length === 2, ratio.rec.replace(/\n/g, ' | '))
  await p.clickText('Analytics', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('${sec} [data-category-lines]') !== null`, 20000, 'charts view')
  await sleep(800)
  check('analytics, General: pies, bars and category lines, no tables, no summaries, no money moved', await p.eval(`(() => { const s = document.querySelector('${sec}'); return s.querySelectorAll('[data-pie]').length >= 2 && !!s.querySelector('[data-bars]') && !!s.querySelector('[data-category-lines]') && !s.querySelector('table:not([data-conversions])') && !s.querySelector('[data-totals]') && !s.querySelector('[data-flowchart]') && !s.querySelector('[data-transfer-timeline]') })()`))
  check('Analytics in two parts, General first', await p.eval(`[...document.querySelectorAll('[data-analytics-view] button')].map(b => b.innerText + (b.getAttribute('aria-pressed') === 'true' ? '*' : '')).join()`) === 'General*,Transfers')
  check('the two parts under the kinds of period, on a line of their own', await p.eval(`(() => { const a = document.querySelector('main button[value="month"]').closest('[role=group]').getBoundingClientRect(); const g = document.querySelector('[data-analytics-view]').getBoundingClientRect(); return g.top >= a.bottom && Math.abs(g.left - a.left) <= 0.5 })()`))
  // Transfers: the money moved over time, then in total; none of the charts of General.
  await analyticsPart(p, 'Transfers')
  await p.waitFor(`document.querySelector('${sec} [data-flowchart]') !== null && document.querySelector('${sec} [data-transfer-timeline] svg') !== null`, 20000, 'transfers view')
  check('analytics, Transfers: the timeline then money moved, no pies, the export at the top of the period', await p.eval(`(() => { const s = document.querySelector('${sec}'); const t = s.querySelector('[data-transfer-timeline]'); const f = s.querySelector('[data-flowchart]'); return !s.querySelector('[data-pie]') && !s.querySelector('[data-bars]') && !!(t.compareDocumentPosition(f) & Node.DOCUMENT_POSITION_FOLLOWING) && !!s.querySelector('[data-export]') })()`))
  await p.eval(`(() => { const f = document.querySelector('${sec} [data-transfer-timeline]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await sleep(700)
  await shot(p, '09c-transfers-timeline.png')
  await timelineChecks(p, sec, (await p.engine('periods.report', { from: '2025-08-01', to: '2025-08-31', granularity: 'month' })).global.timeline, 'timeline of August')
  await shot(p, '09c2-transfers-timeline-hover.png')
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 })
  // Money moved: the outside box has the size of the accounts, no amounts written in it, every arrow its own points.
  await p.eval(`(() => { const f = document.querySelector('${sec} [data-flowchart]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await sleep(600)
  await shot(p, '09d-money-moved.png')
  const flow = await p.eval(`(() => { const f = document.querySelector('${sec} [data-flowchart]'); const o = f.querySelector('[data-outside-node] rect'); const a = f.querySelector('[data-account-node] rect'); return { same: !!o && !!a && o.getAttribute('width') === a.getAttribute('width') && o.getAttribute('height') === a.getAttribute('height'), text: [...f.querySelectorAll('[data-outside-node] text')].map(t => t.textContent).join(' | '), blob: !!f.querySelector('ellipse') } })()`)
  check('outside box the size of the accounts, no amounts inside', flow.same && !flow.blob && flow.text === 'Outside the app | Other accounts & people', flow.text)
  // The diagram is not a bank's: one card of its own per period, after the banks, every account inside the box of its bank.
  const own = await p.eval(`(() => { const s = document.querySelector('${sec}'); const f = s.querySelector('[data-flow-card]'); const inBox = (a, b) => a.left >= b.left - 0.5 && a.right <= b.right + 0.5 && a.top >= b.top - 0.5 && a.bottom <= b.bottom + 0.5; const banks = [...f.querySelectorAll('[data-bank-node] rect')].map(r => r.getBoundingClientRect()); const accounts = [...f.querySelectorAll('[data-account-node] rect')].map(r => r.getBoundingClientRect()); const parts = [...s.querySelectorAll('[data-part-block]')]; return { cards: s.querySelectorAll('[data-flowchart]').length, inPart: !!s.querySelector('[data-part-block] [data-flowchart]'), after: parts.every(b => b.compareDocumentPosition(f) & Node.DOCUMENT_POSITION_FOLLOWING), banks: [...f.querySelectorAll('[data-bank-node]')].map(b => b.dataset.bankNode), inside: accounts.length > 0 && accounts.every(a => banks.some(b => inBox(a, b))) } })()`)
  check('money moved: one card of its own after the banks, each account inside the box of its bank', own.cards === 1 && !own.inPart && own.after && own.banks.length > 0 && own.inside, JSON.stringify(own))
  await analyticsPart(p, 'General')
  await p.waitFor(`document.querySelector('${sec} [data-bars]') !== null`, 20000, 'general view again')
  await settle(p, sec)
  // Income and expenses per day: the amounts of the day on hover (the bars grow in when drawn: measured once still).
  await bringIntoView(p, `${sec} [data-bars]`)
  // (While the view changes the charts of a bank are drawn again: for a moment there are none to measure.)
  const tallest = () => p.eval(`(() => { const r = [...(document.querySelector('${sec} [data-bars]')?.querySelectorAll('rect.MuiBarChart-element') ?? [])].filter(r => r.getBoundingClientRect().height > 2).sort((a, b) => b.getBoundingClientRect().height - a.getBoundingClientRect().height)[0]?.getBoundingClientRect(); return r ? { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2), h: Math.round(r.height) } : null })()`)
  // Measured when still, then pointed at; again if the page moved meanwhile (a period being drawn moves what is under it).
  let bar = null
  for (let attempt = 0; attempt < 12; attempt++) {
    await bringIntoView(p, `${sec} [data-bars]`)
    const still = async () => { for (let i = 0, last = null; i < 40; i++) { bar = await tallest(); if (bar && last && bar.h === last.h && bar.y === last.y) break; last = bar; await sleep(150) } }
    await still()
    // None drawn yet (the charts of a bank come in after its pies): a little further down, inside August.
    if (!bar) { await p.eval(`(() => { const s = document.querySelector('${sec}'), m = document.querySelector('main'); if (s && s.getBoundingClientRect().bottom > innerHeight + 400) m.scrollBy({ top: 400 }); else s?.scrollIntoView({ block: 'start' }) })()`); await sleep(800); continue }
    // The bar itself in the middle of the screen (a chart taller than what is left under the bar can end below it).
    if (bar.y < 200 || bar.y > 820) { await p.eval(`document.querySelector('main').scrollBy({ top: ${bar.y - 460} })`); await sleep(600); await still(); if (!bar || bar.y < 120 || bar.y > 900) continue }
    for (const [dx, dy] of [[-2, -2], [0, 0], [1, 1]]) { await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: bar.x + dx, y: bar.y + dy }); await sleep(150) }
    if (await p.waitFor(`!!document.querySelector('.MuiChartsTooltip-root')`, 2000, 'bar tooltip').then(() => true, () => false)) break
    await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 })
  }
  const tip = await p.eval(`(() => { const t = document.querySelector('.MuiChartsTooltip-root'); if (!t) return null; const r = t.getBoundingClientRect(); return { text: t.innerText, x: r.left, y: r.top } })()`)
  await shot(p, '09e-day-hover.png')
  check('income and expenses of that day shown on hover, next to the pointer', !!tip && /Day \d+/.test(tip.text) && /Expenses/.test(tip.text) && /\$[\d,]+\.\d\d/.test(tip.text) && !!bar && Math.abs(tip.y - bar.y) < 120 && Math.abs(tip.x - bar.x) < 200, JSON.stringify(tip))
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 })
  // Expenses per category per day: under "Categories", Selected total (the categories ticked, added together), Total
  // (every expense, never added to the selected total), then every category with expenses in the period (no
  // drop-down), each ticked on or off; the selected total and the five largest at first, the total not.
  // August is in CAD at every bank: all banks together hold every operation of the month in CAD.
  const augAll = `${sec} [data-part-block="all"]`
  // Every bank together comes after the banks, drawn as it nears the screen.
  for (let i = 0; i < 40 && !(await p.eval(`!!document.querySelector('${augAll} [data-category-lines]')`)); i++) {
    await p.eval(`(() => { const b = [...document.querySelectorAll('${sec} [data-part-block]')].at(-1) ?? document.querySelector('${sec}'); b?.scrollIntoView({ block: 'end' }) })()`)
    await sleep(500)
  }
  await bringIntoView(p, `${augAll} [data-category-lines]`)
  await shot(p, '09f-category-lines.png')
  const lineCount = () => p.eval(`document.querySelectorAll('${augAll} [data-category-lines] path.MuiLineChart-line').length`)
  const augSummary = await p.engine('periods.summary', { from: '2025-08-01', to: '2025-08-31', granularity: 'month', currency: 'CAD' })
  const choiceKeys = await p.eval(`[...document.querySelectorAll('${augAll} [data-category-choices] li[data-choice]')].map(l => l.dataset.choice)`)
  const lineCats = choiceKeys.filter((k) => !k.startsWith('__'))
  const linesText = await p.eval(`document.querySelector('${augAll} [data-category-lines]').innerText`)
  check('categories listed under a Categories title, after Selected total and Total: those with expenses in the period, no drop-down',
    linesText.includes('Categories') && !/Top \d|All \(|chosen/.test(linesText) && !(await p.eval(`!!document.querySelector('${augAll} [data-category-lines] [role=combobox]')`))
      && choiceKeys[0] === '__selected' && choiceKeys[1] === '__total' && lineCats.join() === augSummary.byTopCategory.map((c) => c.key).join(), `${lineCats.length} categories`)
  const isTicked = (key) => p.eval(`document.querySelector('${augAll} [data-category-choices] li[data-choice="${key}"] input').checked`)
  check('the selected total and the five largest categories ticked at first, the total not', (await lineCount()) === 1 + Math.min(5, lineCats.length) && !(await isTicked('__total')), `${await lineCount()} lines`)
  const tickLine = async (key) => { await p.eval(`document.querySelector('${augAll} [data-category-choices] li[data-choice="${key}"] input').click()`); await sleep(350) }
  const scaleTop = () => p.eval(`Math.max(...[...document.querySelectorAll('${augAll} [data-category-lines] .MuiChartsAxis-directionY text')].map(t => { const v = parseFloat(t.textContent.replace(/[^0-9.]/g, '')); return /K/.test(t.textContent) ? v * 1000 : v }).filter(v => !isNaN(v)))`)
  const withoutTotal = await scaleTop()
  await tickLine('__total')
  const withTotalLines = await lineCount()
  const withTotal = await scaleTop()
  await shot(p, '09g-category-lines-with-total.png')
  check('the total ticked: one more line, the scale follows the lines shown', withTotalLines === 2 + Math.min(5, lineCats.length) && withTotal >= withoutTotal, `${withTotalLines} lines, scale ${withoutTotal} -> ${withTotal}`)
  const sums = () => p.eval(`(() => { const t = (k) => document.querySelector('${augAll} li[data-choice="' + k + '"]').innerText.replace(/\\s+/g, ' '); return { selected: t('__selected'), total: t('__total') } })()`)
  const sums1 = await sums()
  await tickLine(lineCats[0])
  const sums2 = await sums()
  await tickLine(lineCats[0])
  check('the selected total changes with the ticks, the total never', sums1.selected !== sums2.selected && sums1.total === sums2.total, `${JSON.stringify(sums1)} -> ${JSON.stringify(sums2)}`)
  await tickLine('__total')
  for (const k of lineCats.slice(5)) await tickLine(k)
  const allLines = await lineCount()
  for (const k of lineCats) await tickLine(k)
  const noCategory = await lineCount()
  await tickLine('__selected')
  const noLines = await lineCount()
  await tickLine(lineCats[Math.min(2, lineCats.length - 1)])
  const oneLine = await lineCount()
  const oneScale = await scaleTop()
  await tickLine('__selected')
  check('any number of categories, from none to all, each total on or off', allLines === 1 + lineCats.length && noCategory === 1 && noLines === 0 && oneLine === 1 && oneScale < withTotal && (await lineCount()) === 2,
    `all ${allLines}, no category ${noCategory}, none ${noLines}, one ${oneLine} (scale ${oneScale} under the total's ${withTotal})`)
  // The choices stay when the period goes far from the screen and comes back.
  await p.scrollMain(0)
  await p.eval(`document.querySelector('main').scrollBy({ top: 30000 })`)
  await sleep(1500)
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${augAll} [data-category-lines]') !== null`, 30000, 'period back')
  await sleep(800)
  check('chosen lines kept after scrolling away and back', (await lineCount()) === 2, `${await lineCount()} lines`)
  // Pies: shares with two decimals, so a small share never reads 0%.
  const shares = await p.eval(`[...document.querySelectorAll('${sec} [data-pie] li')].map(l => l.innerText.split('\\n').pop())`)
  check('pie shares with two decimals', shares.length > 0 && shares.every((x) => /^\d+\.\d\d%$/.test(x)) && !shares.includes('0.00%'), shares.slice(-3).join(' '))
  await p.clickText('Statements', { selector: 'nav button' })
  await sleep(600)
  check('the parts of Analytics only shown in Analytics', await p.eval(`document.querySelector('[data-analytics-view]') === null`))

  // date format menu
  await p.scrollMain(0)
  await p.waitFor(`document.querySelector('section th button') !== null`, 15000, 'first period loaded')
  await p.clickText('Date', { selector: 'section th button' })
  await shot(p, '10-date-menu.png')
  await p.clickText('YYYY-MM-DD', { selector: '[role=menuitem]', partial: true })
  await sleep(500)
  const firstDate = await p.eval(`document.querySelector('section tbody td').innerText`)
  check('date format switch', /^\d{4}-\d{2}-\d{2}$/.test(firstDate), firstDate)
  await p.clickText('Date', { selector: 'section th button' })
  await p.clickText('DD month YYYY', { selector: '[role=menuitem]', partial: true })
  await sleep(400)

  // category filter
  await p.clickText('Category', { selector: 'section th button' })
  await shot(p, '11-category-menu.png')
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await sleep(300)
  // Filters of a table: dates, categories and brands, each all, none, one or any; they stay when the period scrolls away.
  const rowsOf = () => p.eval(`[...document.querySelectorAll('${sec} [data-section="main"] table')[0].querySelectorAll('tbody tr')].map(r => [...r.children].map(c => c.innerText))`)
  const openFilter = async (label) => {
    await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
    await sleep(300)
    await p.eval(`[...document.querySelectorAll('${sec} [data-section="main"] table')[0].querySelectorAll('th button')].find(b => b.innerText.trim() === '${label}').click()`)
    await p.waitFor(`document.querySelector('[role=menu]') !== null`, 5000, label + ' menu')
    await sleep(250)
  }
  const pick = async (choice) => { await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].find(l => l.dataset.choice === ${JSON.stringify(choice)}).click()`); await sleep(250) }
  // The lines of a table come in a few at a time: the rows, once there are as many as expected (or after 8 s).
  const rowsWhen = async (n) => { let rows = await rowsOf(); for (let i = 0; i < 40 && rows.length !== n; i++) { await sleep(200); rows = await rowsOf() } return rows }
  const closeMenu = async () => { await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 }); await sleep(400) }
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${sec} [data-section="main"] table') !== null`, 20000, 'August table')
  await sleep(500)
  const allRows = await rowsOf()
  await openFilter('Category')
  await shot(p, '11b-category-filter.png')
  const catSpacing = await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].filter(li => li.querySelector('.tnum')).every(li => { const n = li.querySelector('.tnum'); const t = n.previousElementSibling.getBoundingClientRect(); return n.getBoundingClientRect().left - t.right >= 12 })`)
  check('category filter: names and counts apart', catSpacing)
  await pick('none')
  const noneRows = await rowsOf()
  const catKeys = await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].map(l => l.dataset.choice).filter(k => k !== 'all' && k !== 'none')`)
  await pick(catKeys[1])
  await closeMenu()
  const oneCat = await rowsOf()
  check('category filter: none, then one category', noneRows.length === 1 && noneRows[0][0].includes('No operations match') && oneCat.length > 0 && oneCat.every((r) => r[1] === oneCat[0][1]), `${oneCat.length} rows of ${oneCat[0]?.[1]}`)
  // Scroll far down (the period lets go of its table) and back: the filter is still there.
  await p.eval(`document.querySelector('main').scrollBy({ top: 30000 })`)
  await sleep(1500)
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${sec} [data-section="main"] table') !== null`, 20000, 'table back')
  await sleep(600)
  check('category filter kept after scrolling away and back', JSON.stringify(await rowsOf()) === JSON.stringify(oneCat))
  await openFilter('Category')
  await pick('all')
  await closeMenu()
  check('all categories again', (await rowsWhen(allRows.length)).length === allRows.length)
  await openFilter('Brand Name')
  await shot(p, '11c-brand-filter.png')
  await pick('none')
  const brand = allRows[0][2]
  await pick(brand)
  await closeMenu()
  const oneBrand = await rowsOf()
  check('brand filter: none, then one brand', oneBrand.length > 0 && oneBrand.every((r) => r[2] === brand), `${oneBrand.length} rows of ${brand}`)
  await openFilter('Brand Name')
  await pick('all')
  await closeMenu()
  await openFilter('Date')
  await shot(p, '11d-date-filter.png')
  const dateKeys = await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].map(l => l.dataset.choice).filter(k => /^\\d{4}-/.test(k))`)
  await pick('none')
  await pick(dateKeys[0])
  await pick(dateKeys[2])
  await closeMenu()
  const twoDays = await rowsOf()
  const dayNames = [...new Set(twoDays.map((r) => r[0]))]
  check('date filter: none, then two days', twoDays.length > 0 && dayNames.length === 2, dayNames.join(', '))
  await openFilter('Date')
  await pick('all')
  await closeMenu()
  check('every operation shown again', (await rowsWhen(allRows.length)).length === allRows.length)

  // correcting a category teaches the dictionary
  const before = await p.eval(`document.querySelector('section [data-section="main"] tbody tr td:nth-child(3)').innerText`)
  await p.click('section [data-section="main"] tbody tr td:nth-child(2) button', 0)
  await sleep(300)
  await p.eval(`[...document.querySelectorAll('.MuiListSubheader-root')].find(e => /income/i.test(e.innerText)).scrollIntoView({ block: 'start' })`)
  await sleep(300)
  await shot(p, '12a-category-picker.png')
  await p.clickText('Entertainment', { selector: '[role=menuitem]' })
  // The person is asked: this operation only, or every operation of the brand.
  await p.waitFor(`document.querySelector('[data-scope-dialog]') !== null`, 5000, 'scope question')
  await shot(p, '12b-scope-question.png')
  await p.clickText('All “', { selector: '[role=dialog] button', partial: true })
  await p.waitFor(`document.body.innerText.includes('Saved for')`, 10000, 'learned toast')
  await shot(p, '12-learned.png')
  const rules = (await p.engine('brands.list')).rules
  check('correction learned', rules.some((r) => r.categoryId === 'entertainment'), `${before} -> Entertainment`)

  // ---- categories: made, nested, coloured, and picked for an operation
  // Categories are only in Settings, under Definitions.
  check('categories not in the side bar', !(await p.eval(`[...document.querySelectorAll('nav button')].some(b => b.innerText.includes('Categories'))`)))
  // Settings > Information, after Definitions: the banks read by themselves, a table per country with its flag.
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('Information', { selector: 'button' })
  await p.waitFor(`document.querySelectorAll('[data-supported-bank]').length > 0`, 10000, 'supported banks')
  const supported = await p.eval(`({ tabs: [...document.querySelectorAll('main button[value]')].map(b => b.innerText).join(), tables: [...document.querySelectorAll('[data-country]')].map(t => ({ code: t.dataset.country, flag: !!t.closest('[data-supported-banks] > *').querySelector('svg'), head: [...t.querySelectorAll('thead th')].map(h => h.innerText).join('|'), banks: [...t.querySelectorAll('[data-supported-bank]')].map(r => r.dataset.supportedBank + (r.querySelector('[data-confirmed="true"]') ? '*' : '')) })) })`)
  check('Settings > Information after Definitions: the banks read by themselves, a table with its flag per country', supported.tabs === 'General,Bank Configuration,Definitions,Information'
    && supported.tables.map((t) => t.code).join() === 'CA,FR,GB' && supported.tables.every((t) => t.flag && t.head === 'Name|Considered|Confirmed|')
    && supported.tables[0].banks.join() === 'National Bank of Canada*,Scotiabank*' && supported.tables[1].banks.join() === 'BoursoBank,Caisse d\'Epargne,CIC,Fortuneo' && supported.tables[2].banks.join() === 'Revolut', JSON.stringify(supported))
  const flagSizes = await p.eval(`[...document.querySelectorAll('[data-supported-banks] > * svg')].filter(s => !s.closest('table')).map(s => { const r = s.getBoundingClientRect(); return r.width + 'x' + r.height })`)
  check('the flags of the countries all of one size', flagSizes.length === 3 && new Set(flagSizes).size === 1, flagSizes.join())
  await shot(p, '10z-information.png')
  const supportedFile = join(data, 'supported-banks.json')
  check('the list of Information is a JSON file of the data folder', existsSync(supportedFile) && JSON.parse(readFileSync(supportedFile, 'utf8')).map((c) => c.code).join() === 'CA,FR,GB')
  check('a pencil on every bank of Information', await p.eval(`[...document.querySelectorAll('[data-supported-bank]')].every(r => !!r.querySelector('[data-row-edit]'))`))
  await p.eval(`document.querySelector('[data-supported-bank="CIC"] [data-row-edit]').click()`)
  await p.waitFor(`!!document.querySelector('[data-supported-bank-dialog] [data-field="confirmed"]')`, 5000, 'Information pencil dialog')
  await sleep(300)
  await shot(p, '10z-information-edit.png')
  const dialogShown = await p.eval(`(() => { const d = document.querySelector('[data-supported-bank-dialog]'); return { title: d.querySelector('h2').innerText, name: d.querySelector('[data-field="name"]').value, considered: d.querySelector('[data-field="considered"]').checked, confirmed: d.querySelector('[data-field="confirmed"]').checked } })()`)
  check('the pencil of a bank: its name, Considered and Confirmed', dialogShown.title === 'Edit CIC' && dialogShown.name === 'CIC' && dialogShown.considered && !dialogShown.confirmed, JSON.stringify(dialogShown))
  await p.eval(`document.querySelector('[data-supported-bank-dialog] [data-field="confirmed"]').click()`)
  await p.clickText('Save', { selector: '[data-supported-bank-dialog] button' })
  await p.waitFor(`!document.querySelector('[data-supported-bank-dialog]')`, 5000, 'Information dialog closed')
  const cicFile = () => JSON.parse(readFileSync(supportedFile, 'utf8')).find((c) => c.code === 'FR').banks.find((b) => b.name === 'CIC')
  check('a bank confirmed with its pencil: shown and written to the file', await p.eval(`!!document.querySelector('[data-supported-bank="CIC"] [data-confirmed="true"]')`) && cicFile()?.confirmed === true, JSON.stringify(cicFile()))
  await p.engine('banks.supportedSave', { country: 'FR', name: 'CIC', bank: { name: 'CIC', considered: true, confirmed: false } })
  check('and back as it was', cicFile()?.confirmed === false)
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('Definitions', { selector: 'button' })
  await p.clickText('Categories', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-categories]') !== null`, 10000, 'categories in settings')
  const makeCategory = async (name, color, parentRow, kind = 'expense') => {
    if (parentRow) {
      await p.eval(`document.querySelector('[data-category="${parentRow}"] [aria-label="Add a subcategory"]').click()`)
    } else {
      await p.click(`[data-add-kind="${kind}"]`)
    }
    await p.waitFor(`document.querySelector('[data-category-dialog]') !== null`, 5000, 'category dialog')
    await p.send('Input.insertText', { text: name })
    if (color) await p.eval(`document.querySelector('[data-category-dialog] [data-color="${color}"]').click()`)
    await sleep(200)
    if (!parentRow) await shot(p, '12c-new-category.png')
    await p.clickText('Create', { selector: '[role=dialog] button' })
    await p.waitFor(`document.querySelector('[data-category="${name}"]') !== null`, 5000, 'category ' + name)
  }
  await makeCategory('Home projects', '#00897b')
  await makeCategory('Paint', null, 'Home projects')
  await makeCategory('Brushes', '#fb8c00', 'Paint')
  await makeCategory('To my sister', null, null, 'transfer')
  check('a category added under Transfers', (await p.engine('app.init')).categories.find((c) => c.name === 'To my sister')?.kind === 'transfer')
  await sleep(300)
  await p.eval(`document.querySelector('[data-category="Brushes"]').scrollIntoView({ block: 'center' })`)
  await shot(p, '12d-categories.png')
  const cats = (await p.engine('app.init')).categories
  const byName = (n) => cats.find((c) => c.name === n)
  check('category nested twice', byName('Brushes')?.parentId === byName('Paint')?.id && byName('Paint')?.parentId === byName('Home projects')?.id)
  check('category colours kept', byName('Home projects')?.color === '#00897b' && byName('Brushes')?.color === '#fb8c00')
  // edit an existing one: rename it
  await p.eval(`document.querySelector('[data-category="Paint"] [aria-label="Edit"]').click()`)
  await p.waitFor(`document.querySelector('[data-category-dialog]') !== null`, 5000, 'edit dialog')
  await p.eval(`document.querySelector('[data-category-dialog] input').select()`)
  await p.send('Input.insertText', { text: 'Paint & tools' })
  await p.clickText('Save', { selector: '[role=dialog] button' })
  await p.waitFor(`document.querySelector('[data-category="Paint & tools"]') !== null`, 5000, 'renamed category')
  check('category renamed', true)
  // the picker shows it nested and coloured
  await p.clickText('Statements', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('section [data-section="main"] tbody tr td:nth-child(2) button') !== null`, 20000, 'operations')
  await p.click('section [data-section="main"] tbody tr td:nth-child(2) button', 0)
  await sleep(300)
  await p.eval(`[...document.querySelectorAll('[role=menuitem]')].find(e => e.innerText === 'Brushes').scrollIntoView({ block: 'center' })`)
  await sleep(200)
  await shot(p, '12e-picker-nested.png')
  await p.clickText('Brushes', { selector: '[role=menuitem]' })
  await p.waitFor(`document.querySelector('[data-scope-dialog]') !== null`, 5000, 'scope question')
  await p.clickText('Only this one', { selector: '[role=dialog] button' })
  await p.waitFor(`[...document.querySelectorAll('section [data-section="main"] tbody tr td:nth-child(2)')].some(td => td.innerText === 'Brushes')`, 10000, 'subcategory set')
  await sleep(300)
  await shot(p, '12f-colored-pill.png')
  check('operation put in a subcategory', true)
  const path = await p.eval(`[...document.querySelectorAll('section [data-path]')].map(e => e.dataset.path).find(x => x.endsWith('Brushes')) ?? ''`)
  check('full path of a category on hover', path === 'Expenses › Home projects › Paint & tools › Brushes', path)

  // ---- a category above others (test data, deleted after): "Food (test)" over Groceries and Restaurants & cafés,
  // four levels nested under Restaurants. Charts show Food once with all of it (setting on, the default), or every
  // nested category (setting off); nothing is counted twice and the categories themselves stay.
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('Definitions', { selector: 'button' })
  await p.clickText('Categories', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-categories]') !== null`, 10000, 'categories again')
  await p.click('[data-add-kind="expense"]')
  await p.waitFor(`document.querySelector('[data-category-dialog]') !== null`, 5000, 'category dialog')
  await p.send('Input.insertText', { text: 'Food (test)' })
  await p.clickText('Create', { selector: '[role=dialog] button' })
  await p.waitFor(`document.querySelector('[data-category="Food (test)"]') !== null`, 5000, 'Food')
  const placeInside = async (name, parent) => {
    await p.eval(`document.querySelector('[data-category="${name}"] [aria-label="Edit"]').click()`)
    await p.waitFor(`document.querySelector('[data-category-dialog]') !== null`, 5000, 'edit ' + name)
    await p.mouseClick('[data-category-dialog] [role=combobox]')
    await p.clickText(parent, { selector: '[role=option]' })
    await sleep(200)
    await p.clickText('Save', { selector: '[role=dialog] button' })
    await p.waitFor(`document.querySelector('[data-category-dialog]') === null`, 5000, 'saved ' + name)
    await sleep(300)
  }
  await placeInside('Groceries', 'Food (test)')
  await placeInside('Restaurants & cafés', 'Food (test)')
  await makeCategory('Nested 1', null, 'Restaurants & cafés')
  for (let i = 2; i <= 4; i++) await makeCategory(`Nested ${i}`, null, `Nested ${i - 1}`)
  const nestedCats = (await p.engine('app.init')).categories
  const catId = (n) => nestedCats.find((c) => c.name === n)?.id
  check('four levels nested under a category placed under Food', nestedCats.find((c) => c.id === catId('Nested 4'))?.parentId === catId('Nested 3') && nestedCats.find((c) => c.id === 'restaurants')?.parentId === catId('Food (test)'))
  await p.eval(`document.querySelector('[data-category="Nested 4"]').scrollIntoView({ block: 'center' })`)
  await shot(p, '12i-food-nested.png')
  // Four operations of August go to the four nested levels (put back at the end).
  const augBefore = await p.engine('periods.summary', { from: '2025-08-01', to: '2025-08-31', granularity: 'month', currency: 'CAD' })
  const isMoveCat = (id) => nestedCats.find((c) => c.id === id)?.kind === 'transfer'
  const moved = augBefore.transactions.filter((t) => t.amount < 0 && !isMoveCat(t.categoryId) && t.recurring == null && !['groceries', 'restaurants'].includes(t.categoryId)).slice(0, 4)
  for (let i = 0; i < 4; i++) await p.engine('tx.setCategory', { id: moved[i].id, categoryId: catId(`Nested ${i + 1}`), allOfBrand: false })
  const augNested = await p.engine('periods.summary', { from: '2025-08-01', to: '2025-08-31', granularity: 'month', currency: 'CAD' })
  const rootOf = (id) => { let c = nestedCats.find((x) => x.id === id); while (c?.parentId) c = nestedCats.find((x) => x.id === c.parentId); return c?.id }
  const food = augNested.transactions.filter((t) => t.amount < 0 && rootOf(t.categoryId) === catId('Food (test)')).reduce((s, t) => s - t.amount, 0)
  const cents = (v) => Math.round(v * 100)
  check('Food counts Groceries, Restaurants and the nested levels once', cents(augNested.byTopCategory.find((s) => s.key === catId('Food (test)'))?.amount ?? 0) === cents(food)
    && cents(augNested.byTopCategory.reduce((s, x) => s + x.amount, 0)) === cents(augNested.expenses) && cents(augNested.expenses) === cents(augBefore.expenses)
    && cents(augNested.byCategory.reduce((s, x) => s + x.amount, 0)) === cents(augNested.expenses), `Food ${food.toFixed(2)}, expenses ${augNested.expenses}`)
  const analyticsOfAugust = async (ready = () => true) => {
    await p.clickText('Statements', { selector: 'nav button' })
    await p.waitFor(`document.querySelector('section h2') !== null`, 20000, 'periods')
    await p.clickText('Analytics', { selector: 'nav button' })
    await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
    await p.waitFor(`document.querySelector('${sec} [data-pie="Expenses per category"]') !== null && document.querySelector('${sec} [data-category-lines]') !== null`, 20000, 'August analytics')
    await sleep(800)
    await settle(p, sec)
    // The lines of every bank together (each bank lists only its own categories): drawn as that part nears the screen.
    for (let i = 0; i < 40 && !(await p.eval(`!!document.querySelector('${sec} [data-part-block="all"] [data-category-choices]')`)); i++) {
      await p.eval(`([...document.querySelectorAll('${sec} [data-part-block]')].at(-1) ?? document.querySelector('${sec}'))?.scrollIntoView({ block: 'end' })`)
      await sleep(500)
    }
    const read =() => p.eval(`(() => { const s = document.querySelector('${sec}'); return { pie: [...s.querySelectorAll('[data-pie="Expenses per category"] li')].map(l => l.innerText.split('\\n')[0]), lines: [...s.querySelectorAll('[data-part-block="all"] [data-category-choices] li[data-choice]')].map(l => l.dataset.choice), expenses: s.querySelector('[data-part-block="all"] [data-pie="Expenses per category"]')?.innerText.match(/Total\\s+(\\S+)/)?.[1] } })()`)
    let got = await read()
    for (let i = 0; i < 40 && !ready(got); i++) { await sleep(250); got = await read() }
    return got
  }
  const showPie = async (name) => {
    // Every bank together's pie, drawn again if the list of periods was made again meanwhile.
    for (let i = 0; i < 40 && !(await p.eval(`!!document.querySelector('${sec} [data-part-block="all"] [data-pie="Expenses per category"]')`)); i++) {
      await p.eval(`(() => { const parts = document.querySelectorAll('${sec} [data-part-block]'); (parts.length ? parts[parts.length - 1] : document.querySelector('${sec}'))?.scrollIntoView({ block: 'end' }) })()`)
      await sleep(500)
    }
    await bringIntoView(p, `${sec} [data-part-block="all"] [data-pie="Expenses per category"]`)
    await p.eval(`(() => { const s = document.querySelector('${sec} [data-part-block="all"] [data-pie="Expenses per category"]'); if (s) document.querySelector('main').scrollBy({ top: s.getBoundingClientRect().top - 160 }) })()`)
    await sleep(500)
    await shot(p, name)
  }
  const firstLevel = await analyticsOfAugust()
  await showPie('12j-charts-first-level.png')
  const leafNames = ['Groceries', 'Restaurants & cafés', 'Nested 1', 'Nested 2', 'Nested 3', 'Nested 4']
  check('first level only (default): Food in the charts, not the categories in it', firstLevel.pie.includes('Food (test)') && !firstLevel.pie.some((n) => leafNames.includes(n))
    && firstLevel.lines.includes(catId('Food (test)')) && !firstLevel.lines.includes('groceries') && !firstLevel.lines.includes(catId('Nested 4')), firstLevel.pie.join(', '))
  // The setting, in Settings > Display > Analytics: on by default; off, the charts go down to every nested category.
  const firstLevelSwitch = `document.querySelector('[aria-label="Expenses per category charts: main categories only"]')`
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('General', { selector: 'button' })
  await p.waitFor(`${firstLevelSwitch} !== null`, 5000, 'first level setting')
  check('first level setting in Analytics, on by default', await p.eval(`(() => { const i = ${firstLevelSwitch}; return i.checked && !!i.closest('[data-group="Analytics"]') })()`))
  await p.eval(`${firstLevelSwitch}.click()`)
  await p.waitFor(`${firstLevelSwitch}.checked === false`, 5000, 'setting off')
  const everyLevel = await analyticsOfAugust((x) => !x.pie.includes('Food (test)') && !x.lines.includes(catId('Food (test)')))
  await showPie('12k-charts-every-level.png')
  // (The pie keeps its five largest slices and "Other" by default; the list of the lines has every category.)
  check('setting off: every nested category in the charts, Food gone, same expenses', ['Groceries', 'Restaurants & cafés'].every((n) => everyLevel.pie.includes(n)) && !everyLevel.pie.includes('Food (test)')
    && [1, 2, 3, 4].every((i) => everyLevel.lines.includes(catId(`Nested ${i}`)))
    && everyLevel.lines.includes(catId('Nested 4')) && everyLevel.lines.includes('groceries') && !everyLevel.lines.includes(catId('Food (test)')) && everyLevel.expenses === firstLevel.expenses, JSON.stringify({ lines: everyLevel.lines, nested: [1, 2, 3, 4].map((i) => catId(`Nested ${i}`)), expenses: [firstLevel.expenses, everyLevel.expenses] }))
  await p.clickText('Statements', { selector: 'nav button' })
  await openPeriod(p, '2025-08-01')
  // Every bank's table of the month: the categories its operations have.
  const tableCats = []
  const bankParts = `${sec} [data-part-block]:not([data-part-block="all"])`
  await p.waitFor(`[...document.querySelectorAll('${bankParts}')].length > 0 && [...document.querySelectorAll('${bankParts}')].every(b => b.querySelector('[data-section="main"] table'))`, 30000, 'August banks open')
  const bankCount = await p.eval(`document.querySelectorAll('${bankParts}').length`)
  for (let i = 0; i < bankCount; i++) {
    await p.eval(`[...document.querySelectorAll('${bankParts}')[${i}].querySelector('[data-section="main"] table').querySelectorAll('th button')].find(b => b.innerText.trim() === 'Category').click()`)
    await p.waitFor(`document.querySelector('[role=menu]') !== null`, 5000, 'category filter')
    tableCats.push(...await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].map(l => l.dataset.choice)`))
    await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
    await sleep(300)
  }
  check('the operations keep their own categories in the tables', [1, 2, 3, 4].every((i) => tableCats.includes(catId(`Nested ${i}`))) && tableCats.includes('groceries') && !tableCats.includes(catId('Food (test)')), `${bankCount} banks: ${[...new Set(tableCats)].join(', ')}`)
  // Clean up: the setting back on, the operations back in their categories, the test categories deleted.
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('General', { selector: 'button' })
  await p.waitFor(`${firstLevelSwitch} !== null`, 5000, 'first level setting')
  await p.eval(`${firstLevelSwitch}.click()`)
  await p.waitFor(`${firstLevelSwitch}.checked === true`, 5000, 'setting on')
  for (const t of moved) await p.engine('tx.setCategory', { id: t.id, categoryId: t.categoryId, allOfBrand: false })
  await p.clickText('Definitions', { selector: 'button' })
  await p.clickText('Categories', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-categories]') !== null`, 10000, 'categories for clean up')
  await placeInside('Groceries', 'Top level')
  await placeInside('Restaurants & cafés', 'Top level')
  for (const name of ['Nested 4', 'Nested 3', 'Nested 2', 'Nested 1', 'Food (test)']) {
    await p.eval(`document.querySelector('[data-category="${name}"] button[aria-label="Delete"]').click()`)
    await p.waitFor(`document.querySelector('[role=dialog]') !== null`, 5000, 'delete ' + name)
    await p.clickText('Delete', { selector: '[role=dialog] button' })
    await p.waitFor(`document.querySelector('[data-category="${name}"]') === null`, 5000, 'deleted ' + name)
  }
  const cleaned = await p.engine('app.init')
  const augAfter = await p.engine('periods.summary', { from: '2025-08-01', to: '2025-08-31', granularity: 'month', currency: 'CAD' })
  check('test categories deleted, everything as before', !cleaned.categories.some((c) => c.name.startsWith('Nested') || c.name === 'Food (test)') && cleaned.settings.chartsTopLevel === 'true'
    && !cleaned.categories.find((c) => c.id === 'groceries')?.parentId && JSON.stringify(augAfter.byCategory) === JSON.stringify(augBefore.byCategory))
  await p.clickText('Statements', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('section h2') !== null`, 20000, 'periods again')

  // ---- tags (test data, deleted after): none come with the app; made in Definitions > Tags; put on a category
  // (every operation of it carries them) and on single operations; a column and a filter in every table; a Tags pie in
  // Analytics for every kind of period, where an operation with several tags is counted once.
  check('no tags come with the app', (await p.engine('app.init')).tags.tags.length === 0)
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('Definitions', { selector: 'button' })
  await p.clickText('Tags', { selector: 'main [role=group] button' })
  await p.waitFor(`document.body.innerText.includes('No tags yet')`, 5000, 'tags page')
  await shot(p, '22a-tags-empty.png')
  const makeTag = async (name, color) => {
    await p.clickText('Add tag', { selector: 'main button' })
    await p.waitFor(`document.querySelector('[data-tag-dialog] [data-field="name"]') !== null`, 5000, 'tag dialog')
    await p.send('Input.insertText', { text: name })
    if (color) await p.eval(`document.querySelector('[data-tag-dialog] [data-color="${color}"]').click()`)
    await p.clickText('Create', { selector: '[role=dialog] button' })
    await p.waitFor(`document.querySelector('[data-tag-row="${name}"]') !== null`, 5000, 'tag ' + name)
  }
  await makeTag('Trip (test)', '#1e88e5')
  await makeTag('Work (test)', '#8e24aa')
  await makeTag('Family (test)', '#43a047')
  const tagIds = Object.fromEntries((await p.engine('app.init')).tags.tags.map((t) => [t.name, t.id]))
  check('tags made in Definitions > Tags', Object.keys(tagIds).length === 3)
  // Restaurants & cafés carries two tags: every one of its operations has both.
  await p.clickText('Categories', { selector: 'main [role=group] button' })
  await p.waitFor(`document.querySelector('[data-categories]') !== null`, 10000, 'categories')
  await p.eval(`document.querySelector('[data-category="Restaurants & cafés"] [aria-label="Edit"]').click()`)
  await p.waitFor(`document.querySelector('[data-category-dialog] [data-field="tags"] input') !== null`, 5000, 'category tags field')
  await p.mouseClick('[data-category-dialog] [data-field="tags"] input')
  for (const name of ['Trip (test)', 'Work (test)']) {
    await p.waitFor(`document.querySelector('[data-tag-option="${name}"]') !== null`, 5000, 'tag option')
    await p.eval(`document.querySelector('[data-tag-option="${name}"]').click()`)
    await sleep(200)
  }
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await sleep(200)
  await shot(p, '22b-category-tags.png')
  await p.clickText('Save', { selector: '[role=dialog] button' })
  await p.waitFor(`document.querySelector('[data-category-dialog]') === null`, 5000, 'category saved')
  const catTags = (await p.engine('app.init')).tags.categoryTags.restaurants ?? []
  check('two tags put on a category', catTags.length === 2 && catTags.includes(tagIds['Trip (test)']) && catTags.includes(tagIds['Work (test)']))
  // In the tables: the restaurant operations carry both; one other operation gets its own tag from its Tags cell.
  await p.clickText('Statements', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('section h2') !== null`, 20000, 'periods')
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${sec} table [data-tag-cell]') !== null`, 20000, 'tags column')
  await sleep(500)
  const tagRows = () => p.eval(`[...document.querySelectorAll('${sec} [data-section="main"] table')[0].querySelectorAll('tbody tr')].filter(r => r.children.length > 2).map(r => ({ cat: r.children[1].innerText, tags: [...r.querySelectorAll('[data-tag]')].map(t => t.dataset.tag) }))`)
  await p.waitFor(`[...document.querySelectorAll('${sec} [data-section="main"] table')[0].querySelectorAll('tbody tr')].some(r => r.children[1]?.innerText === 'Restaurants & cafés')`, 15000, 'restaurant rows').catch(() => undefined)
  const tagBefore = await tagRows()
  const restaurantRows = tagBefore.filter((r) => r.cat === 'Restaurants & cafés')
  check('Tags column: a category\'s tags on every one of its operations', restaurantRows.length > 0 && restaurantRows.every((r) => r.tags.join() === 'Trip (test),Work (test)')
    && tagBefore.filter((r) => r.cat !== 'Restaurants & cafés').every((r) => r.tags.length === 0), `${restaurantRows.length} restaurant rows`)
  const target = tagBefore.findIndex((r) => r.cat !== 'Restaurants & cafés')
  await p.eval(`[...document.querySelectorAll('${sec} [data-section="main"] table')[0].querySelectorAll('tbody tr')][${target}].querySelector('[data-tag-cell]').click()`)
  await p.waitFor(`document.querySelector('[data-tag-choice="Family (test)"]') !== null`, 5000, 'tag menu')
  await p.eval(`document.querySelector('[data-tag-choice="Family (test)"]').click()`)
  await sleep(300)
  await p.eval(`document.querySelector('[data-tag-choice="Work (test)"]').click()`)
  await sleep(300)
  await shot(p, '22c-tag-cell.png')
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await p.waitFor(`[...document.querySelectorAll('${sec} [data-section="main"] table')[0].querySelectorAll('tbody tr')][${target}].querySelectorAll('[data-tag]').length === 2`, 10000, 'own tags shown')
  const augTagged = await p.engine('periods.summary', { from: '2025-08-01', to: '2025-08-31', granularity: 'month', currency: 'CAD' })
  const ownOp = augTagged.transactions.find((t) => t.ownTags.length > 0)
  check('two tags put on one operation', ownOp?.ownTags.length === 2 && ownOp.tags.includes(tagIds['Family (test)']) && ownOp.tags.includes(tagIds['Work (test)']))
  // The Tags filter: none, then one tag (once the period is read again with the new tags).
  for (let i = 0; i < 20; i++) {
    await p.eval(`[...document.querySelectorAll('${sec} [data-section="main"] table')[0].querySelectorAll('th button')].find(b => b.innerText.trim() === 'Tags').click()`)
    await p.waitFor(`document.querySelector('[role=menu]') !== null`, 5000, 'tags filter')
    if (await p.eval(`!!document.querySelector('[role=menu] li[data-choice="${tagIds['Family (test)']}"]')`)) break
    await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
    await sleep(500)
  }
  await shot(p, '22d-tags-filter.png')
  await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].find(l => l.dataset.choice === 'none').click()`)
  await sleep(250)
  await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].find(l => l.dataset.choice === '${tagIds['Family (test)']}').click()`)
  await sleep(250)
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await sleep(400)
  await p.waitFor(`[...document.querySelectorAll('${sec} [data-section="main"] table')[0].querySelectorAll('tbody tr')].filter(r => r.children.length > 2).length === 1`, 10000, 'one tagged row').catch(() => undefined)
  const familyRows = await tagRows()
  check('Tags filter: one tag', familyRows.length === 1 && familyRows[0].tags.includes('Family (test)'))
  await p.eval(`[...document.querySelectorAll('${sec} [data-section="main"] table')[0].querySelectorAll('th button')].find(b => b.innerText.trim() === 'Tags').click()`)
  await p.waitFor(`document.querySelector('[role=menu]') !== null`, 5000, 'tags filter')
  await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].find(l => l.dataset.choice === 'all').click()`)
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await sleep(400)
  check('Tags column in the transfers table too', await p.eval(`(() => { const s = document.querySelector('${sec}'); if (!s.querySelector('[data-movements]')) s.querySelector('.MuiAccordionSummary-root')?.click(); return true })()`)
    && await p.waitFor(`[...document.querySelectorAll('${sec} [data-movements] th')].some(th => th.innerText.trim() === 'Tags')`, 5000, 'transfers tags').then(() => true, () => false))
  // The Tags pie: every tagged expense once, in the slice of its set of tags.
  const tagged = augTagged.transactions.filter((t) => t.amount < 0 && t.tags.length > 0 && !isMoveCat(t.categoryId)).reduce((s, t) => s - t.amount, 0)
  check('Tags pie adds up to the tagged expenses, counted once', cents(augTagged.tagPie.reduce((s, x) => s + x.amount, 0)) === cents(tagged)
    && augTagged.tagPie.some((x) => x.label === 'Trip (test) + Work (test)') && augTagged.tagPie.some((x) => x.label === 'Family (test) + Trip (test) + Work (test)' || x.label === 'Family (test) + Work (test)'),
    augTagged.tagPie.map((x) => `${x.label} ${x.amount}`).join(' | '))
  await p.clickText('Analytics', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('${sec} [data-pie="Tags"]') !== null`, 20000, 'tags pie')
  await p.eval(`(() => { const t = document.querySelector('${sec} [data-pie="Tags"]'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 200 }) })()`)
  await sleep(600)
  await shot(p, '22e-tags-pie.png')
  const piePerMode = {}
  for (const mode of ['Year', 'All time', 'Custom']) {
    await p.clickText(mode, { selector: 'button' })
    // The period holding the tagged operations of August 2025: the year 2025, all time, the custom period.
    const at = mode === 'Year' ? 'section[data-period="2025-01-01"]' : 'section[data-period]'
    const slices = `document.querySelectorAll('${at} [data-pie="Tags"] li').length`
    for (let i = 0; i < 60 && !(await p.eval(`${slices} > 0`)); i++) {
      await p.eval(`(() => { const s = document.querySelector('${at}'); if (!s) return; const t = s.querySelector('[data-pie="Tags"]'); if (t) document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 200 }); else if (s.getBoundingClientRect().top > 300 || s.getBoundingClientRect().bottom < 300) s.scrollIntoView({ block: 'start' }); else document.querySelector('main').scrollBy({ top: 500 }) })()`)
      await sleep(400)
    }
    piePerMode[mode] = await p.eval(slices)
  }
  check('Tags pie in Year, All time and Custom periods', Object.values(piePerMode).every((n) => n >= 1), JSON.stringify(piePerMode))
  await p.clickText('Month', { selector: 'button' })
  await sleep(600)

  // ---- Folded periods (Statements; they replace the Summary view): every kind of period folds to the summary of every
  // bank together, Earnings, Expenses, Balance and Expenses / earnings, in the global display currency; Fold
  // everything and Unfold everything in the bar.
  await p.clickText('Statements', { selector: 'nav button' })
  await sleep(600)
  const viewOrder = await p.eval(`[...document.querySelector('nav').querySelectorAll('button')].map(b => b.innerText.trim()).filter(Boolean).join()`)
  check('the menu: Statements then Analytics first, no Periods and no Summary', viewOrder === 'Statements,Analytics,Calendar,Import,CAD,Settings', viewOrder)
  check('no Statements and Analytics buttons in the page any more', await p.eval(`!document.querySelector('[data-period-view]')`))
  // The display currency: one button of the menu, just above the light / dark button and of its size; clicked, the
  // currencies drop down from it.
  const currencyButton = await p.eval(`(() => { const c = document.querySelector('nav [data-global-currency]'); const t = document.querySelector('nav button[aria-label="Dark mode"], nav button[aria-label="Light mode"]'); const r = c.getBoundingClientRect(); const q = t.getBoundingClientRect(); return { text: c.innerText.trim(), next: c.nextElementSibling === t, size: [r.width, r.height], theme: [q.width, q.height], above: r.bottom <= q.top + 0.5, centre: Math.abs((r.left + r.right) - (q.left + q.right)) / 2, font: getComputedStyle(c).fontFamily === getComputedStyle(document.querySelector('nav p')).fontFamily } })()`)
  check('the display currency: a button of the menu, just above the light / dark button, of its size, in the font of the menu', currencyButton.text === 'CAD' && currencyButton.font && currencyButton.next && currencyButton.above && currencyButton.centre <= 0.5
    && currencyButton.size.join() === currencyButton.theme.join(), JSON.stringify(currencyButton))
  // The period at the top of the screen (under the bar), and how far down the screen its top is.
  const topPeriod = () => p.eval(`(() => { const m = document.querySelector('main').getBoundingClientRect(); const y = m.top + document.querySelector('main [role=group]').parentElement.getBoundingClientRect().height; const s = [...document.querySelectorAll('section[data-period]')].find(x => x.getBoundingClientRect().bottom > y); return s.dataset.period + ' ' + Math.round(s.getBoundingClientRect().top - y) })()`)
  await p.eval(`(() => { const s = document.querySelector('section[data-period="2025-08-01"]'); document.querySelector('main').scrollBy({ top: s.getBoundingClientRect().top - 300 }) })()`)
  await sleep(1200)
  const readBefore = await topPeriod()
  const openPeriods = () => p.eval(`[...document.querySelectorAll('section[data-period][data-open]')].map(s => s.dataset.period).join()`)
  const openBefore = await openPeriods()
  await p.click('nav [data-global-currency]')
  await p.waitFor(`document.querySelectorAll('[role=menuitem]').length > 0`, 5000, 'currency menu')
  await sleep(400)
  await shot(p, '24-currency-menu.png')
  const currencyItems = await p.eval(`[...document.querySelectorAll('[role=menuitem]')].map(m => m.innerText.trim() + (m.classList.contains('Mui-selected') ? '*' : '')).join()`)
  check('clicked, the currencies drop down from it, the one in use marked', currencyItems === 'CAD*,USD,EUR,GBP,CHF,AUD,JPY,MXN', currencyItems)
  await p.clickText('EUR', { selector: '[role=menuitem]' })
  await p.waitFor(`document.querySelector('nav [data-global-currency]').innerText.trim() === 'EUR'`, 5000, 'currency EUR')
  await p.waitFor(`!document.querySelector('[role=menu]') && document.querySelector('section[data-period] [data-part-block="all"]')?.innerText.includes('€')`, 20000, 'amounts in euros')
  check('a currency picked: the menu closed, every amount in it', (await p.engine('app.init')).settings.currency === 'EUR' && await p.eval(`!document.querySelector('section[data-period] [data-part-block="all"]').innerText.includes('$')`))
  // Once the period read is drawn in euros, and the page no longer moves.
  await p.waitFor(`!!document.querySelector('section[data-period="${readBefore.split(' ')[0]}"] [data-part-block="all"]')?.innerText.includes('€')`, 20000, 'the period read in euros')
  await sleep(1500)
  const readAfter = await topPeriod()
  check('another currency picked: the period read stays where it was on the screen', readAfter.split(' ')[0] === readBefore.split(' ')[0] && Math.abs(+readAfter.split(' ')[1] - +readBefore.split(' ')[1]) <= 2, `${readBefore} -> ${readAfter}`)
  const openAfter = await openPeriods()
  check('another currency picked: the periods open stay open', openBefore.length > 0 && openAfter === openBefore, `${openBefore} -> ${openAfter}`)
  await shot(p, '24-currency-euros.png')
  await p.click('nav [data-global-currency]')
  await p.waitFor(`document.querySelectorAll('[role=menuitem]').length > 0`, 5000, 'currency menu')
  await p.clickText('CAD', { selector: '[role=menuitem]' })
  await p.waitFor(`document.querySelector('nav [data-global-currency]').innerText.trim() === 'CAD'`, 5000, 'currency CAD')
  check('and back to CAD', (await p.engine('app.init')).settings.currency === 'CAD')
  await sleep(800)
  // Statements and Analytics show the same periods: the kind chosen in one is kept in the other.
  await p.click('main button[value="year"]')
  await p.waitFor(`/^\\d{4}$/.test(document.querySelector('section[data-period] h2')?.innerText.trim() ?? '')`, 20000, 'years')
  await p.clickText('Analytics', { selector: 'nav button' })
  await sleep(1200)
  const keptKind = await p.eval(`({ year: document.querySelector('main button[value="year"]').getAttribute('aria-pressed'), parts: !!document.querySelector('[data-analytics-view]'), fold: !!document.querySelector('[data-fold-everything]') })`)
  check('Year chosen in Statements is kept in Analytics (General and Transfers, no folding there)', keptKind.year === 'true' && keptKind.parts && !keptKind.fold, JSON.stringify(keptKind))
  await p.clickText('Statements', { selector: 'nav button' })
  await sleep(800)
  check('and back in Statements, still Year, the folding button back', await p.eval(`document.querySelector('main button[value="year"]').getAttribute('aria-pressed') === 'true' && !!document.querySelector('[data-fold-everything]') && !document.querySelector('[data-analytics-view]')`))
  await p.click('main button[value="month"]')
  await sleep(1500)
  const foldAll = async (open) => {
    const label = await p.eval(`document.querySelector('[data-fold-everything]').innerText.trim()`)
    if (label === (open ? 'Unfold everything' : 'Fold everything')) await p.click('[data-fold-everything]')
    await sleep(800)
  }
  await foldAll(false)
  const foldedSummary = async (sel) => {
    await p.eval(`document.querySelector('${sel}').scrollIntoView({ block: 'start' })`)
    await p.waitFor(`!!document.querySelector('${sel} [data-part-block="all"] [data-totals]')`, 30000, 'summary of ' + sel)
    await sleep(300)
    return p.eval(`(() => { const s = document.querySelector('${sel}'); const all = s.querySelector('[data-part-block="all"]'); return { open: s.hasAttribute('data-open'), tables: s.querySelectorAll('table').length, banks: s.querySelectorAll('[data-part-block]:not([data-part-block="all"])').length, stats: [...all.querySelectorAll('[data-stat]')].map(x => x.dataset.stat), balance: all.querySelector('[data-stat="Balance"]').innerText, text: s.innerText } })()`)
  }
  const money2 = (v) => Math.abs(v).toLocaleString('en-CA', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
  const isSummary = (f) => !f.open && f.tables === 0 && f.banks === 0 && ['Earnings', 'Expenses', 'Balance', 'Expenses / earnings'].every((x) => f.stats.includes(x))
  const augFolded = await foldedSummary(sec)
  const augReport = await p.engine('periods.report', { from: '2025-08-01', to: '2025-08-31', granularity: 'month' })
  check('a folded month: the summary of every bank together under its title and days, no table', isSummary(augFolded) && augFolded.text.includes('August 2025') && augFolded.text.includes('31 days')
    && augFolded.balance.includes(money2(augReport.global.balance)), JSON.stringify({ stats: augFolded.stats, balance: augFolded.balance }))
  await shot(p, '23a-folded-month.png')
  await p.clickText('Year', { selector: 'button' })
  await p.waitFor(`!!document.querySelector('section[data-period="2025-01-01"]')`, 20000, 'year 2025')
  const yearFolded = await foldedSummary('section[data-period="2025-01-01"]')
  const yearSum = (await p.engine('periods.report', { from: '2025-01-01', to: '2025-12-31', granularity: 'year' })).global
  check('a folded year: the summary of every bank together, its balance', isSummary(yearFolded) && yearFolded.balance.includes(money2(yearSum.balance)), JSON.stringify({ stats: yearFolded.stats, balance: yearFolded.balance }))
  await shot(p, '23b-folded-year.png')
  await p.clickText('All time', { selector: 'button' })
  await p.waitFor(`!!document.querySelector('section[data-period]')`, 20000, 'all time')
  const allFrom = await p.eval(`document.querySelector('section[data-period]').dataset.period`)
  const allFolded = await foldedSummary(`section[data-period="${allFrom}"]`)
  check('All time folded: the summary of every bank together', isSummary(allFolded), JSON.stringify(allFolded.stats))
  await shot(p, '23c-folded-all-time.png')
  // Unfold everything opens the period, then every bank in it (all banks first); Fold everything folds it back.
  await foldAll(true)
  await p.waitFor(`document.querySelectorAll('section[data-period="${allFrom}"] [data-part-block]:not([data-part-block="all"])').length > 0`, 30000, 'banks of all time')
  const unfolded = await p.eval(`(() => { const s = document.querySelector('section[data-period="${allFrom}"]'); const parts = [...s.querySelectorAll('[data-part-block]')]; return { open: s.hasAttribute('data-open'), first: parts[0]?.dataset.partBlock, banks: parts.length - 1, label: document.querySelector('[data-fold-everything]').innerText.trim() } })()`)
  check('Unfold everything: the period open, all banks first, then each bank; the button then folds', unfolded.open && unfolded.first === 'all' && unfolded.banks > 0 && unfolded.label === 'Fold everything', JSON.stringify(unfolded))
  await shot(p, '23d-unfolded-all-time.png')
  await foldAll(false)
  check('Fold everything folds them back', isSummary(await foldedSummary(`section[data-period="${allFrom}"]`)))
  await p.clickText('Custom', { selector: 'button' })
  await p.waitFor(`!!document.querySelector('section[data-period] [data-part-block="all"] [data-stat="Balance"]')`, 20000, 'custom summary')
  check('a custom period folds to its summary too', isSummary(await foldedSummary('section[data-period]')))
  await p.clickText('Month', { selector: 'button' })
  await sleep(600)

  // ---- Banks side by side (the samples have Canadian banks in CAD and French banks in EUR in October 2025): every bank
  // in the display currency, then all banks together; every euro operation converted at the rate of its day, with the
  // amount its bank wrote under it.
  const octSec = 'section[data-period="2025-10-01"]'
  // Opened as a person does: the period, every bank together (where how amounts are converted is told), each bank.
  await openPeriod(p, '2025-10-01', true)
  await p.waitFor(`document.querySelector('${octSec} [data-conversion-panel]') !== null`, 30000, 'October with all banks')
  await sleep(500)
  const octUi = await p.eval(`(() => { const s = document.querySelector('${octSec}'); return { parts: [...s.querySelectorAll('[data-part-block]')].map(b => [b.dataset.partBlock, b.querySelector('[data-part-name]').textContent, b.querySelector('[data-part-currency]').textContent]), balance: s.querySelector('[data-part-block="all"] [data-totals] [data-stat="Balance"]').textContent, panel: s.querySelector('[data-conversion-panel]').textContent, originals: [...s.querySelectorAll('[data-original]')].map(o => o.textContent).slice(0, 3) } })()`)
  const oct = await p.engine('periods.report', { from: '2025-10-01', to: '2025-10-31', granularity: 'month' })
  const eurLines = oct.conversion.lines.filter((l) => l.from === 'EUR' && l.to === 'CAD')
  check('all banks in CAD first, then a block per bank in the display currency (euro banks shown EUR → CAD)', octUi.parts.length === oct.banks.length + 1
    && oct.banks.every((b) => b.currency === 'CAD') && octUi.parts.some((x) => x[2] === 'EUR → CAD') && octUi.parts[0].join() === 'all,All banks,CAD', octUi.parts.map((x) => x[1] + ' ' + x[2]).join(' | '))
  check('all banks = every bank added up, each euro operation converted on its own', eurLines.length > 0 && eurLines.every((l) => l.unconverted === 0 && l.rates.length > 0)
    && cents(oct.global.expenses) === cents(oct.banks.reduce((x, b) => x + b.summary.expenses, 0))
    && cents(oct.global.earnings) === cents(oct.banks.reduce((x, b) => x + b.summary.earnings, 0))
    && octUi.balance.includes(Math.abs(oct.global.balance).toLocaleString('en-CA', { minimumFractionDigits: 2 })), `${eurLines.length} euro banks`)
  check('a converted operation shows the amount its bank wrote', octUi.originals.length > 0 && octUi.originals.every((o) => o.includes('€')), octUi.originals.join(' '))
  check('how amounts are converted is shown: source, method, each rate', /Bank of Canada/.test(octUi.panel) && /rate published for its own day/.test(octUi.panel) && /rounded to the cent/.test(octUi.panel) && /1 EUR = [\d.]+/.test(octUi.panel))
  await p.eval(`document.querySelector('${octSec} [data-rates-toggle]').click()`)
  await sleep(300)
  const dayRates = await p.eval(`[...document.querySelectorAll('${octSec} [data-rates] p')].length`)
  check('the rate of every day with operations on demand', dayRates === eurLines[0].rates.length, `${dayRates} days`)
  await shot(p, '24a-banks-and-conversion.png')
  // The bank's name stays at the top of the screen while its table scrolls, the table's header just under it.
  await p.eval(`(() => { const t = [...document.querySelectorAll('${octSec} [data-part-block]')].find(b => b.querySelector('[data-section="main"] table')).querySelector('tbody tr:nth-child(12)'); const m = document.querySelector('main'); m.scrollTop += t.getBoundingClientRect().top - m.getBoundingClientRect().top - 220 })()`)
  await sleep(500)
  const pinned = await p.eval(`(() => { const b = [...document.querySelectorAll('${octSec} [data-part-block]')].find(b => b.querySelector('[data-section="main"] table')); const h = b.querySelector('[data-part-head]').getBoundingClientRect(); const th = b.querySelector('[data-section="main"] thead th').getBoundingClientRect(); const bar = document.querySelector('main [class*=MuiToggleButtonGroup]').parentElement.getBoundingClientRect(); return { bar: Math.round(bar.bottom), head: [Math.round(h.top), Math.round(h.bottom)], th: [Math.round(th.top), Math.round(th.bottom)], top: Math.round(b.getBoundingClientRect().top) } })()`)
  check('the bank stays named while its table scrolls, the table header under its name', pinned.top < pinned.head[0] && Math.abs(pinned.head[0] - pinned.bar) <= 1 && Math.abs(pinned.th[0] - pinned.head[1]) <= 1, JSON.stringify(pinned))
  await shot(p, '24b-bank-name-pinned.png')
  // Money moved in October: the banks as boxes holding their accounts; 500 EUR sent from the Caisse d'Epargne to
  // Northwind Bank (received as 800.00 CAD) is one movement between the two banks, shown in the display currency.
  const between = oct.global.movements.filter((m) => !m.fromOutside && !m.toOutside && m.fromBank !== m.toBank)
  check('a transfer between two banks in two currencies: one movement, out of spending and income', between.some((m) => m.fromBank === "Caisse d'Epargne" && m.toBank === 'Northwind Bank')
    && !oct.banks.some((b) => b.summary.transactions.some((t) => Math.abs(t.amount) === 800 && t.categoryId !== 'transfers')), JSON.stringify(between))
  await p.clickText('Analytics', { selector: 'nav button' })
  await analyticsPart(p, 'Transfers')
  await p.waitFor(`document.querySelector('${octSec} [data-flow-card] [data-flowchart]') !== null`, 20000, 'October money moved')
  await p.eval(`(() => { const f = document.querySelector('${octSec} [data-transfer-timeline]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await sleep(700)
  await shot(p, '24c0-timeline-between-banks.png')
  const octLine = await timelineChecks(p, octSec, oct.global.timeline, 'timeline of October, two currencies')
  check('the timeline tells the transfer between two banks from those inside a bank', octLine.kinds.includes('between') && oct.global.timeline.between > 0, JSON.stringify(octLine.kinds))
  await p.eval(`(() => { const f = document.querySelector('${octSec} [data-flow-card]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 })
  await sleep(800)
  await shot(p, '24c-money-moved-between-banks.png')
  await p.eval(`document.querySelector('main').scrollBy({ top: 560 })`)
  await sleep(400)
  await shot(p, '24d-money-moved-between-banks-lower.png')
  const octFlow = await p.eval(`(() => { const f = document.querySelector('${octSec} [data-flow-card]'); return { banks: [...f.querySelectorAll('[data-bank-node]')].map(b => b.dataset.bankNode), labels: [...f.querySelectorAll('g text.tnum')].map(t => t.textContent) } })()`)
  check('the October diagram: every bank with movements as a box, amounts in the display currency', octFlow.banks.includes("Caisse d'Epargne") && octFlow.banks.includes('Northwind Bank')
    && octFlow.labels.length > 0 && octFlow.labels.every((l) => l.includes('$') && !l.includes('€')), JSON.stringify(octFlow))
  await analyticsPart(p, 'General')
  // The width of the page changing (the window resized, a scrollbar coming and going) while the charts are drawn:
  // the month read stays where it is, and the browser reports no loop of size changes.
  await p.eval(`window.__sizeLoops = 0; window.addEventListener('error', (e) => { if (/ResizeObserver/.test(e.message)) window.__sizeLoops++ })`)
  // Where the month is once the charts are drawn and the page no longer moves.
  const octTop = () => p.eval(`Math.round(document.querySelector('${octSec}').getBoundingClientRect().top)`)
  let octBefore = await octTop()
  for (let i = 0, same = 0; i < 40 && same < 4; i++) { await sleep(250); const t = await octTop(); same = t === octBefore ? same + 1 : 0; octBefore = t }
  for (let i = 0; i < 12; i++) { await p.eval(`document.querySelector('main').style.paddingRight = '${i % 2 ? 0 : 23}px'`); await sleep(350) }
  await sleep(1500)
  const widened = await p.eval(`({ loops: window.__sizeLoops, top: Math.round(document.querySelector('${octSec}').getBoundingClientRect().top) })`)
  check('the width changing while the charts are drawn: the month stays in place, no loop of size changes', widened.loops === 0 && Math.abs(widened.top - octBefore) <= 2, `${JSON.stringify(widened)}, before ${octBefore}`)
  await p.clickText('Statements', { selector: 'nav button' })
  await sleep(600)
  // Folded, October shows the global balance: every bank together, in the display currency.
  await p.click(`${octSec} .p-period-head [data-fold]`)
  await sleep(500)
  const octFolded = await foldedSummary(octSec)
  check('folded, a period shows the global balance', isSummary(octFolded) && octFolded.balance.includes(money2(oct.global.balance)), octFolded.balance)
  const typeInto = async (selector, value) => {
    await p.eval(`(() => { const i = document.querySelector('${selector}'); i.focus(); i.select() })()`)
    await p.send('Input.insertText', { text: value })
    await sleep(150)
  }
  // Settings > Display: the global display currency and the bank of the exchange rates are rows like the date format,
  // their values in the same column; the Bank of Canada by default.
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('General', { selector: 'button' })
  await sleep(600)
  const display = await p.eval(`(() => {
    const card = [...document.querySelectorAll('main h6')].find(h => h.innerText === 'Display').closest('div').parentElement
    const rows = [...card.children].filter(r => r.querySelector('.MuiInputBase-root')).slice(0, 2)
    return rows.map(r => { const v = r.querySelector('.MuiInputBase-root').getBoundingClientRect(); return { label: r.firstElementChild.innerText, value: r.querySelector('.MuiInputBase-root').innerText.replace(/[​s]+/g, ' ').trim(), right: Math.round(v.right), width: Math.round(v.width) } })
  })()`)
  check('Display: exchange rates and date format as rows, values aligned; the global currency is in the menu, not here', display.map((r) => r.label).join() === 'Exchange rates,Date format'
    && new Set(display.map((r) => r.right)).size === 1 && new Set(display.map((r) => r.width)).size === 1 && display[0].value === 'Bank of Canada'
    && !(await p.eval(`document.querySelector('main').innerText.includes('Global display currency') || !!document.querySelector('main [data-global-currency]')`))
    && (await p.eval(`document.querySelector('[data-rates-link]')?.innerText`)) === 'Select source', JSON.stringify(display))
  await shot(p, '24c-display-settings.png')
  check('the Bank of Canada by default, rates against CAD', oct.conversion.source === 'Bank of Canada' && oct.conversion.base === 'CAD')
  await p.engine('settings.set', { key: 'ratesSource', value: 'ecb' })
  const ecb = await p.engine('rates.update')
  const octEcb = await p.engine('periods.report', { from: '2025-10-01', to: '2025-10-31', granularity: 'month' })
  check('another central bank: the European Central Bank, rates against EUR', ecb.error === null && ecb.base === 'EUR' && octEcb.conversion.source === 'European Central Bank' && octEcb.conversion.base === 'EUR'
    && octEcb.conversion.lines.length > 0 && octEcb.conversion.lines.every((l) => l.unconverted === 0) && Math.abs(octEcb.global.balance - oct.global.balance) < Math.abs(oct.global.balance) * 0.02,
    `${octEcb.global.balance} with the ECB, ${oct.global.balance} with the Bank of Canada`)
  await p.engine('settings.set', { key: 'ratesSource', value: 'boc' })
  // Definitions > Exchange rates: one line per central bank the app reads (all three to start with), the round button for
  // the one in use, what each download holds; Refresh all; a new line is a choice among the banks; a wrong address is
  // checked in the background and the reason shown cleanly.
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('General', { selector: 'button' })
  await sleep(500)
  await p.mouseClick('[data-rates-link]')
  await p.waitFor(`document.querySelector('[data-rate-sources]') !== null`, 10000, 'sources table')
  await p.click('[data-rates-refresh]')
  await p.waitFor(`!document.querySelector('[data-rates-refresh]').disabled`, 120000, 'every bank downloaded')
  await sleep(400)
  const ratesTable = () => p.eval(`(() => ({
    tab: [...document.querySelectorAll('main [role=group] button[aria-pressed=true]')].map(b => b.innerText),
    rows: [...document.querySelectorAll('[data-rate-source]')].map(r => ({ name: r.cells[1].innerText, url: r.querySelector('[data-field=url]').value, currencies: r.querySelector('[data-currencies]').innerText, latest: r.querySelector('[data-latest]').innerText, inUse: r.querySelector('input[type=radio]').checked, tint: getComputedStyle(r).backgroundColor, error: r.querySelector('[data-rates-error]')?.innerText ?? null })),
    help: document.querySelector('[data-rates-help]').innerText,
    tabs: [...document.querySelectorAll('main [role=group]')].at(-1).innerText.replace(/\\s+/g, ' '),
    head: [...document.querySelectorAll('[data-rate-sources] thead th')].map(t => t.innerText).join('|'),
  }))()`)
  let rt = await ratesTable()
  check('Definitions: Exchange rates after Bank, opened from the Settings row', rt.tab.includes('Exchange rates') && /^Bank Exchange rates Brands/.test(rt.tabs), rt.tabs)
  check('the three central banks to start with, each with its currencies and last day read from its download', rt.rows.length === 3
    && rt.rows[0].name === 'European Central Bank' && rt.rows[0].url.startsWith('https://www.ecb.europa.eu/') && rt.rows[1].name === 'Bank of Canada' && rt.rows[1].url.startsWith('https://www.bankofcanada.ca/')
    && rt.rows[2].name === 'US Federal Reserve' && rt.rows[2].url.startsWith('https://www.federalreserve.gov/')
    && rt.rows.every((r) => Number(r.currencies) > 20 && r.latest !== '' && r.error === null) && rt.head === 'In use|Central bank|Address of the rates file|Currencies|Rates up to|', JSON.stringify(rt.rows))
  // Every piece of a line on one middle: the round button, the name, the address, the numbers, the delete icon.
  const middles = await p.eval(`[...document.querySelectorAll('[data-rate-source]')].map((row) => {
    const mid = (r) => r.top + r.height / 2
    const text = (el) => { const w = document.createTreeWalker(el, NodeFilter.SHOW_TEXT); let n; while ((n = w.nextNode())) if (n.textContent.trim()) { const g = document.createRange(); g.selectNodeContents(n); return mid(g.getBoundingClientRect()) } return null }
    const td = row.cells, del = td[5].querySelector('svg')
    const all = [mid(td[0].querySelector('svg').getBoundingClientRect()), text(td[1]), mid(row.querySelector('[data-field=url]').getBoundingClientRect()), text(td[3]), text(td[4]), del && mid(del.getBoundingClientRect())].filter((x) => x !== null)
    return +(Math.max(...all) - Math.min(...all)).toFixed(1)
  })`)
  check('each line aligned: round button, texts, address and delete icon on one middle', middles.every((d) => d <= 1.5), middles.join())
  check('the bank in use (the Bank of Canada): its round button checked, its line tinted, the others plain', rt.rows[1].inUse && !rt.rows[0].inUse && rt.rows[0].tint !== rt.rows[1].tint, rt.rows.map((r) => r.tint).join())
  check('what to paste as the address: one short sentence', (rt.help.match(/[.!?](\s|$)/g) ?? []).length === 1 && rt.help.length < 110 && /not the address of its web page/.test(rt.help), rt.help)
  // Choose the European Central Bank with its round button: its line tinted, the Bank of Canada's plain.
  await p.mouseClick('[data-rate-source="ecb"] input[type=radio]')
  await p.waitFor(`document.querySelector('[data-rate-source="ecb"] input[type=radio]').checked`, 10000, 'the ECB in use')
  check('a round button makes its bank the one in use', (await p.engine('rates.status')).sourceId === 'ecb')
  await p.mouseClick('[data-rate-source="boc"] input[type=radio]')
  await p.waitFor(`document.querySelector('[data-rate-source="boc"] input[type=radio]').checked`, 10000, 'back to the Bank of Canada')
  check('every central bank of the list in the table to start with: no more to add', await p.eval(`[...document.querySelectorAll('main button')].find(b => b.innerText === 'Add bank').disabled`))
  // The US Federal Reserve deleted from its own button, after saying so.
  await p.mouseClick('[data-rate-source="fed"] button[aria-label^="Delete"]')
  await p.clickText('Delete', { selector: '[role=dialog] button' })
  await p.waitFor(`document.querySelector('[data-rate-source="fed"]') === null`, 10000, 'the Fed deleted')
  check('a bank not in use can be deleted', (await p.engine('rates.sources')).length === 2)
  // A new bank, from the Add bank button, in a dialog: a choice among the central banks not in the table yet (the US
  // Federal Reserve), with its address; nothing typed into the table.
  await p.clickText('Add bank', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-rate-source-dialog] [data-field=url]') !== null`, 5000, 'add bank dialog')
  await p.mouseClick('[data-rate-source-dialog] [role=combobox]')
  const choices = await p.eval(`[...document.querySelectorAll('[role=option]')].map(o => o.innerText).join()`)
  await p.clickText('US Federal Reserve', { selector: '[role=option]' })
  check('a new bank: from the Add bank button, in a dialog: one of the central banks the app reads, the US Federal Reserve among them', choices === 'US Federal Reserve'
    && (await p.eval(`document.querySelector('[data-rate-source-dialog] [data-field=url]').value`)).startsWith('https://www.federalreserve.gov/') && (await p.eval(`document.querySelector('[data-rate-sources] tbody input:not([data-field=url]):not([type=radio])') === null`)), choices)
  // Its address replaced by the web page of its release: refused cleanly, in the background.
  await typeInto('[data-rate-source-dialog] [data-field="url"]', 'https://www.federalreserve.gov/releases/h10/current/')
  await p.clickText('Add', { selector: '[data-rate-source-dialog] button' })
  await p.waitFor(`[...document.querySelectorAll('[data-rate-source]')].some(r => r.querySelector('[data-rates-error]'))`, 60000, 'the wrong address checked')
  const wrong = await p.eval(`[...document.querySelectorAll('[data-rate-source]')].map(r => r.querySelector('[data-rates-error]')?.innerText).find(Boolean)`)
  check('a wrong address: checked in the background, the reason shown under it', /web page, not a file of rates/.test(wrong) && !(await p.eval(`!!document.querySelector('.MuiSnackbar-root .MuiAlert-standardError, .MuiSnackbar-root .MuiAlert-filledError')`)), wrong)
  check('every central bank of the list in the table: no more to add', await p.eval(`[...document.querySelectorAll('main button')].find(b => b.innerText === 'Add bank').disabled`))
  // Its pencil opens the same dialog, on its address.
  await p.mouseClick('[data-rate-sources] tbody tr:last-child [data-row-edit]')
  await p.waitFor(`document.querySelector('[data-rate-source-dialog] [data-field=url]') !== null`, 5000, 'edit bank dialog')
  check('a bank\'s pencil: the same dialog, on its address', (await p.eval(`document.querySelector('[data-rate-source-dialog] h2').innerText`)) === 'Edit the US Federal Reserve'
    && (await p.eval(`document.querySelector('[data-rate-source-dialog] [data-field=url]').value`)) === 'https://www.federalreserve.gov/releases/h10/current/')
  await p.clickText('Cancel', { selector: '[data-rate-source-dialog] button' })
  await sleep(300)
  await shot(p, '24d-exchange-rate-sources.png')
  // The delete button: a plain icon, red under the mouse, nothing drawn around it.
  const del = await p.eval(`(() => { const b = document.querySelector('[data-rate-source="ecb"] button[aria-label^="Delete"]'); const r = b.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 } })()`)
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: del.x, y: del.y })
  await sleep(300)
  const hovered = await p.eval(`(() => { const s = (q) => getComputedStyle(document.querySelector(q + ' button[aria-label^="Delete"]')); const h = s('[data-rate-source="ecb"]'); const o = s('[data-rate-source]:last-child'); return { bg: h.backgroundColor, color: h.color, other: o.color } })()`)
  check('delete under the mouse: red, no shape around it', /rgba\(0, 0, 0, 0\)|transparent/.test(hovered.bg) && hovered.color !== hovered.other, JSON.stringify(hovered))
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 })
  // Chosen in Settings: the same reason under the choice; nothing said when a good one is chosen again.
  await p.clickText('General', { selector: 'button' })
  await sleep(500)
  await p.mouseClick('[data-rates-source-select]')
  await p.clickText('US Federal Reserve', { selector: '[role=option]' })
  await p.waitFor(`document.querySelector('main [data-rates-error]') !== null`, 60000, 'the error under the choice')
  check('the chosen bank checked at once, its error under the choice', /web page/.test(await p.eval(`document.querySelector('main [data-rates-error]').innerText`)))
  await shot(p, '24e-wrong-source-chosen.png')
  await p.mouseClick('[data-rates-source-select]')
  await p.clickText('Bank of Canada', { selector: '[role=option]' })
  await p.waitFor(`document.querySelector('main [data-rates-error]') === null`, 90000, 'the good source checked')
  check('a good source: chosen silently', !(await p.eval(`[...document.querySelectorAll('.MuiSnackbar-root')].some(x => /rates/i.test(x.innerText))`)) && (await p.engine('rates.status')).error === null)
  // Its own address back: its rates again.
  const fed = (await p.engine('rates.sources')).find((x) => x.format === 'fed')
  await p.engine('rates.saveSource', { id: fed.id, url: 'https://www.federalreserve.gov/datadownload/Output.aspx?rel=H10&series=60f32914ab61dfab590e0e470153e3ae&lastobs=5000&from=&to=&filetype=csv&label=include&layout=seriescolumn&type=package' })
  check('the US Federal Reserve with its own address: its rates again', (await p.engine('rates.update', { id: fed.id })).error === null)
  // The display currency changes every amount: every bank in euros, the Canadian ones converted.
  await p.engine('settings.set', { key: 'currency', value: 'EUR' })
  const octEur = await p.engine('periods.report', { from: '2025-10-01', to: '2025-10-31', granularity: 'month' })
  check('in euros, every bank in euros, the Canadian ones converted', octEur.currency === 'EUR' && octEur.banks.every((b) => b.currency === 'EUR')
    && octEur.conversion.lines.length > 0 && octEur.conversion.lines.every((l) => l.from === 'CAD' && l.to === 'EUR')
    && octEur.banks.filter((b) => b.native === 'CAD').every((b) => b.summary.transactions.every((t) => t.currency === 'EUR' && t.originalCurrency === 'CAD')))
  await p.engine('settings.set', { key: 'currency', value: 'CAD' })
  // The calendar: every bank together, the month's totals in its header; a euro operation shows what the bank wrote.
  await reload(p)
  await p.waitFor(`document.querySelector('nav') !== null`, 20000, 'reloaded')
  await p.clickText('Calendar', { selector: 'nav button' })
  await sleep(1200)
  const converted = oct.banks.find((b) => b.native === 'EUR').summary.transactions.find((t) => t.originalCurrency === 'EUR')
  for (let i = 0; i < 24 && (await p.eval(`document.querySelector('main h5')?.innerText`)) !== 'October 2025'; i++) await p.click('[aria-label="Previous month"]')
  await sleep(600)
  const header = await p.eval(`document.querySelector('[data-month-totals]')?.innerText.replace(/\\s+/g, ' ')`)
  check('calendar: the month’s totals in its header: earnings, expenses, balance', header?.includes('Earnings') && header.includes('Expenses') && header.includes('Balance') && header.includes(Math.abs(oct.global.balance).toLocaleString('en-CA', { minimumFractionDigits: 2 })), header)
  await p.mouseClick(`div[data-date="${converted.date}"]`)
  await sleep(600)
  check('calendar: all banks, a converted amount with the amount the bank wrote', await p.eval(`[...document.querySelectorAll('[data-original]')].some(e => e.innerText.includes('€'))`), converted.date)
  await shot(p, '24f-calendar-all-banks.png')

  // ---- Definitions > Holidays: Canada (Federal) with Québec under it, both checked; the United States (Federal) and
  // France ready; Refresh all; several checked at once; their holidays on the calendar and out of the business days;
  // the weekend days chosen under the table; deleted, added again; a wrong address said.
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('Definitions', { selector: 'button' })
  await p.clickText('Holidays', { selector: 'main [role=group] button' })
  await p.waitFor(`document.querySelector('[data-holiday-calendars]') !== null`, 10000, 'holidays table')
  const moneyCad = (v) => '$' + v.toLocaleString('en-CA', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
  const toDecember = async () => {
    for (let i = 0; i < 24 && (await p.eval(`document.querySelector('main h5')?.innerText`)) !== 'December 2025'; i++)
      await p.click(`[aria-label="${/^(January|February|March|April|May|June|July|August|September|October|November) 2025$|202[0-4]/.test(await p.eval(`document.querySelector('main h5').innerText`)) ? 'Next' : 'Previous'} month"]`)
    await sleep(600)
  }
  const holidaysIdle = `![...document.querySelectorAll('[data-holiday-calendar]')].some(r => r.innerText.includes('Downloading')) && !document.querySelector('[data-holidays-refresh]').disabled`
  await p.click('[data-holidays-refresh]')
  await p.waitFor(holidaysIdle, 180000, 'every calendar downloaded')
  await sleep(400)
  const holidayRows = () => p.eval(`[...document.querySelectorAll('[data-holiday-calendar]')].map(r => ({ id: r.dataset.holidayCalendar, parent: r.dataset.parent ?? null, location: r.querySelector('[data-field=location]').value, url: r.querySelector('[data-field=url]').value, perYear: Number(r.querySelector('[data-per-year]').innerText || 0), active: r.querySelector('input[type=checkbox]').checked, tint: getComputedStyle(r).backgroundColor, error: r.querySelector('[data-holidays-error]')?.innerText ?? null, textLeft: Math.round(r.querySelector('[data-field=location]').getBoundingClientRect().left), checkLeft: Math.round(r.querySelector('input[type=checkbox]').closest('span').getBoundingClientRect().left) }))`)
  let hr = await holidayRows()
  check('Holidays: Location, URL, Number of Holidays per Year', (await p.eval(`[...document.querySelectorAll('[data-holiday-calendars] thead th')].map(t => t.innerText).join('|')`)) === 'Shown|Location|URL|Number of Holidays per Year|')
  check('to start with: Canada (Federal) and Québec checked, the United States (Federal) and France; each with its holidays of this year', hr.map((r) => r.location).join() === 'Canada (Federal),Québec,United States (Federal),France'
    && hr.map((r) => r.active).join() === 'true,true,false,false' && hr.every((r) => r.perYear >= 7 && r.perYear <= 14 && r.error === null) && hr[0].tint !== hr[2].tint, JSON.stringify(hr.map((r) => [r.location, r.active, r.perYear, r.error])))
  // Québec under Canada: set in, joined to it by a line, no rule between them; the check boxes in one column.
  const tree = await p.eval(`(() => { const ca = document.querySelector('[data-holiday-calendar="ca"]'), qc = document.querySelector('[data-holiday-calendar="qc"]'), line = qc.querySelector('[data-tree-line]').getBoundingClientRect(), caText = ca.querySelector('[data-field=location]').getBoundingClientRect(), qcText = qc.querySelector('[data-field=location]').getBoundingClientRect()
    return { parent: qc.dataset.parent, lineLeft: Math.round(line.left), caTextLeft: Math.round(caText.left), lineTop: Math.round(line.top), caBottom: Math.round(caText.bottom), lineRight: Math.round(line.right), qcTextLeft: Math.round(qcText.left), rule: getComputedStyle(ca.cells[1]).borderBottomColor } })()`)
  check('Québec under Canada: set in, joined by a line from Canada, no rule between them, the check boxes aligned', tree.parent === 'ca' && Math.abs(tree.lineLeft - tree.caTextLeft) <= 2
    && tree.lineTop >= tree.caBottom - 2 && tree.lineTop <= tree.caBottom + 10 && tree.qcTextLeft - tree.lineRight >= 2 && tree.qcTextLeft - tree.lineRight <= 10
    && /rgba\(0, 0, 0, 0\)|transparent/.test(tree.rule) && new Set(hr.map((r) => r.checkLeft)).size === 1, JSON.stringify(tree))
  const holidayMiddles = await p.eval(`[...document.querySelectorAll('[data-holiday-calendar]')].map((row) => {
    const mid = (r) => r.top + r.height / 2
    const td = row.cells
    const g = document.createRange(); g.selectNodeContents(td[3].querySelector('div')); const num = td[3].innerText.trim() ? mid(g.getBoundingClientRect()) : null
    const all = [mid(td[0].querySelector('svg').getBoundingClientRect()), mid(td[1].querySelector('input').getBoundingClientRect()), mid(td[2].querySelector('input').getBoundingClientRect()), num, mid(td[4].querySelector('svg').getBoundingClientRect())].filter((x) => x !== null)
    return +(Math.max(...all) - Math.min(...all)).toFixed(1)
  })`)
  check('each line aligned like the exchange rates: check box, place, address, number and delete icon', holidayMiddles.every((d) => d <= 1.5), holidayMiddles.join())
  check('weekend days under the table, Saturday and Sunday by default', (await p.eval(`[...document.querySelectorAll('[data-weekend-days] input[type=checkbox]')].map(i => i.checked ? i.dataset.weekday : '').filter(Boolean).sort().join()`)) === '6,7'
    && (await p.eval(`document.querySelector('[data-weekend-days]').getBoundingClientRect().top > document.querySelector('[data-holiday-calendars]').getBoundingClientRect().bottom`)))
  const totalShown = () => p.eval(`document.querySelector('[data-holidays-total] [data-total]').innerText`)
  const year = new Date().getFullYear()
  const placed = await p.eval(`(() => { const t = document.querySelector('[data-holiday-calendars]').getBoundingClientRect(), s = document.querySelector('[data-holidays-total]').getBoundingClientRect(), w = document.querySelector('[data-weekend-days]').getBoundingClientRect(); return s.top > t.bottom && s.bottom <= w.top + 1 })()`)
  await p.waitFor(`document.querySelector('[data-holidays-total] [data-total]').innerText !== ''`, 10000, 'the total')
  const bothTotal = await totalShown()
  check('under the table, before the weekend days: the holidays of this year of the checked calendars, each day once (Canada 12 and Québec 8 together: 13)', placed && bothTotal === '13'
    && (await p.eval(`document.querySelector('[data-holidays-total]').innerText`)).startsWith(`Holidays in ${year}`), bothTotal)
  await shot(p, '24g-holidays.png')
  // Each calendar checked on its own: Québec unchecked, Canada stays; Canada unchecked, Québec checked alone; Canada again.
  const activeOf = async () => (await holidayRows()).map((r) => r.active).join()
  const waitTotal = (old) => p.waitFor(`document.querySelector('[data-holidays-total] [data-total]').innerText !== ${JSON.stringify(old)}`, 10000, 'the total again')
  await p.mouseClick('[data-holiday-calendar="qc"] input[type=checkbox]')
  await p.waitFor(`!document.querySelector('[data-holiday-calendar="qc"] input[type=checkbox]').checked`, 10000, 'Québec unchecked')
  await waitTotal('13')
  const canadaAlone = [await activeOf(), await totalShown()]
  await p.mouseClick('[data-holiday-calendar="ca"] input[type=checkbox]')
  await p.waitFor(`!document.querySelector('[data-holiday-calendar="ca"] input[type=checkbox]').checked`, 10000, 'Canada unchecked')
  await p.mouseClick('[data-holiday-calendar="qc"] input[type=checkbox]')
  await p.waitFor(`document.querySelector('[data-holiday-calendar="qc"] input[type=checkbox]').checked`, 10000, 'Québec checked')
  await sleep(500)
  const quebecAlone = [await activeOf(), await totalShown()]
  await p.mouseClick('[data-holiday-calendar="ca"] input[type=checkbox]')
  await p.waitFor(`document.querySelector('[data-holiday-calendar="ca"] input[type=checkbox]').checked`, 10000, 'Canada checked again')
  await p.mouseClick('[data-holiday-calendar="ca"] input[type=checkbox]')
  await p.waitFor(`!document.querySelector('[data-holiday-calendar="ca"] input[type=checkbox]').checked`, 10000, 'Canada unchecked again')
  const quebecStays = await activeOf()
  await p.mouseClick('[data-holiday-calendar="ca"] input[type=checkbox]')
  await p.waitFor(`document.querySelector('[data-holiday-calendar="ca"] input[type=checkbox]').checked`, 10000, 'Canada checked')
  await sleep(500)
  check('each calendar checked on its own: Canada without Québec (12), Québec without Canada (8), unchecking Canada leaves Québec', canadaAlone.join() === 'true,false,false,false,12'
    && quebecAlone.join() === 'false,true,false,false,8' && quebecStays === 'false,true,false,false' && (await activeOf()) === 'true,true,false,false' && (await totalShown()) === '13', `${canadaAlone} | ${quebecAlone} | ${quebecStays}`)
  // November 2025: Remembrance Day (Tuesday 11, federal); the United States checked too take out Thanksgiving (27).
  const novDays = async () => (await p.engine('periods.report', { from: '2025-11-01', to: '2025-11-30', granularity: 'month' })).businessDays
  const novCanada = await novDays()
  await p.mouseClick('[data-holiday-calendar="us"] input[type=checkbox]')
  await p.waitFor(`document.querySelector('[data-holiday-calendar="us"] input[type=checkbox]').checked`, 10000, 'the United States checked')
  await sleep(300)
  hr = await holidayRows()
  check('several calendars checked at once, each line tinted; their holidays out of the business days', novCanada === 19 && (await novDays()) === 18 && hr[2].active && hr[2].tint === hr[0].tint && hr[3].tint !== hr[0].tint, `${novCanada} then ${await novDays()}`)
  // The calendar: Christmas Day of the checked calendars written once, with their places; Boxing Day (federal) the next day.
  await p.clickText('Calendar', { selector: 'nav button' })
  await sleep(1000)
  await toDecember()
  await p.waitFor(`document.querySelector('[data-holiday="2025-12-25"]') !== null`, 10000, 'Christmas on the calendar')
  await sleep(400)
  const christmas = await p.eval(`[...document.querySelectorAll('[data-holiday="2025-12-25"]')].map(h => h.innerText + ' / ' + h.title)`)
  await p.mouseClick('[data-holiday="2025-12-25"]')
  await sleep(500)
  const panelHoliday = await p.eval(`[...document.querySelectorAll('[data-day-holiday]')].map(h => h.innerText).join(' | ')`)
  check('the calendar: the checked calendars\' holidays on their day, the same one written once with its places', christmas.length === 1 && /Christmas Day \/ Christmas Day · Canada \(Federal\), Québec, United States \(Federal\)/.test(christmas[0])
    && /Christmas Day · Canada \(Federal\), Québec, United States \(Federal\)/.test(panelHoliday) && /Boxing Day/.test(await p.eval(`document.querySelector('[data-holiday="2025-12-26"]')?.innerText ?? ''`)), `${christmas.join()} | ${panelHoliday}`)
  const weekendCells = () => p.eval(`document.querySelectorAll('.weekend-day').length`)
  const weekendBefore = await weekendCells()
  await shot(p, '24h-calendar-holidays.png')
  for (let i = 0; i < 12; i++) await p.click('[aria-label="Next month"]')
  await p.waitFor(`document.querySelector('main h5')?.innerText === 'December 2026'`, 10000, 'December 2026')
  await sleep(600)
  const weekendHoliday = await p.eval(`({ sat: document.querySelector('[data-holiday="2026-12-26"]')?.innerText ?? '', satWeekend: document.querySelector('[data-holiday="2026-12-26"]')?.closest('[data-date]')?.classList.contains('weekend-day') ?? false, off: document.querySelector('[data-holiday="2026-12-28"]')?.innerText ?? '' })`)
  check('a holiday on a weekend day is on the calendar too: Boxing Day on Saturday 26 December 2026, its day off on Monday the 28th', weekendHoliday.sat === 'Boxing Day' && weekendHoliday.satWeekend && weekendHoliday.off === 'Boxing Day (observed)', JSON.stringify(weekendHoliday))
  await shot(p, '24h1-calendar-weekend-holiday.png')
  // Settings > General > Calendar: the week starts on Sunday (North America) by default; Monday puts the weekend at the
  // right, Saturday at the left.
  const firstShown = () => p.eval(`(() => { const d = document.querySelector('main [data-date]').dataset.date; return new Date(d + 'T12:00').getDay() })()`)
  const lastHead = () => p.eval(`[...document.querySelectorAll('main .weekend-head')].map(h => h.innerText.trim()).join()`)
  const sundayFirst = [await firstShown(), await lastHead()]
  const weekStarts = async (label) => {
    await p.clickText('Settings', { selector: 'nav button' })
    await p.clickText('General', { selector: 'button' })
    await sleep(400)
    await p.mouseClick('[data-calendar-first-day]')
    await p.clickText(label, { selector: '[role=option]' })
    await sleep(300)
    await p.clickText('Calendar', { selector: 'nav button' })
    await sleep(1000)
    return [await firstShown(), await lastHead()]
  }
  const mondayFirst = await weekStarts('Monday (Europe)')
  await shot(p, '24h1b-calendar-monday-first.png')
  const saturdayFirst = await weekStarts('Saturday (Middle East)')
  const back = await weekStarts('Sunday (North America)')
  check('the week starts on Sunday by default (North America), or Monday (weekend at the right), or Saturday (weekend at the left)', (await p.engine('app.init')).settings.calendarFirstDay === '0'
    && sundayFirst[0] === 0 && mondayFirst[0] === 1 && saturdayFirst[0] === 6 && back[0] === 0, JSON.stringify({ sundayFirst, mondayFirst, saturdayFirst, back }))
  await toDecember()
  // Friday too as a weekend day: shaded on the calendar, out of the business days; then back to Saturday and Sunday.
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('Definitions', { selector: 'button' })
  await p.clickText('Holidays', { selector: 'main [role=group] button' })
  await p.waitFor(`document.querySelector('[data-weekend-days]') !== null`, 10000, 'weekend days')
  await p.mouseClick('[data-weekend-days] input[data-weekday="5"]')
  await p.waitFor(`document.querySelector('[data-weekend-days] input[data-weekday="5"]').checked`, 10000, 'Friday checked')
  await sleep(300)
  const novFriday = await novDays()
  await p.clickText('Calendar', { selector: 'nav button' })
  await sleep(1000)
  await toDecember()
  const weekendFriday = await weekendCells()
  await shot(p, '24h2-calendar-friday-weekend.png')
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('Definitions', { selector: 'button' })
  await p.clickText('Holidays', { selector: 'main [role=group] button' })
  await p.waitFor(`document.querySelector('[data-weekend-days]') !== null`, 10000, 'weekend days again')
  await p.mouseClick('[data-weekend-days] input[data-weekday="5"]')
  await p.mouseClick('[data-weekend-days] input[data-weekday="6"]')
  await p.waitFor(`!document.querySelector('[data-weekend-days] input[data-weekday="6"]').checked`, 10000, 'Saturday unchecked')
  await sleep(300)
  const novSundayOnly = await novDays()
  await p.mouseClick('[data-weekend-days] input[data-weekday="6"]')
  await p.waitFor(`document.querySelector('[data-weekend-days] input[data-weekday="6"]').checked`, 10000, 'Saturday checked again')
  await sleep(300)
  // November 2025: 20 days Monday to Friday; Friday off: 16 left (4 Fridays), less the 11th and the 27th; Sunday alone: 25, less the 11th and the 27th.
  check('more or fewer weekend days: the calendar shades them and the business days follow', weekendBefore === 10 && weekendFriday === 15 && novFriday === 14 && novSundayOnly === 23 && (await novDays()) === 18,
    `${weekendBefore} then ${weekendFriday} shaded days; ${novFriday} and ${novSundayOnly} business days`)
  // Unchecked: not on the calendar any more, business days again.
  await p.mouseClick('[data-holiday-calendar="us"] input[type=checkbox]')
  await p.waitFor(`!document.querySelector('[data-holiday-calendar="us"] input[type=checkbox]').checked`, 10000, 'the United States unchecked')
  check('unchecked: its holidays are business days again', (await novDays()) === 19 && !(await p.engine('holidays.list', { from: '2025-11-01', to: '2025-11-30' })).some((x) => x.location.startsWith('United States')))
  // Deleting Canada says that Québec goes with it (not done).
  await p.mouseClick('[data-holiday-calendar="ca"] button[aria-label^="Delete"]')
  await p.waitFor(`document.querySelector('[role=dialog]') !== null`, 5000, 'delete Canada asked')
  check('deleting a country says the calendars under it go too', /Québec, under it, is deleted too/.test(await p.eval(`document.querySelector('[role=dialog]').innerText`)))
  await p.clickText('Cancel', { selector: '[role=dialog] button' })
  await sleep(300)
  // France deleted with its button, then added again from the choices of a new line, with its address.
  await p.mouseClick('[data-holiday-calendar="fr"] button[aria-label^="Delete"]')
  await p.clickText('Delete', { selector: '[role=dialog] button' })
  await p.waitFor(`document.querySelector('[data-holiday-calendar="fr"]') === null`, 10000, 'France deleted')
  check('a calendar deleted with its delete icon', (await p.engine('holidays.calendars')).length === 3)
  await p.clickText('Add calendar', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-calendar-dialog] [data-field=url]') !== null`, 5000, 'add calendar dialog')
  await p.mouseClick('[data-calendar-dialog] [data-field=location]')
  await sleep(400)
  const holidayChoices = await p.eval(`[...document.querySelectorAll('[role=option]')].map(o => o.innerText).join()`)
  const prefilled = await p.eval(`[document.querySelector('[data-calendar-dialog] [data-field=location]').value, document.querySelector('[data-calendar-dialog] [data-field=url]').value]`)
  check('a new calendar: from the Add calendar button, in a dialog offering the calendars to start with that are not in the table, with their address', holidayChoices === 'France' && prefilled[0] === 'France'
    && prefilled[1].startsWith('https://calendrier.api.gouv.fr/') && (await p.eval(`document.querySelector('[data-holiday-calendars] tbody .MuiOutlinedInput-root') === null`)), `${holidayChoices} ${prefilled.join(' ')}`)
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await sleep(200)
  await shot(p, '24h3-holidays-new-line.png')
  await p.clickText('Add', { selector: '[data-calendar-dialog] button' })
  await p.waitFor(`document.querySelectorAll('[data-holiday-calendar]').length === 4`, 10000, 'France added again')
  await p.waitFor(holidaysIdle, 120000, 'France downloaded')
  await sleep(300)
  check('added again: downloaded at once, its holidays counted', (await holidayRows()).at(-1).perYear === 11)
  // A province of one's own under Canada, with the address of a web page: set under Canada, refused cleanly under it.
  await p.clickText('Add calendar', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-calendar-dialog] [data-field=url]') !== null`, 5000, 'another add calendar dialog')
  await typeInto('[data-calendar-dialog] [data-field="location"]', 'Ontario')
  await typeInto('[data-calendar-dialog] [data-field="url"]', 'https://www.opm.gov/policy-data-oversight/pay-leave/federal-holidays/')
  await p.mouseClick('[data-calendar-dialog] [data-field="parent"]')
  await p.clickText('Canada (Federal)', { selector: '[role=option]' })
  await p.clickText('Add', { selector: '[data-calendar-dialog] button' })
  await p.waitFor(`document.querySelectorAll('[data-holiday-calendar]').length === 5`, 10000, 'own calendar added')
  await p.waitFor(holidaysIdle, 120000, 'own calendar checked')
  await sleep(300)
  hr = await holidayRows()
  const ontario = hr.find((r) => r.location === 'Ontario')
  check('a province added under Canada, after Québec; a wrong address: the reason shown under it', ontario?.parent === 'ca' && hr.map((r) => r.location).slice(0, 3).join() === 'Canada (Federal),Québec,Ontario'
    && /web page, not a calendar file/.test(ontario?.error ?? ''), JSON.stringify(ontario))
  await shot(p, '24i-holidays-wrong-address.png')
  // The same place twice: said in the dialog.
  await p.clickText('Add calendar', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-calendar-dialog] [data-field=url]') !== null`, 5000, 'add calendar dialog again')
  await typeInto('[data-calendar-dialog] [data-field="location"]', 'france')
  await typeInto('[data-calendar-dialog] [data-field="url"]', 'https://example.org/holidays.ics')
  await p.clickText('Add', { selector: '[data-calendar-dialog] button' })
  await p.waitFor(`document.querySelector('[data-calendar-dialog] [data-holidays-error]') !== null`, 10000, 'the same place refused')
  check('the same place twice: refused in the dialog', (await p.eval(`document.querySelector('[data-calendar-dialog] [data-holidays-error]').innerText`)) === 'France is already in the table.')
  await p.clickText('Cancel', { selector: '[data-calendar-dialog] button' })
  await sleep(300)
  // Ontario's pencil: the same dialog; taken out of Canada, it becomes a country of its own, after the others.
  const ontarioId = (await p.engine('holidays.calendars')).find((c) => c.location === 'Ontario').id
  await p.mouseClick(`[data-holiday-calendar="${ontarioId}"] [data-row-edit]`)
  await p.waitFor(`document.querySelector('[data-calendar-dialog] [data-field=url]') !== null`, 5000, 'edit calendar dialog')
  const editTitle = await p.eval(`document.querySelector('[data-calendar-dialog] h2').innerText`)
  await p.mouseClick('[data-calendar-dialog] [data-field="parent"]')
  await p.clickText('No country', { selector: '[role=option]' })
  await p.clickText('Save', { selector: '[data-calendar-dialog] button' })
  await p.waitFor(`document.querySelector('[data-calendar-dialog]') === null && !document.querySelector('[data-holiday-calendar="${ontarioId}"]').dataset.parent`, 10000, 'Ontario out of Canada')
  hr = await holidayRows()
  check('a calendar\'s pencil: the same dialog; out of Canada, Ontario is a country of its own', editTitle === 'Edit Ontario' && hr.at(-1).location === 'Ontario' && hr.at(-1).parent === null, hr.map((r) => r.location).join())
  await p.engine('holidays.remove', { id: ontarioId })
  // Analytics: the net salary per hour and per year, worked out from the business days, in the display currency.
  const decReport = await p.engine('periods.report', { from: '2025-12-01', to: '2025-12-31', granularity: 'month' })
  const sal = decReport.salary
  const salaryOk = sal && sal.workingDays > 0 && sal.hoursPerDay === 8 && Math.abs(sal.perDay - sal.net / sal.workingDays) < 0.006 && Math.abs(sal.perHour - sal.net / (sal.workingDays * 8)) < 0.006 && Math.abs(sal.perYear - sal.net / sal.workingDays * sal.yearWorkingDays) < 0.006
  await p.clickText('Statements', { selector: 'nav button' })
  await p.clickText('Analytics', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('section[data-period="2025-12-01"] [data-salary]') !== null`, 20000, 'the salary in December')
  await p.eval(`document.querySelector('section[data-period="2025-12-01"]').scrollIntoView({ block: 'start' })`)
  await sleep(600)
  const salaryShown = await p.eval(`(() => { const s = document.querySelector('section[data-period="2025-12-01"] [data-salary]'); const v = (a) => s.querySelector('[' + a + ']')?.innerText.replace(/\\s+/g, ' ').trim(); return { day: v('data-salary-day'), hour: v('data-salary-hour'), year: v('data-salary-year') } })()`)
  check('Analytics: the net salary per working day, per hour and per year', salaryOk && salaryShown.day === `Net salary per day ${moneyCad(sal.perDay)}` && salaryShown.hour === `Per hour ${moneyCad(sal.perHour)}` && salaryShown.year === `Per year ${moneyCad(sal.perYear)}`,
    `${JSON.stringify(salaryShown)} ${JSON.stringify(sal)}`)
  await shot(p, '24j-analytics-salary.png')
  await p.engine('settings.set', { key: 'currency', value: 'EUR' })
  const salEur = (await p.engine('periods.report', { from: '2025-12-01', to: '2025-12-31', granularity: 'month' })).salary
  check('the salary in the display currency (euros)', salEur && salEur.net < sal.net * 0.75 && salEur.net > sal.net * 0.55 && salEur.businessDays === sal.businessDays, `${salEur?.net} EUR, ${sal.net} CAD`)
  await p.engine('settings.set', { key: 'currency', value: 'CAD' })
  await p.clickText('Statements', { selector: 'nav button' })
  await p.clickText('Statements', { selector: 'nav button' })
  await sleep(600)

  // Clean up the test tags: off the category, then deleted (they leave every operation).
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('Definitions', { selector: 'button' })
  await p.clickText('Tags', { selector: 'main [role=group] button' })
  await p.waitFor(`document.querySelector('[data-tag-row="Trip (test)"]') !== null`, 5000, 'tags list')
  await shot(p, '22f-tags-list.png')
  check('Tags page: categories carrying each tag, operations counted', await p.eval(`(() => { const r = document.querySelector('[data-tag-row="Work (test)"]').innerText; return r.includes('Restaurants & cafés') && r.trim().endsWith('1') })()`))
  for (const name of ['Trip (test)', 'Work (test)', 'Family (test)']) {
    await p.eval(`document.querySelector('[data-tag-row="${name}"] button[aria-label="Delete"]').click()`)
    await p.waitFor(`document.querySelector('[role=dialog]') !== null`, 5000, 'delete tag')
    await p.clickText('Delete', { selector: '[role=dialog] button' })
    await p.waitFor(`document.querySelector('[data-tag-row="${name}"]') === null`, 5000, 'deleted ' + name)
  }
  const afterTags = await p.engine('app.init')
  const augClean = await p.engine('periods.summary', { from: '2025-08-01', to: '2025-08-31', granularity: 'month', currency: 'CAD' })
  check('test tags deleted: off every operation and category', afterTags.tags.tags.length === 0 && Object.keys(afterTags.tags.categoryTags).length === 0
    && augClean.transactions.every((t) => t.tags.length === 0) && augClean.tagPie.length === 0)
  await p.clickText('Statements', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('section h2') !== null`, 20000, 'periods again')

  // custom period
  await p.scrollMain(0)
  await p.clickText('Custom', { selector: 'button' })
  await p.waitFor(`document.querySelector('section h2')?.innerText.startsWith('From')`, 20000, 'custom title')
  await sleep(1000)
  await shot(p, '12g-custom-period.png')
  check('custom period with its days and business days', /^\d+ days · \d+ business days$/.test(await p.eval(`document.querySelector('section [data-days]')?.innerText ?? ''`)))

  // Ctrl+F
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'f', code: 'KeyF', modifiers: 2, windowsVirtualKeyCode: 70 })
  await p.waitFor(`document.querySelector('[data-findbar]') !== null`, 5000, 'find bar')
  await p.send('Input.insertText', { text: 'Expenses' })
  await sleep(400)
  await shot(p, '12h-find.png')
  check('Ctrl+F finds text in the window', await p.eval(`CSS.highlights.get('find')?.size ?? 0`) > 0)
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await p.clickText('Month', { selector: 'button' })
  await sleep(600)

  // year and all time
  await p.clickText('Year', { selector: 'button' })
  await p.waitFor(`document.querySelector('section h2')?.innerText === '2025'`, 20000, 'year title')
  await sleep(1200)
  await shot(p, '13-year.png')
  await p.clickText('All time', { selector: 'button' })
  await sleep(1500)
  await shot(p, '14-all-time.png')
  const today = new Date()
  const allTime = await p.eval(`document.querySelector('section h2')?.innerText ?? ''`)
  check('all time runs from the first operation to today', allTime.startsWith('From') && allTime.endsWith(`${today.getDate()}, ${today.getFullYear()}`) && !(await text(p)).includes('Several years'), allTime)
  await p.clickText('Month', { selector: 'button' })
  await sleep(800)

  // export the first month to PDF: one Export at the top of each period, in every view; the parts to export ticked
  check('one Export at the top of each period, none at the bottom', await p.eval(`(() => { const s = [...document.querySelectorAll('main section[data-period]')].filter(x => x.querySelector('h2')); return s.length > 0 && s.every(x => { const b = x.querySelector('[data-export]'); return !!b && b.getBoundingClientRect().top < x.querySelector('h2').getBoundingClientRect().bottom }) && ![...document.querySelectorAll('main button')].some(b => b.innerText === 'Export to PDF') })()`))
  await p.clickText('Analytics', { selector: 'nav button' })
  await sleep(600)
  check('Export in Analytics too', await p.eval(`!!document.querySelector('main section[data-period] [data-export]')`))
  await p.clickText('Statements', { selector: 'nav button' })
  await sleep(600)
  await p.clickText('Export', { selector: 'section button[data-export]', index: 0 })
  await p.waitFor(`document.querySelector('[role=dialog]')?.innerText.includes('File name')`, 10000, 'export dialog')
  await sleep(300)
  await shot(p, '15-export-dialog.png')
  const ticked = await p.eval(`Object.fromEntries([...document.querySelectorAll('[role=dialog] [data-export-part]')].map(c => [c.dataset.exportPart, c.querySelector('input')?.checked ?? c.checked]))`)
  check('the parts to export: everything but the summary at first', JSON.stringify(ticked) === JSON.stringify({ summary: false, statements: true, analytics: true, transfers: true }), JSON.stringify(ticked))
  // Nothing ticked: nothing to export.
  for (const part of ['statements', 'analytics', 'transfers']) await p.mouseClick(`[role=dialog] [data-export-part="${part}"] input`)
  check('nothing ticked, nothing to export', await p.eval(`[...document.querySelectorAll('[role=dialog] button')].find(b => b.innerText === 'Export').disabled`))
  for (const part of ['summary', 'statements', 'analytics', 'transfers']) await p.mouseClick(`[role=dialog] [data-export-part="${part}"] input`)
  await p.clickText('Export', { selector: '[role=dialog] button' })
  await p.waitFor(`document.body.innerText.includes('Report saved')`, 30000, 'report saved')
  const pdfs = readdirSync(reports).filter((f) => f.endsWith('.pdf'))
  check('PDF report written', pdfs.length === 1, pdfs.join(', '))

  // add operation dialog
  await p.clickText('Add operation')
  await sleep(500)
  await shot(p, '16-add-operation.png')
  check('a new operation goes in an account of a bank', await p.eval(`(() => { const d = document.querySelector('[role=dialog]'); return !d.innerText.includes('Cash & manual') && [...d.querySelectorAll('[role=combobox]')].some(c => c.innerText.includes(' · ')) })()`))
  // Entering exactly an operation the day already has: said while typing, not refused (two coffees happen).
  const firstAccount = (await p.engine('accounts.list'))[0]
  const todayIso = new Date().toLocaleDateString('en-CA')
  const coffee = await p.engine('tx.create', { input: { date: todayIso, amount: -4.75, brand: 'E2E Coffee', categoryId: 'restaurants', accountId: firstAccount.id, note: null } })
  const typeIn = async (selector, value) => {
    await p.eval(`(() => { const i = document.querySelector('${selector}'); i.focus(); i.select() })()`)
    await p.send('Input.insertText', { text: value })
    await sleep(150)
  }
  await p.eval(`(() => { const d = document.querySelector('[role=dialog]'); const sel = [...d.querySelectorAll('[role=combobox]')].find(c => c.innerText.includes(' · ')); return !!sel })()`)
  await typeIn('[role=dialog] input[inputmode=decimal]', '4.75')
  await typeIn('[role=dialog] .MuiAutocomplete-root input', 'E2E Coffee')
  await p.eval(`document.activeElement.blur()`)
  const sameShown = await p.waitFor(`document.querySelector('[data-same-operation]') !== null`, 5000, 'same operation warning').then(() => true, () => false)
  await shot(p, '16b-same-operation-warning.png')
  const addEnabled = await p.eval(`[...document.querySelectorAll('[role=dialog] button')].find(b => b.innerText === 'Add')?.disabled === false`)
  check('the same operation on the same day: a warning while typing, adding still possible', sameShown && addEnabled
    && (await p.eval(`document.querySelector('[data-same-operation]')?.innerText`)) === 'The same operation is already on this day.', `${sameShown} ${addEnabled}`)
  await p.clickText('Cancel', { selector: '[role=dialog] button' })
  await p.engine('tx.delete', { id: coffee.id })

  // calendar, settings
  await p.clickText('Calendar', { selector: 'nav button' })
  await sleep(1500)
  await shot(p, '17-calendar.png')
  await p.clickText('Settings', { selector: 'nav button' })
  await sleep(800)
  await shot(p, '19-settings-general.png')
  check('Statements, Analytics and Calendar inside Display, no Periods', await p.eval(`(() => { const g = document.querySelector('[data-group="Statements"]'); const a = document.querySelector('[data-group="Analytics"]'); const c = document.querySelector('[data-group="Calendar"]'); return !!g && !!a && !!c && !document.querySelector('[data-group="Periods"]') && g.parentElement === c.parentElement && a.parentElement === c.parentElement && g.nextElementSibling === a && a.nextElementSibling === c && g.parentElement.querySelector('h6')?.innerText === 'Display' && getComputedStyle(g.previousElementSibling).borderBottomWidth === '0px' && !!g.querySelector('input') && a.querySelectorAll('input[type=checkbox]').length === 3 })()`))
  await p.clickText('Bank Configuration', { selector: 'button' })
  await sleep(600)
  await shot(p, '19b-settings-bank-configuration.png')
  check('one tab per bank, accounts with type and currency', await p.eval(`document.querySelector('[data-accounts]')?.innerText.includes('Account Type')`))
  // Banks are added in Definitions > Bank only; accounts with the button of Bank Configuration and a dialog, not typed
  // into the tables.
  const mainButtons = () => p.eval(`[...document.querySelectorAll('main button')].map(b => b.innerText.trim())`)
  check('Bank Configuration: Add account, no Add bank', (await mainButtons()).includes('Add account') && !(await mainButtons()).includes('Add bank'))
  await p.clickText('Definitions', { selector: 'button' })
  await p.clickText('Bank', { selector: 'main [role=group] button' })
  await sleep(500)
  await p.clickText('Add bank', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-bank-dialog] [data-field="name"]') !== null`, 5000, 'add bank dialog')
  await typeInto('[data-bank-dialog] [data-field="name"]', 'Contoso Test Bank')
  await typeInto('[data-bank-dialog] [data-field="country"]', 'Canada')
  await shot(p, '19d-add-bank.png')
  await p.clickText('Add', { selector: '[role=dialog] button' })
  await p.waitFor(`document.querySelector('[data-bank-dialog]') === null`, 10000, 'bank added')
  check('bank added from Definitions', (await p.engine('banks.list')).some((b) => b.name === 'Contoso Test Bank' && b.country === 'Canada'))
  await p.clickText('Bank Configuration', { selector: 'button' })
  await sleep(500)
  await p.clickText('Contoso Test Bank', { selector: 'main button' })
  await p.waitFor(`document.body.innerText.includes('No accounts at Contoso Test Bank yet')`, 10000, 'new bank tab')
  await p.clickText('Add account', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-account-dialog] [data-field="name"]') !== null`, 5000, 'add account dialog')
  check('the account goes at the bank of the open tab', await p.eval(`document.querySelector('[data-account-dialog]').innerText.includes('Contoso Test Bank')`))
  await typeInto('[data-account-dialog] [data-field="name"]', 'Contoso chequing')
  await shot(p, '19e-add-account.png')
  await p.clickText('Add', { selector: '[role=dialog] button' })
  await p.waitFor(`[...document.querySelectorAll('[data-accounts] input')].some(i => i.value === 'Contoso chequing')`, 10000, 'new account row')
  const added = (await p.engine('accounts.list')).find((a) => a.name === 'Contoso chequing')
  const contoso = (await p.engine('banks.list')).find((b) => b.name === 'Contoso Test Bank')
  check('account added at its bank', added?.bankId === contoso?.id && added?.kind === 'bank' && added?.currency === 'CAD', JSON.stringify(added))
  await shot(p, '19f-account-added.png')
  await p.clickText('Definitions', { selector: 'button' })
  check('definitions in order Bank, Exchange rates, Brands, Categories, Tags, Holidays', (await p.eval(`[...document.querySelectorAll('main [role=group]')][1].innerText`)).split(/\s+/).join(' ') === 'Bank Exchange rates Brands Categories Tags Holidays')
  // One brand learned for three statement wordings (three operations renamed to it by hand).
  const relabel = (await p.engine('periods.report', { from: '2025-08-01', to: '2025-12-31', granularity: 'range' })).banks
    .flatMap((b) => b.summary.transactions).filter((t) => t.amount < 0 && t.rawLabel && t.categoryId !== 'transfers')
  const distinct = [...new Map(relabel.map((t) => [t.rawLabel.split(' ')[0], t])).values()].slice(0, 3)
  for (const t of distinct) await p.engine('tx.setBrand', { id: t.id, brand: 'Voyages Exemple' })
  await p.clickText('Brands', { selector: 'main button' })
  await sleep(1000)
  await shot(p, '18-brands.png')
  const learnedText = await p.eval(`document.querySelector('[data-learned]')?.innerText ?? ''`)
  const learnedBrands = await p.eval(`[...document.querySelectorAll('[data-learned] tbody tr')].map(r => r.children[2].innerText)`)
  check('each learned brand listed once', !learnedText.includes('Every operation of this brand') && new Set(learnedBrands).size === learnedBrands.length, learnedBrands.join(', '))
  const wordings = await p.eval(`[...document.querySelectorAll('[data-learned] tbody tr')].map(r => r.children[1].innerText.trim())`)
  check('every learned correction shows its statement wordings', wordings.every((w) => w.length > 1 && w !== '—'), wordings.join(' | '))
  // The wordings one after the other, comma-separated, a line breaking only between two of them.
  await p.eval(`document.querySelector('[data-learned]').scrollIntoView({ block: 'center' })`)
  await sleep(400)
  await shot(p, '18b-learned-wordings.png')
  const wordingCells = await p.eval(`[...document.querySelectorAll('[data-learned] [data-wordings]')].map(c => ({ text: c.innerText, parts: [...c.children].map(x => getComputedStyle(x).whiteSpace) }))`)
  check('learned wordings: comma-separated, each kept whole on its line', wordingCells.length > 0 && wordingCells.every((c) => c.parts.every((w) => w === 'nowrap') && (c.parts.length < 2 || c.text.includes(', '))), JSON.stringify(wordingCells.slice(0, 3)))
  await p.clickText('Bank', { selector: 'button' })
  await sleep(600)
  await shot(p, '19c-settings-banks.png')
  check('banks table', await p.eval(`document.querySelector('[data-banks]')?.innerText.includes('Banking System')`))
  check('every bank shows its date format', await p.eval(`(() => { const t = document.querySelector('[data-banks]'); const rows = t.querySelectorAll('tbody tr').length; return t.innerText.includes('Date Format') && rows > 0 && [...t.querySelectorAll('tbody tr')].every(r => /DD\\/MM\\/YYYY|MM\\/DD\\/YYYY|YYYY-MM-DD/.test(r.innerText)) })()`))
  check('no row to type a new bank into', await p.eval(`document.querySelector('[data-new-bank]') === null && !document.querySelector('[data-banks]').innerText.includes('New bank')`))
  await p.clickText('Add bank', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-bank-dialog] [data-field="name"]') !== null`, 5000, 'add bank dialog')
  await typeInto('[data-bank-dialog] [data-field="name"]', 'Fabrikam Test Bank')
  await p.clickText('Add', { selector: '[role=dialog] button' })
  await p.waitFor(`[...document.querySelectorAll('[data-banks] input')].some(i => i.value === 'Fabrikam Test Bank')`, 10000, 'new bank row')
  check('bank added from Definitions with the button', true)
  check('banking systems named by how cards work', await p.eval(`document.querySelector('[data-banks]').innerText.includes('Credit card system') && !/North America|France/.test([...document.querySelectorAll('[data-banks] [role=combobox]')].map(c => c.innerText).join())`))
  await p.clickText('Categories', { selector: 'main button' })
  await p.waitFor(`document.querySelector('[data-categories]') !== null`, 5000, 'categories in settings')
  const labels = await p.eval(`[...document.querySelectorAll('[data-section-label] input')].map(i => i.value)`)
  check('categories under the three kinds of operation, no Exceptional', labels.join() === 'Expenses,Income,Transfers' && !(await text(p)).includes('xceptional'), labels.join(', '))
  // A label is renamed in place, like a category.
  await p.eval(`(() => { const i = document.querySelector('[data-section-label="transfer"] input'); i.focus(); i.select() })()`)
  await p.send('Input.insertText', { text: 'Between accounts' })
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 })
  await sleep(400)
  await shot(p, '19d-settings-categories.png')
  check('label renamed', (await p.engine('app.init')).settings['section.transfer'] === 'Between accounts')
  await p.engine('settings.set', { key: 'section.transfer', value: 'Transfers' })

  // ---- other formats, read by the app itself (drafts discarded after): a picture of a statement page, OFX / QFX,
  // QIF and a web page saved as ".xls".
  const pdfName = readdirSync(samples).find((f) => f.startsWith('Northwind Bank') && f.endsWith('.pdf'))
  const pdfDraft = await p.engine('import.parseBytes', { name: pdfName, base64: readFileSync(join(samples, pdfName)).toString('base64') })
  const page = await p.engine('import.page', { draftId: pdfDraft.id, page: 0, width: 1700 })
  // Only where the machine has text recognition (Windows with a recognition language, Linux with Tesseract, macOS).
  let photo = null
  if ((await p.engine('app.init')).ocrAvailable) {
    photo = await p.engine('import.parseBytes', { name: 'statement photo.png', base64: page.png })
    check('a picture of a statement is read like a scan', photo.usedOcr && photo.rows.length === pdfDraft.rows.length && photo.status === pdfDraft.status
      && cents(photo.rows.reduce((s, r) => s + r.amount, 0)) === cents(pdfDraft.rows.reduce((s, r) => s + r.amount, 0)), `${photo.rows.length}/${pdfDraft.rows.length} lines, ${photo.status}`)
  } else console.log('SKIP  a picture of a statement is read like a scan  — no text recognition on this machine')
  const ofx = 'OFXHEADER:100\r\nDATA:OFXSGML\r\n\r\n<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>CAD<BANKACCTFROM><ACCTID>0000123456<ACCTTYPE>CHECKING</BANKACCTFROM><BANKTRANLIST>'
    + '<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260302<TRNAMT>-4.85<FITID>1<NAME>TIM HORTONS #2345</STMTTRN>'
    + '<STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260305<TRNAMT>2140.55<FITID>2<NAME>PAYROLL DEPOSIT EXAMPLE</STMTTRN>'
    + '</BANKTRANLIST><LEDGERBAL><BALAMT>3135.70<DTASOF>20260331</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>'
  const ofxDraft = await p.engine('import.parseBytes', { name: 'activity.qfx', base64: Buffer.from(ofx).toString('base64') })
  const qif = '!Type:Bank\nD03/02/2026\nT-4.85\nPTIM HORTONS #2345\n^\nD03/05/2026\nT2,140.55\nPPAYROLL DEPOSIT EXAMPLE\n^\n'
  const qifDraft = await p.engine('import.parseBytes', { name: 'activity.qif', base64: Buffer.from(qif).toString('base64') })
  const html = '<html><body><table><tr><td>Date</td><td>Description</td><td>Amount</td><td>Balance</td></tr><tr><td>2026-03-02</td><td>TIM HORTONS #2345</td><td>-4.85</td><td>995.15</td></tr>'
    + '<tr><td>2026-03-05</td><td>PAYROLL DEPOSIT EXAMPLE</td><td>2140.55</td><td>3135.70</td></tr></table></body></html>'
  const xlsDraft = await p.engine('import.parseBytes', { name: 'activity.xls', base64: Buffer.from(html).toString('base64') })
  const two = (d) => d.rows.length === 2 && d.rows[0].amount === -4.85 && d.rows[1].amount === 2140.55 && d.rows[0].date === '2026-03-02'
  check('OFX / QFX, QIF and ".xls" web pages read by the app', two(ofxDraft) && ofxDraft.closing === 3135.70 && two(qifDraft) && two(xlsDraft) && xlsDraft.status === 'verified',
    `${ofxDraft.method} ${qifDraft.method} ${xlsDraft.method} ${xlsDraft.status}`)
  for (const d of [pdfDraft, photo, ofxDraft, qifDraft, xlsDraft].filter(Boolean)) await p.engine('import.discard', { draftId: d.id })

  // dark theme, from the icon above the settings
  await p.click('nav [aria-label="Dark mode"]')
  await sleep(600)
  check('theme toggled from the side bar', (await p.engine('app.init')).settings.theme === 'dark')
  await p.clickText('Statements', { selector: 'nav button' })
  await sleep(1500)
  await shot(p, '20-dark-periods.png')
  await p.eval(`(() => { const s = document.querySelector('section'); const t = [...s.querySelectorAll('p, span')].find(e => e.innerText === 'Expenses'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 160 }) })()`)
  await sleep(800)
  await shot(p, '21-dark-totals.png')
  await p.clickText('Analytics', { selector: 'nav button' })
  await sleep(1200)
  await shot(p, '21b-dark-charts.png')
  await analyticsPart(p, 'Transfers')
  await p.waitFor(`document.querySelector('section [data-flowchart]') !== null`, 20000, 'dark transfers')
  await p.eval(`(() => { const f = document.querySelector('section [data-transfer-timeline]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await sleep(700)
  await shot(p, '21b2-dark-transfers-timeline.png')
  await p.eval(`(() => { const f = document.querySelector('section [data-flowchart]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await sleep(800)
  await shot(p, '21c-dark-money-moved.png')
  await p.clickText('Calendar', { selector: 'nav button' })
  await sleep(1200)
  await shot(p, '22-dark-calendar.png')
  await p.engine('settings.set', { key: 'theme', value: 'light' })

  // Every table: a pencil at the far right of each line, always shown; in a table that deletes, the delete button after
  // it, last.
  const pencils = () => p.eval(`[...document.querySelectorAll('main table tbody tr')].filter(r => r.querySelector('[data-row-edit]')).map(r => {
    const buttons = [...r.querySelectorAll('button')].filter(b => b.getBoundingClientRect().width > 0)
    const edit = r.querySelector('[data-row-edit]'), del = r.querySelector('[data-row-delete]')
    const right = Math.max(...buttons.map(b => b.getBoundingClientRect().right))
    const e = edit.getBoundingClientRect(), d = del?.getBoundingClientRect()
    return { shown: getComputedStyle(edit).opacity === '1' && getComputedStyle(edit).visibility === 'visible', order: !d || d.left >= e.right, last: Math.abs((d ?? e).right - right) < 1 }
  })`)
  const tables = {}
  const visit = async (name, go) => { await go(); await sleep(900); const rows = await pencils(); tables[name] = { rows: rows.length, ok: rows.length > 0 && rows.every((r) => r.shown && r.order && r.last) } }
  const definition = (tab) => async () => { await p.clickText('Settings', { selector: 'nav button' }); await p.clickText('Definitions', { selector: 'button' }); await sleep(300); await p.clickText(tab, { selector: 'main [role=group] button' }) }
  await visit('operations', async () => {
    await p.clickText('Statements', { selector: 'nav button' }); await sleep(800); await p.clickText('Statements', { selector: 'nav button' })
    await openPeriod(p, await p.eval(`document.querySelector('section[data-period]').dataset.period`))
  })
  await visit('accounts', async () => { await p.clickText('Settings', { selector: 'nav button' }); await p.clickText('Bank Configuration', { selector: 'button' }) })
  await visit('banks', definition('Bank'))
  await visit('rates', definition('Exchange rates'))
  await visit('brands', definition('Brands'))
  await visit('categories', definition('Categories'))
  // (the test tags are deleted by now: one made for the check, deleted after)
  const pencilTag = (await p.engine('tags.create', { name: 'Pencil check', color: null })).tags.find((t) => t.name === 'Pencil check')
  await reload(p)
  await p.waitFor(`document.querySelector('nav') !== null`, 20000, 'reloaded with the tag')
  await visit('tags', definition('Tags'))
  await p.engine('tags.delete', { id: pencilTag.id })
  await visit('holidays', definition('Holidays'))
  await visit('statements', async () => { await p.clickText('Import', { selector: 'nav button' }) })
  check('every table: the pencil at the far right of each line, always shown, before the delete button, which is last', Object.values(tables).every((t) => t.ok), JSON.stringify(tables))
  // The pencils of the tables without one before: each opens its dialog on the line.
  const opens = {}
  const openEdit = async (name, go, dialog) => {
    await go(); await sleep(800)
    await p.mouseClick('main table tbody tr [data-row-edit]')
    await p.waitFor(`document.querySelector('${dialog}') !== null`, 5000, name + ' dialog')
    opens[name] = (await p.eval(`document.querySelector('${dialog} h2').innerText`)).startsWith('Edit')
    await p.clickText('Cancel', { selector: `${dialog} button` })
    await sleep(300)
  }
  await openEdit('bank', definition('Bank'), '[data-bank-dialog]')
  await openEdit('brand', definition('Brands'), '[data-brand-dialog]')
  await openEdit('account', async () => { await p.clickText('Settings', { selector: 'nav button' }); await p.clickText('Bank Configuration', { selector: 'button' }) }, '[data-account-dialog]')
  await openEdit('statement', async () => { await p.clickText('Import', { selector: 'nav button' }) }, '[data-statement-dialog]')
  check('the pencils of banks, brands, accounts and statements open their dialog on the line', Object.values(opens).every(Boolean) && Object.keys(opens).length === 4, JSON.stringify(opens))
  // A brand renamed from its pencil: every operation of it takes the new name.
  const brandBefore = (await p.engine('brands.list')).brands[0]
  await definition('Brands')(); await sleep(800)
  await p.eval(`[...document.querySelectorAll('main table tbody tr')].find(r => r.querySelector('[data-row-edit]')?.getAttribute('aria-label') === ${JSON.stringify('Edit ' + brandBefore.brand)}).querySelector('[data-row-edit]').click()`)
  await p.waitFor(`document.querySelector('[data-brand-dialog] [data-field=name]') !== null`, 5000, 'brand dialog')
  await sleep(300)
  await shot(p, '25b-brand-dialog.png')
  check('the brand dialog: brand and category as the same kind of field (a brand list, a category list)', await p.eval(`(() => { const d = document.querySelector('[data-brand-dialog]'); return !!d.querySelector('.MuiAutocomplete-root input[data-field=name]') && d.querySelectorAll('.MuiOutlinedInput-root').length === 2 && /Brand Name/.test(d.innerText) && /Category/.test(d.innerText) && !d.querySelector('.MuiChip-root') })()`),
    await p.eval(`(() => { const d = document.querySelector('[data-brand-dialog]'); return JSON.stringify({ auto: !!d.querySelector('.MuiAutocomplete-root input[data-field=name]'), outlined: d.querySelectorAll('.MuiOutlinedInput-root').length, labels: [...d.querySelectorAll('label')].map(l => l.textContent.trim()), chips: d.querySelectorAll('.MuiChip-root').length }) })()`))
  await typeInto('[data-brand-dialog] [data-field="name"]', brandBefore.brand + ' Renamed')
  await p.clickText('Save', { selector: '[data-brand-dialog] button' })
  await p.waitFor(`document.querySelector('[data-brand-dialog]') === null`, 10000, 'brand saved')
  await sleep(500)
  const renamed = (await p.engine('brands.list')).brands.find((b) => b.brand === brandBefore.brand + ' Renamed')
  check('a brand renamed from its pencil: every operation of it renamed', renamed?.count === brandBefore.count && !(await p.engine('brands.list')).brands.some((b) => b.brand === brandBefore.brand), `${brandBefore.brand} (${brandBefore.count}) -> ${renamed?.brand} (${renamed?.count})`)
  await p.engine('brands.rename', { from: brandBefore.brand + ' Renamed', to: brandBefore.brand })
  await shot(p, '25-brands-pencils.png')

  // The downloaded rates and holidays: files in their own format next to the database, in downloads/<kind>/<source>/
  // <day of the download>/<what it holds>.<format>; two downloads at most per source.
  const listing = (dir) => existsSync(dir) ? readdirSync(dir).sort() : []
  const downloads = join(data, 'downloads')
  const layout = {}
  for (const kind of ['exchange-rates', 'holidays'])
    for (const source of listing(join(downloads, kind)))
      layout[`${kind}/${source}`] = listing(join(downloads, kind, source)).map((day) => `${day}: ${listing(join(downloads, kind, source, day)).join(' ')}`)
  const dayName = /^\d{4}-\d{2}-\d{2}$/
  const okLayout = ['exchange-rates/european-central-bank', 'exchange-rates/bank-of-canada', 'exchange-rates/us-federal-reserve', 'holidays/canada-federal', 'holidays/quebec', 'holidays/united-states-federal', 'holidays/france'].every((k) => layout[k]?.length >= 1 && layout[k].length <= 2)
    && Object.entries(layout).every(([k, copies]) => copies.every((c) => dayName.test(c.split(':')[0]) && c.split(': ')[1].split(' ').every((f) => (k.startsWith('exchange-rates') ? /^rates\.(zip|csv|json)$/ : /^holidays(-\d{4})?\.(json|ics)$/).test(f))))
    && layout['exchange-rates/european-central-bank'][0].endsWith('rates.zip') && layout['exchange-rates/bank-of-canada'][0].endsWith('rates.json') && layout['exchange-rates/us-federal-reserve'][0].endsWith('rates.csv')
    && layout['holidays/quebec'][0].includes('holidays-2026.json') && layout['holidays/united-states-federal'][0].endsWith('holidays.ics')
  check('the downloads: the files of the banks and calendars as downloaded, in downloads/<kind>/<source>/<day>/, plain standard names, two copies at most', okLayout, JSON.stringify(layout).slice(0, 900))
  // The database moved to another folder: its downloads go with it, the rates and holidays still in use.
  const movedTo = join(out, 'moved-database')
  mkdirSync(movedTo, { recursive: true })
  await p.engine('data.moveDatabase', { folder: movedTo })
  const afterMove = await p.engine('periods.report', { from: '2025-10-01', to: '2025-10-31', granularity: 'month' })
  check('the database moved: its downloads moved with it, the rates and holidays still used from there', existsSync(join(movedTo, 'downloads', 'exchange-rates', 'bank-of-canada')) && !existsSync(downloads)
    && afterMove.conversion.lines.length > 0 && afterMove.conversion.lines.every((l) => l.unconverted === 0) && afterMove.businessDays === 22, `${afterMove.businessDays} business days`)
  await p.engine('data.moveDatabase', { folder: data })

  // ---- Four banks in three currencies, Wise between them (October 2026): every statement imported into a new account
  // at its bank; transfers into Wise, between its currencies and out of it, inside each bank, and from France to Canada
  // directly are one movement each; the money moved drawn readable, the month and the year.
  const fourDir = join(samples, 'four banks')
  const fourList = JSON.parse(readFileSync(join(fourDir, 'accounts.json'), 'utf8'))
  const camel = (v) => v[0].toLowerCase() + v.slice(1)
  const bankIdOf = {}
  for (const b of await p.engine('banks.list')) bankIdOf[b.name] = b.id
  for (const f of fourList) {
    if (!bankIdOf[f.bank]) {
      const banks = await p.engine('banks.save', { bank: { id: 0, name: f.bank, country: f.country, system: camel(f.system), currency: f.currency } })
      bankIdOf[f.bank] = banks.find((b) => b.name === f.bank).id
    }
    const draft = await p.engine('import.parseBytes', { name: f.file, base64: readFileSync(join(fourDir, f.file)).toString('base64') })
    await p.engine('import.choose', { draftId: draft.id, kind: camel(f.kind), bankId: bankIdOf[f.bank] })
    await p.engine('import.commit', { draftId: draft.id, newAccountName: f.account, currency: f.currency, kind: camel(f.kind), bankId: bankIdOf[f.bank] })
  }
  const fourOct = await p.engine('periods.report', { from: '2026-10-01', to: '2026-10-31', granularity: 'month' })
  const moves = fourOct.global.movements
  const fourMoved = (from, to) => moves.some((m) => m.from === from && m.to === to)
  check('four banks with Wise: into Wise, between its currencies, out of it, and from France to Canada directly, each one movement',
    fourMoved('Northwind chequing', 'Wise CAD') && fourMoved('Wise CAD', 'Wise EUR') && fourMoved('Wise EUR', 'Compte courant') && fourMoved('Contoso checking', 'Wise USD')
    && fourMoved('Wise USD', 'Wise CAD') && fourMoved('Compte courant', 'Northwind chequing') && fourMoved('Northwind chequing', 'Northwind savings') && fourMoved('Compte courant', 'Livret A'),
    moves.map((m) => `${m.from} > ${m.to}`).join(' | '))
  const direct = moves.find((m) => m.from === 'Compte courant' && m.to === 'Northwind chequing')
  check('an international transfer: each side in its own currency, what the banks kept in the display currency', direct?.sent === 820 && direct?.sentCurrency === 'EUR'
    && direct?.received === 1296.26 && direct?.receivedCurrency === 'CAD' && direct?.lost > 0 && direct?.lost < 40, JSON.stringify(direct))
  check('four banks: the diagram has each bank holding its accounts, investments outside', fourOct.global.diagram.banks.length === 4
    && fourOct.global.diagram.nodes.filter((n) => !n.outside).length === 11 && fourOct.global.diagram.nodes.some((n) => n.outside), fourOct.global.diagram.banks.map((b) => b.name).join(', '))
  // Seen in the app, the month and the year.
  await p.clickText('Statements', { selector: 'nav button' })
  await p.clickText('Month', { selector: 'button' })
  await p.clickText('Analytics', { selector: 'nav button' })
  await analyticsPart(p, 'Transfers')
  const fourSec = 'section[data-period="2026-10-01"]'
  await p.waitFor(`document.querySelector('${fourSec}') !== null`, 20000, 'October 2026')
  await p.eval(`document.querySelector('${fourSec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${fourSec} [data-flow-card] [data-flowchart]') !== null`, 30000, 'four banks money moved')
  await p.eval(`(() => { const f = document.querySelector('${fourSec} [data-transfer-timeline]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await sleep(700)
  await shot(p, '25a0-four-banks-timeline.png')
  const fourLine = await timelineChecks(p, fourSec, fourOct.global.timeline, 'timeline of four banks')
  await shot(p, '25a1-four-banks-timeline-hover.png')
  check('four banks: the timeline has the four banks, movements inside them and between them, and what the banks kept', fourLine.banks.length === 4 && fourLine.kinds.includes('inside') && fourLine.kinds.includes('between') && fourLine.kinds.includes('outside') && /\$/.test(fourLine.totals.lost ?? ''), JSON.stringify(fourLine.totals))
  await p.eval(`(() => { const f = document.querySelector('${fourSec} [data-flow-card]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 })
  await sleep(900)
  await shot(p, '25a-four-banks-month.png')
  await p.eval(`document.querySelector('main').scrollBy({ top: 600 })`)
  await sleep(500)
  await shot(p, '25b-four-banks-month-lower.png')
  const legend = await p.eval(`document.querySelector('${fourSec} [data-flow-card] [data-lost]')?.innerText ?? ''`)
  check('the money-moved card says what was lost to exchange and fees on transfers between currencies', /Lost to exchange and fees/.test(legend) && /\$/.test(legend), legend)
  // The movements table: an international transfer written in both currencies.
  await p.clickText('Statements', { selector: 'nav button' })
  await sleep(900)
  await openPeriod(p, '2026-10-01')
  // The transfers of every bank, unfolded.
  await p.eval(`[...document.querySelectorAll('${fourSec} [data-section="movements"]:not([data-open]) [data-fold]')].forEach(b => b.click())`)
  await p.waitFor(`[...document.querySelectorAll('${fourSec} [data-section="movements"]')].every(m => m.querySelector('[data-movements] tbody tr'))`, 15000, 'October transfers open').catch(() => false)
  await sleep(600)
  const intl = await p.eval(`[...document.querySelectorAll('${fourSec} [data-international]')].map(e => e.innerText)`)
  check('the movements table writes an international transfer in both currencies', intl.some((t) => t.includes('€820.00 sent') && t.includes('$1,296.26 received')), intl.join(' | '))
  await p.clickText('Year', { selector: 'button' })
  await p.clickText('Analytics', { selector: 'nav button' })
  const yearSec = 'section[data-period="2026-01-01"]'
  await p.waitFor(`document.querySelector('${yearSec}') !== null`, 20000, 'year 2026')
  await p.eval(`document.querySelector('${yearSec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${yearSec} [data-flow-card] [data-flowchart]') !== null`, 30000, 'year money moved')
  await p.eval(`(() => { const f = document.querySelector('${yearSec} [data-transfer-timeline]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await sleep(700)
  await shot(p, '25c0-four-banks-year-timeline.png')
  const yearLine = await timelineChecks(p, yearSec, (await p.engine('periods.report', { from: '2026-01-01', to: '2026-12-31', granularity: 'year' })).global.timeline, 'timeline of a year')
  check('a year is drawn month by month', yearLine.ticks === 12, String(yearLine.ticks))
  await p.eval(`(() => { const f = document.querySelector('${yearSec} [data-flow-card]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 })
  await sleep(900)
  await shot(p, '25c-four-banks-year.png')
  await p.clickText('Month', { selector: 'button' })
  await p.clickText('Statements', { selector: 'nav button' })

  // ---- Logs: three columns, the date to the second, the type (debug, info, warning, error) and the details: who wrote
  // it and what ("Engine: tx.update"), everything about it (a request as received and its whole answer; an exception in
  // full), then always the full call stack of where it was written. The actions are written (what changes something),
  // the pages opened, messages and every failure; not clicks, keys or requests that only read. The latest first.
  // Clear logs is at the top of the page, beside the pages, and empties the file; the file can move.
  const logFile = join(data, 'app.log')
  check('Logs is not in the left menu: Open logs is in Settings > General', !(await p.eval(`[...document.querySelectorAll('nav button')].some(b => b.innerText.includes('Log'))`)))
  await openLogs(p)
  await p.waitFor(`document.querySelectorAll('[data-logs] tbody tr.row').length > 0`, 15000, 'log entries')
  await sleep(600)
  await shot(p, '26-logs.png')
  const logRows = () => p.eval(`[...document.querySelectorAll('[data-logs] tbody tr.row')].map(r => [...r.cells].map(c => c.innerText))`)
  const all = await p.engine('logs.read', { page: 0, size: 100 })
  const firstPage = await logRows()
  const headers = await p.eval(`[...document.querySelectorAll('[data-logs] thead th')].map(t => t.innerText.trim()).join()`)
  const typeNames = ['Debug', 'Info', 'Warning', 'Error']
  check('the log page lists the latest hundred entries in three columns: Date to the second, Type, Details',
    headers === 'Date,Type,Details' && firstPage.length === 100 && firstPage.every((r) => r.length === 3 && /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/.test(r[0]) && typeNames.includes(r[1])),
    headers + ' ' + JSON.stringify(firstPage.slice(0, 2)).slice(0, 600))
  // The file: one line per entry, three columns, and every entry ends with its call stack.
  const fileLines = readFileSync(logFile, 'utf8').split('\n').filter((l) => l.length > 0)
  const badLines = fileLines.filter((l) => { const f = l.split('\t'); return f.length !== 3 || !/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/.test(f[0]) || !['debug', 'info', 'warning', 'error'].includes(f[1]) || !f[2].includes('\\nCall stack:\\n') })
  check('the log file: one line per entry, date, type and details, every entry with its call stack', fileLines.length === all.total && badLines.length === 0,
    `${fileLines.length} lines, ${all.total} entries, ${badLines.length} not so: ${badLines[0]?.slice(0, 300)}`)
  const fullLog = []
  for (let i = 0; i * 1000 < all.total; i++) fullLog.push(...(await p.engine('logs.read', { page: i, size: 1000 })).entries)
  const find = (pred) => fullLog.find(pred)
  const heading = (e) => e.detail.split('\n')[0]
  const stackOf = (detail) => detail.slice(detail.lastIndexOf('\nCall stack:\n') + 13)
  // An action of the window to the engine: the request as received and the whole answer, not cut, then its call stack.
  const request = find((e) => e.level === 'info' && heading(e).startsWith('Engine: ') && e.detail.includes('\nrequest: {"id":'))
  const whole = request ? await p.engine('logs.entry', { n: request.n }) : null
  const answerText = whole ? whole.detail.slice(whole.detail.indexOf('\nanswer: ') + 9, whole.detail.lastIndexOf('\nCall stack:\n')) : ''
  let answer = null
  try { answer = JSON.parse(answerText) } catch { /* checked below */ }
  check('an engine action is written in full: who and what, when it started, the request as received, the whole answer, its call stack',
    !!whole && /^Engine: [a-z]+\.[A-Za-z]+\nstarted: \d{4}-/.test(whole.detail) && whole.detail.includes('\nrequest: {"id":') && answer?.ok === true && !whole.more && /^\s+at /.test(stackOf(whole.detail)),
    whole ? whole.detail.slice(0, 300) : 'none')
  check('requests that only read are not written', !find((e) => /^Engine: (periods\.report|periods\.index|logs\.read|tx\.get)$/.test(heading(e))),
    JSON.stringify([...new Set(fullLog.map(heading))].filter((h) => h.startsWith('Engine: ')).slice(0, 40)))
  // A failed request: its full exception, type, message and call stack.
  const failed = find((e) => heading(e).startsWith('Engine: ') && (e.level === 'warning' || e.level === 'error') && e.detail.includes('\nexception: '))
  const failedWhole = failed ? await p.engine('logs.entry', { n: failed.n }) : null
  check('a failed request is written with its full exception and call stack', !!failedWhole && /\nexception: System\.[A-Za-z.]+Exception: /.test(failedWhole.detail) && failedWhole.detail.includes('\n   at '), failedWhole?.detail.slice(0, 400))
  // A failure the engine handles itself (the wrong address of a download): its exception in full all the same.
  const caught = find((e) => heading(e) === 'App: Caught exception' && e.detail.includes('Update '))
  const caughtWhole = caught ? await p.engine('logs.entry', { n: caught.n }) : null
  check('an exception the engine handles (a download that failed) is written in full', !!caughtWhole && /\nexception: [A-Za-z.]+Exception: /.test(caughtWhole.detail) && caughtWhole.detail.includes('\n   at '), caughtWhole?.detail.slice(0, 400))
  // What the interface writes carries the interface's own call stack.
  const pageEntry = find((e) => heading(e) === 'App: page' && e.detail.includes('"to":"logs"'))
  const pageWhole = pageEntry ? await p.engine('logs.entry', { n: pageEntry.n }) : null
  check('an entry of the interface (a page opened) has the interface’s call stack', !!pageWhole && /^\s*(at .*|\S*@.*)(pageOpened|AppContext)/.test(stackOf(pageWhole.detail)), pageWhole?.detail.slice(0, 600))
  check('the log has every kind of entry: pages, messages, window events, actions, the start and the database',
    !!pageEntry && !!find((e) => heading(e) === 'App: toast')
    && !!find((e) => heading(e) === 'Window: CoreWebView2.NavigationStarting' || heading(e) === 'Window: Load') && !!find((e) => heading(e) === 'App: Start' && e.detail.includes('version: '))
    && !!find((e) => heading(e) === 'App: Database.Open'),
    JSON.stringify([...new Set(fullLog.map(heading))].slice(0, 60)))
  check('no clicks, keys or scrolls in the log', !find((e) => /^You: (click|keydown|scroll|drag|change)$/.test(heading(e))))
  check('more than a page: pages to go through', await p.eval(`document.querySelectorAll('[data-logs] .MuiPagination-root').length === 2`) && all.total > 100, String(all.total))
  // A long entry: its first six lines, Show all shows it whole (read again from the file when the page read it cut).
  const longRow = `[...document.querySelectorAll('[data-logs] tbody tr.row')].find(r => [...r.querySelectorAll('button')].some(b => b.innerText === 'Show all'))`
  const shownBefore = await p.eval(`(() => { const r = ${longRow}; if (!r) return null; r.setAttribute('data-e2e-long', ''); const d = r.querySelector('[data-log-detail]'); return { lines: Math.round(d.getBoundingClientRect().height / parseFloat(getComputedStyle(d).lineHeight)), height: Math.round(d.getBoundingClientRect().height), text: d.innerText.length } })()`)
  await p.clickText('Show all', { selector: '[data-e2e-long] button' })
  await sleep(600)
  const shownAfter = await p.eval(`(() => { const d = document.querySelector('[data-e2e-long] [data-log-detail]'); return { height: Math.round(d.getBoundingClientRect().height), text: d.innerText.length, less: [...document.querySelectorAll('[data-e2e-long] button')].some(b => b.innerText === 'Show less') } })()`)
  check('a long entry shows its first six lines, Show all shows all of it', !!shownBefore && shownBefore.lines === 6 && shownAfter.height > shownBefore.height && shownAfter.text >= shownBefore.text && shownAfter.less, `${JSON.stringify(shownBefore)} -> ${JSON.stringify(shownAfter)}`)
  await shot(p, '26b-logs-show-all.png')
  // A warning of the page's console while the page shows: it comes in at the top without anything being reloaded by hand.
  await p.eval(`console.warn('e2e: a warning written while the Logs page shows')`)
  await p.waitFor(`[...document.querySelectorAll('[data-logs] tbody tr.row')].slice(0, 5).some(r => r.innerText.includes('e2e: a warning written'))`, 5000, 'warning in the log').catch(() => false)
  const warnRow = (await logRows()).slice(0, 5).find((r) => r[2].includes('e2e: a warning written'))
  check('a new entry shows at the top of the open log page: a console warning, type Warning', !!warnRow && warnRow[1] === 'Warning' && warnRow[2].startsWith('App: console.warn'), warnRow?.join(' | ').slice(0, 300))
  await p.clickText('2', { selector: '[data-logs] .MuiPagination-root button' })
  await sleep(700)
  const second = await logRows()
  // Entries are listed in the order they were written: the second page is the engine's second hundred.
  const enginePage2 = (await p.engine('logs.read', { page: 1, size: 100 })).entries.map((e) => [e.time, heading(e)])
  check('the second page goes on from the first', second.length === 100 && JSON.stringify(second.map((r) => [r[0], r[2].split('\n')[0]])) === JSON.stringify(enginePage2), JSON.stringify(second[0]).slice(0, 300))

  // Clear logs, at the top of the page (not under the table, not in the left menu), asks first, then empties the file.
  check('Clear logs is at the top of the Logs page, above the table, not in the left menu', await p.eval(`(() => { const b = document.querySelector('[data-logs] [data-clear-logs]'); const t = document.querySelector('[data-logs] table'); return !!b && b.getBoundingClientRect().bottom <= t.getBoundingClientRect().top && ![...document.querySelectorAll('nav button')].some(n => n.innerText.includes('Clear')) })()`))
  await p.eval(`document.querySelector('main').scrollTo({ top: 0 })`)
  await sleep(300)
  await shot(p, '27a-clear-logs-button.png')
  await p.click('[data-clear-logs]')
  await p.waitFor(`[...document.querySelectorAll('[role=dialog]')].some(d => d.innerText.includes('Clear logs?'))`, 5000, 'clear dialog')
  await shot(p, '27-clear-logs.png')
  await p.clickText('Clear', { selector: '[role=dialog] button' })
  await sleep(1500)
  const cleared = await p.engine('logs.read', { page: 0, size: 100 })
  check('Clear logs empties the log file: only what came after is left', cleared.total <= 6 && !cleared.entries.some((e) => /^App: (Start|Database\.Open)$/.test(heading(e))) && readFileSync(logFile, 'utf8').trim().split('\n').length === cleared.total,
    JSON.stringify(cleared.entries.map(heading)))
  check('the log page shows the emptied log', (await logRows()).length === cleared.total && cleared.total >= 1, String((await logRows()).length))
  await shot(p, '28-logs-cleared.png')

  // Settings > Data > Log location: under the database location; Open and Change; the file moves and goes on there.
  await p.clickText('Settings', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('[data-log-file]') !== null`, 10000, 'log location row')
  const mainText = await p.eval(`document.querySelector('main').innerText`)
  const rowsOrder = mainText.indexOf('Database location') < mainText.indexOf('Log location') && mainText.indexOf('Log location') < mainText.indexOf('Erase all data')
  check('Log location sits under Database location, with the path of the log file', rowsOrder && (await p.eval(`document.querySelector('[data-log-file]').innerText`)) === logFile, await p.eval(`document.querySelector('[data-log-file]').innerText`))
  await p.eval(`document.querySelector('[data-log-file]').scrollIntoView({ block: 'center' })`)
  await sleep(300)
  await shot(p, '29-log-location.png')
  const movedLog = join(data, 'moved-log')
  mkdirSync(movedLog, { recursive: true })
  await p.eval(`[...document.querySelector('[data-log-file]').parentElement.querySelectorAll('button')].find(b => b.innerText.includes('Change')).click()`)
  await p.waitFor(`[...document.querySelectorAll('[role=dialog]')].some(d => d.innerText.includes('Log location') && d.innerText.includes('moved-log'))`, 10000, 'log folder listed')
  await p.clickText('moved-log', { selector: '[role=dialog] [role=button]', partial: true })
  await sleep(600)
  await p.clickText('Use this folder', { selector: '[role=dialog] button' })
  await sleep(1200)
  const shown = await p.eval(`document.querySelector('[data-log-file]').innerText`)
  await openLogs(p)
  await sleep(1200)
  const afterMoveLog = existsSync(join(movedLog, 'app.log')) ? readFileSync(join(movedLog, 'app.log'), 'utf8') : ''
  check('the log file moved to the chosen folder and goes on being written there', shown === join(movedLog, 'app.log') && !existsSync(logFile)
    && afterMoveLog.includes('\tinfo\tEngine: logs.move\\n') && afterMoveLog.includes('"to":"logs"'), shown)
  await p.clickText('Settings', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('[data-log-file]') !== null`, 10000, 'log location row')
  await p.engine('logs.move', { folder: data })

  check('no phrase joined to the next by a semicolon on any screen photographed', semicolons.length === 0, semicolons.slice(0, 3).join(' | '))
  check('no script errors in the interface', p.consoleErrors.length === 0, p.consoleErrors.slice(0, 3).join(' | ') + (p.sizeLoops?.length ? ' | size observers before each loop: ' + p.sizeLoops.join(' || ') : ''))
} catch (e) {
  failed = true
  console.error('ERROR', e.message)
  try { await shot(p, '99-error.png') } catch { /* window gone */ }
} finally {
  await p.close()
}
if (!failed) {
  // The first launch wrote each step, the interface's requests, what it drew, and its end.
  const first = existsSync(firstLaunchLog) ? readFileSync(firstLaunchLog, 'utf8') : ''
  check('the first launch wrote its log: build, requests, what was drawn, the end', /  build: .+ bytes/.test(first) && first.includes('request app.init: ok')
    && /interface drawn: \{"menu":true,"elements":\d{3,}/.test(first) && first.includes('The app ended.') && Buffer.byteLength(first) < 1024 * 1024,
    `${Buffer.byteLength(first)} bytes`)
  // Every warning and error in it comes with the full call stack of where it was written.
  const entries = first.split(/\n(?=\d\d:\d\d:\d\d  )/).map((e) => e.slice(10))
  const errors = entries.filter((e) => /^(warning|error): /.test(e))
  const failedRequests = entries.filter((e) => /^request \S+: failed: /.test(e))
  check('each warning and error of the first-launch log with its call stack', errors.every((e) => /\n {10}Call stack:\n {10}\s*\S/.test(e) && /\n {10}\s*(at |\S*@)/.test(e))
    && failedRequests.every((e) => /\n {10}   at /.test(e)), `${errors.length} warnings and errors, ${failedRequests.length} failed requests written whole`)
}
writeFileSync(join(out, 'result.json'), JSON.stringify({ checks, consoleErrors: p.consoleErrors }, null, 1))
const bad = checks.filter((c) => !c.ok).length
console.log(`\n${checks.length - bad}/${checks.length} checks passed${failed ? ' (stopped early)' : ''}`)
if (existsSync(join(out, 'data'))) { /* left for inspection; throw-away data */ }
process.exit(bad || failed ? 1 : 0)
