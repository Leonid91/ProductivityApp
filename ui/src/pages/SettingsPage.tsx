import { useCallback, useEffect, useState } from 'react'
import {
  Box, Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, InputBase, MenuItem, Radio, Switch, Table, TableBody, TableCell, TableHead, TableRow, TextField, ToggleButton, ToggleButtonGroup,
  Tooltip, Typography, alpha, useTheme,
} from '@mui/material'
/** Read by screen readers only. */
const visuallyHidden = { border: 0, clip: 'rect(0 0 0 0)', height: '1px', m: '-1px', overflow: 'hidden', p: 0, position: 'absolute', whiteSpace: 'nowrap', width: '1px' } as const
import Add from '@mui/icons-material/AddRounded'
import DeleteOutline from '@mui/icons-material/DeleteOutlineRounded'
import FolderOpen from '@mui/icons-material/FolderOpenRounded'
import AccountBalance from '@mui/icons-material/AccountBalanceRounded'
import Refresh from '@mui/icons-material/RefreshRounded'
import ErrorOutline from '@mui/icons-material/ErrorOutlineRounded'
import { accountKinds, api, currencies, type Account, type AccountKind, type Bank, type BankingSystem, type CentralBank, type RateSource } from '../api'
import { useApp } from '../AppContext'
import { formatDate, formatExample } from '../format'
import { FileBrowser } from '../components/FileBrowser'
import { ConfirmDialog, EmptyState, PageHeader, SectionCard } from '../components/common'
import { useRowOrder } from '../components/order'
import { BrandsPanel } from './BrandsPage'
import { CategoriesPanel } from './CategoriesPage'
import { TagsPanel } from './TagsPage'

const systems: { value: BankingSystem; label: string }[] = [
  // Credit cards are accounts of their own, paid from the chequing account; or (France) debit cards only: purchases
  // come out of the current account, once a month for a deferred-debit card.
  { value: 'northAmerica', label: 'Credit card system' },
  { value: 'france', label: 'Debit card system' },
]

type Confirm = { title: string; body?: string; confirm?: string; run: () => Promise<void> } | null

