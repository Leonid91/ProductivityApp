import { memo, useLayoutEffect, useMemo, useRef, useState } from 'react'
import {
  Autocomplete, InputBase, ListItemText, Menu, MenuItem, Pagination, useTheme,
} from '@mui/material'
import ExpandMore from '@mui/icons-material/ExpandMoreRounded'
import Check from '@mui/icons-material/CheckRounded'
import FilterAlt from '@mui/icons-material/FilterAltRounded'
import { api, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { formatDate, formatExample, moneyBeside, plural } from '../format'
import { CategoryIcon } from './CategoryIcon'
import { CategoryPill, MenuDivider, Money } from './common'
import { choiceItems, countChoices, hiding, shownByTags, tagChoices, type Choice } from './filters'
import { Glyph } from './plain'
import { TagCell, useTagName } from './tags'
import { useKept } from '../keep'
import { useStepper } from '../pace'
import { ROW_EDIT_WIDTH, RowActions } from './RowActions'

const NONE_HIDDEN = new Set<string>()

export function HeaderButton({ label, active, onClick }: { label: string; active?: boolean; onClick: (e: React.MouseEvent<HTMLElement>) => void }) {
  return (
    <button className={active ? 'p-head-button p-on' : 'p-head-button'} onClick={onClick}>
      {label}
      {active ? <Glyph icon={FilterAlt} size={15} style={{ marginLeft: 2 }} /> : <Glyph icon={ExpandMore} size={18} />}
    </button>
  )
}

/**
 * A value worked out only once something asks for it (a filter's list of values: when its menu opens or the filter is
 * on), then kept while what it is made from stays the same.
 */
function useLazy<T>(make: () => T, deps: unknown[]): () => T {
  // eslint-disable-next-line react-hooks/exhaustive-deps
  return useMemo(() => { let v: { value: T } | null = null; return () => (v ??= { value: make() }).value }, deps)
}

const NO_CHOICES: Choice[] = []

function BrandCell({ tx, brands, onSaved }: { tx: TransactionView; brands: () => string[]; onSaved: (changed: number, brand: string) => void }) {
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
        options={brands()}
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
    <div className="p-brand-box">
      <div className="p-brand" onClick={() => setEditing(true)}>
        {tx.brand}
      </div>
    </div>
  )
}

/** What the lines of a table do: the table's own functions. */
interface RowActs { setCategory: (t: TransactionView, id: string) => void; learned: (changed: number, what: string) => void; onEdit: (t: TransactionView) => void; brands: () => string[] }

