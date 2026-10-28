import { useMemo, useState } from 'react'
import {
  Autocomplete, Box, Checkbox, IconButton, InputBase, ListItemIcon, ListItemText, Menu, MenuItem, Pagination, Table, TableBody, TableCell,
  TableHead, TableRow, Tooltip, Typography,
} from '@mui/material'
import ExpandMore from '@mui/icons-material/ExpandMoreRounded'
import Check from '@mui/icons-material/CheckRounded'
import EditOutlined from '@mui/icons-material/EditOutlined'
import FilterAlt from '@mui/icons-material/FilterAltRounded'
import { api, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { formatDate, formatExample, plural } from '../format'
import { CategoryIcon } from './CategoryIcon'
import { CategoryPill, MenuDivider, Money } from './common'

function HeaderButton({ label, active, onClick }: { label: string; active?: boolean; onClick: (e: React.MouseEvent<HTMLElement>) => void }) {
  return (
    <Box
      component="button"
      onClick={onClick}
      sx={{
        display: 'inline-flex', alignItems: 'center', gap: 0.25, border: 0, background: 'none', p: '2px 6px', ml: '-6px', borderRadius: 1.5,
        font: 'inherit', color: active ? 'primary.main' : 'inherit', fontWeight: 600, cursor: 'pointer',
        '&:hover': { bgcolor: 'action.hover' },
      }}
    >
      {label}
      {active ? <FilterAlt sx={{ fontSize: 15, ml: 0.25 }} /> : <ExpandMore sx={{ fontSize: 18 }} />}
    </Box>
  )
}

function BrandCell({ tx, brands, onSaved }: { tx: TransactionView; brands: string[]; onSaved: (changed: number, brand: string) => void }) {
  const { fail } = useApp()
  const [editing, setEditing] = useState(false)
  const [value, setValue] = useState(tx.brand)

  const save = async (v: string) => {
    setEditing(false)
    const next = v.trim()
    if (!next || next === tx.brand) { setValue(tx.brand); return }
    try {
      const r = await api.tx.setBrand(tx.id, next)
      onSaved(r.changed, next)
    } catch (e) {
      setValue(tx.brand)
      fail(e)
    }
  }

  if (editing) {
    return (
      <Autocomplete
        freeSolo
        autoHighlight
        openOnFocus={false}
        options={brands}
        value={value}
        onChange={(_, v) => { if (typeof v === 'string') void save(v) }}
        inputValue={value}
        onInputChange={(_, v) => setValue(v)}
        disableClearable
        slotProps={{ paper: { sx: { borderRadius: 3, mt: 0.5 } } }}
        renderInput={(params) => (
          <InputBase
            ref={params.slotProps.input.ref}
            inputProps={{ ...params.slotProps.htmlInput, 'aria-label': 'Brand name' }}
            autoFocus
            onBlur={() => void save(value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void save(value)
              if (e.key === 'Escape') { setValue(tx.brand); setEditing(false) }
            }}
            sx={{ fontSize: 13.5, fontWeight: 520, px: 1, py: 0.25, mx: -1, borderRadius: 2, bgcolor: 'action.hover', width: '100%' }}
          />
        )}
      />
    )
  }
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 0 }}>
      <Tooltip title={tx.rawLabel || 'Entered by hand'} placement="top-start">
        <Box
          onClick={() => setEditing(true)}
          sx={{ fontWeight: 520, cursor: 'text', borderRadius: 1.5, px: 1, mx: -1, py: 0.25, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', '&:hover': { bgcolor: 'action.hover' } }}
        >
          {tx.brand}
        </Box>
      </Tooltip>
    </Box>
  )
}

