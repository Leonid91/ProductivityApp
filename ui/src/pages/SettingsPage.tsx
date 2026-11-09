import { useCallback, useEffect, useState } from 'react'
import {
  Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, InputBase, MenuItem, Switch, Table, TableBody, TableCell, TableHead, TableRow, TextField, ToggleButton, ToggleButtonGroup,
  Tooltip, Typography,
} from '@mui/material'
import Add from '@mui/icons-material/AddRounded'
import DeleteOutline from '@mui/icons-material/DeleteOutlineRounded'
import FolderOpen from '@mui/icons-material/FolderOpenRounded'
import AccountBalance from '@mui/icons-material/AccountBalanceRounded'
import { accountKinds, api, currencies, type Account, type AccountKind, type Bank, type BankingSystem } from '../api'
import { useApp } from '../AppContext'
import { formatExample } from '../format'
import { FileBrowser } from '../components/FileBrowser'
import { ConfirmDialog, EmptyState, PageHeader, SectionCard } from '../components/common'
import { useRowOrder } from '../components/order'
import { BrandsPanel } from './BrandsPage'
import { CategoriesPanel } from './CategoriesPage'
import { TagsPanel } from './TagsPage'

const systems: { value: BankingSystem; label: string }[] = [
  // Credit cards are accounts of their own, paid from the current account; or cards are debited once a month
  // straight from the current account.
  { value: 'northAmerica', label: 'Credit card system' },
  { value: 'france', label: 'Deferred debit system' },
]

type Confirm = { title: string; body?: string; confirm?: string; run: () => Promise<void> } | null

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    // No line under the last row, nor above a group (the group has its own border).
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, py: 1.5, borderBottom: 1, borderColor: 'divider', '&:last-of-type, &:has(+ [data-group])': { borderBottom: 0 } }}>
      <Typography sx={{ flex: 1, fontWeight: 520 }}>{label}</Typography>
      {children}
    </Box>
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

function CurrencySelect({ value, onChange, label }: { value: string; onChange: (c: string) => void; label?: string }) {
  return (
    <TextField select size="small" label={label} value={value} onChange={(e) => onChange(e.target.value)} sx={{ width: 110 }}>
      {[...new Set([value, ...currencies])].map((c) => <MenuItem key={c} value={c}>{c}</MenuItem>)}
    </TextField>
  )
}

function General({ confirm }: { confirm: (c: Confirm) => void }) {
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
          <Toggle label="Hide credit card payments" setting="calendarHideCardPayments" />
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
  const { refreshAccounts, refreshBanks, fail } = useApp()
  const [tab, setTab] = useState<'general' | 'banks' | 'definitions'>('general')
  const [definition, setDefinition] = useState<'bank' | 'brands' | 'categories' | 'tags'>('bank')
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
      {tab === 'general' && <General confirm={setConfirm} />}
      {tab === 'banks' && <BankConfiguration confirm={setConfirm} />}
      {tab === 'definitions' && (
        <>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, flexWrap: 'wrap' }}>
            <Tabs size="small" value={definition} onChange={setDefinition} tabs={[{ value: 'bank', label: 'Bank' }, { value: 'brands', label: 'Brands' }, { value: 'categories', label: 'Categories' }, { value: 'tags', label: 'Tags' }]} />
            <Box sx={{ flex: 1 }} />
            {definition === 'bank' && <Button variant="contained" startIcon={<Add />} onClick={() => setAddingBank(true)}>Add bank</Button>}
            {definition === 'tags' && <Button variant="contained" startIcon={<Add />} onClick={() => setAddingTag(true)}>Add tag</Button>}
          </Box>
          <AddBankDialog open={addingBank} onClose={() => setAddingBank(false)} />
          {definition === 'bank' && <BanksTable confirm={setConfirm} />}
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
