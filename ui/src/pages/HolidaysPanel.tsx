import { useEffect, useState } from 'react'
import {
  Autocomplete, Box, Button, Checkbox, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, InputBase, MenuItem, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography, alpha, useTheme,
} from '@mui/material'
import { api, type HolidayCalendar, type HolidayPreset } from '../api'
import { useApp } from '../AppContext'
import { SectionCard } from '../components/common'
import { ROW_ACTIONS_WIDTH, RowActions } from '../components/RowActions'
import { SOURCE_LINE, SourceError, inlineInput, sourceLine, sourceRow, visuallyHidden } from '../components/sourceTable'

/** What to paste as the address of a calendar, in one sentence. */
const holidaysHelp = 'The link of the holidays file itself (ICS or JSON), not of its web page.'

/**
 * A calendar's link as the person pasted it: the engine keeps "{year}" where the link names a year (to download every
 * year), shown here with this year.
 */
const shownUrl = (url: string) => url.replace(/\{year\}/gi, String(new Date().getFullYear()))

/** How far a province's or a state's place is set in under its country's. */
const CHILD_INDENT = 28

/** The calendars of the table, the ones to start with, the holidays of this year together, and the downloads in progress. */
export function useHolidayCalendars(active: boolean) {
  const { fail, dataChanged, dataVersion } = useApp()
  const [calendars, setCalendars] = useState<HolidayCalendar[] | null>(null)
  const [presets, setPresets] = useState<HolidayPreset[]>([])
  const [total, setTotal] = useState<{ year: number; total: number } | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  useEffect(() => {
    if (!active) return
    api.holidays.calendars().then(setCalendars, fail)
    api.holidays.presets().then(setPresets, fail)
  }, [active, fail])
  // The total follows every change of the table (a calendar checked, downloaded, deleted).
  useEffect(() => {
    if (active) api.holidays.total().then(setTotal, fail)
  }, [active, calendars, dataVersion, fail])
  const refresh = () => {
    setRefreshing(true)
    api.holidays.refresh().then((r) => { setCalendars(r.calendars); if (r.updated) dataChanged() }, fail).finally(() => setRefreshing(false))
  }
  /** The calendars to start with that are not in the table (any more), offered for a new calendar. */
  const free = presets.filter((p) => !calendars?.some((c) => c.location.toLowerCase() === p.location.toLowerCase()))
  return { calendars, setCalendars, presets, free, total, refreshing, refresh }
}

type Confirm = { title: string; body?: string; run: () => Promise<void> }

/** The days of the week with their ISO numbers (Monday 1 to Sunday 7), from the first day of the calendar's week. */
function week(firstDay: number) {
  const days = [['Sun', 7], ['Mon', 1], ['Tue', 2], ['Wed', 3], ['Thu', 4], ['Fri', 5], ['Sat', 6]] as const
  return [...days.slice(firstDay), ...days.slice(0, firstDay)]
}

/**
 * Under the table: the holidays of this year in the checked calendars together (a day that is a holiday federally and
 * in a province, or in two countries, counted once), then the weekend days (Saturday and Sunday by default), not
 * business days; the periods and the calendar follow at once.
 */
function HolidayDays({ total }: { total: { year: number; total: number } | null }) {
  const { settings, setSetting, dataChanged } = useApp()
  const chosen = new Set((settings.weekendDays ?? '6,7').split(',').filter(Boolean).map(Number))
  const toggle = async (n: number, on: boolean) => {
    const next = new Set(chosen)
    if (on) next.add(n)
    else next.delete(n)
    await setSetting('weekendDays', [...next].sort((a, b) => a - b).join(','))
    dataChanged()
  }
  const row = { display: 'flex', alignItems: 'center', flexWrap: 'wrap', columnGap: 2, rowGap: 1, minHeight: 42 } as const
  return (
    <SectionCard>
      <Box data-holidays-total sx={{ ...row, pb: 1.25, borderBottom: 1, borderColor: 'divider' }}>
        <Typography sx={{ flex: 1, fontWeight: 520 }}>Holidays in {total?.year ?? new Date().getFullYear()}</Typography>
        <Typography className="tnum" data-total title="The checked calendars together, each day counted once" sx={{ fontWeight: 650, pr: 1 }}>
          {total ? total.total : ''}
        </Typography>
      </Box>
      <Box data-weekend-days sx={{ ...row, pt: 1.25 }}>
        <Typography sx={{ flex: 1, fontWeight: 520 }}>Weekend days</Typography>
        <Box sx={{ display: 'flex', flexWrap: 'wrap', columnGap: 0.5 }}>
          {week(Number(settings.calendarFirstDay ?? 0)).map(([label, n]) => (
            <FormControlLabel key={n} label={label} sx={{ mx: 0, '& .MuiFormControlLabel-label': { fontSize: 14, fontWeight: 520, minWidth: 30 } }}
              control={<Checkbox size="small" checked={chosen.has(n)} onChange={(e) => void toggle(n, e.target.checked)} slotProps={{ input: { 'data-weekday': n } as React.InputHTMLAttributes<HTMLInputElement> }} />} />
          ))}
        </Box>
      </Box>
    </SectionCard>
  )
}

