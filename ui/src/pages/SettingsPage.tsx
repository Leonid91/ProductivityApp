import { useEffect, useState } from 'react'
import { Box, Button, IconButton, InputBase, MenuItem, TextField, ToggleButton, ToggleButtonGroup, Tooltip, Typography } from '@mui/material'
import DeleteOutline from '@mui/icons-material/DeleteOutlineRounded'
import FolderOpen from '@mui/icons-material/FolderOpenRounded'
import { api } from '../api'
import { useApp } from '../AppContext'
import { formatExample } from '../format'
import { FileBrowser } from '../components/FileBrowser'
import { ConfirmDialog, PageHeader, SectionCard } from '../components/common'

const currencies = ['CAD', 'USD', 'EUR', 'GBP', 'CHF', 'AUD', 'JPY', 'MXN']

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, py: 1.5, borderBottom: 1, borderColor: 'divider', '&:last-of-type': { borderBottom: 0 } }}>
      <Typography sx={{ flex: 1, fontWeight: 520 }}>{label}</Typography>
      {children}
    </Box>
  )
}

export function SettingsPage() {
  const { settings, setSetting, info, accounts, refreshAccounts, dataChanged, notify, fail } = useApp()
  const [browsing, setBrowsing] = useState(false)
  const [confirm, setConfirm] = useState<{ title: string; body?: string; run: () => Promise<void> } | null>(null)

  useEffect(() => { void refreshAccounts() }, [refreshAccounts])

  return (
    <Box sx={{ maxWidth: 860, mx: 'auto', pt: 3, pb: 6, display: 'grid', gap: 3 }}>
      <PageHeader title="Settings" />
      <SectionCard title="Display">
        <Row label="Currency">
          <TextField select size="small" value={settings.currency} onChange={(e) => void setSetting('currency', e.target.value)} sx={{ width: 140 }}>
            {[...new Set([settings.currency, ...currencies])].map((c) => <MenuItem key={c} value={c}>{c}</MenuItem>)}
          </TextField>
        </Row>
        <Row label="Date format">
          <TextField select size="small" value={settings.dateFormat} onChange={(e) => void setSetting('dateFormat', e.target.value)} sx={{ width: 220 }}>
            {info.dateFormats.map((f) => <MenuItem key={f} value={f}>{formatExample(f)}</MenuItem>)}
          </TextField>
        </Row>
        <Row label="Theme">
          <ToggleButtonGroup exclusive value={settings.theme} onChange={(_, v) => v && void setSetting('theme', v)}>
            <ToggleButton value="light">Light</ToggleButton>
            <ToggleButton value="dark">Dark</ToggleButton>
          </ToggleButtonGroup>
        </Row>
      </SectionCard>

      <SectionCard title="Reports">
        <Row label="Save reports in">
          <Typography variant="body2" color="text.secondary" noWrap sx={{ maxWidth: 360 }}>{settings.reportsFolder || 'Documents'}</Typography>
          <Button size="small" startIcon={<FolderOpen />} onClick={() => setBrowsing(true)}>Change</Button>
        </Row>
      </SectionCard>

      <SectionCard title="Accounts">
        {accounts.length === 0 && <Typography color="text.secondary">No accounts yet</Typography>}
        {accounts.map((a) => (
          <Box key={a.id} sx={{ display: 'flex', alignItems: 'center', gap: 2, py: 1, borderBottom: 1, borderColor: 'divider', '&:last-of-type': { borderBottom: 0 } }}>
            <InputBase
              defaultValue={a.name}
              onBlur={(e) => {
                const v = e.target.value.trim()
                if (v && v !== a.name) api.accounts.rename(a.id, v).then(() => { void refreshAccounts(); notify('Account renamed') }, fail)
              }}
              sx={{ flex: 1, fontWeight: 520, px: 1, mx: -1, borderRadius: 2, '&:hover, &.Mui-focused': { bgcolor: 'action.hover' } }}
            />
            <Typography variant="body2" color="text.secondary">{a.kind === 'creditCard' ? 'Credit card' : 'Bank account'} · {a.currency}</Typography>
            <Tooltip title="Delete">
              <IconButton size="small" onClick={() => setConfirm({
                title: `Delete ${a.name}?`,
                body: 'Its statements and operations are deleted too.',
                run: async () => { await api.accounts.remove(a.id); notify('Account deleted'); dataChanged() },
              })}>
                <DeleteOutline fontSize="small" />
              </IconButton>
            </Tooltip>
          </Box>
        ))}
      </SectionCard>

      <SectionCard title="Data">
        <Row label="Stored on this computer">
          <Typography variant="body2" color="text.secondary" noWrap sx={{ maxWidth: 380 }} className="selectable">{info.dataFolder}</Typography>
          <Button size="small" onClick={() => void api.shell.reveal(info.dataFolder).catch(fail)}>Open</Button>
        </Row>
        <Row label="Erase all data">
          <Button color="error" variant="outlined" onClick={() => setConfirm({
            title: 'Erase all data?',
            body: 'Every account, statement, operation, learned brand and template is deleted.',
            run: async () => { await api.eraseAll(); notify('All data erased'); dataChanged() },
          })}>Erase</Button>
        </Row>
        <Typography variant="caption" color="text.disabled" sx={{ display: 'block', mt: 1 }}>Version {info.version}</Typography>
      </SectionCard>

      <FileBrowser
        open={browsing}
        mode="folder"
        title="Save reports in"
        initialPath={settings.reportsFolder || undefined}
        onClose={() => setBrowsing(false)}
        onPick={([p]) => { setBrowsing(false); void setSetting('reportsFolder', p) }}
      />
      <ConfirmDialog
        open={confirm !== null}
        title={confirm?.title ?? ''}
        body={confirm?.body}
        confirm="Delete"
        danger
        onClose={() => setConfirm(null)}
        onConfirm={() => { const c = confirm; if (c) void c.run().catch(fail) }}
      />
    </Box>
  )
}
