import { memo } from 'react'
import { Box, Typography, useTheme } from '@mui/material'
import { PieChart } from '@mui/x-charts/PieChart'
import { BarChart } from '@mui/x-charts/BarChart'
import type { Bar, Slice } from '../api'
import { money, percent } from '../format'

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
export const ExpensePie = memo(function ExpensePie({ title, slices, currency, empty = 'No expenses' }: { title: string; slices: Slice[]; currency: string; empty?: string }) {
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
    </Box>
  )
})

/** Expenses per day (month view), per month (year view) or per year (long ranges). */
export const ExpenseBars = memo(function ExpenseBars({ bars, unit, currency }: { bars: Bar[]; unit: 'day' | 'month' | 'year'; currency: string }) {
  const theme = useTheme()
  const title = unit === 'day' ? 'Expenses per day' : unit === 'month' ? 'Expenses per month' : 'Expenses per year'
  const max = Math.max(0, ...bars.map((b) => b.expenses))
  // Every day of a month is on the axis, from the 1st to the last; longer series keep about twelve labels.
  const every = bars.length <= 31 ? 1 : Math.ceil(bars.length / 12)
  return (
    <Box data-bars sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, pb: 1, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1">{title}</Typography>
      {max === 0 ? (
        <Typography color="text.secondary" sx={{ py: 6, textAlign: 'center' }}>No expenses</Typography>
      ) : (
        <BarChart
          height={250}
          hideLegend
          slotProps={tooltipOnPage}
          borderRadius={4}
          grid={{ horizontal: true }}
          margin={{ left: 4, right: 8, top: 16, bottom: 0 }}
          xAxis={[{
            scaleType: 'band',
            data: bars.map((b) => b.label),
            categoryGapRatio: bars.length > 20 ? 0.3 : 0.45,
            tickLabelInterval: (_v: unknown, i: number) => every === 1 || i === 0 || (i + 1) % every === 0,
            disableTicks: true,
            // Never drop a day label to make room: all of them are wanted.
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
          series={[{
            data: bars.map((b) => b.expenses),
            color: theme.palette.chart.series[0],
            label: 'Expenses',
            valueFormatter: (v) => (v === null ? '' : money(v, currency)),
          }]}
          sx={{
            '& .MuiChartsGrid-line': { stroke: theme.palette.chart.grid, strokeDasharray: 'none' },
            '& .MuiChartsAxis-line': { stroke: 'transparent' },
          }}
        />
      )}
    </Box>
  )
})
