import { memo, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Menu, useTheme } from '@mui/material'
import { Glyph } from './plain'
import East from '@mui/icons-material/EastRounded'
import { api, type MovementView, type TransactionInput, type TransactionView } from '../api'
import { AmountCell, AmountMenu, DateCell, NO_AMOUNT_FILTER, amountFiltering, amountPasses, byAmount, type AmountSort } from './amounts'
import { useApp } from '../AppContext'
import { formatDate, money, plural } from '../format'
import { useAmountsHidden } from '../mask'
import { CategoryIcon } from './CategoryIcon'
import { CategoryPill } from './common'
import { choiceItems, countChoices, hiding, shownByTags, tagChoices } from './filters'
import { TagCell, useTagName } from './tags'
import { BrandCell, HeaderButton, Pager, saveOperation } from './TransactionTable'
import { useKept } from '../keep'
import { useStepper } from '../pace'
import { ROW_EDIT_WIDTH, RowActions } from './RowActions'

const NONE_HIDDEN = new Set<string>()

/** What the lines of the table do: the table's own functions. */
interface MoveActs { setCategory: (m: MovementView, id: string) => void; onEdit: (t: TransactionView) => void; update: (t: TransactionView, patch: Partial<TransactionInput>) => void; learned: (changed: number, brand: string) => void; brands: () => string[] }

/** An account (or a place outside the app), and its kind under it. */
function Place({ name, outside, type }: { name: string; outside: boolean; type: string | null }) {
  const theme = useTheme()
  return (
    <div className="p-place">
      <p className="p-body2 p-place-name" style={{ fontWeight: outside ? 520 : 600, color: outside ? theme.palette.outside : theme.palette.text.primary }}>{name}</p>
      {type && <span className="p-caption p-place-type">{type}</span>}
    </div>
  )
}

