import { useMemo, useState } from 'react'
import { Box, Checkbox, MenuItem, TextField, Typography, useTheme } from '@mui/material'
import { LineChart } from '@mui/x-charts/LineChart'
import type { Slice, TransactionView } from '../api'
import { useApp } from '../AppContext'
import { formatDate, money, parts, monthName } from '../format'
import { sliceColor, tooltipOnPage } from './Charts'

const DEFAULT_SHOWN = 5

/** Every day from one ISO date to another, both included. */
function days(from: string, to: string): string[] {
  const out: string[] = []
  const a = parts(from)
  const end = Date.UTC(parts(to).y, parts(to).m - 1, parts(to).d)
  for (let t = Date.UTC(a.y, a.m - 1, a.d); t <= end; t += 86400000) out.push(new Date(t).toISOString().slice(0, 10))
  return out
}

/**
 * Expenses per category, one line each, one point per day; the line of all expenses is always there. The top five
 * categories are shown first; any number of them (none to all) can be chosen, or each one ticked on or off.
 */
export function CategoryLines({ from, to, transactions, categories, currency }: {
  from: string
  to: string
  transactions: TransactionView[]
  /** Expenses per category of the period, largest first (the same order and colours as the pie). */
  categories: Slice[]
  currency: string
}) {
  const theme = useTheme()
  const { category, settings } = useApp()
  const { series: palette, other } = theme.palette.chart
  const [chosen, setChosen] = useState<Set<string> | null>(null)
  const shown = chosen ?? new Set(categories.slice(0, DEFAULT_SHOWN).map((c) => c.key))
  const topCount = categories.findIndex((c) => !shown.has(c.key))
  const isTop = (topCount === -1 ? categories.length : topCount) === shown.size
  const count = shown.size

  const { axis, total, byCategory } = useMemo(() => {
    const axis = days(from, to)
    const index = new Map(axis.map((d, i) => [d, i]))
    const total = new Array<number>(axis.length).fill(0)
    const byCategory = new Map<string, number[]>()
    for (const t of transactions) {
      if (t.amount >= 0 || category(t.categoryId).kind === 'transfer') continue
      const i = index.get(t.date)
      if (i === undefined) continue
      total[i] += -t.amount
      let line = byCategory.get(t.categoryId)
      if (!line) byCategory.set(t.categoryId, (line = new Array<number>(axis.length).fill(0)))
      line[i] += -t.amount
    }
    // Cents only: sums of decimals stay clean in the tooltip.
    const round = (v: number[]) => v.map((x) => Math.round(x * 100) / 100)
    return { axis, total: round(total), byCategory: new Map([...byCategory].map(([k, v]) => [k, round(v)])) }
  }, [from, to, transactions, category])

  const max = Math.max(0, ...total)
  const oneMonth = from.slice(0, 7) === to.slice(0, 7)
  const long = axis.length > 400
  const tick = (d: string) => {
    const p = parts(d)
    if (oneMonth) return String(p.d)
    return long ? `${monthName(p.m).slice(0, 3)} ${p.y}` : `${monthName(p.m).slice(0, 3)} ${p.d}`
  }
  const every = axis.length <= 31 ? 1 : Math.ceil(axis.length / 12)
  const lines = categories.map((c, i) => ({ c, color: sliceColor(palette, other, c, i) })).filter((x) => shown.has(x.c.key))

  return (
    <Box data-category-lines sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, pb: 1, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1">Expenses per category, per day</Typography>
      {max === 0 ? (
        <Typography color="text.secondary" sx={{ py: 6, textAlign: 'center' }}>No expenses</Typography>
      ) : (
        <Box sx={{ display: 'flex', gap: 2, alignItems: 'flex-start', flexWrap: 'wrap' }}>
          <Box sx={{ flex: '1 1 520px', minWidth: 0 }}>
            <LineChart
              height={320}
              hideLegend
              slotProps={tooltipOnPage}
              grid={{ horizontal: true }}
              margin={{ left: 4, right: 12, top: 16, bottom: 0 }}
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
                tickLabelStyle: { fontSize: 11, fill: theme.palette.chart.axis },
                valueFormatter: (v: number) => money(v, currency, { compact: max >= 10000, whole: true }),
              }]}
              series={[
                {
                  id: '__total',
                  data: total,
                  label: 'Total',
                  color: theme.palette.text.primary,
                  showMark: false,
                  valueFormatter: (v) => (v === null ? '' : money(v, currency)),
                },
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
          <Box sx={{ flex: '0 1 280px', minWidth: 220, pt: 1 }}>
            <TextField
              select
              size="small"
              label="Categories shown"
              value={isTop ? String(count) : 'custom'}
              onChange={(e) => { const n = Number(e.target.value); if (!Number.isNaN(n)) setChosen(new Set(categories.slice(0, n).map((c) => c.key))) }}
              sx={{ width: '100%' }}
            >
              {categories.map((_, i) => i).concat(categories.length).map((n) => (
                <MenuItem key={n} value={String(n)}>{n === 0 ? 'None' : n === categories.length ? `All (${n})` : `Top ${n}`}</MenuItem>
              ))}
              {!isTop && <MenuItem value="custom" sx={{ display: 'none' }}>{`${count} chosen`}</MenuItem>}
            </TextField>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 1.5, mb: 0.5, pl: 0.5 }}>
              <Box sx={{ width: 14, height: 3, borderRadius: 2, bgcolor: 'text.primary', flex: '0 0 14px' }} />
              <Typography variant="body2" sx={{ flex: 1, fontWeight: 600 }}>Total</Typography>
            </Box>
            <Box component="ul" data-category-choices sx={{ listStyle: 'none', m: 0, p: 0, maxHeight: 236, overflowY: 'auto' }}>
              {categories.map((c, i) => (
                <Box component="li" key={c.key} sx={{ display: 'flex', alignItems: 'center', gap: 0.5, minWidth: 0 }}>
                  <Checkbox
                    size="small"
                    checked={shown.has(c.key)}
                    onChange={(e) => {
                      const next = new Set(shown)
                      if (e.target.checked) next.add(c.key); else next.delete(c.key)
                      setChosen(next)
                    }}
                    slotProps={{ input: { 'aria-label': c.label } }}
                    sx={{ p: 0.5, color: sliceColor(palette, other, c, i), '&.Mui-checked': { color: sliceColor(palette, other, c, i) } }}
                  />
                  <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{c.label}</Typography>
                  <Typography variant="body2" className="tnum" color="text.secondary">{money(c.amount, currency)}</Typography>
                </Box>
              ))}
            </Box>
          </Box>
        </Box>
      )}
    </Box>
  )
}