/**
 * A calendar added (from the Add calendar button) or changed (from its pencil): its place (the calendars to start
 * with that are not in the table are offered, with their address), the country's calendar it is part of (a province
 * or a state) and the address of its file. What is refused is said in the dialog; a new address is downloaded at
 * once, which checks it (what is wrong shows under it in the table).
 */
function CalendarDialog({ open, calendar, calendars, free, onClose, onSaved }: {
  open: boolean
  /** The calendar changed; null for a new one. */
  calendar: HolidayCalendar | null
  calendars: HolidayCalendar[]
  free: HolidayPreset[]
  onClose: () => void
  onSaved: (list: HolidayCalendar[], id: string, download: boolean) => void
}) {
  const { notify } = useApp()
  const [location, setLocation] = useState('')
  const [url, setUrl] = useState('')
  const [parent, setParent] = useState('')
  const [error, setError] = useState<string | null>(null)
  const countries = calendars.filter((c) => c.parent === null && c.id !== calendar?.id)
  const hasUnder = calendar !== null && calendars.some((c) => c.parent === calendar.id)
  const pick = (p: HolidayPreset | undefined) => {
    setLocation(p?.location ?? '')
    setUrl(shownUrl(p?.url ?? ''))
    setParent(p?.parent && countries.some((c) => c.id === p.parent) ? p.parent : '')
  }
  useEffect(() => {
    if (!open) return
    setError(null)
    if (calendar) { setLocation(calendar.location); setUrl(shownUrl(calendar.url)); setParent(calendar.parent ?? '') }
    else pick(free[0])
  }, [open, calendar]) // eslint-disable-line react-hooks/exhaustive-deps

  const save = () => api.holidays.save({ id: calendar?.id, location, url, parent: parent || null }).then((r) => {
    notify(calendar ? 'Calendar saved' : 'Calendar added')
    onSaved(r.calendars, r.id, !calendar || shownUrl(calendar.url) !== url.trim())
    onClose()
  }, (e: unknown) => setError(e instanceof Error ? e.message : String(e)))

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth data-calendar-dialog>
      <DialogTitle>{calendar ? `Edit ${calendar.location}` : 'Add calendar'}</DialogTitle>
      <DialogContent sx={{ display: 'grid', gap: 2.25, pt: '8px !important' }}>
        <Autocomplete
          freeSolo
          options={calendar ? [] : free.map((p) => p.location)}
          inputValue={location}
          onInputChange={(_, v) => { setLocation(v); const p = free.find((x) => x.location === v); if (p && !calendar) pick(p) }}
          renderInput={(params) => <TextField {...params} autoFocus label="Location" slotProps={{ ...params.slotProps, htmlInput: { ...params.slotProps.htmlInput, 'data-field': 'location' } }} />}
        />
        <TextField select label="Part of" value={parent} onChange={(e) => setParent(e.target.value)} disabled={hasUnder}
          helperText={hasUnder ? 'Calendars are under it: it stays a country.' : undefined}
          slotProps={{ htmlInput: { 'data-field': 'parent' }, inputLabel: { shrink: true }, select: { displayEmpty: true } }}>
          <MenuItem value="">No country</MenuItem>
          {countries.map((c) => <MenuItem key={c.id} value={c.id}>{c.location}</MenuItem>)}
        </TextField>
        <TextField label="URL" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://" helperText={holidaysHelp}
          slotProps={{ htmlInput: { 'data-field': 'url', spellCheck: false, style: { fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13 } } }} />
        {error && <SourceError message={error} data-holidays-error />}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={location.trim() === '' || url.trim() === ''} onClick={() => void save()}>{calendar ? 'Save' : 'Add'}</Button>
      </DialogActions>
    </Dialog>
  )
}

/**
 * The calendars of public holidays, one line each; a province's or a state's under its country's federal calendar,
 * set in and joined to it. Its check box shows its holidays on the calendar and takes them out of the business days
 * (each line checked on its own; the checked lines tinted). Its place and address can be changed in place or with its
 * pencil, which opens the same dialog as the Add calendar button; the number of holidays of this year its last
 * download holds is shown (nothing without it).
 */
