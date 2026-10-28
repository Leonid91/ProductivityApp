import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Box, Button, Checkbox, Chip, InputBase, MenuItem, Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography,
} from '@mui/material'
import CheckCircle from '@mui/icons-material/CheckCircleRounded'
import ErrorOutline from '@mui/icons-material/ErrorOutlineRounded'
import HelpOutline from '@mui/icons-material/HelpOutlineRounded'
import ViewColumn from '@mui/icons-material/ViewColumnRounded'
import DocumentScanner from '@mui/icons-material/DocumentScannerRounded'
import { accountKinds, api, type AccountKind, type Draft, type DraftEdit } from '../api'
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
      return (
        <Tooltip title="This file prints no opening, closing or running balance, so its total cannot be checked">
          <Chip size="small" icon={<HelpOutline />} label="No balances to check against" variant="outlined" />
        </Tooltip>
      )
    default:
      return <Chip size="small" color="error" icon={<ErrorOutline />} label="No operations found" variant="outlined" />
  }
}

/** The balance check in plain words: what the file says it starts and ends with, and what its lines add up to. */
function BalanceCheck({ draft }: { draft: Draft }) {
  const { settings } = useApp()
  const d = (x: string | null) => (x ? formatDate(x, settings.dateFormat) : '')
  const m = (v: number, sign = false) => money(v, draft.currency, { sign })
  const lines = draft.rows.length
  const sum = draft.rows.reduce((s, r) => s + r.amount, 0)
  let body: React.ReactNode
  let severity: 'success' | 'warning' | 'info' = 'info'
  if (draft.status === 'verified' && draft.opening !== null && draft.closing !== null) {
    severity = 'success'
    body = (
      <>
        The file starts with a balance of <b>{m(draft.opening)}</b> ({d(draft.periodStart)}) and ends with <b>{m(draft.closing)}</b> ({d(draft.periodEnd)}).
        The {plural(lines, 'operation')} read from it add up to <b>{m(sum, true)}</b>, exactly the difference between the two balances.
        So every line of this file was read, none twice.
      </>
    )
  } else if (draft.status === 'mismatch') {
    severity = 'warning'
    body = (
      <>
        The file starts with <b>{m(draft.opening ?? 0)}</b> and ends with <b>{m(draft.closing ?? 0)}</b>, a change of {m((draft.closing ?? 0) - (draft.opening ?? 0), true)}.
        The operations read add up to {m(sum, true)}: off by <b>{m(Math.abs(draft.difference ?? 0))}</b>. Some lines were misread or skipped:
        set the columns, or check the lines below.
      </>
    )
  } else if (draft.status === 'noBalances') {
    body = (
      <>
        This file prints no balance (card exports usually do not), so the app cannot prove that no line is missing.
        Its {plural(lines, 'operation')} add up to <b>{m(sum, true)}</b>: compare with your bank if in doubt.
      </>
    )
  } else return null
  return (
    <Box data-balance-check sx={{ mb: 2, display: 'grid', gap: 1 }}>
      <Alert severity={severity} icon={false} sx={{ '& .MuiAlert-message': { width: '100%' } }}>
        <Typography variant="subtitle2" sx={{ mb: 0.5 }}>Balance check of this file</Typography>
        <Typography variant="body2" component="div">{body}</Typography>
      </Alert>
    </Box>
  )
}

