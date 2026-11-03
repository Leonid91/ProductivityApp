// Frame rate and memory of the real app on large data: imports the files made by "SampleStatements big" through
// the engine, then scrolls and switches views while counting the frames the interface draws.
//
//   node tools/e2e/perf.mjs <ProductivityApp.exe> <big folder> <output folder>
import { execSync } from 'node:child_process'
import { mkdirSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { launch, sleep } from './cdp.mjs'

const [exe, big, out] = process.argv.slice(2)
rmSync(out, { recursive: true, force: true })
mkdirSync(out, { recursive: true })
const p = await launch(exe, ['--offscreen', '--data-dir', join(out, 'data'), '--size', '1440x920'], 9334)
const results = {}
const note = (k, v) => { results[k] = v; console.log(`${k}: ${v}`) }

// Frames drawn per second while `step` runs once per frame for `seconds`.
const fps = (step, seconds = 4) => p.eval(`new Promise((done) => {
  let frames = 0; const end = performance.now() + ${seconds * 1000}
  const tick = () => { frames++; (${step})(); if (performance.now() < end) requestAnimationFrame(tick); else done(Math.round(frames / ${seconds})) }
  requestAnimationFrame(tick)
})`)
const memory = async () => {
  const heap = await p.eval('Math.round(performance.memory.usedJSHeapSize / 1048576)')
  const pid = p.child?.pid
  const ws = pid ? Number(execSync(`powershell -NoProfile -Command "(Get-Process -Id ${pid}).WorkingSet64"`).toString().trim()) : 0
  return `${heap} MB interface heap, ${Math.round(ws / 1048576)} MB engine process`
}

try {
  await p.waitFor(`document.querySelector('nav') !== null`)
  for (const f of readdirSync(big).filter((x) => x.endsWith('.csv'))) {
    const d = await p.engine('import.parseFile', { path: join(big, f) })
    await p.engine('import.commit', { draftId: d.id, accountId: null, newAccountName: null, currency: f.includes('Caisse') ? 'EUR' : 'CAD', kind: 'bank', edits: [], bankId: null, newBankName: f.split(' ')[0] })
  }
  await p.eval('location.reload()')
  await p.waitFor(`document.querySelector('section h2') !== null`, 60000, 'periods')
  await sleep(1500)
  note('memory at start', await memory())
  note('idle fps', await fps('() => {}', 3))
  note('scrolling months fps', await fps(`() => document.querySelector('main').scrollBy(0, 60)`, 6))
  note('memory after scrolling months', await memory())
  await p.clickText('Year', { selector: 'button' })
  await sleep(300)
  note('switching to year fps', await fps('() => {}', 3))
  note('scrolling years fps', await fps(`() => document.querySelector('main').scrollBy(0, 60)`, 5))
  await p.clickText('Several years', { selector: 'button' })
  note('ten years view fps', await fps('() => {}', 3))
  await p.clickText('Month', { selector: 'button' })
  await sleep(500)
  await p.click('section tbody tr td:nth-child(2) button', 0)
  await sleep(400)
  note('category picker scrolling fps', await fps(`() => document.querySelector('[role=menu]')?.parentElement?.scrollBy(0, 20)`, 3))
  await p.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await sleep(400)
  await p.clickText('Calendar', { selector: 'nav button' })
  note('calendar fps', await fps('() => {}', 3))
  await p.clickText('Settings', { selector: 'nav button' })
  await p.clickText('Definitions', { selector: 'button' })
  await sleep(800)
  note('brands list scrolling fps', await fps(`() => document.querySelector('main').scrollBy(0, 60)`, 3))
  note('memory at end', await memory())
} finally {
  await p.close()
}
writeFileSync(join(out, 'perf.json'), JSON.stringify(results, null, 1))