export function HolidaysTable({ holidays, adding, onAdded, confirm }: {
  holidays: ReturnType<typeof useHolidayCalendars>
  /** The Add calendar button was pressed: the dialog is open for a new calendar. */
  adding: boolean
  onAdded: () => void
  confirm: (c: Confirm) => void
}) {
  const { notify, dataChanged, fail } = useApp()
  const theme = useTheme()
  const { calendars, setCalendars, free, refreshing, total } = holidays
  const [checking, setChecking] = useState<Set<string>>(new Set())
  const [editing, setEditing] = useState<HolidayCalendar | null>(null)
  /** Changes refused before being saved, by line. */
  const [refused, setRefused] = useState<Record<string, string>>({})

  /** Downloads a calendar (which checks its address); the periods and the calendar follow when it is checked. */
  const check = (id: string) => {
    setChecking((c) => new Set(c).add(id))
    api.holidays.update(id).then(() => api.holidays.calendars()).then((list) => {
      setCalendars(list)
      if (list.find((c) => c.id === id)?.active) dataChanged()
    }, () => undefined).finally(() => setChecking((c) => { const n = new Set(c); n.delete(id); return n }))
  }
  const save = (c: HolidayCalendar, change: { location?: string; url?: string }) =>
    api.holidays.save({ id: c.id, location: change.location ?? c.location, url: change.url ?? c.url, parent: c.parent }).then((r) => {
      setCalendars(r.calendars)
      setRefused(({ [c.id]: _, ...rest }) => rest)
      if (change.url !== undefined) check(r.id)
      return true
    }, (e: unknown) => {
      setRefused((x) => ({ ...x, [c.id]: e instanceof Error ? e.message : String(e) }))
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
              <TableCell sx={{ width: '26%' }}>Location</TableCell>
              <TableCell>URL</TableCell>
              <TableCell align="right" sx={{ width: 220 }}>Number of Holidays per Year</TableCell>
              <TableCell sx={{ width: ROW_ACTIONS_WIDTH, px: 0 }} />
            </TableRow>
          </TableHead>
          <TableBody>
            {calendars.map((c, i) => {
              const busy = checking.has(c.id) || refreshing
              const error = refused[c.id] ?? (busy ? null : c.error)
              const child = c.parent !== null
              const under = calendars.filter((x) => x.parent === c.id)
              // A country's line has no rule under it when a province follows: they read as one group.
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
                        onBlur={(e) => { const v = e.target.value.trim(); if (v !== c.location) void save(c, { location: v }).then((ok) => { if (!ok) e.target.value = c.location }) }}
                        onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
                        sx={{ ...inlineInput, height: SOURCE_LINE, width: child ? `calc(100% - ${CHILD_INDENT}px)` : '100%', ml: child ? `${CHILD_INDENT - 8}px` : -1, fontWeight: child ? 500 : 620 }}
                      />
                    </Box>
                  </TableCell>
                  <TableCell>
                    <InputBase
                      key={c.url}
                      defaultValue={shownUrl(c.url)}
                      fullWidth
                      inputProps={{ 'aria-label': `URL of the calendar ${c.location}`, 'data-field': 'url', spellCheck: false }}
                      onBlur={(e) => { const v = e.target.value.trim(); if (v !== shownUrl(c.url)) void save(c, { url: v }) }}
                      onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
                      sx={{ ...inlineInput, height: SOURCE_LINE, fontSize: 13, fontWeight: 450, width: '100%', fontFamily: 'ui-monospace, Consolas, monospace', '& input': { textOverflow: 'ellipsis' } }}
                    />
                    {busy && <Typography variant="body2" color="text.secondary" sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 0.5 }}><CircularProgress size={12} />Downloading</Typography>}
                    {error && <Box sx={{ mt: 0.5 }}><SourceError message={error} data-holidays-error /></Box>}
                  </TableCell>
                  <TableCell align="right" className="tnum" data-per-year title={c.perYear !== null ? `In ${c.year}` : undefined}>
                    <Box sx={{ ...sourceLine, justifyContent: 'flex-end' }}>{c.perYear ?? ''}</Box>
                  </TableCell>
                  <TableCell sx={{ px: 0 }}>
                    <Box sx={{ ...sourceLine, justifyContent: 'flex-end', pr: 0.5 }}>
                      <RowActions
                        editLabel={`Edit the calendar of ${c.location}`}
                        onEdit={() => setEditing(c)}
                        deleteLabel={`Delete the calendar of ${c.location}`}
                        onDelete={() => confirm({
                          title: `Delete the calendar of ${c.location}?`,
                          body: under.length > 0
                            ? `${under.map((u) => u.location).join(', ')}, under it, ${under.length > 1 ? 'are' : 'is'} deleted too, with the holidays downloaded for them.`
                            : 'The holidays downloaded for it are deleted too.',
                          run: async () => { setCalendars(await api.holidays.remove(c.id)); if (c.active) dataChanged(); notify('Calendar deleted') },
                        })}
                      />
                    </Box>
                  </TableCell>
                </TableRow>
              )
            })}
          </TableBody>
        </Table>
      </SectionCard>
      <HolidayDays total={total} />
      <CalendarDialog
        open={adding || editing !== null}
        calendar={editing}
        calendars={calendars}
        free={free}
        onClose={() => { setEditing(null); onAdded() }}
        onSaved={(list, id, download) => {
          setCalendars(list)
          setRefused(({ [id]: _, ...rest }) => rest)
          if (download) check(id)
          dataChanged()
        }}
      />
    </>
  )
}
