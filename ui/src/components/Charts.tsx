import { memo, type ReactNode } from 'react'
import { Box, Checkbox, FormControlLabel, Typography, useTheme } from '@mui/material'
import { PieChart } from '@mui/x-charts/PieChart'
import { BarChart } from '@mui/x-charts/BarChart'
import type { Bar, Slice } from '../api'
import { money, percent } from '../format'
import { useAmountsHidden } from '../mask'
import { useKept } from '../keep'

/**
 * Chart tooltips go on the page itself: inside a period (drawn with content-visibility, which makes the period the
 * frame of fixed-position elements) they would be placed far from the pointer, off the screen.
 */
export const tooltipOnPage = { tooltip: { container: () => document.body } }

/** HSL to #rrggbb, the same arithmetic as the PDF palette (Palette.Series in the engine). */
function hsl(h: number, s: number, l: number): string {
  const c = (1 - Math.abs(2 * l - 1)) * s
  const x = c * (1 - Math.abs(((h / 60) % 2) - 1))
  const m = l - c / 2
  const [r, g, b] = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x]
  const hex = (v: number) => Math.round((v + m) * 255).toString(16).padStart(2, '0')
  return `#${hex(r)}${hex(g)}${hex(b)}`
}

/** The eight series colours, then evenly spread hues: any number of slices stays distinguishable. */
export function sliceColor(colors: string[], other: string, slice: Slice, index: number) {
  if (slice.key === '__other') return other
  if (index < colors.length) return colors[index]
  return hsl(((index - colors.length) * 137.508) % 360, 0.55, 0.52)
}

/** Donut with a legend that carries names, amounts and shares in text colours. */
export const ExpensePie = memo(function ExpensePie({ title, slices, currency, empty = 'No expenses', footer }: { title: string; slices: Slice[]; currency: string; empty?: string; footer?: ReactNode }) {
  useAmountsHidden()
  const theme = useTheme()
  const { series, other } = theme.palette.chart
  const total = slices.reduce((s, x) => s + x.amount, 0)
  return (
    <Box sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, flex: '1 1 340px', minWidth: 0, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1" sx={{ mb: 1.5 }}>{title}</Typography>
      {slices.length === 0 ? (
        <Typography color="text.secondary" sx={{ py: 6, textAlign: 'center' }}>{empty}</Typography>
      ) : (
        <Box data-pie={title} sx={{ display: 'flex', alignItems: slices.length > 12 ? 'flex-start' : 'center', gap: 2.5 }}>
          <Box sx={{ position: 'relative', width: 168, height: 168, flex: '0 0 168px' }}>
            <PieChart
              width={168}
              height={168}
              margin={0}
              hideLegend
              slotProps={tooltipOnPage}
              series={[{
                data: slices.map((s, i) => ({ id: s.key, value: s.amount, label: s.label, color: sliceColor(series, other, s, i) })),
                innerRadius: 54,
                outerRadius: 82,
                paddingAngle: 1.2,
                cornerRadius: 4,
                highlightScope: { fade: 'global', highlight: 'item' },
                faded: { additionalRadius: -2, color: theme.palette.mode === 'dark' ? '#3a3a37' : '#e2e1dc' },
                valueFormatter: (v) => money(v.value, currency),
              }]}
            />
            <Box sx={{ position: 'absolute', inset: 0, display: 'grid', placeItems: 'center', pointerEvents: 'none', textAlign: 'center' }}>
              <Box>
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', lineHeight: 1.2 }}>Total</Typography>
                <Typography className="tnum" sx={{ fontWeight: 650, fontSize: 14 }}>{money(total, currency, { compact: total >= 100000 })}</Typography>
              </Box>
            </Box>
          </Box>
          <Box component="ul" sx={{ listStyle: 'none', m: 0, p: 0, flex: 1, minWidth: 0, display: 'grid', gap: slices.length > 14 ? 0.5 : 0.9 }}>
            {slices.map((s, i) => (
              <Box component="li" key={s.key} sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 0 }}>
                <Box sx={{ width: 10, height: 10, borderRadius: '3px', flex: '0 0 10px', bgcolor: sliceColor(series, other, s, i) }} />
                <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{s.label}</Typography>
                <Typography variant="body2" className="tnum" sx={{ fontWeight: 560 }}>{money(s.amount, currency)}</Typography>
                <Typography variant="body2" className="tnum" color="text.secondary" sx={{ width: 58, flex: '0 0 58px', textAlign: 'right' }}>{percent(s.share)}</Typography>
              </Box>
            ))}
          </Box>
        </Box>
      )}
      {footer}
    </Box>
  )
})

