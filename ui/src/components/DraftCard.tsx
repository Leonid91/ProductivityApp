import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Box, Button, Checkbox, Chip, InputBase, MenuItem, Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography,
} from '@mui/material'
import CheckCircle from '@mui/icons-material/CheckCircleRounded'
import ErrorOutline from '@mui/icons-material/ErrorOutlineRounded'
import HelpOutline from '@mui/icons-material/HelpOutlineRounded'
import ViewColumn from '@mui/icons-material/ViewColumnRounded'
import DocumentScanner from '@mui/icons-material/DocumentScannerRounded'
import { api, type AccountKind, type Draft, type DraftEdit } from '../api'
import { useApp } from '../AppContext'
import { formatDate, money, plural, shortDate } from '../format'
import { CategoryPill, Money } from './common'
import { TemplateEditor } from './TemplateEditor'

const currencies = ['CAD', 'USD', 'EUR', 'GBP', 'CHF', 'AUD', 'JPY', 'MXN']

export function StatusChip({ draft }: { draft: Draft }) {
  if (draft.alreadyImported) return <Chip size="small" color="default" label="Already imported" />
  switch (draft.status) {
    case 'verified':
      return <Chip size="small" color="success" icon={<CheckCircle />} label="Balanced to the cent" variant="outlined" />
    case 'mismatch':
      return <Chip size="small" color="warning" icon={<ErrorOutline />} label={`Off by ${money(Math.abs(draft.difference ?? 0), draft.currency)}`} variant="outlined" />
    case 'noBalances':
      return <Chip size="small" icon={<HelpOutline />} label="No balances to check against" variant="outlined" />
    default:
      return <Chip size="small" color="error" icon={<ErrorOutline />} label="No operations found" variant="outlined" />
  }
}

