import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Box, Button, Checkbox, Dialog, DialogActions, IconButton, InputBase, LinearProgress, List, ListItemButton, ListItemIcon, ListItemText,
  Tooltip, Typography,
} from '@mui/material'
import ArrowUpward from '@mui/icons-material/ArrowUpwardRounded'
import Desktop from '@mui/icons-material/DesktopWindowsRounded'
import Description from '@mui/icons-material/DescriptionRounded'
import Download from '@mui/icons-material/DownloadRounded'
import Folder from '@mui/icons-material/FolderRounded'
import FolderOpen from '@mui/icons-material/FolderOpenRounded'
import PictureAsPdf from '@mui/icons-material/PictureAsPdfRounded'
import Search from '@mui/icons-material/SearchRounded'
import Storage from '@mui/icons-material/StorageRounded'
import Usb from '@mui/icons-material/UsbRounded'
import { api, type FolderListing, type Place } from '../api'
import { fileSize, shortDate } from '../format'

const placeIcons: Record<string, React.ReactNode> = {
  desktop: <Desktop fontSize="small" />, documents: <Description fontSize="small" />, downloads: <Download fontSize="small" />,
  drive: <Storage fontSize="small" />, usb: <Usb fontSize="small" />,
}

const memoryKey = 'fileBrowser.lastFolder'
function remembered(): string | null {
  try { return localStorage.getItem(memoryKey) } catch { return null }
}
function remember(path: string) {
  try { localStorage.setItem(memoryKey, path) } catch { /* private storage unavailable: nothing to remember */ }
}

/**
 * In-app file browser (the app shows no Windows dialogs). Picks PDF statements, or a folder.
 */
