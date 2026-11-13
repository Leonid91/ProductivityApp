import { memo, useMemo } from 'react'
import { Box, Checkbox, Typography, useTheme } from '@mui/material'
import { LineChart } from '@mui/x-charts/LineChart'
import type { Slice, TransactionView } from '../api'
import { useApp } from '../AppContext'
import { formatDate, money, parts, monthName } from '../format'
import { useKept } from '../keep'
import { sliceColor, tooltipOnPage } from './Charts'

/** Height of the chart; the list of categories beside it has the same, so the two line up. */
const HEIGHT = 340

/** Every day from one ISO date to another, both included. */
function days(from: string, to: string): string[] {
  const out: string[] = []
  const a = parts(from)
  const end = Date.UTC(parts(to).y, parts(to).m - 1, parts(to).d)
  for (let t = Date.UTC(a.y, a.m - 1, a.d); t <= end; t += 86400000) out.push(new Date(t).toISOString().slice(0, 10))
  return out
}

/** What is drawn: the total line or not, and which categories. */
interface Shown { total: boolean; categories: string[] }

/**
 * Expenses per category, one line each, one point per day. Beside the chart, under "Categories", every category with
 * expenses in the period and the total, each ticked on or off; the total and the five largest categories are ticked
 * at first. The vertical scale follows the lines shown.
 */
