import { useMemo, useState } from 'react'
import {
  Autocomplete, Box, IconButton, InputBase, ListItemText, Menu, MenuItem, Pagination, Table, TableBody, TableCell,
  TableHead, TableRow, Tooltip, Typography,
} from '@mui/material'
import ExpandMore from '@mui/icons-material/ExpandMoreRounded'
import Check from '@mui/icons-material/CheckRounded'
import EditOutlined from '@mui/icons-material/EditOutlined'
import FilterAlt from '@mui/icons-material/FilterAltRounded'
import { api, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { formatDate, formatExample, moneyBeside, plural } from '../format'
import { CategoryIcon } from './CategoryIcon'
import { CategoryPill, MenuDivider, Money } from './common'
import { choiceItems, countChoices, hiding, shownByTags, tagChoices } from './filters'
import { TagCell, useTagName } from './tags'
import { useKept } from '../keep'

const NONE_HIDDEN = new Set<string>()

export function HeaderButton({ label, active, onClick }: { label: string; active?: boolean; onClick: (e: React.MouseEvent<HTMLElement>) => void }) {
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

/**
 * The operations of one period: Date, Category, Brand Name, Tags, (how often it comes back), Amount. Dates, categories,
 * brands and tags each have a filter (all, none, one or any of them); the dates also their format and order.
 */
export function TransactionTable({ keep, transactions, currency, paged, onEdit, frequency = false, empty = 'No operations in this period' }: {
  /** Where the table's filters, order and page are kept while the period is away from the screen. */
  keep: string
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
  const [brandMenu, setBrandMenu] = useState<HTMLElement | null>(null)
  const [tagMenu, setTagMenu] = useState<HTMLElement | null>(null)
  const [hiddenTags, setHiddenTags] = useKept(keep + ':tags', NONE_HIDDEN)
  const tagName = useTagName()
  const [newestFirst, setNewestFirst] = useKept(keep + ':newestFirst', true)
  const [hiddenCategories, setHiddenCategories] = useKept(keep + ':categories', NONE_HIDDEN)
  const [hiddenBrands, setHiddenBrands] = useKept(keep + ':brands', NONE_HIDDEN)
  const [hiddenDates, setHiddenDates] = useKept(keep + ':dates', NONE_HIDDEN)
  const [page, setPage] = useKept(keep + ':page', 1)
  const pageSize = 25

  const categoryChoices = useMemo(() => countChoices(transactions, (t) => t.categoryId, (id) => category(id).name,
    (id) => <CategoryIcon name={category(id).icon} sx={{ fontSize: 18 }} />).sort((a, b) => b.count - a.count), [transactions, category])
  const brandChoices = useMemo(() => countChoices(transactions, (t) => t.brand, (b) => b).sort((a, b) => a.label.localeCompare(b.label)), [transactions])
  const dateChoices = useMemo(() => countChoices(transactions, (t) => t.date, (d) => formatDate(d, settings.dateFormat))
    .sort((a, b) => (newestFirst ? b.key.localeCompare(a.key) : a.key.localeCompare(b.key))), [transactions, settings.dateFormat, newestFirst])

  const tagFilter = useMemo(() => tagChoices(transactions, (t) => t.tags, tagName), [transactions, tagName])
  const brands = useMemo(() => brandChoices.map((c) => c.key), [brandChoices])

  const rows = useMemo(() => {
    let list = transactions.filter((t) => !hiddenCategories.has(t.categoryId) && !hiddenBrands.has(t.brand) && !hiddenDates.has(t.date) && shownByTags(t.tags, hiddenTags))
    if (newestFirst) list = [...list].reverse()
    return list
  }, [transactions, hiddenCategories, hiddenBrands, hiddenDates, hiddenTags, newestFirst])

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

  // A filter changed: back to the first page.
  const filtered = (set: (hidden: Set<string>) => void) => (hidden: Set<string>) => { set(hidden); setPage(1) }

  return (
    <Box>
      <Table size="small" sx={{ tableLayout: 'fixed', '& td, & th': { px: 1.5 }, '& tr > *:first-of-type': { pl: 0.5 } }}>
        <colgroup>
          <col style={{ width: frequency ? '16%' : '18%' }} />
          <col style={{ width: frequency ? '20%' : '22%' }} />
          <col />
          <col style={{ width: frequency ? '15%' : '18%' }} />
          {frequency && <col style={{ width: '12%' }} />}
          <col style={{ width: '15%' }} />
          <col style={{ width: 44 }} />
        </colgroup>
        <TableHead>
          <TableRow>
            <TableCell><HeaderButton label="Date" active={hiding(hiddenDates, dateChoices)} onClick={(e) => setDateMenu(e.currentTarget)} /></TableCell>
            <TableCell><HeaderButton label="Category" active={hiding(hiddenCategories, categoryChoices)} onClick={(e) => setCatMenu(e.currentTarget)} /></TableCell>
            <TableCell><HeaderButton label="Brand Name" active={hiding(hiddenBrands, brandChoices)} onClick={(e) => setBrandMenu(e.currentTarget)} /></TableCell>
            <TableCell><HeaderButton label="Tags" active={hiding(hiddenTags, tagFilter)} onClick={(e) => setTagMenu(e.currentTarget)} /></TableCell>
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
                <TableCell><TagCell operationId={t.id} own={t.ownTags} all={t.tags} categoryName={category(t.categoryId).name} /></TableCell>
                {frequency && <TableCell sx={{ color: 'text.secondary' }}>{t.recurring}</TableCell>}
                <TableCell align="right" data-amount>
                  {t.currency !== currency ? (
                    // No rate for its day: in its own currency, outside the totals.
                    <Box component="span" title={`No ${t.currency} rate for this day: left out of the totals`} sx={{ color: 'text.secondary' }}>
                      {moneyBeside(t.amount, t.currency, currency, { sign: true })}
                    </Box>
                  ) : <Money value={t.amount} currency={currency} sign muted={transfer} />}
                  {t.originalCurrency && t.original != null && (
                    <Typography component="div" data-original className="tnum" title={t.rate ? `1 ${t.originalCurrency} = ${t.rate} ${currency}` : undefined}
                      sx={{ fontSize: 12, color: 'text.secondary', lineHeight: 1.3 }}>
                      {moneyBeside(t.original, t.originalCurrency, currency, { sign: true })}
                    </Typography>
                  )}
                </TableCell>
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
              <TableCell colSpan={frequency ? 7 : 6} sx={{ py: 4, textAlign: 'center', color: 'text.secondary', borderBottom: 0 }}>
                {transactions.length === 0 ? empty : 'No operations match these filters'}
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

      <Menu anchorEl={dateMenu} open={dateMenu !== null} onClose={() => setDateMenu(null)} slotProps={{ paper: { sx: { maxHeight: 560 } } }}>
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
        <MenuDivider />
        {choiceItems({ title: 'Dates', choices: dateChoices, hidden: hiddenDates, onChange: filtered(setHiddenDates), all: 'All dates' })}
      </Menu>

      <Menu anchorEl={catMenu} open={catMenu !== null} onClose={() => setCatMenu(null)} slotProps={{ paper: { sx: { minWidth: 300, maxWidth: 380, maxHeight: 480 } } }}>
        {choiceItems({ choices: categoryChoices, hidden: hiddenCategories, onChange: filtered(setHiddenCategories), all: 'All categories' })}
      </Menu>

      <Menu anchorEl={tagMenu} open={tagMenu !== null} onClose={() => setTagMenu(null)} slotProps={{ paper: { sx: { minWidth: 280, maxWidth: 380, maxHeight: 480 } } }}>
        {choiceItems({ choices: tagFilter, hidden: hiddenTags, onChange: filtered(setHiddenTags), all: 'All tags' })}
      </Menu>

      <Menu anchorEl={brandMenu} open={brandMenu !== null} onClose={() => setBrandMenu(null)} slotProps={{ paper: { sx: { minWidth: 300, maxWidth: 380, maxHeight: 480 } } }}>
        {choiceItems({ choices: brandChoices, hidden: hiddenBrands, onChange: filtered(setHiddenBrands), all: 'All brands' })}
      </Menu>
    </Box>
  )
}