export function FileBrowser({ open, mode, title, initialPath, onClose, onPick }: {
  open: boolean
  mode: 'pdfs' | 'folder'
  title: string
  initialPath?: string
  onClose: () => void
  onPick: (paths: string[]) => void
}) {
  const [places, setPlaces] = useState<Place[]>([])
  const [listing, setListing] = useState<FolderListing | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')

  const load = useCallback(async (path: string) => {
    setBusy(true)
    setError(null)
    try {
      const l = await api.fs.list(path, mode === 'folder')
      setListing(l)
      setSelected(new Set())
      setQuery('')
      remember(l.path)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }, [mode])

  useEffect(() => {
    if (!open) return
    void (async () => {
      const p = await api.fs.places()
      setPlaces(p)
      const start = initialPath || remembered() || p.find((x) => x.icon === 'downloads')?.path || p[0]?.path
      if (start) await load(start).catch(() => load(p[0].path))
    })()
  }, [open, initialPath, load])

  const entries = useMemo(() => {
    const q = query.trim().toLowerCase()
    return (listing?.entries ?? []).filter((e) => !q || e.name.toLowerCase().includes(q))
  }, [listing, query])

  const toggle = (path: string) => {
    const next = new Set(selected)
    if (next.has(path)) next.delete(path)
    else next.add(path)
    setSelected(next)
  }

  const crumbs = useMemo(() => {
    if (!listing) return []
    const parts = listing.path.replace(/\\+$/, '').split('\\')
    return parts.map((name, i) => ({ name: i === 0 ? name + '\\' : name, path: parts.slice(0, i + 1).join('\\') + (i === 0 ? '\\' : '') }))
  }, [listing])

  const pdfs = entries.filter((e) => !e.isFolder)

  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth slotProps={{ paper: { sx: { height: 'min(640px, 86vh)' } } }}>
      <Box sx={{ display: 'flex', height: '100%', minHeight: 0 }}>
        <Box sx={{ width: 210, flex: '0 0 210px', borderRight: 1, borderColor: 'divider', p: 1.5, pt: 2.5, bgcolor: 'surface.sunken', overflow: 'auto' }}>
          <Typography variant="overline" color="text.secondary" sx={{ px: 1.5 }}>Places</Typography>
          <List dense disablePadding sx={{ mt: 0.5 }}>
            {places.map((p) => (
              <ListItemButton key={p.path} selected={listing?.path.toLowerCase().startsWith(p.path.toLowerCase().replace(/\\$/, ''))} onClick={() => void load(p.path)} sx={{ mb: 0.25 }}>
                <ListItemIcon sx={{ minWidth: 32 }}>{placeIcons[p.icon] ?? <Folder fontSize="small" />}</ListItemIcon>
                <ListItemText primary={p.name} slotProps={{ primary: { noWrap: true, sx: { fontSize: 13.5 } } }} />
              </ListItemButton>
            ))}
          </List>
        </Box>
        <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 }}>
          <Box sx={{ px: 3, pt: 2.5, pb: 1.5 }}>
            <Typography variant="h6" sx={{ mb: 1.5 }}>{title}</Typography>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
              <Tooltip title="Up"><span>
                <IconButton size="small" disabled={!listing?.parent} onClick={() => listing?.parent && void load(listing.parent)}><ArrowUpward fontSize="small" /></IconButton>
              </span></Tooltip>
              <Box sx={{ display: 'flex', alignItems: 'center', flexWrap: 'nowrap', overflow: 'hidden', flex: 1, minWidth: 0, gap: 0.25 }}>
                {crumbs.map((c, i) => (
                  <Box key={c.path} sx={{ display: 'flex', alignItems: 'center', minWidth: 0, flexShrink: i === crumbs.length - 1 ? 1 : 2 }}>
                    {i > 0 && <Typography color="text.disabled" sx={{ px: 0.25 }}>›</Typography>}
                    <Box component="button" onClick={() => void load(c.path)} sx={{ border: 0, bgcolor: 'transparent', font: 'inherit', fontSize: 13.5, color: i === crumbs.length - 1 ? 'text.primary' : 'text.secondary', fontWeight: i === crumbs.length - 1 ? 600 : 500, px: 0.75, py: 0.25, borderRadius: 1.5, cursor: 'pointer', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', '&:hover': { bgcolor: 'action.hover' } }}>
                      {c.name}
                    </Box>
                  </Box>
                ))}
              </Box>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, bgcolor: 'action.hover', borderRadius: 999, px: 1.25, width: 190 }}>
                <Search sx={{ fontSize: 18, color: 'text.secondary' }} />
                <InputBase placeholder="Filter" value={query} onChange={(e) => setQuery(e.target.value)} sx={{ fontSize: 13.5, flex: 1 }} />
              </Box>
            </Box>
          </Box>
          {busy ? <LinearProgress sx={{ mx: 3, height: 2 }} /> : <Box sx={{ height: 2 }} />}
          <Box sx={{ flex: 1, overflow: 'auto', px: 1.5 }}>
            {error && <Typography color="error" sx={{ p: 2 }}>{error}</Typography>}
            {!error && listing && entries.length === 0 && (
              <Typography color="text.secondary" sx={{ p: 4, textAlign: 'center' }}>{mode === 'pdfs' ? 'No folders or PDF files here' : 'No folders here'}</Typography>
            )}
            <List dense>
              {entries.map((e) => (
                <ListItemButton
                  key={e.path}
                  selected={selected.has(e.path)}
                  onClick={() => (e.isFolder ? void load(e.path) : toggle(e.path))}
                  onDoubleClick={() => { if (!e.isFolder) onPick([e.path]) }}
                  sx={{ py: 0.5 }}
                >
                  {mode === 'pdfs' && (
                    <Checkbox size="small" disableRipple checked={selected.has(e.path)} sx={{ p: 0.5, mr: 1, visibility: e.isFolder ? 'hidden' : 'visible' }} />
                  )}
                  <ListItemIcon sx={{ minWidth: 34 }}>
                    {e.isFolder ? <Folder sx={{ color: '#d9a531' }} /> : <PictureAsPdf sx={{ color: '#d0483b' }} />}
                  </ListItemIcon>
                  <ListItemText primary={e.name} slotProps={{ primary: { noWrap: true, sx: { fontSize: 13.5 } } }} />
                  {!e.isFolder && (
                    <Typography variant="body2" color="text.secondary" className="tnum" sx={{ ml: 2, whiteSpace: 'nowrap' }}>
                      {shortDate(e.modified)} · {fileSize(e.size)}
                    </Typography>
                  )}
                </ListItemButton>
              ))}
            </List>
          </Box>
          <DialogActions sx={{ px: 3, py: 2, borderTop: 1, borderColor: 'divider' }}>
            {mode === 'pdfs' && pdfs.length > 0 && (
              <Button onClick={() => setSelected(selected.size === pdfs.length ? new Set() : new Set(pdfs.map((p) => p.path)))} sx={{ mr: 'auto' }}>
                {selected.size === pdfs.length ? 'Select none' : 'Select all'}
              </Button>
            )}
            <Button onClick={onClose}>Cancel</Button>
            {mode === 'pdfs' ? (
              <Button variant="contained" disabled={selected.size === 0} onClick={() => onPick([...selected])}>
                {selected.size > 1 ? `Import ${selected.size} files` : 'Import'}
              </Button>
            ) : (
              <Button variant="contained" startIcon={<FolderOpen />} disabled={!listing} onClick={() => listing && onPick([listing.path])}>
                Use this folder
              </Button>
            )}
          </DialogActions>
        </Box>
      </Box>
    </Dialog>
  )
}