export const CategoryLines = memo(function CategoryLines({ keep, from, to, transactions, categories, topLevel, currency }: {
  /** Where the choices are kept while the period is away from the screen. */
  keep: string
  from: string
  to: string
  transactions: TransactionView[]
  /** Expenses per category of the period, largest first (the same order and colours as the pie). */
  categories: Slice[]
  /** Nested categories drawn as the first-level category they are in. */
  topLevel: boolean
  currency: string
}) {
  const theme = useTheme()
  const { category, settings } = useApp()
  const { series: palette, other } = theme.palette.chart
  const [kept, setShown] = useKept<Shown | null>(keep, null)
  const shown: Shown = kept ?? { total: true, categories: categories.slice(0, 5).map((c) => c.key) }
  const on = new Set(shown.categories)
  const toggle = (key: string) => setShown({ ...shown, categories: on.has(key) ? shown.categories.filter((k) => k !== key) : [...shown.categories, key] })

  const { axis, total, byCategory } = useMemo(() => {
    const groupOf = (id: string) => {
      if (!topLevel) return id
      let c = category(id)
      for (let depth = 0; c.parentId && depth < 64; depth++) c = category(c.parentId)
      return c.id
    }
    const axis = days(from, to)
    const index = new Map(axis.map((d, i) => [d, i]))
    const total = new Array<number>(axis.length).fill(0)
    const byCategory = new Map<string, number[]>()
    for (const t of transactions) {
      if (t.amount >= 0 || category(t.categoryId).kind === 'transfer') continue
      const i = index.get(t.date)
      if (i === undefined) continue
      total[i] += -t.amount
      const key = groupOf(t.categoryId)
      let line = byCategory.get(key)
      if (!line) byCategory.set(key, (line = new Array<number>(axis.length).fill(0)))
      line[i] += -t.amount
    }
    // Cents only: sums of decimals stay clean in the tooltip.
    const round = (v: number[]) => v.map((x) => Math.round(x * 100) / 100)
    return { axis, total: round(total), byCategory: new Map([...byCategory].map(([k, v]) => [k, round(v)])) }
  }, [from, to, transactions, category, topLevel])

  const lines = categories.map((c, i) => ({ c, color: sliceColor(palette, other, c, i) })).filter((x) => on.has(x.c.key))
  const drawn = [...(shown.total ? [total] : []), ...lines.map((l) => byCategory.get(l.c.key) ?? [])]
  const max = Math.max(0, ...drawn.flat())
  const oneMonth = from.slice(0, 7) === to.slice(0, 7)
  const long = axis.length > 400
  const tick = (d: string) => {
    const p = parts(d)
    if (oneMonth) return String(p.d)
    return long ? `${monthName(p.m).slice(0, 3)} ${p.y}` : `${monthName(p.m).slice(0, 3)} ${p.d}`
  }
  const every = axis.length <= 31 ? 1 : Math.ceil(axis.length / 12)
  const spent = categories.reduce((s, c) => s + c.amount, 0)

  const row = (key: string, label: string, amount: number, color: string, checked: boolean, onChange: () => void, bold = false) => (
    <Box component="li" key={key} data-choice={key} sx={{ display: 'flex', alignItems: 'center', gap: 0.75, minWidth: 0 }}>
      <Checkbox
        size="small"
        checked={checked}
        onChange={onChange}
        slotProps={{ input: { 'aria-label': label } }}
        sx={{ p: 0.5, color, '&.Mui-checked': { color } }}
      />
      <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: bold ? 650 : undefined }}>{label}</Typography>
      <Typography variant="body2" className="tnum" color="text.secondary" sx={{ ml: 1 }}>{money(amount, currency)}</Typography>
    </Box>
  )

  return (
    <Box data-category-lines sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, pb: 1.5, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1">Expenses per category, per day</Typography>
      {categories.length === 0 ? (
        <Typography color="text.secondary" sx={{ py: 6, textAlign: 'center' }}>No expenses</Typography>
      ) : (
        <Box sx={{ display: 'flex', gap: 2.5, alignItems: 'flex-start', flexWrap: 'wrap', mt: 1 }}>
          <Box sx={{ flex: '1 1 520px', minWidth: 0 }}>
            <LineChart
              height={HEIGHT}
              hideLegend
              slotProps={tooltipOnPage}
              grid={{ horizontal: true }}
              margin={{ left: 4, right: 12, top: 12, bottom: 0 }}
              xAxis={[{
                scaleType: 'point',
                data: axis,
                disableTicks: true,
                tickLabelMinGap: 0,
                tickLabelInterval: (_v: unknown, i: number) => every === 1 || i % every === 0,
                tickLabelStyle: { fontSize: 11, fill: theme.palette.chart.axis },
                valueFormatter: (v: string, ctx) => (ctx.location === 'tooltip' ? formatDate(v, settings.dateFormat) : tick(v)),
              }]}
              yAxis={[{
                width: 64,
                disableTicks: true,
                tickNumber: 5,
                // The scale follows the lines shown: without the total, the categories fill the height.
                min: 0,
                max: max > 0 ? undefined : 1,
                tickLabelStyle: { fontSize: 11, fill: theme.palette.chart.axis },
                valueFormatter: (v: number) => money(v, currency, { compact: max >= 10000, whole: true }),
              }]}
              series={[
                ...(shown.total ? [{
                  id: '__total',
                  data: total,
                  label: 'Total',
                  color: theme.palette.text.primary,
                  showMark: false,
                  valueFormatter: (v: number | null) => (v === null ? '' : money(v, currency)),
                }] : []),
                ...lines.map(({ c, color }) => ({
                  id: c.key,
                  data: byCategory.get(c.key) ?? new Array<number>(axis.length).fill(0),
                  label: c.label,
                  color,
                  showMark: false,
                  valueFormatter: (v: number | null) => (v === null ? '' : money(v, currency)),
                })),
              ]}
              sx={{
                '& .MuiChartsGrid-line': { stroke: theme.palette.chart.grid, strokeDasharray: 'none' },
                '& .MuiChartsAxis-line': { stroke: 'transparent' },
                '& .MuiLineChart-line': { strokeWidth: 1.75 },
                '& .MuiLineChart-line[data-series="__total"]': { strokeWidth: 2.5 },
              }}
            />
          </Box>
          <Box sx={{ flex: '0 1 300px', minWidth: 230, height: HEIGHT, display: 'flex', flexDirection: 'column' }}>
            <Typography variant="subtitle2" color="text.secondary" sx={{ mb: 0.5, pl: 0.5 }}>Categories</Typography>
            <Box component="ul" data-category-choices sx={{ listStyle: 'none', m: 0, p: 0, flex: 1, minHeight: 0, overflowY: 'auto' }}>
              {row('__total', 'Total', spent, theme.palette.text.primary, shown.total, () => setShown({ ...shown, total: !shown.total }), true)}
              {categories.map((c, i) => row(c.key, c.label, c.amount, sliceColor(palette, other, c, i), on.has(c.key), () => toggle(c.key)))}
            </Box>
          </Box>
        </Box>
      )}
    </Box>
  )
})
