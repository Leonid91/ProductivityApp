import { useMemo, useState } from 'react'
import { Box, IconButton, Menu, Pagination, Table, TableBody, TableCell, TableHead, TableRow, Tooltip, Typography, useTheme } from '@mui/material'
import EditOutlined from '@mui/icons-material/EditOutlined'
import East from '@mui/icons-material/EastRounded'
import { api, type MovementView, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { formatDate, money, plural } from '../format'
import { CategoryIcon } from './CategoryIcon'
import { CategoryPill } from './common'
import { choiceItems, countChoices, hiding, shownByTags, tagChoices } from './filters'
import { TagCell, useTagName } from './tags'
import { HeaderButton } from './TransactionTable'
import { useKept } from '../keep'

const NONE_HIDDEN = new Set<string>()

/**
 * Transfers, card payments and investments, each movement once: from which account (or the outside) to which account
 * (or the outside). Places outside the app (accounts not imported, other people, investments) are in orange.
 * Dates, categories, descriptions and tags each have a filter (all, none, one or any of them).
 */
export function MovementsTable({ keep, movements, transactions, currency, paged, onEdit }: {
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
  const theme = useTheme()
  const [page, setPage] = useKept(keep + ':page', 1)
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
  const tagsOf = (m: MovementView) => byId.get(m.editId)?.tags ?? []
  const tagFilter = useMemo(() => tagChoices(movements, (m) => byId.get(m.editId)?.tags ?? [], tagName), [movements, byId, tagName])
  const rows = useMemo(() => [...movements].reverse().filter((m) => !hiddenDates.has(m.date) && !hiddenCategories.has(m.categoryId) && !hiddenLabels.has(m.label)
    && shownByTags(byId.get(m.editId)?.tags ?? [], hiddenTags)),
    [movements, byId, hiddenDates, hiddenCategories, hiddenLabels, hiddenTags])
  const filtered = (set: (hidden: Set<string>) => void) => (hidden: Set<string>) => { set(hidden); setPage(1) }
  const pages = Math.max(1, Math.ceil(rows.length / pageSize))
  const visible = paged ? rows.slice((Math.min(page, pages) - 1) * pageSize, Math.min(page, pages) * pageSize) : rows

  const place = (name: string, outside: boolean, type: string | null) => (
    <Box sx={{ minWidth: 0 }}>
      <Typography variant="body2" title={name} sx={{ overflowWrap: 'anywhere', fontWeight: outside ? 520 : 600, color: outside ? theme.palette.outside : 'text.primary', lineHeight: 1.3 }}>{name}</Typography>
      {type && <Typography variant="caption" color="text.secondary" sx={{ display: 'block', lineHeight: 1.2 }}>{type}</Typography>}
    </Box>
  )


  return (
    <Box>
      <Table size="small" data-movements sx={{ tableLayout: 'fixed', '& td, & th': { px: 1.25 }, '& tr > *:first-of-type': { pl: 0.5 } }}>
        <colgroup>
          <col style={{ width: '14%' }} />
          <col style={{ width: 196 }} />
          <col />
          <col style={{ width: '14%' }} />
          <col style={{ width: 22 }} />
          <col style={{ width: '16%' }} />
          <col style={{ width: '12%' }} />
          <col style={{ width: '10%' }} />
          <col style={{ width: 40 }} />
        </colgroup>
        <TableHead>
          <TableRow>
            <TableCell><HeaderButton label="Date" active={hiding(hiddenDates, dateChoices)} onClick={(e) => setDateMenu(e.currentTarget)} /></TableCell>
            <TableCell><HeaderButton label="Category" active={hiding(hiddenCategories, categoryChoices)} onClick={(e) => setCatMenu(e.currentTarget)} /></TableCell>
            <TableCell><HeaderButton label="Description" active={hiding(hiddenLabels, labelChoices)} onClick={(e) => setLabelMenu(e.currentTarget)} /></TableCell>
            <TableCell>From</TableCell>
            <TableCell padding="none" />
            <TableCell>To</TableCell>
            <TableCell><HeaderButton label="Tags" active={hiding(hiddenTags, tagFilter)} onClick={(e) => setTagMenu(e.currentTarget)} /></TableCell>
            <TableCell align="right">Amount ({currency})</TableCell>
            <TableCell />
          </TableRow>
        </TableHead>
        <TableBody>
          {visible.map((m) => {
            const tx = byId.get(m.editId)
            return (
              <TableRow key={m.editId} hover sx={{ '&:hover .row-action': { opacity: 1 }, '& td': { py: 0.9 } }}>
                <TableCell className="tnum" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>{formatDate(m.date, settings.dateFormat)}</TableCell>
                <TableCell><CategoryPill id={m.categoryId} onChange={(id) => void setCategory(m, id)} /></TableCell>
                <TableCell>
                  <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 0 }}>
                    <Typography variant="body2" noWrap title={tx?.rawLabel ?? m.label}>{m.label}</Typography>
                  </Box>
                </TableCell>
                <TableCell>{place(m.from, m.fromOutside, m.fromType)}</TableCell>
                <TableCell padding="none"><East sx={{ fontSize: 16, color: 'text.disabled', display: 'block' }} /></TableCell>
                <TableCell>{place(m.to, m.toOutside, m.toType)}</TableCell>
                <TableCell>{tx && <TagCell operationId={tx.id} own={tx.ownTags} all={tagsOf(m)} categoryName={category(tx.categoryId).name} />}</TableCell>
                <TableCell align="right" className="tnum" sx={{ fontWeight: 560, whiteSpace: 'nowrap' }}>{money(m.amount, currency)}</TableCell>
                <TableCell padding="none" align="right">
                  {tx && (
                    <Tooltip title="Edit">
                      <IconButton className="row-action" size="small" aria-label="Edit" onClick={() => onEdit(tx)} sx={{ opacity: 0, transition: 'opacity 120ms', '&:focus-visible': { opacity: 1 } }}>
                        <EditOutlined sx={{ fontSize: 17 }} />
                      </IconButton>
                    </Tooltip>
                  )}
                </TableCell>
              </TableRow>
            )
          })}
          {visible.length === 0 && (
            <TableRow>
              <TableCell colSpan={9} sx={{ py: 4, textAlign: 'center', color: 'text.secondary', borderBottom: 0 }}>No movements match these filters</TableCell>
            </TableRow>
          )}
        </TableBody>
      </Table>
      {paged && pages > 1 && (
        <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mt: 1.5 }}>
          <Typography variant="body2" color="text.secondary">{plural(rows.length, 'movement')}</Typography>
          <Pagination count={pages} page={Math.min(page, pages)} onChange={(_, p) => setPage(p)} shape="rounded" size="small" />
        </Box>
      )}
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
    </Box>
  )
}