/** One line of the table (drawn again only when its operation or the table's settings change). */
const TxRow = memo(function TxRow({ t, currency, frequency, act }: { t: TransactionView; currency: string; frequency: boolean; act: RowActs }) {
  const { settings, category } = useApp()
  const theme = useTheme()
  const transfer = category(t.categoryId).kind === 'transfer'
  return (
    <tr className="row">
      <td className="tnum" style={{ color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>{formatDate(t.date, settings.dateFormat)}</td>
      <td><CategoryPill id={t.categoryId} onChange={(id) => act.setCategory(t, id)} /></td>
      <td><BrandCell tx={t} brands={act.brands} onSaved={act.learned} /></td>
      <td><TagCell operationId={t.id} own={t.ownTags} all={t.tags} categoryName={category(t.categoryId).name} /></td>
      {frequency && <td style={{ color: theme.palette.text.secondary }}>{t.recurring}</td>}
      <td style={{ textAlign: 'right' }} data-amount>
        {t.currency !== currency ? (
          // No rate for its day: in its own currency, outside the totals.
          <span className="p-muted" title={`No ${t.currency} rate for this day: left out of the totals`}>
            {moneyBeside(t.amount, t.currency, currency, { sign: true })}
          </span>
        ) : <Money value={t.amount} currency={currency} sign muted={transfer} />}
        {t.originalCurrency && t.original != null && (
          <div data-original className="p-body1 tnum p-original" title={t.rate ? `1 ${t.originalCurrency} = ${t.rate} ${currency}` : undefined}>
            {moneyBeside(t.original, t.originalCurrency, currency, { sign: true })}
          </div>
        )}
      </td>
      <td style={{ textAlign: 'right', paddingRight: 4 }}>
        <RowActions onEdit={() => act.onEdit(t)} />
      </td>
    </tr>
  )
})

/** The pages of a table (drawn again only when its page or number of pages change). */
export const Pager = memo(function Pager({ count, page, onChange }: { count: number; page: number; onChange: (page: number) => void }) {
  return <Pagination count={count} page={page} onChange={(_, p) => onChange(p)} shape="rounded" size="small" />
})

/**
 * The operations of one period: Date, Category, Brand Name, Tags, (how often it comes back), Amount. Dates, categories,
 * brands and tags each have a filter (all, none, one or any of them); the dates also their format and order.
 * Drawn again only when what it shows changes.
 */
export const TransactionTable = memo(function TransactionTable({ keep, transactions, currency, paged, onEdit, frequency = false, empty = 'No operations in this period' }: {
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
  const { settings, setSetting, info, category, tag, notify, fail, dataChanged, askScope } = useApp()
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

  // The values of each filter (with how many operations have them) are worked out only when its menu opens or while
  // it hides something: a period shows at once, however many operations it has.
  const [catMade, setCatMade] = useState(false)
  const [brandMade, setBrandMade] = useState(false)
  const [dateMade, setDateMade] = useState(false)
  const [tagMade, setTagMade] = useState(false)
  const allCategories = useLazy(() => countChoices(transactions, (t) => t.categoryId, (id) => category(id).name,
    (id) => <CategoryIcon name={category(id).icon} sx={{ fontSize: 18 }} />).sort((a, b) => b.count - a.count), [transactions, category])
  const allBrands = useLazy(() => countChoices(transactions, (t) => t.brand, (b) => b).sort((a, b) => a.label.localeCompare(b.label)), [transactions])
  const allDates = useLazy(() => countChoices(transactions, (t) => t.date, (d) => formatDate(d, settings.dateFormat))
    .sort((a, b) => (newestFirst ? b.key.localeCompare(a.key) : a.key.localeCompare(b.key))), [transactions, settings.dateFormat, newestFirst])
  const allTags = useLazy(() => tagChoices(transactions, (t) => t.tags, tagName), [transactions, tag])
  const categoryChoices = catMade || hiddenCategories.size > 0 ? allCategories() : NO_CHOICES
  const brandChoices = brandMade || hiddenBrands.size > 0 ? allBrands() : NO_CHOICES
  const dateChoices = dateMade || hiddenDates.size > 0 ? allDates() : NO_CHOICES
  const tagFilter = tagMade || hiddenTags.size > 0 ? allTags() : NO_CHOICES
  // The brands already written, offered when a brand is typed.
  const brands = useLazy(() => allBrands().map((c) => c.key), [allBrands])

  // The operations the filters let through (all of them when no filter hides anything).
  const filtering = hiddenCategories.size > 0 || hiddenBrands.size > 0 || hiddenDates.size > 0 || hiddenTags.size > 0
  const rows = useMemo(() => !filtering ? transactions
    : transactions.filter((t) => !hiddenCategories.has(t.categoryId) && !hiddenBrands.has(t.brand) && !hiddenDates.has(t.date) && shownByTags(t.tags, hiddenTags)),
  [transactions, filtering, hiddenCategories, hiddenBrands, hiddenDates, hiddenTags])

  const pages = Math.max(1, Math.ceil(rows.length / pageSize))
  // The lines of the page shown, newest first or oldest first: only those are taken from the list.
  const visible = useMemo(() => {
    const n = rows.length
    const [start, end] = paged ? [(Math.min(page, pages) - 1) * pageSize, Math.min(page, pages) * pageSize] : [0, n]
    return newestFirst ? rows.slice(Math.max(0, n - end), n - start).reverse() : rows.slice(start, end)
  }, [rows, paged, page, pages, newestFirst])

  // The lines appear five at a time, paced to the screen with every other piece of the page (see pace).
  const table = useRef<HTMLTableElement>(null)
  const drawnRows = useStepper(visible.length, visible.map((t) => t.id).join(), table, 5, 5)

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

  // What a line does (change its category, its brand, open it), the same functions from one drawing to the next: a
  // line is drawn again only when it changes.
  const latest = useRef({ setCategory, learned, onEdit, brands })
  useLayoutEffect(() => { latest.current = { setCategory, learned, onEdit, brands } })
  const act = useMemo<RowActs>(() => ({
    setCategory: (t, id) => void latest.current.setCategory(t, id),
    learned: (n, what) => latest.current.learned(n, what),
    onEdit: (t) => latest.current.onEdit(t),
    brands: () => latest.current.brands(),
  }), [])

  // A filter changed: back to the first page.
  const filtered = (set: (hidden: Set<string>) => void) => (hidden: Set<string>) => { set(hidden); setPage(1) }

  return (
    <div className="p-lines">
      <table ref={table} className="p-table">
        <colgroup>
          <col style={{ width: frequency ? '16%' : '18%' }} />
          <col style={{ width: frequency ? '20%' : '22%' }} />
          <col />
          <col style={{ width: frequency ? '15%' : '18%' }} />
          {frequency && <col style={{ width: '12%' }} />}
          <col style={{ width: '15%' }} />
          <col style={{ width: ROW_EDIT_WIDTH }} />
        </colgroup>
        <thead>
          <tr>
            <th><HeaderButton label="Date" active={hiding(hiddenDates, dateChoices)} onClick={(e) => { setDateMenu(e.currentTarget); setDateMade(true) }} /></th>
            <th><HeaderButton label="Category" active={hiding(hiddenCategories, categoryChoices)} onClick={(e) => { setCatMenu(e.currentTarget); setCatMade(true) }} /></th>
            <th><HeaderButton label="Brand Name" active={hiding(hiddenBrands, brandChoices)} onClick={(e) => { setBrandMenu(e.currentTarget); setBrandMade(true) }} /></th>
            <th><HeaderButton label="Tags" active={hiding(hiddenTags, tagFilter)} onClick={(e) => { setTagMenu(e.currentTarget); setTagMade(true) }} /></th>
            {frequency && <th>Comes back</th>}
            <th className="p-right">Amount ({currency})</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {visible.slice(0, drawnRows).map((t) => <TxRow key={t.id} t={t} currency={currency} frequency={frequency} act={act} />)}
          {visible.length === 0 && (
            <tr>
              <td colSpan={frequency ? 7 : 6} className="p-empty">
                {transactions.length === 0 ? empty : 'No operations match these filters'}
              </td>
            </tr>
          )}
        </tbody>
      </table>
      {paged && pages > 1 && drawnRows >= visible.length && (
        <div className="p-pager">
          <p className="p-body2">{plural(rows.length, 'operation')}</p>
          <Pager count={pages} page={Math.min(page, pages)} onChange={setPage} />
        </div>
      )}

      {dateMade && <Menu anchorEl={dateMenu} open={dateMenu !== null} onClose={() => setDateMenu(null)} slotProps={{ paper: { sx: { maxHeight: 560 } } }}>
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
      </Menu>}

      {catMade && <Menu anchorEl={catMenu} open={catMenu !== null} onClose={() => setCatMenu(null)} slotProps={{ paper: { sx: { minWidth: 300, maxWidth: 380, maxHeight: 480 } } }}>
        {choiceItems({ choices: categoryChoices, hidden: hiddenCategories, onChange: filtered(setHiddenCategories), all: 'All categories' })}
      </Menu>}

      {tagMade && <Menu anchorEl={tagMenu} open={tagMenu !== null} onClose={() => setTagMenu(null)} slotProps={{ paper: { sx: { minWidth: 280, maxWidth: 380, maxHeight: 480 } } }}>
        {choiceItems({ choices: tagFilter, hidden: hiddenTags, onChange: filtered(setHiddenTags), all: 'All tags' })}
      </Menu>}

      {brandMade && <Menu anchorEl={brandMenu} open={brandMenu !== null} onClose={() => setBrandMenu(null)} slotProps={{ paper: { sx: { minWidth: 300, maxWidth: 380, maxHeight: 480 } } }}>
        {choiceItems({ choices: brandChoices, hidden: hiddenBrands, onChange: filtered(setHiddenBrands), all: 'All brands' })}
      </Menu>}
    </div>
  )
})
