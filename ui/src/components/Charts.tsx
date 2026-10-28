import { Box, Typography, useTheme } from '@mui/material'
import { PieChart } from '@mui/x-charts/PieChart'
import { BarChart } from '@mui/x-charts/BarChart'
import type { Bar, Slice } from '../api'
import { money, percent } from '../format'

function sliceColor(colors: string[], other: string, slice: Slice, index: number) {
  return slice.key === '__other' ? other : colors[index % colors.length]
}

/** Donut of expenses with a legend that carries names, amounts and shares in text colours. */
export function ExpensePie({ title, slices, currency }: { title: string; slices: Slice[]; currency: string }) {
  const theme = useTheme()
  const { series, other } = theme.palette.chart
  const total = slices.reduce((s, x) => s + x.amount, 0)
  return (
    <Box sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, flex: '1 1 340px', minWidth: 0, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1" sx={{ mb: 1.5 }}>{title}</Typography>
      {slices.length === 0 ? (
        <Typography color="text.secondary" sx={{ py: 6, textAlign: 'center' }}>No expenses</Typography>
      ) : (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2.5 }}>
          <Box sx={{ position: 'relative', width: 168, height: 168, flex: '0 0 168px' }}>
            <PieChart
              width={168}
              height={168}
              margin={0}
              hideLegend
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
          <Box component="ul" sx={{ listStyle: 'none', m: 0, p: 0, flex: 1, minWidth: 0, display: 'grid', gap: 0.9 }}>
            {slices.map((s, i) => (
              <Box component="li" key={s.key} sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 0 }}>
                <Box sx={{ width: 10, height: 10, borderRadius: '3px', flex: '0 0 10px', bgcolor: sliceColor(series, other, s, i) }} />
                <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{s.label}</Typography>
                <Typography variant="body2" className="tnum" sx={{ fontWeight: 560 }}>{money(s.amount, currency)}</Typography>
                <Typography variant="body2" className="tnum" color="text.secondary" sx={{ width: 38, textAlign: 'right' }}>{percent(s.share)}</Typography>
              </Box>
            ))}
          </Box>
        </Box>
      )}
    </Box>
  )
}

/** Expenses per day (month view), per month (year view) or per year (long ranges). */
export function ExpenseBars({ bars, unit, currency }: { bars: Bar[]; unit: 'day' | 'month' | 'year'; currency: string }) {
  const theme = useTheme()
  const title = unit === 'day' ? 'Expenses per day' : unit === 'month' ? 'Expenses per month' : 'Expenses per year'
  const max = Math.max(0, ...bars.map((b) => b.expenses))
  const every = bars.length <= 14 ? 1 : bars.length <= 31 ? 5 : Math.ceil(bars.length / 12)
  return (
    <Box sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, pb: 1, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1">{title}</Typography>
      {max === 0 ? (
        <Typography color="text.secondary" sx={{ py: 6, textAlign: 'center' }}>No expenses</Typography>
      ) : (
        <BarChart
          height={250}
          hideLegend
          borderRadius={4}
          grid={{ horizontal: true }}
          margin={{ left: 4, right: 8, top: 16, bottom: 0 }}
          xAxis={[{
            scaleType: 'band',
            data: bars.map((b) => b.label),
            categoryGapRatio: bars.length > 20 ? 0.35 : 0.45,
            tickLabelInterval: (_v: unknown, i: number) => every === 1 || i === 0 || (i + 1) % every === 0,
            disableTicks: true,
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
}