/** One movement (drawn again only when it changes). */
const MoveRow = memo(function MoveRow({ m, tx, currency, act }: { m: MovementView; tx: TransactionView | undefined; currency: string; act: MoveActs }) {
  useAmountsHidden()
  const { settings, category } = useApp()
  const theme = useTheme()
  return (
    <tr className="row">
      <td className="tnum" style={{ color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>
        {tx ? <DateCell date={m.date} label={formatDate(m.date, settings.dateFormat)} onPick={(date) => act.update(tx, { date })} /> : formatDate(m.date, settings.dateFormat)}
      </td>
      <td><CategoryPill id={m.categoryId} onChange={(id) => act.setCategory(m, id)} /></td>
      <td>
        {/* The description (the brand of the operation), in full, changed in place. */}
        {tx ? <BrandCell tx={tx} brands={act.brands} onSaved={act.learned} wrap /> : <p className="p-body2 p-brand p-wrap">{m.label}</p>}
        {/* An international transfer: each side in its own currency, and what the banks kept. */}
        {m.sent != null && m.sentCurrency && m.received != null && m.receivedCurrency && (
          <span data-international className="p-caption tnum" style={{ display: 'block' }}>
            {money(m.sent, m.sentCurrency)} sent, {money(m.received, m.receivedCurrency)} received
            {m.lost != null && <>, {money(m.lost, currency)} lost to exchange and fees</>}
          </span>
        )}
      </td>
      <td><Place name={m.from} outside={m.fromOutside} type={m.fromType} /></td>
      <td><Glyph icon={East} size={16} style={{ color: theme.palette.text.disabled, display: 'block' }} /></td>
      <td><Place name={m.to} outside={m.toOutside} type={m.toType} /></td>
      <td>{tx && <TagCell operationId={tx.id} own={tx.ownTags} all={tx.tags} categoryName={category(tx.categoryId).name} />}</td>
      <td className="tnum" style={{ textAlign: 'right', fontWeight: 560, whiteSpace: 'nowrap' }}>
        {/* Typed as the size moved: the operation keeps its direction (and its currency, when converted). */}
        {tx ? <AmountCell value={m.amount} editValue={tx.original != null ? Math.abs(tx.original) : undefined} onSave={(amount) => act.update(tx, { amount: Math.sign(tx.amount || -1) * Math.abs(amount) })}>{money(m.amount, currency)}</AmountCell> : money(m.amount, currency)}
      </td>
      <td style={{ textAlign: 'right', paddingRight: 4 }}>
        {tx && <RowActions onEdit={() => act.onEdit(tx)} />}
      </td>
    </tr>
  )
})

/**
 * Transfers, card payments and investments, each movement once: from which account (or the outside) to which account
 * (or the outside). Places outside the app (accounts not imported, other people, investments) are in orange.
 * Dates, categories, descriptions and tags each have a filter (all, none, one or any of them).
 */
export const MovementsTable = memo(function MovementsTable({ keep, movements, transactions, currency, paged, onEdit }: {
  /** Where the table's filters and page are kept while the period is away from the screen. */
  keep: string
  movements: MovementView[]
  transactions: TransactionView[]
  currency: string
  paged: boolean
  onEdit: (tx: TransactionView) => void
}) {
  const { settings, askScope, category, notify, fail, dataChanged } = useApp()

  // A movement can be put in another category (a friend paying you back is a refund, not a transfer):
  // for this operation only, or for every operation of the same name.
  const setCategory = async (m: MovementView, id: string) => {
    if (id === m.categoryId) return
    const all = await askScope(m.label, category(id).name)
    if (all === null) return
    try {
      const r = await api.tx.setCategory(m.editId, id, all)
      notify(all ? `Saved for every “${m.label}”` + (r.changed ? ` · ${plural(r.changed, 'other operation')} updated` : '') : 'Category changed for this operation only')
      dataChanged()
    } catch (e) {
      fail(e)
    }
  }
  const [page, setPage] = useKept(keep + ':page', 1)
  const [amountMenu, setAmountMenu] = useState<HTMLElement | null>(null)
  const [amountFilter, setAmountFilter] = useKept(keep + ':amount', NO_AMOUNT_FILTER)
  const [amountSort, setAmountSort] = useKept<AmountSort>(keep + ':amountSort', 'none')
  const [hiddenDates, setHiddenDates] = useKept(keep + ':dates', NONE_HIDDEN)
  const [hiddenLabels, setHiddenLabels] = useKept(keep + ':labels', NONE_HIDDEN)
  const [hiddenCategories, setHiddenCategories] = useKept(keep + ':categories', NONE_HIDDEN)
  const [catMenu, setCatMenu] = useState<HTMLElement | null>(null)
  const [hiddenTags, setHiddenTags] = useKept(keep + ':tags', NONE_HIDDEN)
  const [tagMenu, setTagMenu] = useState<HTMLElement | null>(null)
  const tagName = useTagName()
  const [dateMenu, setDateMenu] = useState<HTMLElement | null>(null)
  const [labelMenu, setLabelMenu] = useState<HTMLElement | null>(null)
  const pageSize = 25
  const byId = useMemo(() => new Map(transactions.map((t) => [t.id, t])), [transactions])
  const dateChoices = useMemo(() => countChoices(movements, (m) => m.date, (d) => formatDate(d, settings.dateFormat)).sort((a, b) => b.key.localeCompare(a.key)), [movements, settings.dateFormat])
  const labelChoices = useMemo(() => countChoices(movements, (m) => m.label, (l) => l).sort((a, b) => a.label.localeCompare(b.label)), [movements])
  const categoryChoices = useMemo(() => countChoices(movements, (m) => m.categoryId, (id) => category(id).name,
    (id) => <CategoryIcon name={category(id).icon} sx={{ fontSize: 18 }} />).sort((a, b) => b.count - a.count), [movements, category])
  const tagFilter = useMemo(() => tagChoices(movements, (m) => byId.get(m.editId)?.tags ?? [], tagName), [movements, byId, tagName])
  const rows = useMemo(() => byAmount([...movements].reverse().filter((m) => !hiddenDates.has(m.date) && !hiddenCategories.has(m.categoryId) && !hiddenLabels.has(m.label)
    && shownByTags(byId.get(m.editId)?.tags ?? [], hiddenTags) && amountPasses(amountFilter, m.amount)), amountSort, (m) => m.amount),
    [movements, byId, hiddenDates, hiddenCategories, hiddenLabels, hiddenTags, amountFilter, amountSort])
  const filtered = (set: (hidden: Set<string>) => void) => (hidden: Set<string>) => { set(hidden); setPage(1) }
  const pages = Math.max(1, Math.ceil(rows.length / pageSize))
  const visible = paged ? rows.slice((Math.min(page, pages) - 1) * pageSize, Math.min(page, pages) * pageSize) : rows

  // The lines appear five at a time, paced to the screen with every other piece of the page (see pace).
  const table = useRef<HTMLTableElement>(null)
  const drawnRows = useStepper(visible.length, visible.map((m) => m.editId).join(), table, 5, 5)

  // What a line does, the same functions from one drawing to the next: a line is drawn again only when it changes.
  const update = async (t: TransactionView, patch: Partial<TransactionInput>) => {
    try {
      await saveOperation(t, currency, patch)
      notify('Operation updated')
      dataChanged()
    } catch (e) {
      fail(e)
    }
  }
  const learned = (changed: number, brand: string) => {
    notify(changed > 0 ? `Saved for every “${brand}” · ${plural(changed, 'other operation')} updated` : `Saved for every “${brand}”`)
    dataChanged()
  }
  const brands = () => labelChoices.map((c) => c.key)
  const latest = useRef({ setCategory, onEdit, update, learned, brands })
  useLayoutEffect(() => { latest.current = { setCategory, onEdit, update, learned, brands } })
  const act = useMemo<MoveActs>(() => ({
    setCategory: (m, id) => void latest.current.setCategory(m, id),
    onEdit: (t) => latest.current.onEdit(t),
    update: (t, patch) => void latest.current.update(t, patch),
    learned: (n, b) => latest.current.learned(n, b),
    brands: () => latest.current.brands(),
  }), [])


  return (
    <div className="p-lines">
      <table ref={table} className="p-table p-tight" data-movements>
        <colgroup>
          <col style={{ width: 132 }} />
          <col style={{ width: 200 }} />
          <col />
          <col style={{ width: '14%' }} />
          <col style={{ width: 22 }} />
          <col style={{ width: '15%' }} />
          <col style={{ width: '10%' }} />
          <col style={{ width: 118 }} />
          <col style={{ width: ROW_EDIT_WIDTH }} />
        </colgroup>
        <thead>
          <tr>
            <th><HeaderButton label="Date" active={hiding(hiddenDates, dateChoices)} onClick={(e) => setDateMenu(e.currentTarget)} /></th>
            <th><HeaderButton label="Category" active={hiding(hiddenCategories, categoryChoices)} onClick={(e) => setCatMenu(e.currentTarget)} /></th>
            <th><HeaderButton label="Description" active={hiding(hiddenLabels, labelChoices)} onClick={(e) => setLabelMenu(e.currentTarget)} /></th>
            <th>From</th>
            <th className="p-none" />
            <th>To</th>
            <th><HeaderButton label="Tags" active={hiding(hiddenTags, tagFilter)} onClick={(e) => setTagMenu(e.currentTarget)} /></th>
            <th className="p-right"><HeaderButton right label={`Amount (${currency})`} active={amountFiltering(amountFilter) || amountSort !== 'none'} onClick={(e) => setAmountMenu(e.currentTarget)} /></th>
            <th />
          </tr>
        </thead>
        <tbody>
          {visible.slice(0, drawnRows).map((m) => <MoveRow key={m.editId} m={m} tx={byId.get(m.editId)} currency={currency} act={act} />)}
          {visible.length === 0 && (
            <tr>
              <td colSpan={9} className="p-empty">No movements match these filters</td>
            </tr>
          )}
        </tbody>
      </table>
      {drawnRows >= visible.length && (
        <div className="p-pager">
          <p className="p-body2" data-table-count>{plural(rows.length, 'money movement')}</p>
          {paged && pages > 1 && <Pager count={pages} page={Math.min(page, pages)} onChange={setPage} />}
        </div>
      )}
      <AmountMenu anchor={amountMenu} onClose={() => setAmountMenu(null)} filter={amountFilter} setFilter={(f) => { setAmountFilter(f); setPage(1) }} sort={amountSort} setSort={(s) => { setAmountSort(s); setPage(1) }} />
      <Menu anchorEl={dateMenu} open={dateMenu !== null} onClose={() => setDateMenu(null)} slotProps={{ paper: { sx: { minWidth: 280, maxHeight: 480 } } }}>
        {choiceItems({ choices: dateChoices, hidden: hiddenDates, onChange: filtered(setHiddenDates), all: 'All dates' })}
      </Menu>
      <Menu anchorEl={catMenu} open={catMenu !== null} onClose={() => setCatMenu(null)} slotProps={{ paper: { sx: { minWidth: 300, maxWidth: 380, maxHeight: 480 } } }}>
        {choiceItems({ choices: categoryChoices, hidden: hiddenCategories, onChange: filtered(setHiddenCategories), all: 'All categories' })}
      </Menu>
      <Menu anchorEl={tagMenu} open={tagMenu !== null} onClose={() => setTagMenu(null)} slotProps={{ paper: { sx: { minWidth: 280, maxWidth: 380, maxHeight: 480 } } }}>
        {choiceItems({ choices: tagFilter, hidden: hiddenTags, onChange: filtered(setHiddenTags), all: 'All tags' })}
      </Menu>
      <Menu anchorEl={labelMenu} open={labelMenu !== null} onClose={() => setLabelMenu(null)} slotProps={{ paper: { sx: { minWidth: 300, maxWidth: 380, maxHeight: 480 } } }}>
        {choiceItems({ choices: labelChoices, hidden: hiddenLabels, onChange: filtered(setHiddenLabels), all: 'All descriptions' })}
      </Menu>
    </div>
  )
})