/** A setting: its label on the left, its value on the right; note: a line under the value (an error). */
function Row({ label, children, note }: { label: string; children: React.ReactNode; note?: React.ReactNode }) {
  return (
    // No line under the last row, nor above a group (the group has its own border).
    <Box sx={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', columnGap: 2, rowGap: 0.75, py: 1.5, borderBottom: 1, borderColor: 'divider', '&:last-of-type, &:has(+ [data-group])': { borderBottom: 0 } }}>
      <Typography sx={{ flex: 1, fontWeight: 520 }}>{label}</Typography>
      {children}
      {note && <Box sx={{ flexBasis: '100%', display: 'flex', justifyContent: 'flex-end' }}>{note}</Box>}
    </Box>
  )
}

/** Why a source of exchange rates gives no rates, in red under its address or its choice. */
function RatesError({ message }: { message: string }) {
  return (
    <Typography variant="body2" role="alert" data-rates-error sx={{ color: 'error.main', display: 'flex', alignItems: 'flex-start', gap: 0.75, maxWidth: 560 }}>
      <ErrorOutline sx={{ fontSize: 18, mt: '1px', flexShrink: 0 }} />
      {message}
    </Typography>
  )
}

/** A group of settings inside a card, under its own small heading (Periods and Calendar inside Display). */
function Group({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <Box data-group={title} sx={{ mt: 2, border: 1, borderColor: 'divider', borderRadius: '14px', bgcolor: 'surface.sunken', px: 2, pt: 1.5, pb: 0.5 }}>
      <Typography variant="subtitle2" color="text.secondary">{title}</Typography>
      {children}
    </Box>
  )
}

function Toggle({ label, setting }: { label: string; setting: string }) {
  const { settings, setSetting } = useApp()
  return (
    <Row label={label}>
      <Switch slotProps={{ input: { 'aria-label': label } }} checked={settings[setting] === 'true'} onChange={(e) => void setSetting(setting, e.target.checked ? 'true' : 'false')} />
    </Row>
  )
}

function Tabs<T extends string>({ value, onChange, tabs, size }: { value: T; onChange: (v: T) => void; tabs: { value: T; label: string }[]; size?: 'small' }) {
  return (
    <ToggleButtonGroup exclusive size={size} value={value} onChange={(_, v) => v && onChange(v)} sx={{ flexWrap: 'wrap', justifySelf: 'start' }}>
      {tabs.map((t) => <ToggleButton key={t.value} value={t.value}>{t.label}</ToggleButton>)}
    </ToggleButtonGroup>
  )
}

const inlineInput = { flex: 1, fontWeight: 520, px: 1, mx: -1, borderRadius: 2, fontSize: 14, '&:hover, &.Mui-focused': { bgcolor: 'action.hover' } }

function CurrencySelect({ value, onChange, label, width = 110 }: { value: string; onChange: (c: string) => void; label?: string; width?: number }) {
  return (
    <TextField select size="small" label={label} value={value} onChange={(e) => onChange(e.target.value)} sx={{ width }}>
      {[...new Set([value, ...currencies])].map((c) => <MenuItem key={c} value={c}>{c}</MenuItem>)}
    </TextField>
  )
}

/**
 * The central bank whose daily exchange rates convert the other currencies (European Central Bank by default), chosen
 * among the banks of Definitions > Exchange rates (the same choice as the round buttons of that table). Choosing one
 * without rates yet downloads them in the background, which checks its address: nothing is said when it works, the
 * reason is written under the choice when it does not.
 */
function RatesSourceRow({ onEdit }: { onEdit: () => void }) {
  const { settings, dataVersion } = useApp()
  const [sources, setSources] = useState<RateSource[]>([])
  const [checking, setChecking] = useState<string | null>(null)
  useEffect(() => { api.rates.sources().then(setSources, () => setSources([])) }, [settings.ratesSource, dataVersion])
  const choose = useChooseRates(setSources, setChecking)
  const current = settings.ratesSource || 'ecb'
  const error = sources.find((s) => s.id === current)?.error
  return (
    <Row label="Exchange rates" note={!checking && error ? <RatesError message={error} /> : undefined}>
      {checking && <CircularProgress size={16} aria-label="Downloading the exchange rates" />}
      <Button size="small" data-rates-link onClick={onEdit}>Edit banks</Button>
      <TextField select size="small" value={sources.some((s) => s.id === current) ? current : ''} onChange={(e) => void choose(e.target.value, sources)} sx={{ width: 220 }}
        slotProps={{ htmlInput: { 'data-rates-source-select': '' } }}>
        {sources.map((s) => <MenuItem key={s.id} value={s.id}>{s.name}</MenuItem>)}
      </TextField>
    </Row>
  )
}

/**
 * Makes a bank the one whose rates are used. A bank without rates yet is downloaded at once in the background (which
 * checks its address); the new list of banks, with any error, is given back.
 */
function useChooseRates(setSources: (s: RateSource[]) => void, setChecking: (id: string | null) => void) {
  const { setSetting, dataChanged, fail } = useApp()
  return async (id: string, sources: RateSource[]) => {
    await setSetting('ratesSource', id)
    dataChanged()
    if (sources.find((s) => s.id === id)?.latest) { api.rates.sources().then(setSources, fail); return }
    setChecking(id)
    try {
      await api.rates.update(id)
      dataChanged()
    } catch {
      // Whatever went wrong is kept as the bank's error, shown with it.
    } finally {
      setChecking(null)
      api.rates.sources().then(setSources, fail)
    }
  }
}

/** What to paste as the address of a bank's file of rates, in one sentence. */
const ratesHelp = 'Paste the link of the bank’s rates file (CSV, ZIP or JSON), not the address of its web page.'

/** The banks of the table, the central banks the app reads, and the downloads in progress (shared by the table and its buttons). */
function useRateSources(active: boolean) {
  const { fail, dataChanged } = useApp()
  const [sources, setSources] = useState<RateSource[] | null>(null)
  const [banks, setBanks] = useState<CentralBank[]>([])
  const [refreshing, setRefreshing] = useState(false)
  useEffect(() => {
    if (!active) return
    api.rates.sources().then(setSources, fail)
    api.rates.banks().then(setBanks, fail)
  }, [active, fail])
  const refresh = () => {
    setRefreshing(true)
    api.rates.refresh().then((r) => { setSources(r.sources); if (r.updated) dataChanged() }, fail).finally(() => setRefreshing(false))
  }
  const free = banks.filter((b) => !sources?.some((s) => s.format === b.format))
  return { sources, setSources, banks, free, refreshing, refresh }
}

/** The delete button of a line: a plain icon that turns red under the mouse, with no shape of its own around it. */
const quietDelete = {
  color: 'text.secondary', borderRadius: '8px', transition: 'color 120ms',
  '&:hover': { bgcolor: 'transparent', color: 'error.main' },
  '&.Mui-focusVisible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 1 },
}

/**
 * The central banks the exchange rates can come from, one line each: the round button chooses the one in use (its line
 * is tinted), the address of its file can be changed (checked at once by downloading it, in the background; what is
 * wrong shows under the address), and what its last download holds: the number of currencies and the last day of
 * rates, read from the data itself (nothing is shown without it). A new line is one of the central banks the app reads.
 */
/** The first line of every cell of a line of banks: the same height, its content centred on it, so the round button, the
 * texts, the address and the delete icon share one middle (what shows under the address only adds lines below). */
const RATES_LINE = 32
const ratesLine = { height: RATES_LINE, display: 'flex', alignItems: 'center' } as const

function RatesSourcesTable({ rates, adding, onAdded, confirm }: {
  rates: ReturnType<typeof useRateSources>
  adding: boolean
  onAdded: () => void
  confirm: (c: Confirm) => void
}) {
  const { settings, notify, dataChanged } = useApp()
  const theme = useTheme()
  const { sources, setSources, free, refreshing } = rates
  const [checking, setChecking] = useState<Set<string>>(new Set())
  const [choosing, setChoosing] = useState<string | null>(null)
  /** Addresses refused before being saved, by line ("" for the new one). */
  const [refused, setRefused] = useState<Record<string, string>>({})
  const [newBank, setNewBank] = useState('')
  const [newUrl, setNewUrl] = useState('')
  const choose = useChooseRates(setSources, setChoosing)
  useEffect(() => {
    if (!adding) return
    setNewBank(free[0]?.format ?? '')
    setNewUrl(free[0]?.defaultUrl ?? '')
    setRefused(({ '': _, ...rest }) => rest)
  }, [adding]) // eslint-disable-line react-hooks/exhaustive-deps

  const check = (id: string) => {
    setChecking((c) => new Set(c).add(id))
    api.rates.update(id).then(() => api.rates.sources()).then((list) => {
      setSources(list)
      if (list.find((s) => s.id === id)?.inUse) dataChanged()
    }, () => undefined).finally(() => setChecking((c) => { const n = new Set(c); n.delete(id); return n }))
  }
  const save = (s: { id?: string; format?: string; url: string }) =>
    api.rates.saveSource(s).then((r) => {
      setSources(r.sources)
      setRefused(({ [s.id ?? '']: _, ...rest }) => rest)
      check(r.id)
      return true
    }, (e: unknown) => {
      setRefused((x) => ({ ...x, [s.id ?? '']: e instanceof Error ? e.message : String(e) }))
      return false
    })

  if (!sources) return null
  const inUseTint = alpha(theme.palette.primary.main, theme.palette.mode === 'dark' ? 0.12 : 0.06)
  return (
    <>
      <Typography variant="body2" color="text.secondary" data-rates-help className="selectable" sx={{ mt: -1 }}>{ratesHelp}</Typography>
      <SectionCard>
        <Table size="small" data-rate-sources sx={{ tableLayout: 'fixed', '& td': { borderColor: 'divider' } }}>
          <TableHead>
            <TableRow>
              <TableCell padding="checkbox" sx={{ width: 52 }}><Box component="span" sx={visuallyHidden}>In use</Box></TableCell>
              <TableCell sx={{ width: '24%' }}>Central bank</TableCell>
              <TableCell>Address of the rates file</TableCell>
              <TableCell align="right" sx={{ width: 104 }}>Currencies</TableCell>
              <TableCell sx={{ width: 170, pl: 3 }}>Rates up to</TableCell>
              <TableCell padding="checkbox" sx={{ width: 52 }} />
            </TableRow>
          </TableHead>
          <TableBody>
            {sources.map((s) => {
              const busy = checking.has(s.id) || choosing === s.id || refreshing
              const error = refused[s.id] ?? (busy ? null : s.error)
              return (
                <TableRow key={s.id} data-rate-source={s.id} data-in-use={s.inUse || undefined}
                  sx={{ bgcolor: s.inUse ? inUseTint : undefined, transition: 'background-color 160ms', verticalAlign: 'top', '& td, & td.MuiTableCell-paddingCheckbox': { py: 1.25 } }}>
                  <TableCell padding="checkbox">
                    <Box sx={{ ...ratesLine, justifyContent: 'center' }}>
                      <Radio size="small" checked={s.inUse} onChange={() => void choose(s.id, sources)} slotProps={{ input: { 'aria-label': `Use the rates of the ${s.name}` } }} />
                    </Box>
                  </TableCell>
                  <TableCell sx={{ fontWeight: s.inUse ? 650 : 520 }}><Box sx={ratesLine}>{s.name}</Box></TableCell>
                  <TableCell>
                    <InputBase
                      key={s.url}
                      defaultValue={s.url}
                      fullWidth
                      inputProps={{ 'aria-label': `Address of the rates file of the ${s.name}`, 'data-field': 'url', spellCheck: false }}
                      onBlur={(e) => { const v = e.target.value.trim(); if (v !== s.url) void save({ id: s.id, url: v }) }}
                      onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
                      sx={{ ...inlineInput, height: RATES_LINE, fontSize: 13, fontWeight: 450, width: '100%', fontFamily: 'ui-monospace, Consolas, monospace', '& input': { textOverflow: 'ellipsis' } }}
                    />
                    {busy && <Typography variant="body2" color="text.secondary" sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 0.5 }}><CircularProgress size={12} />Downloading</Typography>}
                    {error && <Box sx={{ mt: 0.5 }}><RatesError message={error} /></Box>}
                  </TableCell>
                  <TableCell align="right" className="tnum" data-currencies><Box sx={{ ...ratesLine, justifyContent: 'flex-end' }}>{s.currencies > 0 ? s.currencies : ''}</Box></TableCell>
                  <TableCell className="tnum" data-latest sx={{ pl: 3, whiteSpace: 'nowrap' }}><Box sx={ratesLine}>{s.latest ? formatDate(s.latest, settings.dateFormat) : ''}</Box></TableCell>
                  <TableCell padding="checkbox">
                    <Box sx={{ ...ratesLine, justifyContent: 'center' }}>
                    {!s.inUse && (
                      <IconButton size="small" disableRipple aria-label={`Delete the ${s.name}`} sx={quietDelete} onClick={() => confirm({
                        title: `Delete the ${s.name}?`,
                        body: 'The rates downloaded from it are deleted too.',
                        run: async () => { setSources(await api.rates.removeSource(s.id)); notify('Bank deleted') },
                      })}>
                        <DeleteOutline fontSize="small" />
                      </IconButton>
                    )}
                    </Box>
                  </TableCell>
                </TableRow>
              )
            })}
            {adding && (
              <TableRow data-new-rate-source sx={{ verticalAlign: 'top', '& td': { py: 1.25, borderBottom: 0 } }}>
                <TableCell padding="checkbox" />
                <TableCell>
                  <TextField select size="small" fullWidth value={newBank} autoFocus
                    onChange={(e) => { setNewBank(e.target.value); setNewUrl(free.find((b) => b.format === e.target.value)?.defaultUrl ?? '') }}
                    slotProps={{ htmlInput: { 'aria-label': 'Central bank', 'data-field': 'bank' } }}>
                    {free.map((b) => <MenuItem key={b.format} value={b.format}>{b.name}</MenuItem>)}
                  </TextField>
                </TableCell>
                <TableCell>
                  <TextField size="small" fullWidth value={newUrl} onChange={(e) => setNewUrl(e.target.value)} placeholder="https://"
                    slotProps={{ htmlInput: { 'aria-label': 'Address of the rates file', 'data-field': 'url', spellCheck: false, style: { fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13 } } }} />
                  {refused[''] && <Box sx={{ mt: 0.5 }}><RatesError message={refused['']} /></Box>}
                </TableCell>
                <TableCell colSpan={3} sx={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                  <Button onClick={onAdded} sx={{ mr: 1 }}>Cancel</Button>
                  <Button variant="contained" disabled={!newBank || newUrl.trim() === ''}
                    onClick={() => void save({ format: newBank, url: newUrl }).then((ok) => { if (ok) { notify('Bank added'); onAdded() } })}>Add</Button>
                </TableCell>
              </TableRow>
            )}
          </TableBody>
        </Table>
      </SectionCard>
    </>
  )
}

