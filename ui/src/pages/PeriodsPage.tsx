import { memo, useCallback, useDeferredValue, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Box, Button, ToggleButton, ToggleButtonGroup } from '@mui/material'
import { DatePicker } from '@mui/x-date-pickers/DatePicker'
import dayjs, { type Dayjs } from 'dayjs'
import Add from '@mui/icons-material/AddRounded'
import UnfoldLess from '@mui/icons-material/UnfoldLessRounded'
import UnfoldMore from '@mui/icons-material/UnfoldMoreRounded'
import UploadFile from '@mui/icons-material/UploadFileRounded'
import { api, type PeriodIndex, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { daysInMonth, iso } from '../format'
import { holding, loadCharts, PeriodCard, periodId, type PeriodView } from '../components/PeriodCard'
import { TransactionDialog, pickerFormats } from '../components/TransactionDialog'
import { EmptyState } from '../components/common'
import { pace, useRealHeights } from '../pace'
import { setEverything, useAnythingOpen } from '../folds'
import { whenIdle } from '../idle'

interface Period { from: string; to: string; key: string; count?: number }
interface Item { key: string; p: Period; granularity: 'month' | 'year' | 'range' }

/**
 * The periods shown, and the ones of the kind just left: those are hidden at once and let go of one at a time in the
 * background (paced to the screen, see pace), so switching kind never has to take down a whole list in one frame.
 * Switching back before they are gone shows them again at once.
 */
const PeriodList = memo(function PeriodList({ items, currency, view, onEdit }: { items: Item[]; currency: string; view: PeriodView; onEdit: (tx: TransactionView) => void }) {
  const [lastList, setLastList] = useState(items)
  const [retiring, setRetiring] = useState<Item[]>([])
  const held = (i: Item) => holding.has(periodId(i.granularity, i.p.from, i.p.to, currency))
  if (lastList !== items) {
    const now = new Set(items.map((i) => i.key))
    setRetiring((r) => [...r.filter((i) => !now.has(i.key)), ...lastList.filter((i) => !now.has(i.key) && held(i))])
    setLastList(items)
  }
  // Taken away once they hold nothing any more (each lets go of its data in its turn, see PeriodCard).
  useEffect(() => {
    if (retiring.length === 0) return
    return pace(null, () => setRetiring((r) => { const i = r.findIndex((x) => !held(x)); return i < 0 ? [...r] : [...r.slice(0, i), ...r.slice(i + 1)] }))
  })
  // One list (the cards keep their place, nothing is made again when a period starts or stops retiring).
  return [
    ...items.map(({ key, p, granularity }) => (
      <PeriodCard key={key} from={p.from} to={p.to} granularity={granularity} currency={currency} view={view} onEdit={onEdit} count={p.count} />
    )),
    ...retiring.map(({ key, p, granularity }) => (
      <PeriodCard key={key} from={p.from} to={p.to} granularity={granularity} currency={currency} view={view} onEdit={onEdit} retiring />
    )),
  ]
})

const FIELD = { textField: { size: 'small', sx: { width: 230 } } } as const

/** The two days of a custom period, picked on the calendar or typed (drawn again only when they change). */
const CustomDates = memo(function CustomDates({ from, to, setFrom, setTo, format }: {
  from: Dayjs | null; to: Dayjs | null; setFrom: (d: Dayjs | null) => void; setTo: (d: Dayjs | null) => void; format: string
}) {
  return (
    <>
      <DatePicker label="From" value={from} onChange={setFrom} format={format} slotProps={FIELD} />
      <DatePicker label="To" value={to} onChange={setTo} minDate={from ?? undefined} format={format} slotProps={FIELD} />
    </>
  )
})

export function PeriodsPage() {
  const { settings, dataVersion, fail, go } = useApp()
  // The charts are not needed to start: their code is read once the browser is idle, so Analytics opens at once.
  useEffect(() => {
    return whenIdle(() => void loadCharts(), 4000)
  }, [])
  const [index, setIndex] = useState<PeriodIndex | null>(null)
  const [mode, setMode] = useState<'month' | 'year' | 'all' | 'custom'>('month')
  const [pickersMade, setPickersMade] = useState(false)
  useEffect(() => {
    return whenIdle(() => setPickersMade(true), 5000)
  }, [])
  // The periods follow the button in the background: the button answers at once, the periods of the old kind are
  // replaced a little at a time (the screen keeps moving).
  const listMode = useDeferredValue(mode)
  // Second layer, the same for every kind of period: its statements (tables) or analytics (charts). In Statements every
  // period folds down to its summary (see folds), bank by bank; Analytics shows everything, unfolded, without summaries.
  const [view, setView] = useState<'statements' | 'analytics'>('statements')
  const anythingOpen = useAnythingOpen()
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
    if (listMode === 'month') return index.months.map((m) => ({ from: iso(m.year, m.month, 1), to: iso(m.year, m.month, daysInMonth(m.year, m.month)), key: `${m.year}-${m.month}`, count: m.count }))
    if (listMode === 'year') return index.years.map((y) => ({ from: iso(y, 1, 1), to: iso(y, 12, 31), key: `${y}`, count: index.months.filter((m) => m.year === y).reduce((n, m) => n + m.count, 0) }))
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

  // The periods shown (see PeriodList).
  const items: Item[] = useMemo(() => periods.map((p) => ({ key: `${listMode}:${p.key}:${currency}`, p, granularity: listMode === 'month' || listMode === 'year' ? listMode : 'range' })), [periods, listMode, currency])

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
  // letting go of their data, the view switched (Statements, Analytics), a period or bank folded or unfolded. Scrolling never jumps back, even
  // at the bottom of the page where the browser itself pulls the page up when it gets shorter.
  const list = useRef<HTMLDivElement>(null)
  useRealHeights(list, empty)
  useEffect(() => {
    const el = list.current
    const scroller = el?.closest('main')
    if (!el || !scroller) return
    const line = () => scroller.getBoundingClientRect().top + (bar.current?.offsetHeight ?? 0)
    // The element kept in place, and the period it is in (kept in its place when the element goes: another view drawn).
    // (top: where the page was then: a scroll not yet told when something changes height is the person's, not undone.)
    let anchor: { el: Element; offset: number; period: Element; periodOffset: number; top: number } | null = null
    let lastHeight = scroller.scrollHeight
    // Where this code itself put the page: the scroll that follows is not the person's.
    let own = -1
    const pick = () => {
      if (own >= 0 && Math.abs(scroller.scrollTop - own) < 1) { own = -1; return }
      own = -1
      // At the bottom, a page that got shorter is pulled up by the browser itself (another view drawn first at guessed
      // heights, periods folded): that scroll is not the person's, the period on screen is put back where it was as soon
      // as the page is long enough again. Any other scroll moves the anchor.
      const shrunk = scroller.scrollHeight < lastHeight && scroller.scrollTop >= scroller.scrollHeight - scroller.clientHeight - 1
      lastHeight = scroller.scrollHeight
      if (shrunk && anchor) { anchor.top = scroller.scrollTop; return }
      const y = line()
      // What is read at the top of the screen stays there: the period across the top line (under the bar), then the bank
      // across it inside, then the section; where nothing crosses the line, the first one that starts on screen. An
      // element below the top line is never the one kept: whatever grows between it and the top of the screen (charts
      // drawn, banks and tables drawn as they come near, a period loading) would push what is read away.
      const { top, bottom } = scroller.getBoundingClientRect()
      const first = (list: Element[]) => list.find((c) => { const r = c.getBoundingClientRect(); return r.top >= top - 1 && r.top < bottom })
      const crossing = (list: Element[]) => list.find((c) => { const r = c.getBoundingClientRect(); return r.top < y && r.bottom > y })
      const children = [...el.children].filter((c) => !c.hasAttribute('data-retiring'))
      let s = crossing(children) ?? first(children)
      for (const inner of ['[data-part-block]', '[data-section]']) {
        if (!s || s.getBoundingClientRect().top >= y) break
        const list = [...s.querySelectorAll(inner)]
        const next = crossing(list) ?? first(list)
        if (!next) break
        s = next
      }
      const period = s?.closest('section[data-period]') ?? s
      anchor = s && period && scroller.scrollTop > 0 ? { el: s, offset: s.getBoundingClientRect().top - y, period, periodOffset: period.getBoundingClientRect().top - y, top: scroller.scrollTop } : null
    }
    const ro = new ResizeObserver(() => {
      if (!anchor) return
      if (!anchor.el.isConnected) {
        if (!anchor.period.isConnected) return
        anchor = { ...anchor, el: anchor.period, offset: anchor.periodOffset }
      }
      // A page that got shorter at the bottom is pulled up by the browser: not the person's scroll.
      const pulled = scroller.scrollHeight < lastHeight && scroller.scrollTop >= scroller.scrollHeight - scroller.clientHeight - 1
      if (pulled) anchor.top = scroller.scrollTop
      const moved = anchor.el.getBoundingClientRect().top - line() - (anchor.offset - (scroller.scrollTop - anchor.top))
      const was = scroller.scrollTop
      if (Math.abs(moved) >= 1) {
        scroller.scrollTop += moved
        // What the page could take: a correction the page is still too short for stays owed, made once it is longer.
        anchor.top += scroller.scrollTop - was
        own = scroller.scrollTop
      }
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
    <Box sx={{ maxWidth: 1480, mx: 'auto', '--sticky-top': `${barHeight}px`, overflowAnchor: 'none' }}>
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
        {(mode === 'custom' || pickersMade) && (
          <div style={{ display: mode === 'custom' ? 'contents' : 'none' }}>
            <CustomDates from={customFrom} to={customTo} setFrom={setCustomFrom} setTo={setCustomTo} format={pickerFormats[settings.dateFormat] ?? 'DD MMMM YYYY'} />
          </div>
        )}
        <Box sx={{ flex: 1 }} />
        <Button variant="contained" startIcon={<Add />} onClick={() => setAdding(true)}>Add operation</Button>
        <Box sx={{ flexBasis: '100%', display: 'flex', alignItems: 'flex-start', gap: 1.5 }}>
          <Box sx={{ flex: 1 }}>
          <ToggleButtonGroup data-period-view exclusive size="small" value={view} onChange={(_, v) => v && setView(v)}>
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
          {/* In the bar that stays at the top of the screen: every period and bank folded to its summary, or all unfolded
              (Statements only: Analytics never folds). */}
          {view === 'statements' && <Button data-fold-everything variant="outlined" size="small" startIcon={anythingOpen ? <UnfoldLess /> : <UnfoldMore />} onClick={() => setEverything(!anythingOpen)}>
            {anythingOpen ? 'Fold everything' : 'Unfold everything'}
          </Button>}
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
          <PeriodList items={items} currency={currency} view={shown} onEdit={onEdit} />
        </Box>
      )}

      <TransactionDialog open={editing !== null || adding} tx={editing} brands={brands} onClose={() => { setEditing(null); setAdding(false) }} />
    </Box>
  )
}
