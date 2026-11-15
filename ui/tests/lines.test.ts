// Run with: node --test tests/  (Node 24 reads TypeScript as it is)
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { axisOf, categoryLines, type Spent } from '../src/lines.ts'

const october: Spent[] = [
  { date: '2026-10-02', spent: 100, line: 'groceries' },
  { date: '2026-10-02', spent: 20, line: 'restaurants' },
  { date: '2026-10-03', spent: 12.6, line: 'groceries' },
  { date: '2026-10-03', spent: 5, line: 'transport' },
]
const sum = (v: number[]) => Math.round(v.reduce((s, x) => s + x, 0) * 100) / 100

test('the selected total adds the ticked categories only, and changes with them', () => {
  const a = categoryLines(october, '2026-10-01', '2026-10-31', ['groceries'])
  assert.equal(sum(a.selectedTotal), 112.6)
  assert.deepEqual(a.selectedTotal.slice(1, 3), [100, 12.6])
  const b = categoryLines(october, '2026-10-01', '2026-10-31', ['groceries', 'transport'])
  assert.equal(sum(b.selectedTotal), 117.6)
  assert.equal(sum(categoryLines(october, '2026-10-01', '2026-10-31', []).selectedTotal), 0)
})

test('the total is every expense once, whatever is ticked: the selected total is never added to it', () => {
  for (const selected of [[], ['groceries'], ['groceries', 'restaurants', 'transport'], ['groceries', 'groceries']]) {
    const r = categoryLines(october, '2026-10-01', '2026-10-31', selected)
    assert.equal(sum(r.total), 137.6)
    assert.deepEqual(r.total.slice(1, 3), [120, 17.6])
  }
  // Everything ticked: the selected total is the total, not twice it.
  const all = categoryLines(october, '2026-10-01', '2026-10-31', ['groceries', 'restaurants', 'transport'])
  assert.deepEqual(all.selectedTotal, all.total)
})

test('a day per point within a month, a month per point beyond', () => {
  assert.equal(axisOf('2026-10-01', '2026-10-31').points.length, 31)
  const year = axisOf('2026-01-01', '2026-12-31')
  assert.deepEqual([year.unit, year.points.length, year.points[0], year.points[11]], ['month', 12, '2026-01', '2026-12'])
  const r = categoryLines([...october, { date: '2026-11-05', spent: 7, line: 'groceries' }], '2026-09-15', '2026-11-20', ['groceries'])
  assert.deepEqual(r.points, ['2026-09', '2026-10', '2026-11'])
  assert.deepEqual(r.selectedTotal, [0, 112.6, 7])
  assert.deepEqual(r.total, [0, 137.6, 7])
})