/** A statement read from a PDF, waiting for the person to check it and save it. */
export function DraftCard({ draft: initial, onDone }: { draft: Draft; onDone: (saved: boolean) => void }) {
  const { accounts, settings, notify, fail, dataChanged, refreshAccounts, askScope, category } = useApp()
  const [draft, setDraft] = useState(initial)
  // Nothing is chosen for the person: the file waits until they say which account it belongs to.
  const [accountId, setAccountId] = useState<number | 'new' | ''>('')
  const [name, setName] = useState('')
  const [kind, setKind] = useState<AccountKind | ''>('')
  const [currency, setCurrency] = useState(initial.currency || settings.currency)
  const [edits, setEdits] = useState<Map<number, DraftEdit>>(new Map())
  const [busy, setBusy] = useState(false)
  const [editingColumns, setEditingColumns] = useState(false)

  useEffect(() => { void refreshAccounts() }, [refreshAccounts])

  const choose = async (account: number | 'new', newKind: AccountKind | '') => {
    setAccountId(account)
    const a = accounts.find((x) => x.id === account)
    if (a) { setCurrency(a.currency); setKind(a.kind) }
    else setKind(newKind)
    if (account === 'new' && newKind === '') return
    try {
      // The file is read again for the chosen type, and checked against that account only.
      setDraft(await api.imports.choose(draft.id, account === 'new' ? null : account, account === 'new' ? newKind as AccountKind : null))
    } catch (e) {
      fail(e)
    }
  }

  const kindLabel = (k: AccountKind) => accountKinds(settings.bankingSystem).find((x) => x.value === k)?.label ?? k
  const chosen = accountId !== '' && (accountId !== 'new' || kind !== '')

  const edit = (index: number, change: Partial<DraftEdit>) => {
    const next = new Map(edits)
    const current = next.get(index) ?? { index, brand: null, categoryId: null, include: true }
    next.set(index, { ...current, ...change })
    setEdits(next)
  }

  /** A new category for one line: for it only, or for every line of the same brand (and learned for later imports). */
  const changeCategory = async (index: number, id: string) => {
    const row = draft.rows.find((r) => r.index === index)
    if (!row) return
    const brand = edits.get(index)?.brand ?? row.brand
    const all = await askScope(brand, category(id).name)
    if (all === null) return
    const next = new Map(edits)
    const targets = all ? draft.rows.filter((r) => (edits.get(r.index)?.brand ?? r.brand).toLowerCase() === brand.toLowerCase()) : [row]
    for (const r of targets) {
      const current = next.get(r.index) ?? { index: r.index, brand: null, categoryId: null, include: true }
      next.set(r.index, { ...current, categoryId: id, allOfBrand: all })
    }
    setEdits(next)
  }

  const included = useMemo(() => draft.rows.filter((r) => !r.duplicate && (edits.get(r.index)?.include ?? true)), [draft.rows, edits])
  const duplicates = draft.rows.filter((r) => r.duplicate).length

  const commit = async () => {
    setBusy(true)
    try {
      const r = await api.imports.commit({
        draftId: draft.id,
        accountId: accountId === 'new' || accountId === '' ? null : accountId,
        newAccountName: accountId === 'new' ? name : null,
        currency,
        kind: kind === '' ? null : kind,
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
          {draft.method === 'CSV' && <Chip size="small" label="CSV" variant="outlined" />}
          {draft.template && <Chip size="small" icon={<ViewColumn />} label={draft.template.name} variant="outlined" />}
          <StatusChip draft={draft} />
        </Box>
      </Box>

      {!draft.alreadyImported && <BalanceCheck draft={draft} />}
      {needsColumns && draft.pageCount > 0 && (
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
            <TextField
              select
              size="small"
              label="Account"
              value={accountId}
              error={accountId === ''}
              onChange={(e) => void choose(e.target.value === 'new' ? 'new' : Number(e.target.value), '')}
              sx={{ minWidth: 280 }}
              slotProps={{ select: { displayEmpty: true, renderValue: (v) => v === '' ? 'Choose the account' : v === 'new' ? 'New account' : accounts.find((a) => a.id === v)?.name ?? '' }, inputLabel: { shrink: true } }}
            >
              {accounts.map((a) => (
                <MenuItem key={a.id} value={a.id}>
                  <Box sx={{ flex: 1 }}>{a.name}</Box>
                  <Typography variant="body2" color="text.secondary" sx={{ ml: 2 }}>{kindLabel(a.kind)} · {a.currency}</Typography>
                </MenuItem>
              ))}
              <MenuItem value="new">New account</MenuItem>
            </TextField>
            {accountId === 'new' && (
              <>
                <TextField
                  select
                  size="small"
                  label="Type"
                  value={kind}
                  error={kind === ''}
                  onChange={(e) => void choose('new', e.target.value as AccountKind)}
                  sx={{ width: 210 }}
                  slotProps={{ select: { displayEmpty: true, renderValue: (v) => v === '' ? 'Choose the type' : kindLabel(v as AccountKind) }, inputLabel: { shrink: true } }}
                >
                  {accountKinds(settings.bankingSystem).map((k) => <MenuItem key={k.value} value={k.value}>{k.label}</MenuItem>)}
                </TextField>
                <TextField size="small" label="Name" value={name} placeholder={draft.defaultAccountName} onChange={(e) => setName(e.target.value)} sx={{ minWidth: 260, flex: '1 1 260px' }} slotProps={{ inputLabel: { shrink: true } }} />
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
                      <TableCell><CategoryPill id={e?.categoryId ?? r.categoryId} onChange={(id) => void changeCategory(r.index, id)} /></TableCell>
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
        {!draft.alreadyImported && draft.pageCount > 0 && (
          <Button startIcon={<ViewColumn />} onClick={() => setEditingColumns(true)}>Columns</Button>
        )}
        <Box sx={{ flex: 1 }} />
        {duplicates > 0 && !draft.alreadyImported && (
          <Typography variant="body2" color="text.secondary" sx={{ mr: 1 }}>{duplicates} already imported</Typography>
        )}
        <Button onClick={() => void discard()}>{draft.alreadyImported ? 'Close' : 'Discard'}</Button>
        {!draft.alreadyImported && (
          <Button variant="contained" disabled={busy || !chosen || included.length === 0} onClick={() => void commit()}>
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
