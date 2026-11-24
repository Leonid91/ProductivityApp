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

// Each case is a test of its own (the theories of these tests): its name says the case.
const ticked: [string[], number, number[]][] = [
  [['groceries'], 112.6, [100, 12.6]],
  [['groceries', 'transport'], 117.6, [100, 17.6]],
  [[], 0, [0, 0]],
]
ticked.forEach(([selected, total, days]) =>
  test(`categoryLines_CategoriesTicked_SelectedTotalAddsThoseOnly ([${selected}])`, () => {
    const result = categoryLines(october, '2026-10-01', '2026-10-31', selected)

    assert.deepEqual([sum(result.selectedTotal), result.selectedTotal.slice(1, 3)], [total, days])
  }))

const anySelection = [[], ['groceries'], ['groceries', 'restaurants', 'transport'], ['groceries', 'groceries']]
anySelection.forEach((selected) =>
  test(`categoryLines_AnyCategoriesTicked_TotalIsEveryExpenseOnce ([${selected}])`, () => {
    const result = categoryLines(october, '2026-10-01', '2026-10-31', selected)

    assert.deepEqual([sum(result.total), result.total.slice(1, 3)], [137.6, [120, 17.6]])
  }))

test('categoryLines_EveryCategoryTicked_SelectedTotalIsTheTotalNotTwiceIt', () => {
  const result = categoryLines(october, '2026-10-01', '2026-10-31', ['groceries', 'restaurants', 'transport'])

  assert.deepEqual(result.selectedTotal, result.total)
})

test('axisOf_Month_HasADayPerPoint', () => {
  const result = axisOf('2026-10-01', '2026-10-31')

  assert.equal(result.points.length, 31)
})

test('axisOf_Year_HasAMonthPerPoint', () => {
  const result = axisOf('2026-01-01', '2026-12-31')

  assert.deepEqual([result.unit, result.points.length, result.points[0], result.points[11]], ['month', 12, '2026-01', '2026-12'])
})

test('categoryLines_PeriodBeyondAMonth_AddsEachMonthsExpensesInItsPoint', () => {
  const result = categoryLines([...october, { date: '2026-11-05', spent: 7, line: 'groceries' }], '2026-09-15', '2026-11-20', ['groceries'])

  assert.deepEqual([result.points, result.selectedTotal, result.total], [['2026-09', '2026-10', '2026-11'], [0, 112.6, 7], [0, 137.6, 7]])
})
