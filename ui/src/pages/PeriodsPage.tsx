import { useCallback, useDeferredValue, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Box, Button, ToggleButton, ToggleButtonGroup } from '@mui/material'
import { DatePicker } from '@mui/x-date-pickers/DatePicker'
import dayjs, { type Dayjs } from 'dayjs'
import Add from '@mui/icons-material/AddRounded'
import UploadFile from '@mui/icons-material/UploadFileRounded'
import { api, type PeriodIndex, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { daysInMonth, iso } from '../format'
import { loadCharts, PeriodCard, type PeriodView } from '../components/PeriodCard'
import { TransactionDialog, pickerFormats } from '../components/TransactionDialog'
import { EmptyState } from '../components/common'

interface Period { from: string; to: string; key: string }

export function PeriodsPage() {
  const { settings, dataVersion, fail, go } = useApp()
  // The charts are not needed to start: their code is read once the browser is idle, so Analytics opens at once.
  useEffect(() => {
    const id = requestIdleCallback(() => void loadCharts(), { timeout: 4000 })
    return () => cancelIdleCallback(id)
  }, [])
  const [index, setIndex] = useState<PeriodIndex | null>(null)
  const [mode, setMode] = useState<'month' | 'year' | 'all' | 'custom'>('month')
  // The periods follow the button in the background: the button answers at once, the periods of the old kind are
  // replaced a little at a time (the screen keeps moving).
  const listMode = useDeferredValue(mode)
  // Second layer, the same for every kind of period: its summary (balances), statements (tables) or analytics (charts).
  const [view, setView] = useState<'summary' | 'statements' | 'analytics'>('statements')
  // Analytics in two parts: the charts of the spending and income (General), the money moved between accounts (Transfers).
  const [analytics, setAnalytics] = useState<'general' | 'transfers'>('general')
  // The parts of the analytics sit under the Analytics button, on a line of their own.
  const analyticsButton = useRef<HTMLButtonElement>(null)
  const [analyticsLeft, setAnalyticsLeft] = useState(0)
  useLayoutEffect(() => { if (view === 'analytics' && analyticsButton.current) setAnalyticsLeft(analyticsButton.current.offsetLeft) }, [view])
  const shown: PeriodView = view === 'analytics' && analytics === 'transfers' ? 'transfers' : view
  // Custom period: any two days, picked on the calendar or typed.
  const [customFrom, setCustomFrom] = useState<Dayjs | null>(null)
  const [customTo, setCustomTo] = useState<Dayjs | null>(null)
  // Every bank together is shown in the global display currency (Settings).
  const currency = settings.currency
  const [editing, setEditing] = useState<TransactionView | null>(null)
  const [adding, setAdding] = useState(false)
  const [brands, setBrands] = useState<string[]>([])

  // The periods of every bank, whatever their currency.
  useEffect(() => {
    api.periods.index().then((ix) => {
      setIndex(ix)
      if (ix.last) {
        const last = dayjs(ix.last + '-01')
        setCustomFrom((d) => d ?? last)
        setCustomTo((d) => d ?? last.endOf('month'))
      }
    }, fail)
  }, [dataVersion, fail])

  useEffect(() => {
    api.brands.list().then((b) => setBrands(b.brands.map((x) => x.brand)), () => setBrands([]))
  }, [dataVersion])

  const periods: Period[] = useMemo(() => {
    if (!index) return []
    if (listMode === 'month') return index.months.map((m) => ({ from: iso(m.year, m.month, 1), to: iso(m.year, m.month, daysInMonth(m.year, m.month)), key: `${m.year}-${m.month}` }))
    if (listMode === 'year') return index.years.map((y) => ({ from: iso(y, 1, 1), to: iso(y, 12, 31), key: `${y}` }))
    if (listMode === 'custom') {
      if (!customFrom?.isValid() || !customTo?.isValid() || customTo.isBefore(customFrom, 'day')) return []
      const from = customFrom.format('YYYY-MM-DD')
      const to = customTo.format('YYYY-MM-DD')
      return [{ from, to, key: `${from}_${to}` }]
    }
    // All time: from the first operation to today, found by itself.
    if (!index.first) return []
    const today = dayjs().format('YYYY-MM-DD')
    return [{ from: index.first, to: today < index.first ? index.first : today, key: `${index.first}_${today}` }]
  }, [index, listMode, customFrom, customTo])

  // An operation shown converted is edited as its bank wrote it.
  const onEdit = useCallback((tx: TransactionView) => {
    if (tx.originalCurrency || tx.currency !== settings.currency) api.tx.get(tx.id).then(setEditing, fail)
    else setEditing(tx)
  }, [settings.currency, fail])

  // Table headers stick just under the bar of tabs, whatever its height.
  const bar = useRef<HTMLDivElement>(null)
  const [barHeight, setBarHeight] = useState(0)
  useEffect(() => {
    const el = bar.current
    if (!el) return
    const ro = new ResizeObserver(() => setBarHeight(el.offsetHeight))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  const empty = index !== null && index.months.length === 0

  // The period at the top of the screen stays where it is, whatever changes height above it: periods loading or
  // letting go of their data, the view switched (Summary, Statements, Analytics). Scrolling never jumps back, even
  // at the bottom of the page where the browser itself pulls the page up when it gets shorter.
  const list = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const el = list.current
    const scroller = el?.closest('main')
    if (!el || !scroller) return
    const line = () => scroller.getBoundingClientRect().top + (bar.current?.offsetHeight ?? 0)
    let anchor: { el: Element; offset: number } | null = null
    let lastHeight = scroller.scrollHeight
    // Where this code itself put the page: the scroll that follows is not the person's.
    let own = -1
    const pick = () => {
      if (own >= 0 && Math.abs(scroller.scrollTop - own) < 1) { own = -1; return }
      own = -1
      // At the bottom, a page that got shorter is pulled up by the browser itself: that scroll is not the person's, the
      // period on screen is put back where it was. Any other scroll moves the anchor.
      const shrunk = scroller.scrollHeight < lastHeight && scroller.scrollTop >= scroller.scrollHeight - scroller.clientHeight - 1
      lastHeight = scroller.scrollHeight
      if (shrunk && anchor) return
      const y = line()
      // The first period that starts on screen, even under the bar (a period whose last lines show at the top may grow
      // downwards when it loads); failing that, the one that fills the screen.
      const { top, bottom } = scroller.getBoundingClientRect()
      const children = [...el.children]
      const s = children.find((c) => { const r = c.getBoundingClientRect(); return r.top >= top - 1 && r.top < bottom })
        ?? children.find((c) => c.getBoundingClientRect().bottom > y)
      anchor = s && scroller.scrollTop > 0 ? { el: s, offset: s.getBoundingClientRect().top - y } : null
    }
    const ro = new ResizeObserver(() => {
      if (!anchor?.el.isConnected) return
      const moved = anchor.el.getBoundingClientRect().top - line() - anchor.offset
      if (Math.abs(moved) >= 1) { scroller.scrollTop += moved; own = scroller.scrollTop }
      lastHeight = scroller.scrollHeight
    })
    const watch = () => { for (const c of el.children) ro.observe(c) }
    watch()
    const mo = new MutationObserver(watch)
    mo.observe(el, { childList: true })
    scroller.addEventListener('scroll', pick, { passive: true })
    return () => { ro.disconnect(); mo.disconnect(); scroller.removeEventListener('scroll', pick) }
  }, [empty])

  return (
    <Box sx={{ maxWidth: 1180, mx: 'auto', '--sticky-top': `${barHeight}px`, overflowAnchor: 'none' }}>
      <Box
        ref={bar}
        sx={{
          position: 'sticky', top: 0, zIndex: 5, display: 'flex', alignItems: 'center', gap: 1.5, flexWrap: 'wrap',
          py: 2, mb: 1, bgcolor: 'background.default',
        }}
      >
        <ToggleButtonGroup exclusive value={mode} onChange={(_, v) => v && setMode(v)}>
          <ToggleButton value="month">Month</ToggleButton>
          <ToggleButton value="year">Year</ToggleButton>
          <ToggleButton value="all">All time</ToggleButton>
          <ToggleButton value="custom">Custom</ToggleButton>
        </ToggleButtonGroup>
        {mode === 'custom' && (
          <>
            <DatePicker label="From" value={customFrom} onChange={setCustomFrom} format={pickerFormats[settings.dateFormat] ?? 'DD MMMM YYYY'} slotProps={{ textField: { size: 'small', sx: { width: 230 } } }} />
            <DatePicker label="To" value={customTo} onChange={setCustomTo} minDate={customFrom ?? undefined} format={pickerFormats[settings.dateFormat] ?? 'DD MMMM YYYY'} slotProps={{ textField: { size: 'small', sx: { width: 230 } } }} />
          </>
        )}
        <Box sx={{ flex: 1 }} />
        <Button variant="contained" startIcon={<Add />} onClick={() => setAdding(true)}>Add operation</Button>
        <Box sx={{ flexBasis: '100%' }}>
          <ToggleButtonGroup data-period-view exclusive size="small" value={view} onChange={(_, v) => v && setView(v)}>
            <ToggleButton value="summary">Summary</ToggleButton>
            <ToggleButton value="statements">Statements</ToggleButton>
            <ToggleButton ref={analyticsButton} value="analytics">Analytics</ToggleButton>
          </ToggleButtonGroup>
          {view === 'analytics' && (
            <Box sx={{ mt: 1, pl: `${analyticsLeft}px` }}>
              <ToggleButtonGroup data-analytics-view exclusive size="small" value={analytics} onChange={(_, v) => v && setAnalytics(v)}>
                <ToggleButton value="general">General</ToggleButton>
                <ToggleButton value="transfers">Transfers</ToggleButton>
              </ToggleButtonGroup>
            </Box>
          )}
        </Box>
      </Box>

      {empty ? (
        <EmptyState
          icon={<UploadFile />}
          title="No operations yet"
          action={<Button variant="contained" onClick={() => go('import')}>Import a statement</Button>}
        />
      ) : (
        <Box ref={list} sx={{ display: 'grid', gridTemplateColumns: 'minmax(0, 1fr)', gap: 3, pb: 6 }}>
          {periods.map((p) => (
            <PeriodCard
              key={`${listMode}:${p.key}:${currency}`}
              from={p.from}
              to={p.to}
              granularity={listMode === 'month' || listMode === 'year' ? listMode : 'range'}
              currency={currency}
              view={shown}
              onEdit={onEdit}
            />
          ))}
        </Box>
      )}

      <TransactionDialog open={editing !== null || adding} tx={editing} brands={brands} onClose={() => { setEditing(null); setAdding(false) }} />
    </Box>
  )
}
