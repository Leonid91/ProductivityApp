import { useRef, useState } from 'react'
import { Box, ListItemText, ListSubheader, Menu, MenuItem, Popover, TextField } from '@mui/material'
import Check from '@mui/icons-material/CheckRounded'
import { DateCalendar } from '@mui/x-date-pickers/DateCalendar'
import dayjs from 'dayjs'
import { MenuDivider } from './common'

/** A filter on the amounts of a table: greater than, less than or exactly a value (by size: 24.14 for −24.14). */
export interface AmountFilter { op: 'any' | 'gt' | 'lt' | 'eq'; value: string }
export const NO_AMOUNT_FILTER: AmountFilter = { op: 'any', value: '' }
/** The order of a table: its dates (the usual one), or its amounts by size, smallest or largest first. */
export type AmountSort = 'none' | 'asc' | 'desc'

const number = (v: string) => Number(v.replace(/\s/g, '').replace(',', '.'))

/** True when the filter holds a number to compare with. */
export const amountFiltering = (f: AmountFilter) => f.op !== 'any' && f.value.trim() !== '' && Number.isFinite(number(f.value))

/** Whether an amount passes the filter (compared by size, to the cent). */
export function amountPasses(f: AmountFilter, amount: number): boolean {
  if (!amountFiltering(f)) return true
  const a = Math.round(Math.abs(amount) * 100)
  const v = Math.round(Math.abs(number(f.value)) * 100)
  return f.op === 'gt' ? a > v : f.op === 'lt' ? a < v : a === v
}

/** Rows ordered by the size of their amount, smallest or largest first. */
export function byAmount<T>(rows: T[], sort: AmountSort, amountOf: (r: T) => number): T[] {
  if (sort === 'none') return rows
  const out = [...rows].sort((x, y) => Math.abs(amountOf(x)) - Math.abs(amountOf(y)))
  return sort === 'desc' ? out.reverse() : out
}

/** The menu of the Amount column: its order and its filter. */
export function AmountMenu({ anchor, onClose, filter, setFilter, sort, setSort }: {
  anchor: HTMLElement | null; onClose: () => void; filter: AmountFilter; setFilter: (f: AmountFilter) => void; sort: AmountSort; setSort: (s: AmountSort) => void
}) {
  const tick = (on: boolean) => (on ? <Check fontSize="small" sx={{ color: 'primary.main', ml: 2 }} /> : null)
  const order = (value: AmountSort, label: string) => (
    <MenuItem key={value} data-amount-sort={value} onClick={() => { setSort(value); onClose() }}>
      <ListItemText>{label}</ListItemText>
      {tick(sort === value)}
    </MenuItem>
  )
  return (
    <Menu anchorEl={anchor} open={anchor !== null} onClose={onClose} slotProps={{ paper: { sx: { minWidth: 300 } } }}>
      <ListSubheader sx={{ lineHeight: '30px', fontSize: 12.5, fontWeight: 650, bgcolor: 'background.paper' }}>Order</ListSubheader>
      {order('none', 'By date')}
      {order('asc', 'Amount, smallest first')}
      {order('desc', 'Amount, largest first')}
      <MenuDivider />
      <ListSubheader sx={{ lineHeight: '30px', fontSize: 12.5, fontWeight: 650, bgcolor: 'background.paper' }}>Filter</ListSubheader>
      {/* Typed keys stay in the fields (a menu would move to the item starting with the letter typed). */}
      <Box data-amount-filter sx={{ display: 'flex', gap: 1, px: 2, pb: 1.5, pt: 0.5 }} onKeyDown={(e) => { e.stopPropagation(); if (e.key === 'Enter' || e.key === 'Escape') onClose() }}>
        <TextField select size="small" value={filter.op} onChange={(e) => setFilter({ ...filter, op: e.target.value as AmountFilter['op'] })} sx={{ width: 150 }}
          slotProps={{ htmlInput: { 'aria-label': 'Amount filter' } }}>
          <MenuItem value="any">Any amount</MenuItem>
          <MenuItem value="gt">Greater than</MenuItem>
          <MenuItem value="lt">Less than</MenuItem>
          <MenuItem value="eq">Exactly</MenuItem>
        </TextField>
        <TextField size="small" value={filter.value} disabled={filter.op === 'any'} placeholder="0.00" onChange={(e) => setFilter({ ...filter, value: e.target.value })}
          sx={{ width: 120 }} slotProps={{ htmlInput: { inputMode: 'decimal', 'aria-label': 'Amount', className: 'tnum' } }} />
      </Box>
    </Menu>
  )
}

