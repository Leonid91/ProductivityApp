// The figures of the chart "Expenses per category": one point per day for a period within one month, one per month
// beyond. No imports: it is tested on its own (tests/lines.test.ts).

/** An expense: its day (ISO), what was spent (positive) and the line it belongs to (its category, or its main one). */
export interface Spent { date: string; spent: number; line: string }

/** The points of the axis: every day of a period within one month, else every month ("2026-10") it touches. */
export function axisOf(from: string, to: string): { unit: 'day' | 'month'; points: string[] } {
  if (from.slice(0, 7) === to.slice(0, 7)) {
    const points: string[] = []
    const [y, m, d] = from.split('-').map(Number)
    const last = Number(to.slice(8, 10))
    for (let day = d; day <= last; day++) points.push(`${y}-${String(m).padStart(2, '0')}-${String(day).padStart(2, '0')}`)
    return { unit: 'day', points }
  }
  const points: string[] = []
  let [y, m] = from.split('-').map(Number)
  const [ty, tm] = to.split('-').map(Number)
  while (y * 12 + m <= ty * 12 + tm) {
    points.push(`${y}-${String(m).padStart(2, '0')}`)
    if (++m > 12) { m = 1; y++ }
  }
  return { unit: 'month', points }
}

const cents = (v: number[]) => v.map((x) => Math.round(x * 100) / 100)

/**
 * Total: every expense of the period, whatever is ticked. Each line: the expenses of its category. Selected total:
 * the lines ticked added together (never the total, nor itself, added again).
 */
export function categoryLines(expenses: Spent[], from: string, to: string, selected: string[]) {
  const { unit, points } = axisOf(from, to)
  const index = new Map(points.map((p, i) => [p, i]))
  const total = new Array<number>(points.length).fill(0)
  const lines = new Map<string, number[]>()
  for (const e of expenses) {
    const i = index.get(unit === 'day' ? e.date : e.date.slice(0, 7))
    if (i === undefined) continue
    total[i] += e.spent
    let line = lines.get(e.line)
    if (!line) lines.set(e.line, (line = new Array<number>(points.length).fill(0)))
    line[i] += e.spent
  }
  const chosen = new Array<number>(points.length).fill(0)
  for (const key of new Set(selected)) {
    const line = lines.get(key)
    if (line) for (let i = 0; i < line.length; i++) chosen[i] += line[i]
  }
  return { unit, points, total: cents(total), selectedTotal: cents(chosen), lines: new Map([...lines].map(([k, v]) => [k, cents(v)])) }
}