function General({ confirm, onEditRates }: { confirm: (c: Confirm) => void; onEditRates: () => void }) {
  const { settings, setSetting, info, dataFolder, setDataFolder, dataChanged, notify, fail } = useApp()
  const [browsing, setBrowsing] = useState<'reports' | 'database' | null>(null)

  const moveDatabase = async (folder: string) => {
    try {
      setDataFolder((await api.moveDatabase(folder)).dataFolder)
      notify('Database moved')
      dataChanged()
    } catch (e) {
      fail(e)
    }
  }

  return (
    <>
      <SectionCard title="Display">
        {/* Every bank together is converted to this currency and shown in it. */}
        <Row label="Global display currency">
          <Box data-global-currency sx={{ display: 'contents' }}>
            <CurrencySelect width={220} value={settings.currency} onChange={(c) => void setSetting('currency', c)} />
          </Box>
        </Row>
        <RatesSourceRow onEdit={onEditRates} />
        <Row label="Date format">
          <TextField select size="small" value={settings.dateFormat} onChange={(e) => void setSetting('dateFormat', e.target.value)} sx={{ width: 220 }}>
            {info.dateFormats.map((f) => <MenuItem key={f} value={f}>{formatExample(f)}</MenuItem>)}
          </TextField>
        </Row>
        <Group title="Periods">
          <Row label="Transfers, card payments and investments">
            <TextField select size="small" value={settings.periodTransfers} onChange={(e) => void setSetting('periodTransfers', e.target.value)} sx={{ width: 260 }}>
              <MenuItem value="separate">In a separate folded table</MenuItem>
              <MenuItem value="table">In a separate table</MenuItem>
              <MenuItem value="hidden">Hidden</MenuItem>
              <MenuItem value="inline">With the other operations</MenuItem>
            </TextField>
          </Row>
          <Toggle label="Show all data in pie charts" setting="pieShowAll" />
          <Toggle label="Show only first level categories in charts" setting="chartsTopLevel" />
        </Group>
        <Group title="Calendar">
          <Toggle label="Hide transfers" setting="calendarHideTransfers" />
          <Toggle label="Hide card statement payments" setting="calendarHideCardPayments" />
          <Toggle label="Hide investments" setting="calendarHideInvestments" />
        </Group>
      </SectionCard>

      <SectionCard title="Data">
        <Row label="Report location">
          <Typography variant="body2" color="text.secondary" noWrap sx={{ maxWidth: 360 }} className="selectable">{settings.reportsFolder || 'Documents'}</Typography>
          <Button size="small" startIcon={<FolderOpen />} onClick={() => setBrowsing('reports')}>Change</Button>
        </Row>
        <Row label="Database location">
          <Typography variant="body2" color="text.secondary" noWrap sx={{ maxWidth: 360 }} className="selectable">{dataFolder}</Typography>
          <Button size="small" onClick={() => void api.shell.reveal(dataFolder).catch(fail)}>Open</Button>
          <Button size="small" startIcon={<FolderOpen />} onClick={() => setBrowsing('database')}>Change</Button>
        </Row>
        <Row label="Erase all data">
          <Button color="error" variant="outlined" onClick={() => confirm({
            title: 'Erase all data?',
            body: 'Every account, statement, operation, learned brand and template is deleted.',
            run: async () => { await api.eraseAll(); notify('All data erased'); dataChanged() },
          })}>Erase</Button>
        </Row>
        <Typography variant="caption" color="text.disabled" sx={{ display: 'block', mt: 1 }}>Version {info.version}</Typography>
      </SectionCard>

      <FileBrowser
        open={browsing !== null}
        mode="folder"
        title={browsing === 'database' ? 'Database location' : 'Report location'}
        initialPath={(browsing === 'database' ? dataFolder : settings.reportsFolder) || undefined}
        onClose={() => setBrowsing(null)}
        onPick={([p]) => {
          const which = browsing
          setBrowsing(null)
          if (which === 'database') void moveDatabase(p)
          else void setSetting('reportsFolder', p)
        }}
      />
    </>
  )
}

