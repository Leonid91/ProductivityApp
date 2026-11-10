import { useEffect, useState } from 'react'
import {
  Autocomplete, Box, Button, Checkbox, CircularProgress, IconButton, InputBase, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography, alpha, useTheme,
} from '@mui/material'
import DeleteOutline from '@mui/icons-material/DeleteOutlineRounded'
import { api, type HolidayCalendar, type HolidayPreset } from '../api'
import { useApp } from '../AppContext'
import { SectionCard } from '../components/common'
import { SOURCE_LINE, SourceError, inlineInput, quietDelete, sourceLine, sourceRow, visuallyHidden } from '../components/sourceTable'

/** What to paste as the address of a calendar, in one sentence. */
const holidaysHelp = 'Paste the link of the holidays file (ICS or JSON), not of its web page; write {year} where the link names a year.'

/** The calendars of the table, the ones to start with, and the downloads in progress (shared by the table and its buttons). */
export function useHolidayCalendars(active: boolean) {
  const { fail, dataChanged } = useApp()
  const [calendars, setCalendars] = useState<HolidayCalendar[] | null>(null)
  const [presets, setPresets] = useState<HolidayPreset[]>([])
  const [refreshing, setRefreshing] = useState(false)
  useEffect(() => {
    if (!active) return
    api.holidays.calendars().then(setCalendars, fail)
    api.holidays.presets().then(setPresets, fail)
  }, [active, fail])
  const refresh = () => {
    setRefreshing(true)
    api.holidays.refresh().then((r) => { setCalendars(r.calendars); if (r.updated) dataChanged() }, fail).finally(() => setRefreshing(false))
  }
  /** The calendars to start with that are not in the table (any more), offered for a new line. */
  const free = presets.filter((p) => !calendars?.some((c) => c.location.toLowerCase() === p.location.toLowerCase()))
  return { calendars, setCalendars, presets, free, refreshing, refresh }
}

type Confirm = { title: string; body?: string; run: () => Promise<void> }

/**
 * The calendars of public holidays, one line each: its check box shows its holidays on the calendar and takes them
 * out of the business days (several can be checked; the line is tinted), its place and the address of its file can
 * be changed (a new address is checked at once by downloading it, in the background; what is wrong shows under it),
 * and the number of holidays of this year its last download holds (nothing is shown without it).
 */