/**
 * Income and expenses per day (a period within a month) or per month: two bars side by side, income in green and
 * expenses in red, both drawn upwards. Either can be unticked.
 */
export const IncomeExpenseBars = memo(function IncomeExpenseBars({ bars, unit, currency, keep }: { bars: Bar[]; unit: 'day' | 'month' | 'year'; currency: string; keep: string }) {
  useAmountsHidden()
  const theme = useTheme()
  const [show, setShow] = useKept(keep, { income: true, expenses: true })
  const per = unit === 'day' ? 'day' : unit === 'month' ? 'month' : 'year'
  const max = Math.max(0, ...bars.map((b) => Math.max(show.expenses ? b.expenses : 0, show.income ? b.earnings : 0)))
  const any = bars.some((b) => b.expenses > 0 || b.earnings > 0)
  // Every day of a month is on the axis, from the 1st to the last; longer series keep about twelve labels.
  const every = bars.length <= 31 ? 1 : Math.ceil(bars.length / 12)
  const tick = (i: number) => (every === 1 || i === 0 || (i + 1) % every === 0)
  const series = [
    ...(show.income ? [{ id: 'income', data: bars.map((b) => b.earnings), color: theme.palette.money.positive, label: 'Income', valueFormatter: (v: number | null) => (v === null ? '' : money(v, currency)) }] : []),
    ...(show.expenses ? [{ id: 'expenses', data: bars.map((b) => b.expenses), color: theme.palette.money.negative, label: 'Expenses', valueFormatter: (v: number | null) => (v === null ? '' : money(v, currency)) }] : []),
  ]
  const box = (key: 'income' | 'expenses', label: string, color: string) => (
    <FormControlLabel
      data-bars-choice={key}
      control={<Checkbox size="small" checked={show[key]} onChange={() => setShow({ ...show, [key]: !show[key] })} sx={{ p: 0.5, color, '&.Mui-checked': { color } }} />}
      label={<Typography variant="body2">{label}</Typography>}
      sx={{ mr: 1.5, ml: 0 }}
    />
  )
  return (
    <Box data-bars sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, pb: 1, bgcolor: 'background.paper' }}>
      <Box sx={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: 1 }}>
        <Typography variant="subtitle1" sx={{ flex: 1 }}>Income and expenses per {per}</Typography>
        {box('income', 'Income', theme.palette.money.positive)}
        {box('expenses', 'Expenses', theme.palette.money.negative)}
      </Box>
      {!any || series.length === 0 ? (
        <Typography color="text.secondary" sx={{ py: 6, textAlign: 'center' }}>{!any ? 'No income or expenses' : 'Tick income or expenses'}</Typography>
      ) : (
        <BarChart
          height={250}
          hideLegend
          slotProps={tooltipOnPage}
          borderRadius={3}
          grid={{ horizontal: true }}
          margin={{ left: 4, right: 8, top: 16, bottom: 0 }}
          xAxis={[{
            scaleType: 'band',
            data: bars.map((b) => b.label),
            categoryGapRatio: bars.length > 20 ? 0.25 : 0.4,
            barGapRatio: 0.1,
            tickLabelInterval: (_v: string, i: number) => tick(i),
            disableTicks: true,
            tickLabelMinGap: 0,
            tickLabelStyle: { fontSize: 11, fill: theme.palette.chart.axis },
            valueFormatter: (v: string, ctx) => (ctx.location === 'tooltip' ? (unit === 'day' ? `Day ${v}` : v) : v),
          }]}
          yAxis={[{
            width: 64,
            disableTicks: true,
            tickLabelStyle: { fontSize: 11, fill: theme.palette.chart.axis },
            valueFormatter: (v: number) => money(v, currency, { compact: max >= 10000, whole: true }),
            tickNumber: 4,
          }]}
          series={series}
          sx={{
            '& .MuiChartsGrid-line': { stroke: theme.palette.chart.grid, strokeDasharray: 'none' },
            '& .MuiChartsAxis-line': { stroke: 'transparent' },
          }}
        />
      )}
    </Box>
  )
})
