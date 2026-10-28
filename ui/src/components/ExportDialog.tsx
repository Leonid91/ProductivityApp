import { useEffect, useState } from 'react'
import { Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, InputAdornment, TextField, Typography } from '@mui/material'
import FolderOpen from '@mui/icons-material/FolderOpenRounded'
import { api, type Granularity } from '../api'
import { useApp } from '../AppContext'
import { FileBrowser } from './FileBrowser'

export function ExportDialog({ open, onClose, from, to, granularity, currency, title }: {
  open: boolean
  onClose: () => void
  from: string
  to: string
  granularity: Granularity
  currency: string
  title: string
}) {
  const { notify, fail } = useApp()
  const [folder, setFolder] = useState('')
  const [name, setName] = useState('')
  const [browsing, setBrowsing] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!open) return
    api.report.defaults(from, to, granularity, currency).then((d) => { setFolder(d.folder); setName(d.fileName) }, fail)
  }, [open, from, to, granularity, currency, fail])

  const exportNow = async () => {
    setBusy(true)
    try {
      const r = await api.report.export({ from, to, granularity, currency, folder, fileName: name })
      onClose()
      notify('Report saved', 'success', [
        { label: 'Open', run: () => void api.shell.open(r.path).catch(fail) },
        { label: 'Show in folder', run: () => void api.shell.reveal(r.path).catch(fail) },
      ])
    } catch (e) {
      fail(e)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
        <DialogTitle>Export to PDF</DialogTitle>
        <DialogContent sx={{ display: 'grid', gap: 2.5, pt: '6px !important' }}>
          <Typography color="text.secondary">{title}</Typography>
          <TextField
            label="Folder"
            value={folder}
            slotProps={{
              input: {
                readOnly: true,
                endAdornment: (
                  <InputAdornment position="end">
                    <Button size="small" startIcon={<FolderOpen />} onClick={() => setBrowsing(true)}>Change</Button>
                  </InputAdornment>
                ),
              },
            }}
          />
          <TextField label="File name" value={name} onChange={(e) => setName(e.target.value)} />
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2.5 }}>
          <Box sx={{ flex: 1 }} />
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="contained" disabled={busy || !folder || !name.trim()} onClick={() => void exportNow()}>Export</Button>
        </DialogActions>
      </Dialog>
      <FileBrowser
        open={browsing}
        mode="folder"
        title="Save reports in"
        initialPath={folder}
        onClose={() => setBrowsing(false)}
        onPick={([p]) => { setFolder(p); setBrowsing(false) }}
      />
    </>
  )
}
