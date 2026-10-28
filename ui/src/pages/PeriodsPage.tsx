import { useCallback, useEffect, useMemo, useState } from 'react'
import { Box, Button, MenuItem, TextField, ToggleButton, ToggleButtonGroup } from '@mui/material'
import Add from '@mui/icons-material/AddRounded'
import UploadFile from '@mui/icons-material/UploadFileRounded'
import { api, type Granularity, type PeriodIndex, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { daysInMonth, iso } from '../format'
import { PeriodCard } from '../components/PeriodCard'
import { TransactionDialog } from '../components/TransactionDialog'
import { EmptyState } from '../components/common'

interface Period { from: string; to: string; key: string }

export function PeriodsPage() {
  const { settings, dataVersion, fail, go } = useApp()
  const [index, setIndex] = useState<PeriodIndex | null>(null)
  const [mode, setMode] = useState<Granularity>('month')
  const [currency, setCurrency] = useState(settings.currency)
  const [fromYear, setFromYear] = useState<number | null>(null)
  const [toYear, setToYear] = useState<number | null>(null)
  const [editing, setEditing] = useState<TransactionView | null>(null)
  const [adding, setAdding] = useState(false)
  const [brands, setBrands] = useState<string[]>([])

  useEffect(() => { setCurrency(settings.currency) }, [settings.currency])

  useEffect(() => {
    api.periods.index(currency).then((ix) => {
      setIndex(ix)
      if (ix.years.length) {
        setFromYear((y) => y ?? ix.years[ix.years.length - 1])
        setToYear((y) => y ?? ix.years[0])
      }
    }, fail)
  }, [currency, dataVersion, fail])

  useEffect(() => {
    api.brands.list().then((b) => setBrands(b.brands.map((x) => x.brand)), () => setBrands([]))
  }, [dataVersion])

  const periods: Period[] = useMemo(() => {
    if (!index) return []
    if (mode === 'month') return index.months.map((m) => ({ from: iso(m.year, m.month, 1), to: iso(m.year, m.month, daysInMonth(m.year, m.month)), key: `${m.year}-${m.month}` }))
    if (mode === 'year') return index.years.map((y) => ({ from: iso(y, 1, 1), to: iso(y, 12, 31), key: `${y}` }))
    if (fromYear === null || toYear === null) return []
    const a = Math.min(fromYear, toYear)
    const b = Math.max(fromYear, toYear)
    return [{ from: iso(a, 1, 1), to: iso(b, 12, 31), key: `${a}-${b}` }]
  }, [index, mode, fromYear, toYear])

  const onEdit = useCallback((tx: TransactionView) => setEditing(tx), [])

  const empty = index !== null && index.months.length === 0

  return (
    <Box sx={{ maxWidth: 1180, mx: 'auto' }}>
      <Box
        sx={{
          position: 'sticky', top: 0, zIndex: 5, display: 'flex', alignItems: 'center', gap: 1.5, flexWrap: 'wrap',
          py: 2, mb: 1, bgcolor: 'background.default',
        }}
      >
        <ToggleButtonGroup exclusive value={mode} onChange={(_, v) => v && setMode(v)}>
          <ToggleButton value="month">Month</ToggleButton>
          <ToggleButton value="year">Year</ToggleButton>
          <ToggleButton value="range">Several years</ToggleButton>
        </ToggleButtonGroup>
        {mode === 'range' && index && index.years.length > 0 && (
          <>
            <TextField select size="small" label="From" value={fromYear ?? ''} onChange={(e) => setFromYear(Number(e.target.value))} sx={{ width: 110 }}>
              {[...index.years].reverse().map((y) => <MenuItem key={y} value={y}>{y}</MenuItem>)}
            </TextField>
            <TextField select size="small" label="To" value={toYear ?? ''} onChange={(e) => setToYear(Number(e.target.value))} sx={{ width: 110 }}>
              {index.years.map((y) => <MenuItem key={y} value={y}>{y}</MenuItem>)}
            </TextField>
          </>
        )}
        <Box sx={{ flex: 1 }} />
        {index && index.currencies.length > 1 && (
          <TextField select size="small" label="Currency" value={currency} onChange={(e) => setCurrency(e.target.value)} sx={{ width: 110 }}>
            {index.currencies.map((c) => <MenuItem key={c} value={c}>{c}</MenuItem>)}
          </TextField>
        )}
        <Button variant="contained" startIcon={<Add />} onClick={() => setAdding(true)}>Add operation</Button>
      </Box>

      {empty ? (
        <EmptyState
          icon={<UploadFile />}
          title={index.currencies.length ? `No operations in ${currency}` : 'No operations yet'}
          action={<Button variant="contained" onClick={() => go('import')}>Import a statement</Button>}
        />
      ) : (
        <Box sx={{ display: 'grid', gap: 3, pb: 6 }}>
          {periods.map((p) => (
            <PeriodCard key={`${mode}:${p.key}:${currency}`} from={p.from} to={p.to} granularity={mode} currency={currency} onEdit={onEdit} />
          ))}
        </Box>
      )}

      <TransactionDialog open={editing !== null || adding} tx={editing} brands={brands} onClose={() => { setEditing(null); setAdding(false) }} />
    </Box>
  )
}
