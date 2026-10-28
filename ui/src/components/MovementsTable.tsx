import { useMemo, useState } from 'react'
import { Box, IconButton, Pagination, Table, TableBody, TableCell, TableHead, TableRow, Tooltip, Typography, useTheme } from '@mui/material'
import EditOutlined from '@mui/icons-material/EditOutlined'
import East from '@mui/icons-material/EastRounded'
import { api, type MovementView, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { formatDate, money, plural } from '../format'
import { CategoryPill } from './common'

/**
 * Transfers, card payments and investments, each movement once: from which account (or the outside) to which account
 * (or the outside). Places outside the app (accounts not imported, other people, investments) are in orange.
 */
export function MovementsTable({ movements, transactions, currency, paged, onEdit }: {
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
  const [page, setPage] = useState(1)
  const pageSize = 25
  const byId = useMemo(() => new Map(transactions.map((t) => [t.id, t])), [transactions])
  const rows = useMemo(() => [...movements].reverse(), [movements])
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
          <col style={{ width: '16%' }} />
          <col style={{ width: 22 }} />
          <col style={{ width: '19%' }} />
          <col style={{ width: '10%' }} />
          <col style={{ width: 40 }} />
        </colgroup>
        <TableHead>
          <TableRow>
            <TableCell>Date</TableCell>
            <TableCell>Category</TableCell>
            <TableCell>Description</TableCell>
            <TableCell>From</TableCell>
            <TableCell padding="none" />
            <TableCell>To</TableCell>
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
        </TableBody>
      </Table>
      {paged && pages > 1 && (
        <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mt: 1.5 }}>
          <Typography variant="body2" color="text.secondary">{plural(rows.length, 'movement')}</Typography>
          <Pagination count={pages} page={Math.min(page, pages)} onChange={(_, p) => setPage(p)} shape="rounded" size="small" />
        </Box>
      )}
    </Box>
  )
}