/** A day shown in a table, changed by picking another on a calendar. */
export function DateCell({ date, label, onPick }: { date: string; label: string; onPick: (day: string) => void }) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  return (
    <>
      <button className="p-cell-edit tnum" data-date-cell={date} aria-label="Change the date" onClick={(e) => setAnchor(e.currentTarget)}>{label}</button>
      {anchor && (
        <Popover open anchorEl={anchor} onClose={() => setAnchor(null)} anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }} slotProps={{ paper: { sx: { borderRadius: 3 } } }}>
          <DateCalendar
            value={dayjs(date)}
            onChange={(d, state) => {
              if (!d || !d.isValid() || state !== 'finish') return
              setAnchor(null)
              const day = d.format('YYYY-MM-DD')
              if (day !== date) onPick(day)
            }}
          />
        </Popover>
      )}
    </>
  )
}

/**
 * An amount shown in a table, typed again in place (Enter or leaving the field saves, Escape leaves it as it was).
 * Typed without a sign it keeps the sign it had.
 */
export function AmountCell({ value, editValue, children, onSave }: { value: number; editValue?: number; children: React.ReactNode; onSave: (amount: number) => void }) {
  const [text, setText] = useState<string | null>(null)
  // Saved once (Enter, then the field leaving the page, would save twice).
  const saved = useRef(false)
  const start = editValue ?? value
  if (text === null) {
    return <button className="p-cell-edit" data-amount-cell aria-label="Change the amount" onClick={() => { saved.current = false; setText(start.toFixed(2)) }} style={{ textAlign: 'right' }}>{children}</button>
  }
  const save = () => {
    if (saved.current) return
    saved.current = true
    const typed = text.trim()
    setText(null)
    const n = number(typed)
    if (typed === '' || !Number.isFinite(n) || n === 0) return
    const signed = /^[+-]/.test(typed) ? n : start < 0 ? -Math.abs(n) : Math.abs(n)
    const rounded = Math.round(signed * 100) / 100
    if (rounded !== start) onSave(rounded)
  }
  return (
    <input
      className="p-cell-input tnum"
      autoFocus
      aria-label="Amount"
      inputMode="decimal"
      value={text}
      onFocus={(e) => e.target.select()}
      onChange={(e) => setText(e.target.value)}
      onBlur={save}
      onKeyDown={(e) => { if (e.key === 'Enter') save(); if (e.key === 'Escape') { saved.current = true; setText(null) } }}
    />
  )
}

/** A name shown in a table, typed again in place (Enter or leaving the field saves, Escape leaves it as it was). */
export function TextCell({ value, label, onSave, children, maxLength = 60 }: { value: string; label: string; onSave: (v: string) => void; children?: React.ReactNode; maxLength?: number }) {
  const [text, setText] = useState<string | null>(null)
  const saved = useRef(false)
  if (text === null) {
    return <button className="p-cell-edit" data-text-cell={value} aria-label={`${label}: ${value}`} onClick={() => { saved.current = false; setText(value) }} style={{ textAlign: 'left' }}>{children ?? value}</button>
  }
  const save = () => {
    if (saved.current) return
    saved.current = true
    const v = text.trim()
    setText(null)
    if (v && v !== value) onSave(v)
  }
  return (
    <input className="p-cell-input" autoFocus aria-label={label} value={text} maxLength={maxLength} style={{ textAlign: 'left', minWidth: 160 }}
      onFocus={(e) => e.target.select()} onChange={(e) => setText(e.target.value)} onBlur={save}
      onKeyDown={(e) => { if (e.key === 'Enter') save(); if (e.key === 'Escape') { saved.current = true; setText(null) } }} />
  )
}