/** The operations of one period: Date, Category, Brand Name, (how often it comes back), Amount. */
export function TransactionTable({ transactions, currency, paged, onEdit, frequency = false, empty = 'No operations in this period' }: {
  transactions: TransactionView[]
  currency: string
  paged: boolean
  onEdit: (tx: TransactionView) => void
  /** Adds the "Comes back" column (recurring payments and income). */
  frequency?: boolean
  empty?: string
}) {
  const { settings, setSetting, info, category, notify, fail, dataChanged, askScope } = useApp()
  const [dateMenu, setDateMenu] = useState<HTMLElement | null>(null)
  const [catMenu, setCatMenu] = useState<HTMLElement | null>(null)
  const [newestFirst, setNewestFirst] = useState(true)
  const [filter, setFilter] = useState<Set<string>>(new Set())
  const [page, setPage] = useState(1)
  const pageSize = 25

  const present = useMemo(() => {
    const counts = new Map<string, number>()
    transactions.forEach((t) => counts.set(t.categoryId, (counts.get(t.categoryId) ?? 0) + 1))
    return [...counts.entries()].sort((a, b) => b[1] - a[1])
  }, [transactions])

  const brands = useMemo(() => [...new Set(transactions.map((t) => t.brand))].sort((a, b) => a.localeCompare(b)), [transactions])

  const rows = useMemo(() => {
    let list = filter.size ? transactions.filter((t) => filter.has(t.categoryId)) : transactions
    if (newestFirst) list = [...list].reverse()
    return list
  }, [transactions, filter, newestFirst])

  const pages = Math.max(1, Math.ceil(rows.length / pageSize))
  const visible = paged ? rows.slice((Math.min(page, pages) - 1) * pageSize, Math.min(page, pages) * pageSize) : rows

  const learned = (changed: number, what: string) => {
    notify(changed > 0 ? `Saved for ${what} · ${plural(changed, 'other operation')} updated` : `Saved for ${what}`)
    dataChanged()
  }

  const setCategory = async (tx: TransactionView, id: string) => {
    if (id === tx.categoryId) return
    // This operation only, or every operation of the brand, past and future: the person decides.
    const all = await askScope(tx.brand, category(id).name)
    if (all === null) return
    try {
      const r = await api.tx.setCategory(tx.id, id, all)
      if (all) learned(r.changed, `every “${tx.brand}”`)
      else { notify('Category changed for this operation only'); dataChanged() }
    } catch (e) {
      fail(e)
    }
  }

  const toggle = (id: string) => {
    const next = new Set(filter)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    setFilter(next)
    setPage(1)
  }

  return (
    <Box>
      <Table size="small" sx={{ tableLayout: 'fixed', '& td, & th': { px: 1.5 }, '& tr > *:first-of-type': { pl: 0.5 } }}>
        <colgroup>
          <col style={{ width: frequency ? '19%' : '22%' }} />
          <col style={{ width: frequency ? '24%' : '27%' }} />
          <col />
          {frequency && <col style={{ width: '13%' }} />}
          <col style={{ width: '17%' }} />
          <col style={{ width: 44 }} />
        </colgroup>
        <TableHead>
          <TableRow>
            <TableCell><HeaderButton label="Date" onClick={(e) => setDateMenu(e.currentTarget)} /></TableCell>
            <TableCell><HeaderButton label="Category" active={filter.size > 0} onClick={(e) => setCatMenu(e.currentTarget)} /></TableCell>
            <TableCell>Brand Name</TableCell>
            {frequency && <TableCell>Comes back</TableCell>}
            <TableCell align="right">Amount ({currency})</TableCell>
            <TableCell />
          </TableRow>
        </TableHead>
        <TableBody>
          {visible.map((t) => {
            const transfer = category(t.categoryId).kind === 'transfer'
            return (
              <TableRow key={t.id} hover sx={{ '&:hover .row-action': { opacity: 1 }, '& td': { py: 0.9 } }}>
                <TableCell className="tnum" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>{formatDate(t.date, settings.dateFormat)}</TableCell>
                <TableCell><CategoryPill id={t.categoryId} onChange={(id) => void setCategory(t, id)} /></TableCell>
                <TableCell><BrandCell tx={t} brands={brands} onSaved={(n, b) => learned(n, b)} /></TableCell>
                {frequency && <TableCell sx={{ color: 'text.secondary' }}>{t.recurring}</TableCell>}
                <TableCell align="right"><Money value={t.amount} currency={currency} sign muted={transfer} /></TableCell>
                <TableCell padding="none" align="right">
                  <IconButton className="row-action" size="small" aria-label="Edit" onClick={() => onEdit(t)} sx={{ opacity: 0, transition: 'opacity 120ms', '&:focus-visible': { opacity: 1 } }}>
                    <EditOutlined sx={{ fontSize: 17 }} />
                  </IconButton>
                </TableCell>
              </TableRow>
            )
          })}
          {visible.length === 0 && (
            <TableRow>
              <TableCell colSpan={frequency ? 6 : 5} sx={{ py: 4, textAlign: 'center', color: 'text.secondary', borderBottom: 0 }}>
                {transactions.length === 0 ? empty : 'No operations in these categories'}
              </TableCell>
            </TableRow>
          )}
        </TableBody>
      </Table>
      {paged && pages > 1 && (
        <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mt: 1.5 }}>
          <Typography variant="body2" color="text.secondary">{plural(rows.length, 'operation')}</Typography>
          <Pagination count={pages} page={Math.min(page, pages)} onChange={(_, p) => setPage(p)} shape="rounded" size="small" />
        </Box>
      )}

      <Menu anchorEl={dateMenu} open={dateMenu !== null} onClose={() => setDateMenu(null)}>
        {info.dateFormats.map((f) => (
          <MenuItem key={f} onClick={() => { void setSetting('dateFormat', f); setDateMenu(null) }} sx={{ minWidth: 250 }}>
            <ListItemText primary={f} secondary={formatExample(f)} slotProps={{ secondary: { className: 'tnum' } }} />
            {settings.dateFormat === f && <Check fontSize="small" sx={{ color: 'primary.main', ml: 2 }} />}
          </MenuItem>
        ))}
        <MenuDivider />
        <MenuItem onClick={() => { setNewestFirst(true); setDateMenu(null) }}>
          <ListItemText>Newest first</ListItemText>
          {newestFirst && <Check fontSize="small" sx={{ color: 'primary.main', ml: 2 }} />}
        </MenuItem>
        <MenuItem onClick={() => { setNewestFirst(false); setDateMenu(null) }}>
          <ListItemText>Oldest first</ListItemText>
          {!newestFirst && <Check fontSize="small" sx={{ color: 'primary.main', ml: 2 }} />}
        </MenuItem>
      </Menu>

      <Menu anchorEl={catMenu} open={catMenu !== null} onClose={() => setCatMenu(null)} slotProps={{ paper: { sx: { width: 270, maxHeight: 440 } } }}>
        <MenuItem onClick={() => { setFilter(new Set()); setPage(1) }}>
          <ListItemText>All categories</ListItemText>
          {filter.size === 0 && <Check fontSize="small" sx={{ color: 'primary.main' }} />}
        </MenuItem>
        <MenuDivider />
        {present.map(([id, n]) => {
          const c = category(id)
          return (
            <MenuItem key={id} onClick={() => toggle(id)} sx={{ py: 0.25 }}>
              <Checkbox size="small" checked={filter.has(id)} sx={{ p: 0.5, mr: 1 }} disableRipple />
              <ListItemIcon sx={{ minWidth: 30 }}><CategoryIcon name={c.icon} sx={{ fontSize: 18 }} /></ListItemIcon>
              <ListItemText>{c.name}</ListItemText>
              <Typography variant="body2" color="text.secondary" className="tnum">{n}</Typography>
            </MenuItem>
          )
        })}
      </Menu>
    </Box>
  )
}