/** The accounts of one bank: name, type (named by the bank's banking system) and currency. */
function AccountsTable({ accounts, system, confirm }: { accounts: Account[]; system: BankingSystem; confirm: (c: Confirm) => void }) {
  const { notify, fail, dataChanged } = useApp()
  const keyOf = useCallback((a: Account) => String(a.id), [])
  const order = useRowOrder('accounts', accounts, keyOf)
  const update = (a: Account, change: Parameters<typeof api.accounts.update>[1], message: string) =>
    api.accounts.update(a, change).then(() => { notify(message); dataChanged() }, fail)

  return (
    <SectionCard>
      <Table size="small" data-accounts>
        <TableHead>
          <TableRow>
            <TableCell padding="none" />
            <TableCell>Account Name</TableCell>
            <TableCell>Account Type</TableCell>
            <TableCell>Currency</TableCell>
            <TableCell padding="checkbox" />
          </TableRow>
        </TableHead>
        <TableBody>
          {order.rows.map((a) => (
            <TableRow key={a.id} hover {...order.rowProps(a)}>
              {order.handle(a)}
              <TableCell>
                <InputBase
                  defaultValue={a.name}
                  inputProps={{ 'aria-label': 'Account name' }}
                  onBlur={(e) => {
                    const v = e.target.value.trim()
                    if (v && v !== a.name) void update(a, { name: v }, 'Account renamed')
                  }}
                  sx={{ ...inlineInput, width: '100%' }}
                />
              </TableCell>
              <TableCell>
                <TextField select size="small" value={a.kind} onChange={(e) => void update(a, { kind: e.target.value as AccountKind }, 'Account type changed')} sx={{ width: 220 }}>
                  {accountKinds(system).map((k) => <MenuItem key={k.value} value={k.value}>{k.label}</MenuItem>)}
                </TextField>
              </TableCell>
              <TableCell><CurrencySelect value={a.currency} onChange={(c) => void update(a, { currency: c }, 'Currency changed')} /></TableCell>
              <TableCell padding="checkbox">
                <Tooltip title="Delete">
                  <IconButton size="small" aria-label="Delete" onClick={() => confirm({
                    title: `Delete ${a.name}?`,
                    body: 'Its statements and operations are deleted too.',
                    run: async () => { await api.accounts.remove(a.id); notify('Account deleted'); dataChanged() },
                  })}>
                    <DeleteOutline fontSize="small" />
                  </IconButton>
                </Tooltip>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </SectionCard>
  )
}

/**
 * One tab per bank, banks with accounts first (the first imported first); accounts are added with the button on the
 * right of the tabs (banks are added in Definitions > Bank).
 */
function BankConfiguration({ confirm }: { confirm: (c: Confirm) => void }) {
  const { accounts, banks } = useApp()
  const [adding, setAdding] = useState(false)
  const first = (bankId: number | null) => {
    const ids = accounts.filter((a) => a.bankId === bankId).map((a) => a.id)
    return ids.length ? Math.min(...ids) : Number.MAX_SAFE_INTEGER
  }
  const tabs = [...banks].sort((a, b) => first(a.id) - first(b.id) || a.id - b.id).map((b) => ({ value: String(b.id), label: b.name }))
  const [tab, setTab] = useState('')
  const current = tabs.find((t) => t.value === tab)?.value ?? tabs[0]?.value
  const bank = banks.find((b) => String(b.id) === current)
  const mine = accounts.filter((a) => String(a.bankId) === current)
  return (
    <>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, flexWrap: 'wrap' }}>
        {tabs.length > 0 && <Tabs size="small" value={current} onChange={setTab} tabs={tabs} />}
        <Box sx={{ flex: 1 }} />
        <Button variant="contained" startIcon={<Add />} disabled={banks.length === 0} onClick={() => setAdding(true)}>Add account</Button>
      </Box>
      {!bank ? <EmptyState icon={<AccountBalance />} title="No banks yet" />
        : mine.length === 0 ? <EmptyState icon={<AccountBalance />} title={`No accounts at ${bank.name} yet`} />
        : <AccountsTable accounts={mine} system={bank.system} confirm={confirm} />}
      <AddAccountDialog open={adding} bankId={bank?.id ?? null} onClose={() => setAdding(false)} />
    </>
  )
}

const emptyBank: Bank = { id: 0, name: '', country: '', system: 'northAmerica', currency: 'CAD' }

/** A new bank: name, country, banking system and the currency of its accounts. Calls back with the new bank's id. */
function AddBankDialog({ open, onClose, onAdded }: { open: boolean; onClose: () => void; onAdded?: (id: number) => void }) {
  const { refreshBanks, dataChanged, notify, fail } = useApp()
  const [draft, setDraft] = useState<Bank>(emptyBank)
  useEffect(() => { if (open) setDraft(emptyBank) }, [open])
  const add = () => api.banks.save(draft).then((list) => {
    notify('Bank added')
    void refreshBanks()
    dataChanged()
    onAdded?.(Math.max(...list.map((b) => b.id)))
    onClose()
  }, fail)
  return (
    <Dialog open={open} onClose={onClose} maxWidth="xs" fullWidth data-bank-dialog>
      <DialogTitle>Add bank</DialogTitle>
      <DialogContent sx={{ display: 'grid', gap: 2.25, pt: '8px !important' }}>
        <TextField autoFocus label="Bank name" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} slotProps={{ htmlInput: { 'data-field': 'name' } }} />
        <TextField label="Country" value={draft.country} onChange={(e) => setDraft({ ...draft, country: e.target.value })} slotProps={{ htmlInput: { 'data-field': 'country' } }} />
        <TextField select label="Banking system" value={draft.system} onChange={(e) => setDraft({ ...draft, system: e.target.value as BankingSystem })}>
          {systems.map((x) => <MenuItem key={x.value} value={x.value}>{x.label}</MenuItem>)}
        </TextField>
        <TextField select label="Currency" value={draft.currency} onChange={(e) => setDraft({ ...draft, currency: e.target.value })}>
          {currencies.map((c) => <MenuItem key={c} value={c}>{c}</MenuItem>)}
        </TextField>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={draft.name.trim() === ''} onClick={() => void add()}>Add</Button>
      </DialogActions>
    </Dialog>
  )
}

