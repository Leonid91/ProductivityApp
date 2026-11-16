// Quick timing of the period changes only (start-up, Year, All time, Month), to compare two builds:
//   node tests/Finance.PerformanceTests/perf-quick.mjs <ProductivityApp.exe> <big folder> <output folder>
import { mkdirSync, readdirSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { launch, sleep } from '../Finance.AppDriver/cdp.mjs'

const [exe, big, out] = process.argv.slice(2)
rmSync(out, { recursive: true, force: true })
mkdirSync(out, { recursive: true })
const p = await launch(exe, ['--offscreen', '--data-dir', join(out, 'data'), '--size', '1440x920'], 9335)
const settled = `(() => { const m = document.querySelector('main').getBoundingClientRect(); const s = [...document.querySelectorAll('section[data-period]')]
  .filter((x) => { const r = x.getBoundingClientRect(); return r.bottom > m.top && r.top < m.bottom }); return s.length > 0 && s.every((x) => !x.querySelector('.MuiSkeleton-root')) })()`
const timed = (text) => p.eval(`new Promise((done) => {
  const t0 = performance.now(); [...document.querySelectorAll('main button')].find((b) => b.innerText.trim() === ${JSON.stringify(text)}).click()
  let seen = 0
  const tick = () => { if (${settled}) { if (++seen === 2) return done(Math.round(performance.now() - t0)) } else seen = 0; if (performance.now() - t0 > 30000) return done(-1); requestAnimationFrame(tick) }
  requestAnimationFrame(tick)
})`)
try {
  await p.waitFor(`document.querySelector('nav') !== null`)
  for (const f of readdirSync(big).filter((x) => x.endsWith('.csv'))) {
    const d = await p.engine('import.parseFile', { path: join(big, f) })
    await p.engine('import.commit', { draftId: d.id, accountId: null, newAccountName: null, currency: f.includes('Caisse') ? 'EUR' : 'CAD', kind: 'bank', edits: [], bankId: null, newBankName: f.split(' ')[0] })
  }
  await p.engine('rates.update')
  await p.send('Page.enable')
  await p.send('Page.addScriptToEvaluateOnNewDocument', { source: `(() => { const tick = () => { const m = document.querySelector('main'); if (m && ${settled}) { window.__startupMs = Math.round(performance.now()); return } requestAnimationFrame(tick) }; requestAnimationFrame(tick) })()` })
  const rows = { startup: [], year: [], all: [], month: [] }
  for (let i = 0; i < 4; i++) {
    await p.eval('location.reload()')
    await p.waitFor(`window.__startupMs !== undefined`, 60000, 'start')
    rows.startup.push(await p.eval('window.__startupMs'))
    await sleep(1500)
    rows.year.push(await timed('Year')); await sleep(1500)
    rows.all.push(await timed('All time')); await sleep(1500)
    rows.month.push(await timed('Month')); await sleep(1500)
  }
  for (const [k, v] of Object.entries(rows)) console.log(`${k} ms: ${v.join(', ')}  (median ${[...v].sort((a, b) => a - b)[Math.floor(v.length / 2)]})`)
} finally {
  await p.close()
}