export function HolidaysTable({ holidays, adding, onAdded, confirm }: {
  holidays: ReturnType<typeof useHolidayCalendars>
  adding: boolean
  onAdded: () => void
  confirm: (c: Confirm) => void
}) {
  const { notify, dataChanged, fail } = useApp()
  const theme = useTheme()
  const { calendars, setCalendars, free, refreshing } = holidays
  const [checking, setChecking] = useState<Set<string>>(new Set())
  /** Changes refused before being saved, by line ("" for the new one). */
  const [refused, setRefused] = useState<Record<string, string>>({})
  const [newLocation, setNewLocation] = useState('')
  const [newUrl, setNewUrl] = useState('')
  useEffect(() => {
    if (!adding) return
    setNewLocation(free[0]?.location ?? '')
    setNewUrl(free[0]?.url ?? '')
    setRefused(({ '': _, ...rest }) => rest)
  }, [adding]) // eslint-disable-line react-hooks/exhaustive-deps

  /** Downloads a calendar (which checks its address); the periods and the calendar follow when it is checked. */
  const check = (id: string) => {
    setChecking((c) => new Set(c).add(id))
    api.holidays.update(id).then(() => api.holidays.calendars()).then((list) => {
      setCalendars(list)
      if (list.find((c) => c.id === id)?.active) dataChanged()
    }, () => undefined).finally(() => setChecking((c) => { const n = new Set(c); n.delete(id); return n }))
  }
  const save = (c: { id?: string; location: string; url: string }, download: boolean) =>
    api.holidays.save(c).then((r) => {
      setCalendars(r.calendars)
      setRefused(({ [c.id ?? '']: _, ...rest }) => rest)
      if (download) check(r.id)
      return true
    }, (e: unknown) => {
      setRefused((x) => ({ ...x, [c.id ?? '']: e instanceof Error ? e.message : String(e) }))
      return false
    })
  const toggle = (c: HolidayCalendar, active: boolean) =>
    api.holidays.setActive(c.id, active).then((list) => {
      setCalendars(list)
      dataChanged()
      // Checked without holidays yet: downloaded at once.
      if (active && c.perYear === null && !c.updatedAt) check(c.id)
    }, fail)

  if (!calendars) return null
  const tint = alpha(theme.palette.primary.main, theme.palette.mode === 'dark' ? 0.12 : 0.06)
  return (
    <>
      <Typography variant="body2" color="text.secondary" data-holidays-help className="selectable" sx={{ mt: -1 }}>{holidaysHelp}</Typography>
      <SectionCard>
        <Table size="small" data-holiday-calendars sx={{ tableLayout: 'fixed', '& td': { borderColor: 'divider' } }}>
          <TableHead>
            <TableRow>
              <TableCell padding="checkbox" sx={{ width: 52 }}><Box component="span" sx={visuallyHidden}>Shown</Box></TableCell>
              <TableCell sx={{ width: '24%' }}>Location</TableCell>
              <TableCell>URL</TableCell>
              <TableCell align="right" sx={{ width: 220 }}>Number of Holidays per Year</TableCell>
              <TableCell padding="checkbox" sx={{ width: 52 }} />
            </TableRow>
          </TableHead>
          <TableBody>
            {calendars.map((c) => {
              const busy = checking.has(c.id) || refreshing
              const error = refused[c.id] ?? (busy ? null : c.error)
              return (
                <TableRow key={c.id} data-holiday-calendar={c.id} data-active={c.active || undefined} sx={{ ...sourceRow, bgcolor: c.active ? tint : undefined }}>
                  <TableCell padding="checkbox">
                    <Box sx={{ ...sourceLine, justifyContent: 'center' }}>
                      <Checkbox size="small" checked={c.active} onChange={(e) => void toggle(c, e.target.checked)}
                        slotProps={{ input: { 'aria-label': `Show the holidays of ${c.location}` } }} />
                    </Box>
                  </TableCell>
                  <TableCell>
                    <InputBase
                      key={c.location}
                      defaultValue={c.location}
                      fullWidth
                      inputProps={{ 'aria-label': `Location of the calendar ${c.location}`, 'data-field': 'location' }}
                      onBlur={(e) => { const v = e.target.value.trim(); if (v !== c.location) void save({ id: c.id, location: v, url: c.url }, false).then((ok) => { if (!ok) e.target.value = c.location }) }}
                      onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
                      sx={{ ...inlineInput, height: SOURCE_LINE, width: '100%', fontWeight: c.active ? 650 : 520 }}
                    />
                  </TableCell>
                  <TableCell>
                    <InputBase
                      key={c.url}
                      defaultValue={c.url}
                      fullWidth
                      inputProps={{ 'aria-label': `URL of the calendar ${c.location}`, 'data-field': 'url', spellCheck: false }}
                      onBlur={(e) => { const v = e.target.value.trim(); if (v !== c.url) void save({ id: c.id, location: c.location, url: v }, true) }}
                      onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
                      sx={{ ...inlineInput, height: SOURCE_LINE, fontSize: 13, fontWeight: 450, width: '100%', fontFamily: 'ui-monospace, Consolas, monospace', '& input': { textOverflow: 'ellipsis' } }}
                    />
                    {busy && <Typography variant="body2" color="text.secondary" sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 0.5 }}><CircularProgress size={12} />Downloading</Typography>}
                    {error && <Box sx={{ mt: 0.5 }}><SourceError message={error} data-holidays-error /></Box>}
                  </TableCell>
                  <TableCell align="right" className="tnum" data-per-year title={c.perYear !== null ? `In ${c.year}` : undefined}>
                    <Box sx={{ ...sourceLine, justifyContent: 'flex-end' }}>{c.perYear ?? ''}</Box>
                  </TableCell>
                  <TableCell padding="checkbox">
                    <Box sx={{ ...sourceLine, justifyContent: 'center' }}>
                      <IconButton size="small" disableRipple aria-label={`Delete the calendar of ${c.location}`} sx={quietDelete} onClick={() => confirm({
                        title: `Delete the calendar of ${c.location}?`,
                        body: 'The holidays downloaded for it are deleted too.',
                        run: async () => { setCalendars(await api.holidays.remove(c.id)); if (c.active) dataChanged(); notify('Calendar deleted') },
                      })}>
                        <DeleteOutline fontSize="small" />
                      </IconButton>
                    </Box>
                  </TableCell>
                </TableRow>
              )
            })}
            {adding && (
              <TableRow data-new-holiday-calendar sx={{ verticalAlign: 'top', '& td': { py: 1.25, borderBottom: 0 } }}>
                <TableCell padding="checkbox" />
                <TableCell>
                  {/* Any place; the calendars to start with that are not in the table are offered, with their address. */}
                  <Autocomplete
                    freeSolo
                    size="small"
                    options={free.map((p) => p.location)}
                    inputValue={newLocation}
                    onInputChange={(_, v) => { setNewLocation(v); const p = free.find((x) => x.location === v); if (p) setNewUrl(p.url) }}
                    renderInput={(params) => <TextField {...params} autoFocus placeholder="Country - Region" slotProps={{ ...params.slotProps, htmlInput: { ...params.slotProps.htmlInput, 'aria-label': 'Location', 'data-field': 'location' } }} />}
                  />
                </TableCell>
                <TableCell>
                  <TextField size="small" fullWidth value={newUrl} onChange={(e) => setNewUrl(e.target.value)} placeholder="https://"
                    slotProps={{ htmlInput: { 'aria-label': 'URL', 'data-field': 'url', spellCheck: false, style: { fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13 } } }} />
                  {refused[''] && <Box sx={{ mt: 0.5 }}><SourceError message={refused['']} data-holidays-error /></Box>}
                </TableCell>
                <TableCell colSpan={2} sx={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                  <Button onClick={onAdded} sx={{ mr: 1 }}>Cancel</Button>
                  <Button variant="contained" disabled={newLocation.trim() === '' || newUrl.trim() === ''}
                    onClick={() => void save({ location: newLocation, url: newUrl }, true).then((ok) => { if (ok) { notify('Calendar added'); onAdded() } })}>Add</Button>
                </TableCell>
              </TableRow>
            )}
          </TableBody>
        </Table>
      </SectionCard>
    </>
  )
}