/** A new account at one of the banks: name, type (named by the bank's banking system) and currency. */
function AddAccountDialog({ open, bankId, onClose }: { open: boolean; bankId: number | null; onClose: () => void }) {
  const { banks, refreshAccounts, dataChanged, notify, fail } = useApp()
  const [bank, setBank] = useState<number | ''>('')
  const [name, setName] = useState('')
  const [kind, setKind] = useState<AccountKind>('bank')
  const [currency, setCurrency] = useState('CAD')
  useEffect(() => {
    if (!open) return
    const b = banks.find((x) => x.id === bankId) ?? banks[0]
    setBank(b?.id ?? '')
    setName('')
    setKind('bank')
    setCurrency(b?.currency ?? 'CAD')
  }, [open, bankId, banks])
  const system = banks.find((x) => x.id === bank)?.system ?? 'northAmerica'
  const add = () => api.accounts.add({ bankId: Number(bank), name, kind, currency }).then(() => {
    notify('Account added')
    void refreshAccounts()
    dataChanged()
    onClose()
  }, fail)
  return (
    <Dialog open={open} onClose={onClose} maxWidth="xs" fullWidth data-account-dialog>
      <DialogTitle>Add account</DialogTitle>
      <DialogContent sx={{ display: 'grid', gap: 2.25, pt: '8px !important' }}>
        <TextField select label="Bank" value={bank} onChange={(e) => { const b = banks.find((x) => x.id === Number(e.target.value)); setBank(Number(e.target.value)); if (b) setCurrency(b.currency) }}>
          {banks.map((b) => <MenuItem key={b.id} value={b.id}>{b.name}</MenuItem>)}
        </TextField>
        <TextField autoFocus label="Account name" value={name} onChange={(e) => setName(e.target.value)} slotProps={{ htmlInput: { 'data-field': 'name' } }} />
        <TextField select label="Account type" value={kind} onChange={(e) => setKind(e.target.value as AccountKind)}>
          {accountKinds(system).map((k) => <MenuItem key={k.value} value={k.value}>{k.label}</MenuItem>)}
        </TextField>
        <TextField select label="Currency" value={currency} onChange={(e) => setCurrency(e.target.value)}>
          {[...new Set([currency, ...currencies])].map((c) => <MenuItem key={c} value={c}>{c}</MenuItem>)}
        </TextField>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={bank === '' || name.trim() === ''} onClick={() => void add()}>Add</Button>
      </DialogActions>
    </Dialog>
  )
}