/** A statement read from a PDF, waiting for the person to check it and save it. */
export function DraftCard({ draft: initial, onDone }: { draft: Draft; onDone: (saved: boolean) => void }) {
  const { accounts, settings, notify, fail, dataChanged, refreshAccounts } = useApp()
  const [draft, setDraft] = useState(initial)
  const [accountId, setAccountId] = useState<number | 'new'>(initial.suggestedAccountId ?? 'new')
  const [name, setName] = useState(initial.defaultAccountName)
  const [kind, setKind] = useState<AccountKind>(initial.kind)
  const [currency, setCurrency] = useState(initial.currency || settings.currency)
  const [edits, setEdits] = useState<Map<number, DraftEdit>>(new Map())
  const [busy, setBusy] = useState(false)
  const [editingColumns, setEditingColumns] = useState(false)

  useEffect(() => { void refreshAccounts() }, [refreshAccounts])

  // A statement read before its account was created by another import: use that account.
  useEffect(() => {
    if (accountId !== 'new' || !draft.last4) return
    const same = accounts.find((a) => a.last4 === draft.last4 && a.kind === draft.kind && a.currency === draft.currency)
    if (same) void pickAccount(same.id)
  }, [accounts])

  const pickAccount = async (v: number | 'new') => {
    setAccountId(v)
    try {
      setDraft(await api.imports.setAccount(draft.id, v === 'new' ? null : v))
    } catch (e) {
      fail(e)
    }
    const a = accounts.find((x) => x.id === v)
    if (a) { setCurrency(a.currency); setKind(a.kind) }
  }

  const edit = (index: number, change: Partial<DraftEdit>) => {
    const next = new Map(edits)
    const current = next.get(index) ?? { index, brand: null, categoryId: null, include: true }
    next.set(index, { ...current, ...change })
    setEdits(next)
  }

  const included = useMemo(() => draft.rows.filter((r) => !r.duplicate && (edits.get(r.index)?.include ?? true)), [draft.rows, edits])
  const duplicates = draft.rows.filter((r) => r.duplicate).length

  const commit = async () => {
    setBusy(true)
    try {
      const r = await api.imports.commit({
        draftId: draft.id,
        accountId: accountId === 'new' ? null : accountId,
        newAccountName: accountId === 'new' ? name : null,
        currency,
        kind,
        edits: [...edits.values()],
      })
      notify(`${plural(r.added, 'operation')} imported` + (r.skippedDuplicates ? ` · ${r.skippedDuplicates} already there` : ''))
      dataChanged()
      onDone(true)
    } catch (e) {
      fail(e)
    } finally {
      setBusy(false)
    }
  }

  const discard = async () => {
    await api.imports.discard(draft.id).catch(() => undefined)
    onDone(false)
  }

  const period = draft.periodStart && draft.periodEnd ? `${shortDate(draft.periodStart)} – ${shortDate(draft.periodEnd)}` : null
  const needsColumns = !draft.alreadyImported && (draft.status === 'noTransactions' || draft.status === 'mismatch')

  return (
    <Box data-draft={draft.fileName} sx={{ border: 1, borderColor: 'divider', borderRadius: '24px', bgcolor: 'background.paper', p: { xs: 2.5, md: 3 } }}>
      <Box sx={{ display: 'flex', alignItems: 'flex-start', gap: 2, flexWrap: 'wrap', mb: 2 }}>
        <Box sx={{ flex: '1 1 300px', minWidth: 0 }}>
          <Typography variant="h6" noWrap>{draft.fileName}</Typography>
          <Typography variant="body2" color="text.secondary">
            {[draft.bank, period, plural(draft.rows.length, 'operation')].filter(Boolean).join(' · ')}
          </Typography>
        </Box>
        <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', alignItems: 'center' }}>
          {draft.usedOcr && <Chip size="small" icon={<DocumentScanner />} label="Scanned" variant="outlined" />}
          {draft.template && <Chip size="small" icon={<ViewColumn />} label={draft.template.name} variant="outlined" />}
          <StatusChip draft={draft} />
        </Box>
      </Box>

      {draft.status === 'verified' && draft.opening !== null && draft.closing !== null && (
        <Typography variant="body2" color="text.secondary" className="tnum" sx={{ mb: 2 }}>
          {money(draft.opening, draft.currency)} → {money(draft.closing, draft.currency)}
        </Typography>
      )}
      {needsColumns && (
        <Alert
          severity={draft.status === 'mismatch' ? 'warning' : 'error'}
          sx={{ mb: 2 }}
          action={<Button color="inherit" size="small" startIcon={<ViewColumn />} onClick={() => setEditingColumns(true)}>Set columns</Button>}
        >
          {draft.status === 'mismatch'
            ? `The operations add up to ${money(draft.computedClosing ?? 0, draft.currency)}, the statement says ${money(draft.closing ?? 0, draft.currency)}.`
            : 'The table of this statement was not recognised.'}
        </Alert>
      )}

      {!draft.alreadyImported && draft.rows.length > 0 && (
        <>
          <Box sx={{ display: 'flex', gap: 1.5, flexWrap: 'wrap', mb: 2 }}>
            <TextField select size="small" label="Account" value={accountId} onChange={(e) => void pickAccount(e.target.value === 'new' ? 'new' : Number(e.target.value))} sx={{ minWidth: 240 }}>
              <MenuItem value="new">New account</MenuItem>
              {accounts.map((a) => <MenuItem key={a.id} value={a.id}>{a.name}</MenuItem>)}
            </TextField>
            {accountId === 'new' && (
              <>
                <TextField size="small" label="Name" value={name} onChange={(e) => setName(e.target.value)} sx={{ minWidth: 260, flex: '1 1 260px' }} />
                <TextField select size="small" label="Type" value={kind} onChange={(e) => setKind(e.target.value as AccountKind)} sx={{ width: 160 }}>
                  <MenuItem value="bank">Bank account</MenuItem>
                  <MenuItem value="creditCard">Credit card</MenuItem>
                </TextField>
                <TextField select size="small" label="Currency" value={currency} onChange={(e) => setCurrency(e.target.value)} sx={{ width: 110 }}>
                  {[...new Set([currency, ...currencies])].map((c) => <MenuItem key={c} value={c}>{c}</MenuItem>)}
                </TextField>
              </>
            )}
          </Box>

          <Box sx={{ maxHeight: 420, overflow: 'auto', border: 1, borderColor: 'divider', borderRadius: '16px' }}>
            <Table size="small" stickyHeader sx={{ '& th': { bgcolor: 'background.paper' } }}>
              <TableHead>
                <TableRow>
                  <TableCell padding="checkbox" />
                  <TableCell>Date</TableCell>
                  <TableCell>Statement label</TableCell>
                  <TableCell>Brand Name</TableCell>
                  <TableCell>Category</TableCell>
                  <TableCell align="right">Amount</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {draft.rows.map((r) => {
                  const e = edits.get(r.index)
                  const include = !r.duplicate && (e?.include ?? true)
                  return (
                    <TableRow key={r.index} sx={{ opacity: include ? 1 : 0.45 }}>
                      <TableCell padding="checkbox">
                        <Tooltip title={r.duplicate ? 'Already imported' : ''}>
                          <span><Checkbox size="small" checked={include} disabled={r.duplicate} onChange={(ev) => edit(r.index, { include: ev.target.checked })} /></span>
                        </Tooltip>
                      </TableCell>
                      <TableCell className="tnum" sx={{ whiteSpace: 'nowrap', color: 'text.secondary' }}>{formatDate(r.date, settings.dateFormat)}</TableCell>
                      <TableCell sx={{ maxWidth: 280 }}>
                        <Typography variant="body2" noWrap title={r.label} className="selectable">{r.label}</Typography>
                      </TableCell>
                      <TableCell sx={{ minWidth: 150 }}>
                        <InputBase
                          value={e?.brand ?? r.brand}
                          onChange={(ev) => edit(r.index, { brand: ev.target.value })}
                          sx={{ fontSize: 13.5, fontWeight: 520, width: '100%', px: 1, mx: -1, borderRadius: 2, '&:hover, &.Mui-focused': { bgcolor: 'action.hover' } }}
                        />
                      </TableCell>
                      <TableCell><CategoryPill id={e?.categoryId ?? r.categoryId} onChange={(id) => edit(r.index, { categoryId: id })} /></TableCell>
                      <TableCell align="right"><Money value={r.amount} currency={currency} sign /></TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          </Box>
        </>
      )}

      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 2.5, flexWrap: 'wrap' }}>
        {!draft.alreadyImported && (
          <Button startIcon={<ViewColumn />} onClick={() => setEditingColumns(true)}>Columns</Button>
        )}
        <Box sx={{ flex: 1 }} />
        {duplicates > 0 && !draft.alreadyImported && (
          <Typography variant="body2" color="text.secondary" sx={{ mr: 1 }}>{duplicates} already imported</Typography>
        )}
        <Button onClick={() => void discard()}>{draft.alreadyImported ? 'Close' : 'Discard'}</Button>
        {!draft.alreadyImported && (
          <Button variant="contained" disabled={busy || included.length === 0} onClick={() => void commit()}>
            Import {plural(included.length, 'operation')}
          </Button>
        )}
      </Box>

      {editingColumns && (
        <TemplateEditor
          draft={draft}
          onClose={(next) => { setEditingColumns(false); if (next) { setDraft(next); setEdits(new Map()) } }}
        />
      )}
    </Box>
  )
}
