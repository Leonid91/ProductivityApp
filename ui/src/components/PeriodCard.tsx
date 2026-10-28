import { useEffect, useRef, useState } from 'react'
import { Box, Button, Skeleton, Typography, useTheme } from '@mui/material'
import PictureAsPdf from '@mui/icons-material/PictureAsPdfOutlined'
import { api, type Granularity, type PeriodSummary, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { money } from '../format'
import { ExpenseBars, ExpensePie } from './Charts'
import { ExportDialog } from './ExportDialog'
import { TransactionTable } from './TransactionTable'

function Stat({ label, value, color }: { label: string; value: string; color?: string }) {
  return (
    <Box sx={{ flex: '1 1 180px', borderRadius: '18px', bgcolor: 'surface.sunken', px: 2.5, py: 2 }}>
      <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 520 }}>{label}</Typography>
      <Typography className="tnum" sx={{ fontSize: 26, fontWeight: 650, letterSpacing: '-0.02em', mt: 0.25, color }}>{value}</Typography>
    </Box>
  )
}

/**
 * One period, as on the sketch: title, operations table, Expenses / Earnings / Balance, the two expense pies,
 * the expenses-per-day chart and the PDF export.
 */
export function PeriodCard({ from, to, granularity, currency, onEdit, onLoaded }: {
  from: string
  to: string
  granularity: Granularity
  currency: string
  onEdit: (tx: TransactionView) => void
  onLoaded?: (s: PeriodSummary) => void
}) {
  const { dataVersion, fail } = useApp()
  const theme = useTheme()
  const ref = useRef<HTMLDivElement>(null)
  const [visible, setVisible] = useState(false)
  const [summary, setSummary] = useState<PeriodSummary | null>(null)
  const [exporting, setExporting] = useState(false)

  // Load a period only when it comes near the screen: years of history stay fast.
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const io = new IntersectionObserver((entries) => { if (entries.some((e) => e.isIntersecting)) setVisible(true) }, { rootMargin: '900px 0px' })
    io.observe(el)
    return () => io.disconnect()
  }, [])

  useEffect(() => {
    if (!visible) return
    let live = true
    api.periods.summary(from, to, granularity, currency).then((s) => { if (live) { setSummary(s); onLoaded?.(s) } }, fail)
    return () => { live = false }
  }, [visible, from, to, granularity, currency, dataVersion, fail, onLoaded])

  const balanceColor = !summary ? undefined
    : summary.balance > 0 ? theme.palette.money.positive
    : summary.balance < 0 ? theme.palette.money.negative
    : theme.palette.money.zero

  return (
    <Box
      ref={ref}
      component="section"
      data-period={from}
      sx={{ border: 1, borderColor: 'divider', borderRadius: '24px', bgcolor: 'background.paper', p: { xs: 2.5, md: 3.5 }, minHeight: summary ? undefined : 560 }}
    >
      {!summary ? (
        <>
          <Skeleton variant="text" width="55%" height={40} />
          <Skeleton variant="rounded" height={220} sx={{ mt: 2, borderRadius: 3 }} />
          <Skeleton variant="rounded" height={90} sx={{ mt: 3, borderRadius: 3 }} />
        </>
      ) : (
        <>
          <Typography variant="h5" component="h2" sx={{ mb: 2.5 }}>{summary.title}</Typography>
          <TransactionTable transactions={summary.transactions} currency={currency} paged={granularity !== 'month'} onEdit={onEdit} />

          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: 3.5 }}>
            <Stat label="Expenses" value={money(summary.expenses, currency)} />
            <Stat label="Earnings" value={money(summary.earnings, currency)} />
            <Stat label="Balance" value={money(summary.balance, currency, { sign: true })} color={balanceColor} />
          </Box>

          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: 1.5 }}>
            <ExpensePie title="Expenses per category" slices={summary.categoryPie} currency={currency} />
            <ExpensePie title="Expenses per brand" slices={summary.brandPie} currency={currency} />
          </Box>
          <Box sx={{ mt: 1.5 }}>
            <ExpenseBars bars={summary.series} unit={summary.seriesUnit} currency={currency} />
          </Box>

          <Box sx={{ display: 'flex', justifyContent: 'flex-end', mt: 2.5 }}>
            <Button variant="outlined" startIcon={<PictureAsPdf />} onClick={() => setExporting(true)}>Export to PDF</Button>
          </Box>
          <ExportDialog
            open={exporting}
            onClose={() => setExporting(false)}
            from={from}
            to={to}
            granularity={granularity}
            currency={currency}
            title={summary.title}
          />
        </>
      )}
    </Box>
  )
}