/** The banks the person can choose from: name, country, banking system and the currency of new accounts. */
function BanksTable({ confirm }: { confirm: (c: Confirm) => void }) {
  const { banks, refreshBanks, dataChanged, notify, fail } = useApp()
  const keyOf = useCallback((b: Bank) => String(b.id), [])
  const order = useRowOrder('banks', banks, keyOf)
  const save = (b: Bank, message: string) => api.banks.save(b).then(() => { notify(message); void refreshBanks(); dataChanged() }, fail)
  const text = (b: Bank, field: 'name' | 'country', label: string) => (
    <InputBase
      defaultValue={b[field]}
      inputProps={{ 'aria-label': label }}
      onBlur={(e) => { const v = e.target.value.trim(); if (v !== b[field] && (v || field === 'country')) void save({ ...b, [field]: v }, 'Bank saved') }}
      sx={{ ...inlineInput, width: '100%' }}
    />
  )
  return (
    <SectionCard>
      <Table size="small" data-banks>
        <TableHead>
          <TableRow>
            <TableCell padding="none" />
            <TableCell>Bank Name</TableCell>
            <TableCell>Country</TableCell>
            <TableCell>Banking System</TableCell>
            <TableCell>Currency</TableCell>
            <TableCell padding="checkbox" />
          </TableRow>
        </TableHead>
        <TableBody>
          {order.rows.map((b) => (
            <TableRow key={b.id} hover {...order.rowProps(b)}>
              {order.handle(b)}
              <TableCell>{text(b, 'name', 'Bank name')}</TableCell>
              <TableCell>{text(b, 'country', 'Country')}</TableCell>
              <TableCell>
                <TextField select size="small" value={b.system} onChange={(e) => void save({ ...b, system: e.target.value as BankingSystem }, 'Banking system changed')} sx={{ width: 190 }}>
                  {systems.map((x) => <MenuItem key={x.value} value={x.value}>{x.label}</MenuItem>)}
                </TextField>
              </TableCell>
              <TableCell><CurrencySelect value={b.currency} onChange={(c) => void save({ ...b, currency: c }, 'Currency changed')} /></TableCell>
              <TableCell padding="checkbox">
                <Tooltip title="Delete">
                  <IconButton size="small" aria-label="Delete" onClick={() => confirm({
                    title: `Delete ${b.name}?`,
                    run: async () => { await api.banks.remove(b.id); notify('Bank deleted'); void refreshBanks(); dataChanged() },
                  })}>
                    <DeleteOutline fontSize="small" />
                  </IconButton>
                </Tooltip>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </SectionCard>
  )
}

export function SettingsPage() {
  const { refreshAccounts, refreshBanks, fail, view, takeView } = useApp()
  const [tab, setTab] = useState<'general' | 'banks' | 'definitions'>(view === 'rates' ? 'definitions' : 'general')
  const [definition, setDefinition] = useState<'bank' | 'rates' | 'brands' | 'categories' | 'tags'>(view === 'rates' ? 'rates' : 'bank')
  const [addingSource, setAddingSource] = useState(false)
  const rates = useRateSources(tab === 'definitions' && definition === 'rates')
  const editRates = useCallback(() => { setTab('definitions'); setDefinition('rates') }, [])
  // Opened from elsewhere on its sources of exchange rates (the conversion panel of the periods).
  useEffect(() => { if (view === 'rates') { editRates(); takeView() } }, [view, editRates, takeView])
  const [addingTag, setAddingTag] = useState(false)
  const [confirm, setConfirm] = useState<Confirm>(null)
  const [addingBank, setAddingBank] = useState(false)

  useEffect(() => { void refreshAccounts(); void refreshBanks() }, [refreshAccounts, refreshBanks])

  return (
    <Box sx={{ maxWidth: 1080, mx: 'auto', pt: 3, pb: 6, display: 'grid', gap: 3 }}>
      <PageHeader title="Settings" />
      <Box sx={{ mt: -1.5 }}>
        <Tabs value={tab} onChange={setTab} tabs={[{ value: 'general', label: 'General' }, { value: 'banks', label: 'Bank Configuration' }, { value: 'definitions', label: 'Definitions' }]} />
      </Box>
      {tab === 'general' && <General confirm={setConfirm} onEditRates={editRates} />}
      {tab === 'banks' && <BankConfiguration confirm={setConfirm} />}
      {tab === 'definitions' && (
        <>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, flexWrap: 'wrap' }}>
            <Tabs size="small" value={definition} onChange={setDefinition} tabs={[{ value: 'bank', label: 'Bank' }, { value: 'rates', label: 'Exchange rates' }, { value: 'brands', label: 'Brands' }, { value: 'categories', label: 'Categories' }, { value: 'tags', label: 'Tags' }]} />
            <Box sx={{ flex: 1 }} />
            {definition === 'bank' && <Button variant="contained" startIcon={<Add />} onClick={() => setAddingBank(true)}>Add bank</Button>}
            {definition === 'rates' && (
              <>
                <Button variant="outlined" data-rates-refresh disabled={rates.refreshing} onClick={rates.refresh}
                  startIcon={rates.refreshing ? <CircularProgress size={16} color="inherit" /> : <Refresh />}>Refresh all</Button>
                <Button variant="contained" startIcon={<Add />} disabled={addingSource || rates.free.length === 0} onClick={() => setAddingSource(true)}>Add bank</Button>
              </>
            )}
            {definition === 'tags' && <Button variant="contained" startIcon={<Add />} onClick={() => setAddingTag(true)}>Add tag</Button>}
          </Box>
          <AddBankDialog open={addingBank} onClose={() => setAddingBank(false)} />
          {definition === 'bank' && <BanksTable confirm={setConfirm} />}
          {definition === 'rates' && <RatesSourcesTable rates={rates} adding={addingSource} onAdded={() => setAddingSource(false)} confirm={setConfirm} />}
          {definition === 'brands' && <BrandsPanel />}
          {definition === 'categories' && <CategoriesPanel />}
          {definition === 'tags' && <TagsPanel adding={addingTag} onAdded={() => setAddingTag(false)} />}
        </>
      )}
      <ConfirmDialog
        open={confirm !== null}
        title={confirm?.title ?? ''}
        body={confirm?.body}
        confirm={confirm?.confirm ?? 'Delete'}
        danger
        onClose={() => setConfirm(null)}
        onConfirm={() => { const c = confirm; if (c) void c.run().catch(fail) }}
      />
    </Box>
  )
}
