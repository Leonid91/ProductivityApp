// End-to-end check of the real app: starts the executable off-screen (no window is ever shown), drives the
// interface through the WebView2 debugging port, imports the sample statements through the in-app file
// browser, exercises the main features and saves screenshots.
//
//   node tools/e2e/run.mjs <ProductivityApp.exe> <samples folder> <output folder>
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { launch, sleep } from './cdp.mjs'

const [exe, samples, out] = process.argv.slice(2)
if (!exe || !samples || !out) { console.error('usage: run.mjs <exe> <samples> <out>'); process.exit(2) }
const data = join(out, 'data')
const reports = join(out, 'reports')
rmSync(out, { recursive: true, force: true })
mkdirSync(reports, { recursive: true })

const checks = []
const check = (name, ok, detail = '') => { checks.push({ name, ok: !!ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`) }
const shot = (p, name) => p.shot(join(out, name))
const text = (p) => p.eval('document.body.innerText')

const p = await launch(exe, ['--offscreen', '--data-dir', data, '--size', '1440x920'], 9333)
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
  await p.clickText(`Import ${files.length} files`, { selector: '[role=dialog] button' })
  await p.waitFor(`document.querySelectorAll('[data-draft]').length === ${files.length}`, 180000, 'all drafts')
  await sleep(500)
  const statuses = await p.eval(`[...document.querySelectorAll('[data-draft]')].map(d => [d.dataset.draft, d.innerText.includes('Balanced to the cent') ? 'verified' : d.innerText.includes('No operations found') ? 'none' : d.innerText.includes('Off by') ? 'mismatch' : 'other'])`)
  writeFileSync(join(out, 'drafts.json'), JSON.stringify(statuses, null, 1))
  const verified = statuses.filter((s) => s[1] === 'verified').length
  // The unusual layout needs columns; a card history (PDF or CSV) prints no balance to check against.
  check('statements read and balanced to the cent', verified === files.length - 3, `${verified}/${files.length}`)
  check('BNC card exports read without balances', statuses.filter((s) => s[0].startsWith('BNC 2025-10-01-120000') && s[1] === 'other').length === 2)
  check('unusual layout asks for columns', statuses.some((s) => s[0].startsWith('Mutuelle') && s[1] === 'none'))
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
  await p.clickText('Save', { selector: '[role=dialog] button' })
  await p.waitFor(`!document.querySelector('[role=dialog] img')`, 10000, 'editor closed')
  await sleep(500)
  check('draft re-read with the template', await p.eval(`document.querySelector('[data-draft^="Mutuelle"]').innerText.includes('Balanced to the cent')`))

  // ---- save every draft (PDFs first; the CSVs of the same BNC accounts must then bring nothing new)
  // Nothing may be pre-selected: every file waits for its account, and cannot be saved before.
  check('no account chosen for any file', await p.eval(`[...document.querySelectorAll('[data-draft]')].every(c => c.innerText.includes('Choose the account'))`))
  check('import impossible before choosing', await p.eval(`[...document.querySelectorAll('[data-draft]')].every(c => [...c.querySelectorAll('button')].filter(b => b.innerText.startsWith('Import')).every(b => b.disabled))`))
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
  await p.clickText('Periods', { selector: 'nav button' })
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
  await p.clickText('Periods', { selector: 'nav button' })
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
  await p.clickText('Analytics', { selector: '[data-period-view] button' })
  await sleep(1200)
  await p.eval(`(() => { const s = document.querySelector('section'); const t = s.querySelector('[data-pie]'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 160 }) })()`)
  await sleep(800)
  await shot(p, '08b-periods-charts.png')
  await p.eval(`document.querySelector('main').scrollBy({ top: 420 })`)
  await sleep(600)
  await shot(p, '09-periods-daily-chart.png')
  await p.clickText('Statements', { selector: '[data-period-view] button' })
  await sleep(800)

  // transfers, card payments and investments: in their own folded table
  await p.eval(`document.querySelector('section[data-period="2025-08-01"]').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('section[data-period="2025-08-01"] .MuiAccordion-root') !== null`, 20000, 'transfers section')
  const mainHasMoves = await p.eval(`[...document.querySelector('section[data-period="2025-08-01"] table').querySelectorAll('tbody tr')].some(r => /Credit card payment|Transfer between accounts/.test(r.innerText))`)
  check('transfers kept out of the main table, folded apart', !mainHasMoves)
  await p.eval(`document.querySelector('section[data-period="2025-08-01"] .MuiAccordionSummary-root').click()`)
  await sleep(700)
  await p.eval(`document.querySelector('section[data-period="2025-08-01"] .MuiAccordion-root').scrollIntoView({ block: 'center' })`)
  await sleep(400)
  await shot(p, '09b-transfers-folded-table.png')
  const newest = await p.eval(`(() => { const d = [...document.querySelector('section[data-period="2025-08-01"] table').querySelectorAll('tbody tr td:first-child')].map(x => x.innerText); return d.length > 1 && new Date(d[0]) >= new Date(d[d.length - 1]) })()`)
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
  check('transfers table: date, category, description and tag filters; one category shown', mvHeaders.join() === 'Date,Category,Description,Tags' && mvCats.length > 1
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
  // Table headers stay under the bar of tabs while the table scrolls.
  await p.eval(`(() => { const t = document.querySelector('section tbody tr:nth-child(12)'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 200 }) })()`)
  await sleep(300)
  const sticky = await p.eval(`(() => { const th = document.querySelector('section thead th').getBoundingClientRect(); const head = document.querySelector('section [data-part-head]').getBoundingClientRect(); const bar = document.querySelector('main [class*=MuiToggleButtonGroup]').parentElement.getBoundingClientRect(); return [Math.round(th.top), Math.round(head.bottom), Math.round(head.top), Math.round(bar.bottom)] })()`)
  await shot(p, '09c-sticky-header.png')
  check('table headers stay visible while scrolling, under the bank’s name, itself under the bar', Math.abs(sticky[0] - sticky[1]) <= 2 && Math.abs(sticky[2] - sticky[3]) <= 2, sticky.join(' / '))

  // Second layer under Month / Year / All time / Custom: Statements or Analytics; the totals under both.
  await p.scrollMain(0)
  const sec = 'section[data-period="2025-08-01"]'
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${sec} [data-totals]') !== null`, 20000, 'period loaded')
  await sleep(500)
  check('periods open on their statements, without charts', await p.eval(`document.querySelector('${sec} table') !== null && document.querySelector('${sec} [data-pie]') === null && document.querySelector('${sec} [data-flowchart]') === null`))
  check('each bank starts with its totals, above its tables', await p.eval(`(() => { const b = document.querySelector('${sec} [data-part-block]'); const t = b.querySelector('[data-totals]'); return !!t && t.getBoundingClientRect().bottom <= b.querySelector('table').getBoundingClientRect().top && !!b.querySelector('[data-stat="Expenses / earnings"]') })()`))
  const ratio = await p.eval(`(() => { const s = document.querySelector('${sec}'); const bg = (l) => getComputedStyle(s.querySelector('[data-stat="' + l + '"]')).backgroundColor; const r = s.querySelector('[data-stat="Expenses / earnings"]'); return { text: r.innerText, rec: s.querySelector('[data-stat="Recurring payments, rent excluded"]').innerText, ratio: bg('Expenses / earnings'), exp: bg('Expenses'), earn: bg('Earnings') } })()`)
  const share = parseInt(ratio.text.match(/(\d+)%/)?.[1] ?? '0', 10)
  check('expenses / earnings red above 100%, green below, without the spent-of-earned line', !ratio.text.includes('spent of') && (share > 100 ? ratio.ratio === ratio.exp : ratio.ratio === ratio.earn), `${share}% ${ratio.ratio}`)
  check('recurring payments card without the line under the amount', ratio.rec.trim().split('\n').filter(Boolean).length === 2, ratio.rec.replace(/\n/g, ' | '))
  await p.clickText('Analytics', { selector: '[data-period-view] button' })
  await p.waitFor(`document.querySelector('${sec} [data-flowchart]') !== null`, 20000, 'charts view')
  await sleep(800)
  check('analytics: pies, bars, category lines and money moved, no tables', await p.eval(`(() => { const s = document.querySelector('${sec}'); return s.querySelectorAll('[data-pie]').length >= 2 && !!s.querySelector('[data-bars]') && !!s.querySelector('[data-category-lines]') && !s.querySelector('table:not([data-conversions])') && !!s.querySelector('[data-totals]') })()`))
  // Money moved: the outside box has the size of the accounts, no amounts written in it, every arrow its own points.
  await p.eval(`(() => { const f = document.querySelector('${sec} [data-flowchart]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await sleep(600)
  await shot(p, '09d-money-moved.png')
  const flow = await p.eval(`(() => { const f = document.querySelector('${sec} [data-flowchart]'); const o = f.querySelector('[data-outside-node] rect'); const a = f.querySelector('[data-account-node] rect'); return { same: !!o && !!a && o.getAttribute('width') === a.getAttribute('width') && o.getAttribute('height') === a.getAttribute('height'), text: [...f.querySelectorAll('[data-outside-node] text')].map(t => t.textContent).join(' | '), blob: !!f.querySelector('ellipse') } })()`)
  check('outside box the size of the accounts, no amounts inside', flow.same && !flow.blob && flow.text === 'Outside the app | Other accounts & people', flow.text)
  // Expenses per day: the amount of the day on hover.
  await p.eval(`(() => { const f = document.querySelector('${sec} [data-bars]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 200 }) })()`)
  await sleep(600)
  const bar = await p.eval(`(() => { const r = [...document.querySelector('${sec} [data-bars]').querySelectorAll('rect')].filter(r => r.getBoundingClientRect().height > 2 && r.getBoundingClientRect().width < 60).sort((a, b) => b.getBoundingClientRect().height - a.getBoundingClientRect().height)[0].getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 } })()`)
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: bar.x - 2, y: bar.y - 2 })
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: bar.x, y: bar.y })
  await sleep(600)
  const tip = await p.eval(`(() => { const t = document.querySelector('.MuiChartsTooltip-root'); if (!t) return null; const r = t.getBoundingClientRect(); return { text: t.innerText, x: r.left, y: r.top } })()`)
  await shot(p, '09e-day-hover.png')
  check('amount spent that day shown on hover, next to the pointer', !!tip && /Day \d+/.test(tip.text) && /\$[\d,]+\.\d\d/.test(tip.text) && Math.abs(tip.y - bar.y) < 120 && Math.abs(tip.x - bar.x) < 200, JSON.stringify(tip))
  await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 })
  // Expenses per category per day: under "Categories", the total and every category with expenses in the period (no
  // drop-down), each ticked on or off; the total and the five largest at first; the scale follows the lines shown.
  // August is in CAD at every bank: all banks together hold every operation of the month in CAD.
  const augAll = `${sec} [data-part-block="all"]`
  await p.eval(`(() => { const f = document.querySelector('${augAll} [data-category-lines]'); document.querySelector('main').scrollBy({ top: f.getBoundingClientRect().top - 140 }) })()`)
  await sleep(600)
  await shot(p, '09f-category-lines.png')
  const lineCount = () => p.eval(`document.querySelectorAll('${augAll} [data-category-lines] path.MuiLineChart-line').length`)
  const augSummary = await p.engine('periods.summary', { from: '2025-08-01', to: '2025-08-31', granularity: 'month', currency: 'CAD' })
  const choiceKeys = await p.eval(`[...document.querySelectorAll('${augAll} [data-category-choices] li[data-choice]')].map(l => l.dataset.choice)`)
  const lineCats = choiceKeys.filter((k) => k !== '__total')
  const linesText = await p.eval(`document.querySelector('${augAll} [data-category-lines]').innerText`)
  check('categories listed under a Categories title: those with expenses in the period, no drop-down',
    linesText.includes('Categories') && !/Top \d|All \(|chosen/.test(linesText) && !(await p.eval(`!!document.querySelector('${augAll} [data-category-lines] [role=combobox]')`))
      && choiceKeys[0] === '__total' && lineCats.join() === augSummary.byTopCategory.map((c) => c.key).join(), `${lineCats.length} categories`)
  check('total and the five largest categories ticked at first', (await lineCount()) === 1 + Math.min(5, lineCats.length), `${await lineCount()} lines`)
  const tickLine = async (key) => { await p.eval(`document.querySelector('${augAll} [data-category-choices] li[data-choice="${key}"] input').click()`); await sleep(350) }
  const scaleTop = () => p.eval(`Math.max(...[...document.querySelectorAll('${augAll} [data-category-lines] .MuiChartsAxis-directionY text')].map(t => { const v = parseFloat(t.textContent.replace(/[^0-9.]/g, '')); return /K/.test(t.textContent) ? v * 1000 : v }).filter(v => !isNaN(v)))`)
  const withTotal = await scaleTop()
  await tickLine('__total')
  const noTotal = await lineCount()
  const withoutTotal = await scaleTop()
  await shot(p, '09g-category-lines-without-total.png')
  check('the total can be unticked, the scale follows the lines shown', noTotal === Math.min(5, lineCats.length) && withoutTotal < withTotal, `${noTotal} lines, scale ${withTotal} -> ${withoutTotal}`)
  for (const k of lineCats.slice(5)) await tickLine(k)
  const allLines = await lineCount()
  for (const k of lineCats) await tickLine(k)
  const noLines = await lineCount()
  await tickLine(lineCats[Math.min(2, lineCats.length - 1)])
  const oneLine = await lineCount()
  await tickLine('__total')
  check('any number of categories, from none to all, the total on or off', allLines === lineCats.length && noLines === 0 && oneLine === 1 && (await lineCount()) === 2, `all ${allLines}, none ${noLines}, one ${oneLine}`)
  // The choices stay when the period goes far from the screen and comes back.
  await p.scrollMain(0)
  await p.eval(`document.querySelector('main').scrollBy({ top: 30000 })`)
  await sleep(1500)
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${augAll} [data-category-lines]') !== null`, 20000, 'period back')
  await sleep(800)
  check('chosen lines kept after scrolling away and back', (await lineCount()) === 2)
  // Pies: shares with two decimals, so a small share never reads 0%.
  const shares = await p.eval(`[...document.querySelectorAll('${sec} [data-pie] li')].map(l => l.innerText.split('\\n').pop())`)
  check('pie shares with two decimals', shares.length > 0 && shares.every((x) => /^\d+\.\d\d%$/.test(x)) && !shares.includes('0.00%'), shares.slice(-3).join(' '))
  await p.clickText('Statements', { selector: '[data-period-view] button' })
  await sleep(600)

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
  const rowsOf = () => p.eval(`[...document.querySelectorAll('${sec} table')[0].querySelectorAll('tbody tr')].map(r => [...r.children].map(c => c.innerText))`)
  const openFilter = async (label) => {
    await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
    await sleep(300)
    await p.eval(`[...document.querySelectorAll('${sec} table')[0].querySelectorAll('th button')].find(b => b.innerText.trim() === '${label}').click()`)
    await p.waitFor(`document.querySelector('[role=menu]') !== null`, 5000, label + ' menu')
    await sleep(250)
  }
  const pick = async (choice) => { await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].find(l => l.dataset.choice === ${JSON.stringify(choice)}).click()`); await sleep(250) }
  const closeMenu = async () => { await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 }); await sleep(400) }
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${sec} table') !== null`, 20000, 'August table')
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
  await p.waitFor(`document.querySelector('${sec} table') !== null`, 20000, 'table back')
  await sleep(600)
  check('category filter kept after scrolling away and back', JSON.stringify(await rowsOf()) === JSON.stringify(oneCat))
  await openFilter('Category')
  await pick('all')
  await closeMenu()
  check('all categories again', (await rowsOf()).length === allRows.length)
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
  check('every operation shown again', (await rowsOf()).length === allRows.length)

  // correcting a category teaches the dictionary
  const before = await p.eval(`document.querySelector('section tbody tr td:nth-child(3)').innerText`)
  await p.click('section tbody tr td:nth-child(2) button', 0)
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
  await p.clickText('Periods', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('section tbody tr td:nth-child(2) button') !== null`, 20000, 'operations')
  await p.click('section tbody tr td:nth-child(2) button', 0)
  await sleep(300)
  await p.eval(`[...document.querySelectorAll('[role=menuitem]')].find(e => e.innerText === 'Brushes').scrollIntoView({ block: 'center' })`)
  await sleep(200)
  await shot(p, '12e-picker-nested.png')
  await p.clickText('Brushes', { selector: '[role=menuitem]' })
  await p.waitFor(`document.querySelector('[data-scope-dialog]') !== null`, 5000, 'scope question')
  await p.clickText('Only this one', { selector: '[role=dialog] button' })
  await p.waitFor(`[...document.querySelectorAll('section tbody tr td:nth-child(2)')].some(td => td.innerText === 'Brushes')`, 10000, 'subcategory set')
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
  const analyticsOfAugust = async () => {
    await p.clickText('Periods', { selector: 'nav button' })
    await p.waitFor(`document.querySelector('section h2') !== null`, 20000, 'periods')
    await p.clickText('Analytics', { selector: '[data-period-view] button' })
    await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
    await p.waitFor(`document.querySelector('${sec} [data-pie="Expenses per category"]') !== null && document.querySelector('${sec} [data-category-lines]') !== null`, 20000, 'August analytics')
    await sleep(800)
    return p.eval(`(() => { const s = document.querySelector('${sec}'); return { pie: [...s.querySelectorAll('[data-pie="Expenses per category"] li')].map(l => l.innerText.split('\\n')[0]), lines: [...s.querySelectorAll('[data-category-choices] li[data-choice]')].map(l => l.dataset.choice), expenses: s.querySelector('[data-stat="Expenses"]').innerText } })()`)
  }
  const showPie = async (name) => {
    await p.eval(`(() => { const s = document.querySelector('${sec} [data-pie="Expenses per category"]'); document.querySelector('main').scrollBy({ top: s.getBoundingClientRect().top - 160 }) })()`)
    await sleep(500)
    await shot(p, name)
  }
  const firstLevel = await analyticsOfAugust()
  await showPie('12j-charts-first-level.png')
  const leafNames = ['Groceries', 'Restaurants & cafés', 'Nested 1', 'Nested 2', 'Nested 3', 'Nested 4']
  check('first level only (default): Food in the charts, not the categories in it', firstLevel.pie.includes('Food (test)') && !firstLevel.pie.some((n) => leafNames.includes(n))
    && firstLevel.lines.includes(catId('Food (test)')) && !firstLevel.lines.includes('groceries') && !firstLevel.lines.includes(catId('Nested 4')), firstLevel.pie.join(', '))
  // The setting, in Settings > Display > Periods: on by default; off, the charts go down to every nested category.
  const firstLevelSwitch = `document.querySelector('[aria-label="Show only first level categories in charts"]')`
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('General', { selector: 'button' })
  await p.waitFor(`${firstLevelSwitch} !== null`, 5000, 'first level setting')
  check('first level setting in Periods, on by default', await p.eval(`(() => { const i = ${firstLevelSwitch}; return i.checked && !!i.closest('[data-group="Periods"]') })()`))
  await p.eval(`${firstLevelSwitch}.click()`)
  await p.waitFor(`${firstLevelSwitch}.checked === false`, 5000, 'setting off')
  const everyLevel = await analyticsOfAugust()
  await showPie('12k-charts-every-level.png')
  // (The pie keeps its five largest slices and "Other" by default; the list of the lines has every category.)
  check('setting off: every nested category in the charts, Food gone, same expenses', ['Groceries', 'Restaurants & cafés'].every((n) => everyLevel.pie.includes(n)) && !everyLevel.pie.includes('Food (test)')
    && [1, 2, 3, 4].every((i) => everyLevel.lines.includes(catId(`Nested ${i}`)))
    && everyLevel.lines.includes(catId('Nested 4')) && everyLevel.lines.includes('groceries') && !everyLevel.lines.includes(catId('Food (test)')) && everyLevel.expenses === firstLevel.expenses, everyLevel.pie.join(', '))
  await p.clickText('Statements', { selector: '[data-period-view] button' })
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${sec} table') !== null`, 20000, 'August statements')
  await sleep(500)
  // Every bank's table of the month: the categories its operations have.
  const tableCats = []
  const bankParts = `${sec} [data-part-block]:not([data-part-block="all"])`
  const bankCount = await p.eval(`document.querySelectorAll('${bankParts}').length`)
  for (let i = 0; i < bankCount; i++) {
    await p.eval(`[...document.querySelectorAll('${bankParts}')[${i}].querySelector('table').querySelectorAll('th button')].find(b => b.innerText.trim() === 'Category').click()`)
    await p.waitFor(`document.querySelector('[role=menu]') !== null`, 5000, 'category filter')
    tableCats.push(...await p.eval(`[...document.querySelectorAll('[role=menu] li[data-choice]')].map(l => l.dataset.choice)`))
    await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
    await sleep(300)
  }
  check('the operations keep their own categories in the tables', [1, 2, 3, 4].every((i) => tableCats.includes(catId(`Nested ${i}`))) && tableCats.includes('groceries') && !tableCats.includes(catId('Food (test)')))
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
  await p.clickText('Periods', { selector: 'nav button' })
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
  await p.clickText('Periods', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('section h2') !== null`, 20000, 'periods')
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${sec} table [data-tag-cell]') !== null`, 20000, 'tags column')
  await sleep(500)
  const tagRows = () => p.eval(`[...document.querySelectorAll('${sec} table')[0].querySelectorAll('tbody tr')].filter(r => r.children.length > 2).map(r => ({ cat: r.children[1].innerText, tags: [...r.querySelectorAll('[data-tag]')].map(t => t.dataset.tag) }))`)
  await p.waitFor(`[...document.querySelectorAll('${sec} table')[0].querySelectorAll('tbody tr')].some(r => r.children[1]?.innerText === 'Restaurants & cafés')`, 15000, 'restaurant rows').catch(() => undefined)
  const tagBefore = await tagRows()
  const restaurantRows = tagBefore.filter((r) => r.cat === 'Restaurants & cafés')
  check('Tags column: a category\'s tags on every one of its operations', restaurantRows.length > 0 && restaurantRows.every((r) => r.tags.join() === 'Trip (test),Work (test)')
    && tagBefore.filter((r) => r.cat !== 'Restaurants & cafés').every((r) => r.tags.length === 0), `${restaurantRows.length} restaurant rows`)
  const target = tagBefore.findIndex((r) => r.cat !== 'Restaurants & cafés')
  await p.eval(`[...document.querySelectorAll('${sec} table')[0].querySelectorAll('tbody tr')][${target}].querySelector('[data-tag-cell]').click()`)
  await p.waitFor(`document.querySelector('[data-tag-choice="Family (test)"]') !== null`, 5000, 'tag menu')
  await p.eval(`document.querySelector('[data-tag-choice="Family (test)"]').click()`)
  await sleep(300)
  await p.eval(`document.querySelector('[data-tag-choice="Work (test)"]').click()`)
  await sleep(300)
  await shot(p, '22c-tag-cell.png')
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await p.waitFor(`[...document.querySelectorAll('${sec} table')[0].querySelectorAll('tbody tr')][${target}].querySelectorAll('[data-tag]').length === 2`, 10000, 'own tags shown')
  const augTagged = await p.engine('periods.summary', { from: '2025-08-01', to: '2025-08-31', granularity: 'month', currency: 'CAD' })
  const ownOp = augTagged.transactions.find((t) => t.ownTags.length > 0)
  check('two tags put on one operation', ownOp?.ownTags.length === 2 && ownOp.tags.includes(tagIds['Family (test)']) && ownOp.tags.includes(tagIds['Work (test)']))
  // The Tags filter: none, then one tag (once the period is read again with the new tags).
  for (let i = 0; i < 20; i++) {
    await p.eval(`[...document.querySelectorAll('${sec} table')[0].querySelectorAll('th button')].find(b => b.innerText.trim() === 'Tags').click()`)
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
  await p.waitFor(`[...document.querySelectorAll('${sec} table')[0].querySelectorAll('tbody tr')].filter(r => r.children.length > 2).length === 1`, 10000, 'one tagged row').catch(() => undefined)
  const familyRows = await tagRows()
  check('Tags filter: one tag', familyRows.length === 1 && familyRows[0].tags.includes('Family (test)'))
  await p.eval(`[...document.querySelectorAll('${sec} table')[0].querySelectorAll('th button')].find(b => b.innerText.trim() === 'Tags').click()`)
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
  await p.clickText('Analytics', { selector: '[data-period-view] button' })
  await p.waitFor(`document.querySelector('${sec} [data-pie="Tags"]') !== null`, 20000, 'tags pie')
  await p.eval(`(() => { const t = document.querySelector('${sec} [data-pie="Tags"]'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 200 }) })()`)
  await sleep(600)
  await shot(p, '22e-tags-pie.png')
  const piePerMode = {}
  for (const mode of ['Year', 'All time', 'Custom']) {
    await p.clickText(mode, { selector: 'button' })
    await p.waitFor(`document.querySelector('section [data-pie="Tags"]') !== null || document.querySelector('section')?.innerText.includes('No tagged expenses')`, 20000, mode + ' tags pie')
    await sleep(500)
    piePerMode[mode] = await p.eval(`document.querySelector('section [data-pie="Tags"]') ? [...document.querySelectorAll('section [data-pie="Tags"] li')].length : 0`)
  }
  check('Tags pie in Year, All time and Custom periods', Object.values(piePerMode).every((n) => n >= 1), JSON.stringify(piePerMode))
  await p.clickText('Month', { selector: 'button' })
  await sleep(600)

  // ---- Summary (left of Statements): only the balance, by month (or by year beyond a year), every kind of period.
  await p.clickText('Summary', { selector: '[data-period-view] button' })
  await p.eval(`document.querySelector('${sec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${sec} [data-summary]') !== null`, 20000, 'month summary')
  await sleep(400)
  const viewOrder = await p.eval(`[...document.querySelectorAll('[data-period-view] button')].map(b => b.innerText.trim()).join()`)
  const monthSummary = await p.eval(`(() => { const s = document.querySelector('${sec}'); return { stats: [...s.querySelectorAll('[data-stat]')].map(x => x.dataset.stat), text: s.innerText, tables: s.querySelectorAll('table').length, pies: s.querySelectorAll('[data-pie]').length } })()`)
  const augBalance = (await p.engine('periods.summary', { from: '2025-08-01', to: '2025-08-31', granularity: 'month', currency: 'CAD' })).balance
  check('Summary first, a month shows only its balance under its title and days', viewOrder === 'Summary,Statements,Analytics' && monthSummary.stats.join() === 'Balance' && monthSummary.tables === 0 && monthSummary.pies === 0
    && monthSummary.text.includes('August 2025') && monthSummary.text.includes('31 days') && monthSummary.text.includes(Math.abs(augBalance).toLocaleString('en-CA', { minimumFractionDigits: 2 })), monthSummary.stats.join())
  await shot(p, '23a-summary-month.png')
  await p.clickText('Year', { selector: 'button' })
  await p.waitFor(`document.querySelector('section [data-summary]') !== null`, 20000, 'year summary')
  await sleep(500)
  const yearCard = await p.eval(`(() => { const s = document.querySelector('section[data-period="2025-01-01"]'); return { parts: s.querySelectorAll('[data-part]').length, stats: [...s.querySelectorAll('[data-stat]')].map(x => x.dataset.stat).join(), text: s.innerText } })()`)
  // The Summary is global: every bank of the year together, in the global display currency.
  const yearSum = (await p.engine('periods.report', { from: '2025-01-01', to: '2025-12-31', granularity: 'year' })).global
  check('Summary of a year: the year alone, with its balance', yearCard.parts === 0 && yearCard.stats === 'Balance' && yearSum.breakdown.length === 1
    && cents(yearSum.breakdown[0].balance) === cents(yearSum.balance) && yearCard.text.includes(Math.abs(yearSum.balance).toLocaleString('en-CA', { minimumFractionDigits: 2 })), yearCard.stats)
  await shot(p, '23b-summary-year.png')
  await p.clickText('All time', { selector: 'button' })
  await p.waitFor(`document.querySelector('section [data-summary]') !== null`, 20000, 'all time summary')
  await sleep(500)
  const allParts = await p.eval(`[...document.querySelector('section [data-summary]').querySelectorAll('[data-part]')].map(x => x.dataset.stat)`)
  check('Summary of more than a year: by year', allParts.length >= 2 && allParts.every((t) => /^\d{4}(, from |$)/.test(t)), allParts.join(' | '))
  await shot(p, '23c-summary-all-time.png')
  await p.clickText('Custom', { selector: 'button' })
  await p.waitFor(`document.querySelector('section [data-summary]') !== null`, 20000, 'custom summary')
  check('Summary of a custom period', await p.eval(`document.querySelector('section [data-summary] [data-stat="Balance"]') !== null`))
  await p.clickText('Month', { selector: 'button' })
  await p.clickText('Statements', { selector: '[data-period-view] button' })
  await sleep(600)

  // ---- Banks side by side (the samples have Canadian banks in CAD and French banks in EUR in October 2025): every bank
  // in the display currency, then all banks together; every euro operation converted at the rate of its day, with the
  // amount its bank wrote under it.
  const octSec = 'section[data-period="2025-10-01"]'
  await p.eval(`document.querySelector('${octSec}').scrollIntoView({ block: 'start' })`)
  await p.waitFor(`document.querySelector('${octSec} [data-conversion-panel]') !== null`, 30000, 'October with all banks')
  await sleep(500)
  const octUi = await p.eval(`(() => { const s = document.querySelector('${octSec}'); return { parts: [...s.querySelectorAll('[data-part-block]')].map(b => [b.dataset.partBlock, b.querySelector('[data-part-name]').textContent, b.querySelector('[data-part-currency]').textContent]), balance: s.querySelector('[data-part-block="all"] [data-totals] [data-stat="Balance"]').textContent, panel: s.querySelector('[data-conversion-panel]').textContent, originals: [...s.querySelectorAll('[data-original]')].map(o => o.textContent).slice(0, 3) } })()`)
  const oct = await p.engine('periods.report', { from: '2025-10-01', to: '2025-10-31', granularity: 'month' })
  const eurLines = oct.conversion.lines.filter((l) => l.from === 'EUR' && l.to === 'CAD')
  check('a block per bank in the display currency (euro banks shown EUR → CAD), then all banks in CAD', octUi.parts.length === oct.banks.length + 1
    && oct.banks.every((b) => b.currency === 'CAD') && octUi.parts.some((x) => x[2] === 'EUR → CAD') && octUi.parts.at(-1).join() === 'all,All banks,CAD', octUi.parts.map((x) => x[1] + ' ' + x[2]).join(' | '))
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
  await p.eval(`(() => { const t = document.querySelector('${octSec} [data-part-block] tbody tr:nth-child(12)'); const m = document.querySelector('main'); m.scrollTop += t.getBoundingClientRect().top - m.getBoundingClientRect().top - 220 })()`)
  await sleep(500)
  const pinned = await p.eval(`(() => { const b = document.querySelector('${octSec} [data-part-block]'); const h = b.querySelector('[data-part-head]').getBoundingClientRect(); const th = b.querySelector('thead th').getBoundingClientRect(); const bar = document.querySelector('[data-period-view]').parentElement.parentElement.getBoundingClientRect(); return { bar: Math.round(bar.bottom), head: [Math.round(h.top), Math.round(h.bottom)], th: [Math.round(th.top), Math.round(th.bottom)], top: Math.round(b.getBoundingClientRect().top) } })()`)
  check('the bank stays named while its table scrolls, the table header under its name', pinned.top < pinned.head[0] && Math.abs(pinned.head[0] - pinned.bar) <= 1 && Math.abs(pinned.th[0] - pinned.head[1]) <= 1, JSON.stringify(pinned))
  await shot(p, '24b-bank-name-pinned.png')
  // The Summary stays global.
  await p.clickText('Summary', { selector: '[data-period-view] button' })
  await p.waitFor(`document.querySelector('${octSec} [data-summary]') !== null`, 20000, 'October summary')
  check('the Summary is the global balance', (await p.eval(`document.querySelector('${octSec} [data-summary]').innerText`)).includes(Math.abs(oct.global.balance).toLocaleString('en-CA', { minimumFractionDigits: 2 })))
  await p.clickText('Statements', { selector: '[data-period-view] button' })
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
    const rows = [...card.children].filter(r => r.querySelector('.MuiInputBase-root')).slice(0, 3)
    return rows.map(r => { const v = r.querySelector('.MuiInputBase-root').getBoundingClientRect(); return { label: r.firstElementChild.innerText, value: r.querySelector('.MuiInputBase-root').innerText.replace(/[​s]+/g, ' ').trim(), right: Math.round(v.right), width: Math.round(v.width) } })
  })()`)
  check('Display: global currency and exchange rates as rows, values aligned with the date format', display.map((r) => r.label).join() === 'Global display currency,Exchange rates,Date format'
    && new Set(display.map((r) => r.right)).size === 1 && new Set(display.map((r) => r.width)).size === 1 && display[1].value === 'Bank of Canada'
    && (await p.eval(`document.querySelector('[data-rates-link]')?.innerText`)) === 'Edit banks', JSON.stringify(display))
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
  await p.eval('location.reload()')
  await p.waitFor(`document.querySelector('nav') !== null`, 20000, 'reloaded')
  await p.clickText('Calendar', { selector: 'nav button' })
  await sleep(1200)
  const converted = oct.banks.find((b) => b.native === 'EUR').summary.transactions.find((t) => t.originalCurrency === 'EUR')
  for (let i = 0; i < 24 && (await p.eval(`document.querySelector('main h5')?.innerText`)) !== 'October 2025'; i++) await p.click('[aria-label="Previous month"]')
  await sleep(600)
  const header = await p.eval(`document.querySelector('[data-month-totals]')?.innerText.replace(/\\s+/g, ' ')`)
  check('calendar: the month’s totals in its header', header?.includes('Spent') && header.includes('Earned') && header.includes(Math.abs(oct.global.balance).toLocaleString('en-CA', { minimumFractionDigits: 2 })), header)
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
  const salaryOk = sal && sal.businessDays === decReport.businessDays && sal.hoursPerDay === 8 && Math.abs(sal.perHour - sal.net / (sal.businessDays * 8)) < 0.006 && Math.abs(sal.perYear - sal.net / sal.businessDays * sal.yearBusinessDays) < 0.006
  await p.clickText('Periods', { selector: 'nav button' })
  await p.clickText('Analytics', { selector: '[data-period-view] button' })
  await p.waitFor(`document.querySelector('section[data-period="2025-12-01"] [data-salary]') !== null`, 20000, 'the salary in December')
  await p.eval(`document.querySelector('section[data-period="2025-12-01"]').scrollIntoView({ block: 'start' })`)
  await sleep(600)
  const salaryText = await p.eval(`document.querySelector('section[data-period="2025-12-01"] [data-salary]').innerText`)
  check('Analytics: the net salary per hour and per year, from the business days', salaryOk && salaryText === `Your net salary per hour is ${moneyCad(sal.perHour)} and per year is ${moneyCad(sal.perYear)}.`, `${salaryText} ${JSON.stringify(sal)}`)
  await shot(p, '24j-analytics-salary.png')
  await p.engine('settings.set', { key: 'currency', value: 'EUR' })
  const salEur = (await p.engine('periods.report', { from: '2025-12-01', to: '2025-12-31', granularity: 'month' })).salary
  check('the salary in the display currency (euros)', salEur && salEur.net < sal.net * 0.75 && salEur.net > sal.net * 0.55 && salEur.businessDays === sal.businessDays, `${salEur?.net} EUR, ${sal.net} CAD`)
  await p.engine('settings.set', { key: 'currency', value: 'CAD' })
  await p.clickText('Statements', { selector: '[data-period-view] button' })
  await p.clickText('Periods', { selector: 'nav button' })
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
  await p.clickText('Periods', { selector: 'nav button' })
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

  // export the first month to PDF
  await p.clickText('Export to PDF', { selector: 'section button', index: 0 })
  await p.waitFor(`document.querySelector('[role=dialog]')?.innerText.includes('File name')`, 10000, 'export dialog')
  await sleep(300)
  await shot(p, '15-export-dialog.png')
  await p.clickText('Export', { selector: '[role=dialog] button' })
  await p.waitFor(`document.body.innerText.includes('Report saved')`, 30000, 'report saved')
  const pdfs = readdirSync(reports).filter((f) => f.endsWith('.pdf'))
  check('PDF report written', pdfs.length === 1, pdfs.join(', '))

  // add operation dialog
  await p.clickText('Add operation')
  await sleep(500)
  await shot(p, '16-add-operation.png')
  check('a new operation goes in an account of a bank', await p.eval(`(() => { const d = document.querySelector('[role=dialog]'); return !d.innerText.includes('Cash & manual') && [...d.querySelectorAll('[role=combobox]')].some(c => c.innerText.includes(' · ')) })()`))
  await p.clickText('Cancel', { selector: '[role=dialog] button' })

  // calendar, settings
  await p.clickText('Calendar', { selector: 'nav button' })
  await sleep(1500)
  await shot(p, '17-calendar.png')
  await p.clickText('Settings', { selector: 'nav button' })
  await sleep(800)
  await shot(p, '19-settings-general.png')
  check('Periods and Calendar inside Display', await p.eval(`(() => { const g = document.querySelector('[data-group="Periods"]'); const c = document.querySelector('[data-group="Calendar"]'); return !!g && !!c && g.parentElement === c.parentElement && g.parentElement.querySelector('h6')?.innerText === 'Display' && getComputedStyle(g.previousElementSibling).borderBottomWidth === '0px' })()`))
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
  await p.clickText('Brands', { selector: 'main button' })
  await sleep(1000)
  await shot(p, '18-brands.png')
  const learnedText = await p.eval(`document.querySelector('[data-learned]')?.innerText ?? ''`)
  const learnedBrands = await p.eval(`[...document.querySelectorAll('[data-learned] tbody tr')].map(r => r.children[2].innerText)`)
  check('each learned brand listed once', !learnedText.includes('Every operation of this brand') && new Set(learnedBrands).size === learnedBrands.length, learnedBrands.join(', '))
  const wordings = await p.eval(`[...document.querySelectorAll('[data-learned] tbody tr')].map(r => r.children[1].innerText.trim())`)
  check('every learned correction shows its statement wordings', wordings.every((w) => w.length > 1 && w !== '—'), wordings.join(' | '))
  await p.clickText('Bank', { selector: 'button' })
  await sleep(600)
  await shot(p, '19c-settings-banks.png')
  check('banks table', await p.eval(`document.querySelector('[data-banks]')?.innerText.includes('Banking System')`))
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
  const photo = await p.engine('import.parseBytes', { name: 'statement photo.png', base64: page.png })
  check('a picture of a statement is read like a scan', photo.usedOcr && photo.rows.length === pdfDraft.rows.length && photo.status === pdfDraft.status
    && cents(photo.rows.reduce((s, r) => s + r.amount, 0)) === cents(pdfDraft.rows.reduce((s, r) => s + r.amount, 0)), `${photo.rows.length}/${pdfDraft.rows.length} lines, ${photo.status}`)
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
  for (const d of [pdfDraft, photo, ofxDraft, qifDraft, xlsDraft]) await p.engine('import.discard', { draftId: d.id })

  // dark theme, from the icon above the settings
  await p.click('nav [aria-label="Dark mode"]')
  await sleep(600)
  check('theme toggled from the side bar', (await p.engine('app.init')).settings.theme === 'dark')
  await p.clickText('Periods', { selector: 'nav button' })
  await sleep(1500)
  await shot(p, '20-dark-periods.png')
  await p.eval(`(() => { const s = document.querySelector('section'); const t = [...s.querySelectorAll('p, span')].find(e => e.innerText === 'Expenses'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 160 }) })()`)
  await sleep(800)
  await shot(p, '21-dark-totals.png')
  await p.clickText('Analytics', { selector: '[data-period-view] button' })
  await sleep(1200)
  await shot(p, '21b-dark-charts.png')
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
  await visit('operations', async () => { await p.clickText('Periods', { selector: 'nav button' }); await sleep(800); await p.clickText('Statements', { selector: '[data-period-view] button' }) })
  await visit('accounts', async () => { await p.clickText('Settings', { selector: 'nav button' }); await p.clickText('Bank Configuration', { selector: 'button' }) })
  await visit('banks', definition('Bank'))
  await visit('rates', definition('Exchange rates'))
  await visit('brands', definition('Brands'))
  await visit('categories', definition('Categories'))
  // (the test tags are deleted by now: one made for the check, deleted after)
  const pencilTag = (await p.engine('tags.create', { name: 'Pencil check', color: null })).tags.find((t) => t.name === 'Pencil check')
  await p.eval('location.reload()')
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

  check('no script errors in the interface', p.consoleErrors.length === 0, p.consoleErrors.slice(0, 3).join(' | '))
} catch (e) {
  failed = true
  console.error('ERROR', e.message)
  try { await shot(p, '99-error.png') } catch { /* window gone */ }
} finally {
  await p.close()
}
writeFileSync(join(out, 'result.json'), JSON.stringify({ checks, consoleErrors: p.consoleErrors }, null, 1))
const bad = checks.filter((c) => !c.ok).length
console.log(`\n${checks.length - bad}/${checks.length} checks passed${failed ? ' (stopped early)' : ''}`)
if (existsSync(join(out, 'data'))) { /* left for inspection; throw-away data */ }
process.exit(bad || failed ? 1 : 0)
