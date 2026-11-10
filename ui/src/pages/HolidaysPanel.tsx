import { useEffect, useState } from 'react'
import {
  Autocomplete, Box, Button, Checkbox, CircularProgress, FormControlLabel, IconButton, InputBase, MenuItem, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography, alpha, useTheme,
} from '@mui/material'
import DeleteOutline from '@mui/icons-material/DeleteOutlineRounded'
import { api, type HolidayCalendar, type HolidayPreset } from '../api'
import { useApp } from '../AppContext'
import { SectionCard } from '../components/common'
import { SOURCE_LINE, SourceError, inlineInput, quietDelete, sourceLine, sourceRow, visuallyHidden } from '../components/sourceTable'

/** What to paste as the address of a calendar, in one sentence. */
const holidaysHelp = 'Paste the link of the holidays file (ICS or JSON), not of its web page; write {year} where the link names a year.'

/** How far a province's or a state's place is set in under its country's. */
const CHILD_INDENT = 28

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

/** The days of the week, Monday first, with their ISO numbers (Monday 1 to Sunday 7). */
const week = [['Mon', 1], ['Tue', 2], ['Wed', 3], ['Thu', 4], ['Fri', 5], ['Sat', 6], ['Sun', 7]] as const

/** The weekend days, not business days (Saturday and Sunday by default); the periods and the calendar follow at once. */
function WeekendDays() {
  const { settings, setSetting, dataChanged } = useApp()
  const chosen = new Set((settings.weekendDays ?? '6,7').split(',').filter(Boolean).map(Number))
  const toggle = async (n: number, on: boolean) => {
    const next = new Set(chosen)
    if (on) next.add(n)
    else next.delete(n)
    await setSetting('weekendDays', [...next].sort((a, b) => a - b).join(','))
    dataChanged()
  }
  return (
    <SectionCard>
      <Box data-weekend-days sx={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', columnGap: 2, rowGap: 1 }}>
        <Typography sx={{ flex: 1, fontWeight: 520 }}>Weekend days</Typography>
        <Box sx={{ display: 'flex', flexWrap: 'wrap', columnGap: 0.5 }}>
          {week.map(([label, n]) => (
            <FormControlLabel key={n} label={label} sx={{ mx: 0, '& .MuiFormControlLabel-label': { fontSize: 14, fontWeight: 520, minWidth: 30 } }}
              control={<Checkbox size="small" checked={chosen.has(n)} onChange={(e) => void toggle(n, e.target.checked)} slotProps={{ input: { 'data-weekday': n } as React.InputHTMLAttributes<HTMLInputElement> }} />} />
          ))}
        </Box>
      </Box>
    </SectionCard>
  )
}

/**
 * The calendars of public holidays, one line each; a province's or a state's under its country's federal calendar,
 * set in and joined to it (its holidays add to the country's). Its check box shows its holidays on the calendar and
 * takes them out of the business days (several can be checked, the lines tinted; checking a province checks its
 * country, unchecking a country unchecks what is under it). Its place and the address of its file can be changed (a
 * new address is checked at once by downloading it, in the background; what is wrong shows under it), and the number
 * of holidays of this year its last download holds is shown (nothing without it). The weekend days are chosen under it.
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
  const [newParent, setNewParent] = useState('')
  const countries = (calendars ?? []).filter((c) => c.parent === null)
  const pick = (p: HolidayPreset | undefined) => {
    setNewLocation(p?.location ?? '')
    setNewUrl(p?.url ?? '')
    setNewParent(p?.parent && countries.some((c) => c.id === p.parent) ? p.parent : '')
  }
  useEffect(() => {
    if (!adding) return
    pick(free[0])
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
  const save = (c: { id?: string; location: string; url: string; parent?: string | null }, download: boolean) =>
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
      // Checked without holidays yet (a country checked with it too): downloaded at once.
      for (const x of list) if (x.active && x.perYear === null && !x.updatedAt && !checking.has(x.id)) check(x.id)
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
              <TableCell sx={{ width: '26%' }}>Location</TableCell>
              <TableCell>URL</TableCell>
              <TableCell align="right" sx={{ width: 220 }}>Number of Holidays per Year</TableCell>
              <TableCell padding="checkbox" sx={{ width: 52 }} />
            </TableRow>
          </TableHead>
          <TableBody>
            {calendars.map((c, i) => {
              const busy = checking.has(c.id) || refreshing
              const error = refused[c.id] ?? (busy ? null : c.error)
              const child = c.parent !== null
              const under = calendars.filter((x) => x.parent === c.id)
              // A country's line has no line under it when a province follows: they read as one group.
              const groupNext = calendars[i + 1]?.parent === c.id || (child && calendars[i + 1]?.parent === c.parent)
              return (
                <TableRow key={c.id} data-holiday-calendar={c.id} data-parent={c.parent ?? undefined} data-active={c.active || undefined}
                  sx={{ ...sourceRow, bgcolor: c.active ? tint : undefined, ...(groupNext ? { '& td': { borderBottomColor: 'transparent' } } : {}) }}>
                  <TableCell padding="checkbox">
                    <Box sx={{ ...sourceLine, justifyContent: 'center' }}>
                      <Checkbox size="small" checked={c.active} onChange={(e) => void toggle(c, e.target.checked)}
                        slotProps={{ input: { 'aria-label': `Show the holidays of ${c.location}` } }} />
                    </Box>
                  </TableCell>
                  <TableCell>
                    {/* Positioned inside the cell, not the cell itself: a positioned cell lets the table's lines show through the tint. */}
                    <Box sx={{ position: 'relative' }}>
                    {child && (
                      // Joins the province to its country: down from the line above, then across to its place.
                      <Box aria-hidden data-tree-line sx={{
                        position: 'absolute', left: 0, top: -18, width: CHILD_INDENT - 6, height: 18 + SOURCE_LINE / 2,
                        borderLeft: '1.5px solid', borderBottom: '1.5px solid', borderColor: 'text.disabled', borderBottomLeftRadius: 6,
                      }} />
                    )}
                    {child && calendars[i + 1]?.parent === c.parent && (
                      // Another province follows under the same country: the line goes on down to it.
                      <Box aria-hidden data-tree-continue sx={{ position: 'absolute', left: 0, top: SOURCE_LINE / 2 - 6, bottom: -11, borderLeft: '1.5px solid', borderColor: 'text.disabled' }} />
                    )}
                    <InputBase
                      key={c.location}
                      defaultValue={c.location}
                      fullWidth
                      inputProps={{ 'aria-label': `Location of the calendar ${c.location}`, 'data-field': 'location' }}
                      onBlur={(e) => { const v = e.target.value.trim(); if (v !== c.location) void save({ id: c.id, location: v, url: c.url }, false).then((ok) => { if (!ok) e.target.value = c.location }) }}
                      onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
                      sx={{ ...inlineInput, height: SOURCE_LINE, width: child ? `calc(100% - ${CHILD_INDENT}px)` : '100%', ml: child ? `${CHILD_INDENT - 8}px` : -1, fontWeight: child ? 500 : 620 }}
                    />
                    </Box>
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
                        body: under.length > 0
                          ? `${under.map((u) => u.location).join(', ')}, under it, ${under.length > 1 ? 'are' : 'is'} deleted too, with the holidays downloaded for them.`
                          : 'The holidays downloaded for it are deleted too.',
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
                    onInputChange={(_, v) => { setNewLocation(v); const p = free.find((x) => x.location === v); if (p) pick(p) }}
                    renderInput={(params) => <TextField {...params} autoFocus placeholder="Place" slotProps={{ ...params.slotProps, htmlInput: { ...params.slotProps.htmlInput, 'aria-label': 'Location', 'data-field': 'location' } }} />}
                  />
                  {/* A province or a state goes under its country's calendar. */}
                  <TextField select size="small" fullWidth label="Part of" value={newParent} onChange={(e) => setNewParent(e.target.value)} sx={{ mt: 1.5 }}
                    slotProps={{ htmlInput: { 'data-field': 'parent' }, inputLabel: { shrink: true }, select: { displayEmpty: true } }}>
                    <MenuItem value="">No country</MenuItem>
                    {countries.map((c) => <MenuItem key={c.id} value={c.id}>{c.location}</MenuItem>)}
                  </TextField>
                </TableCell>
                <TableCell>
                  <TextField size="small" fullWidth value={newUrl} onChange={(e) => setNewUrl(e.target.value)} placeholder="https://"
                    slotProps={{ htmlInput: { 'aria-label': 'URL', 'data-field': 'url', spellCheck: false, style: { fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13 } } }} />
                  {refused[''] && <Box sx={{ mt: 0.5 }}><SourceError message={refused['']} data-holidays-error /></Box>}
                </TableCell>
                <TableCell colSpan={2} sx={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                  <Button onClick={onAdded} sx={{ mr: 1 }}>Cancel</Button>
                  <Button variant="contained" disabled={newLocation.trim() === '' || newUrl.trim() === ''}
                    onClick={() => void save({ location: newLocation, url: newUrl, parent: newParent || null }, true).then((ok) => { if (ok) { notify('Calendar added'); onAdded() } })}>Add</Button>
                </TableCell>
              </TableRow>
            )}
          </TableBody>
        </Table>
      </SectionCard>
      <WeekendDays />
    </>
  )
}
