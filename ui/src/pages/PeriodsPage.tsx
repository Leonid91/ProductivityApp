import { useCallback, useEffect, useMemo, useState } from 'react'
import { Box, Button, MenuItem, TextField, ToggleButton, ToggleButtonGroup } from '@mui/material'
import { DatePicker } from '@mui/x-date-pickers/DatePicker'
import dayjs, { type Dayjs } from 'dayjs'
import Add from '@mui/icons-material/AddRounded'
import UploadFile from '@mui/icons-material/UploadFileRounded'
import { api, type Granularity, type PeriodIndex, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { daysInMonth, iso } from '../format'
import { PeriodCard } from '../components/PeriodCard'
import { TransactionDialog, pickerFormats } from '../components/TransactionDialog'
import { EmptyState } from '../components/common'

interface Period { from: string; to: string; key: string }

export function PeriodsPage() {
  const { settings, dataVersion, fail, go } = useApp()
  const [index, setIndex] = useState<PeriodIndex | null>(null)
  const [mode, setMode] = useState<Granularity | 'custom'>('month')
  // Custom period: any two days, picked on the calendar or typed.
  const [customFrom, setCustomFrom] = useState<Dayjs | null>(null)
  const [customTo, setCustomTo] = useState<Dayjs | null>(null)
  const [currency, setCurrency] = useState(settings.currency)
  const [fromYear, setFromYear] = useState<number | null>(null)
  const [toYear, setToYear] = useState<number | null>(null)
  const [editing, setEditing] = useState<TransactionView | null>(null)
  const [adding, setAdding] = useState(false)
  const [brands, setBrands] = useState<string[]>([])

  useEffect(() => { setCurrency(settings.currency) }, [settings.currency])

  useEffect(() => {
    api.periods.index(currency).then((ix) => {
      // The data may be in other currencies only (a file's currency changed): open the main one.
      if (ix.currencies.length && !ix.currencies.includes(currency)) { setCurrency(ix.currencies[0]); return }
      setIndex(ix)
      if (ix.last) {
        const last = dayjs(ix.last + '-01')
        setCustomFrom((d) => d ?? last)
        setCustomTo((d) => d ?? last.endOf('month'))
      }
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
    if (mode === 'custom') {
      if (!customFrom?.isValid() || !customTo?.isValid() || customTo.isBefore(customFrom, 'day')) return []
      const from = customFrom.format('YYYY-MM-DD')
      const to = customTo.format('YYYY-MM-DD')
      return [{ from, to, key: `${from}_${to}` }]
    }
    if (fromYear === null || toYear === null) return []
    const a = Math.min(fromYear, toYear)
    const b = Math.max(fromYear, toYear)
    return [{ from: iso(a, 1, 1), to: iso(b, 12, 31), key: `${a}-${b}` }]
  }, [index, mode, fromYear, toYear, customFrom, customTo])

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
          <ToggleButton value="custom">Custom</ToggleButton>
        </ToggleButtonGroup>
        {mode === 'custom' && (
          <>
            <DatePicker label="From" value={customFrom} onChange={setCustomFrom} format={pickerFormats[settings.dateFormat] ?? 'DD MMMM YYYY'} slotProps={{ textField: { size: 'small', sx: { width: 230 } } }} />
            <DatePicker label="To" value={customTo} onChange={setCustomTo} minDate={customFrom ?? undefined} format={pickerFormats[settings.dateFormat] ?? 'DD MMMM YYYY'} slotProps={{ textField: { size: 'small', sx: { width: 230 } } }} />
          </>
        )}
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
            <PeriodCard
              key={`${mode}:${p.key}:${currency}`}
              from={p.from}
              to={p.to}
              granularity={mode === 'custom' ? 'range' : mode}
              currency={currency}
              onEdit={onEdit}
              showDays={mode === 'month' || mode === 'custom'}
            />
          ))}
        </Box>
      )}

      <TransactionDialog open={editing !== null || adding} tx={editing} brands={brands} onClose={() => { setEditing(null); setAdding(false) }} />
    </Box>
  )
}
