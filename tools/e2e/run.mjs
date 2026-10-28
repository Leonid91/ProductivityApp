// End-to-end check of the real app: starts the executable off-screen (no window is ever shown), drives the
// interface through the WebView2 debugging port, imports the sample statements through the in-app file
// browser, exercises the main features and saves screenshots.
//
//   node tools/e2e/run.mjs <ProductivityApp.exe> <samples folder> <output folder>
import { existsSync, mkdirSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
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
    const sel = `[data-draft="${file.replace(/"/g, '\\"')}"]`
    const group = groupOf(file)
    await p.mouseClick(`${sel} [role=combobox]`, 0)
    if (created.has(group)) {
      await p.clickText(created.get(group), { selector: '[role=option]', partial: true })
    } else {
      await p.clickText('New account', { selector: '[role=option]' })
      await sleep(300)
      await p.mouseClick(`${sel} [role=combobox]`, 1)
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
  check('accounts created once per account', accounts.length === 8, accounts.map((a) => a.name).join(', '))
  check('BNC savings account recognised', accounts.some((a) => a.kind === 'savings' && a.name.startsWith('Banque Nationale')))
  await p.scrollMain(0)
  await shot(p, '06-import-history.png')

  // ---- periods (the sketch)
  await p.clickText('Periods', { selector: 'nav button' })
  await p.waitFor(`document.querySelector('section h2') !== null`, 20000, 'period title')
  await sleep(1200)
  await shot(p, '07-periods-top.png')
  const title = await p.eval(`document.querySelector('section h2').innerText`)
  check('month title as on the sketch', /^[A-Z][a-z]+, from [A-Z][a-z]+ \d+ to [A-Z][a-z]+ \d+, \d{4}$/.test(title), title)
  await p.eval(`(() => { const s = document.querySelector('section'); const t = [...s.querySelectorAll('p, span')].find(e => e.innerText === 'Expenses'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 160 }) })()`)
  await sleep(800)
  await shot(p, '08-periods-totals-charts.png')
  await p.eval(`document.querySelector('main').scrollBy({ top: 420 })`)
  await sleep(600)
  await shot(p, '09-periods-daily-chart.png')

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

  // date format menu
  await p.scrollMain(0)
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

  // correcting a category teaches the dictionary
  const before = await p.eval(`document.querySelector('section tbody tr td:nth-child(3)').innerText`)
  await p.click('section tbody tr td:nth-child(2) button', 0)
  await sleep(300)
  await p.eval(`[...document.querySelectorAll('.MuiListSubheader-root')].find(e => /exceptional/i.test(e.innerText)).scrollIntoView({ block: 'start' })`)
  await sleep(300)
  await shot(p, '12a-category-picker.png')
  await p.clickText('Entertainment', { selector: '[role=menuitem]' })
  await p.waitFor(`document.body.innerText.includes('Saved for')`, 10000, 'learned toast')
  await shot(p, '12-learned.png')
  const rules = (await p.engine('brands.list')).rules
  check('correction learned', rules.some((r) => r.categoryId === 'entertainment'), `${before} -> Entertainment`)

  // year and several years
  await p.clickText('Year', { selector: 'button' })
  await p.waitFor(`document.querySelector('section h2')?.innerText.startsWith('2025, from')`, 20000, 'year title')
  await sleep(1200)
  await shot(p, '13-year.png')
  await p.clickText('Several years', { selector: 'button' })
  await sleep(1500)
  await shot(p, '14-several-years.png')
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
  await p.clickText('Cancel', { selector: '[role=dialog] button' })

  // calendar, brands, settings
  await p.clickText('Calendar', { selector: 'nav button' })
  await sleep(1500)
  await shot(p, '17-calendar.png')
  await p.clickText('Brands', { selector: 'nav button' })
  await sleep(1200)
  await shot(p, '18-brands.png')
  await p.clickText('Settings', { selector: 'nav button' })
  await sleep(800)
  await p.mouseClick('[role=combobox]', 4)
  await sleep(300)
  await shot(p, '19-settings-bank-menu.png')
  await p.clickText('Banque Nationale (BNC)', { selector: '[role=option]', partial: true })
  await sleep(500)
  check('bank focus saved', (await p.engine('app.init')).settings.bankFocus === 'bnc')
  await shot(p, '19-settings.png')

  // dark theme
  await p.clickText('Dark', { selector: 'button' })
  await sleep(600)
  await p.clickText('Periods', { selector: 'nav button' })
  await sleep(1500)
  await shot(p, '20-dark-periods.png')
  await p.eval(`(() => { const s = document.querySelector('section'); const t = [...s.querySelectorAll('p, span')].find(e => e.innerText === 'Expenses'); document.querySelector('main').scrollBy({ top: t.getBoundingClientRect().top - 160 }) })()`)
  await sleep(800)
  await shot(p, '21-dark-charts.png')
  await p.clickText('Calendar', { selector: 'nav button' })
  await sleep(1200)
  await shot(p, '22-dark-calendar.png')
  await p.engine('settings.set', { key: 'theme', value: 'light' })

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
