import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Box, Button, Pagination, useTheme } from '@mui/material'
import DeleteSweepOutlined from '@mui/icons-material/DeleteSweepOutlined'
import { api, type LogEntry } from '../api'
import { on } from '../bridge'
import { useApp } from '../AppContext'
import { ConfirmDialog, PageHeader } from '../components/common'

const PageSize = 100

const types: Record<LogEntry['level'], string> = { debug: 'Debug', info: 'Info', warning: 'Warning', error: 'Error' }

/**
 * The details of an entry: the first lines, and all of it on Show all (an entry the page read cut is read in full
 * from the file then).
 */
function Detail({ entry }: { entry: LogEntry }) {
  const { fail } = useApp()
  const box = useRef<HTMLDivElement>(null)
  const [clamped, setClamped] = useState(entry.more)
  const [full, setFull] = useState<string | null>(null)

  useLayoutEffect(() => {
    const el = box.current
    if (el && full === null) setClamped(entry.more || el.scrollHeight > el.clientHeight + 1)
  }, [entry, full])

  const open = async () => {
    try { setFull(entry.more ? (await api.logs.entry(entry.n)).detail : entry.detail) } catch (e) { fail(e) }
  }

  return (
    <>
      <div ref={box} className="selectable" data-log-detail
        style={full === null
          ? { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', display: '-webkit-box', WebkitLineClamp: 6, WebkitBoxOrient: 'vertical', overflow: 'hidden' }
          : { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', maxHeight: '70vh', overflow: 'auto' }}>
        {full ?? entry.detail}
      </div>
      {full === null && clamped && <Button size="small" sx={{ ml: -1, mt: 0.25, minWidth: 0 }} onClick={() => void open()}>Show all</Button>}
      {full !== null && <Button size="small" sx={{ ml: -1, mt: 0.25, minWidth: 0 }} onClick={() => setFull(null)}>Show less</Button>}
    </>
  )
}

/**
 * Every entry of the log, the latest first, a hundred per page; new entries show as they are written. Three columns:
 * the date to the second, the type, and the details (who wrote it and what, everything about it, its call stack).
 */
export function LogsPage() {
  const { fail, notify } = useApp()
  const { palette } = useTheme()
  const color = (level: LogEntry['level']) => level === 'error' ? palette.error.main : level === 'warning' ? palette.warning.main : level === 'debug' ? palette.text.secondary : undefined
  const [page, setPage] = useState(1)
  const [data, setData] = useState<{ total: number; entries: LogEntry[] } | null>(null)
  const [clearing, setClearing] = useState(false)
  const loading = useRef(false)
  const again = useRef(false)
  // The page asked for last: a reading still under way when another page is chosen is read again for it.
  const wanted = useRef(page)
  wanted.current = page

  const load = useCallback(async () => {
    // One reading at a time; entries written meanwhile are read right after.
    if (loading.current) { again.current = true; return }
    loading.current = true
    try {
      do {
        again.current = false
        const asked = wanted.current
        const read = await api.logs.read(asked - 1, PageSize)
        if (asked === wanted.current) setData(read)
        else again.current = true
      } while (again.current)
    } catch (e) {
      fail(e)
    } finally {
      loading.current = false
    }
  }, [fail])

  useEffect(() => { void load() }, [page, load])
  useEffect(() => on('logs.changed', () => void load()), [load])
  // New entries are sent only while the page is open.
  useEffect(() => {
    void api.logs.watch(true)
    return () => { void api.logs.watch(false) }
  }, [])

  const pages = data ? Math.max(1, Math.ceil(data.total / PageSize)) : 1
  useEffect(() => { if (page > pages) setPage(pages) }, [page, pages])

  const pager = pages > 1 && (
    <Pagination count={pages} page={page} onChange={(_, p) => setPage(p)} shape="rounded" size="small" />
  )

  return (
    <Box sx={{ maxWidth: 1280, mx: 'auto', pt: 3, pb: 6 }} data-logs>
      <PageHeader title="Logs">
        {pager}
        <Button variant="outlined" color="error" startIcon={<DeleteSweepOutlined />} onClick={() => setClearing(true)} data-clear-logs>Clear logs</Button>
      </PageHeader>
      <Box sx={{ border: 1, borderColor: 'divider', borderRadius: '24px', bgcolor: 'background.paper', px: 3, py: 1.5 }}>
        <table className="p-table p-tight">
          <colgroup>
            <col style={{ width: 176 }} />
            <col style={{ width: 92 }} />
            <col />
          </colgroup>
          <thead>
            <tr><th>Date</th><th>Type</th><th>Details</th></tr>
          </thead>
          <tbody>
            {data?.entries.map((e) => (
              <tr key={`${e.n}-${e.time}`} className="row" data-log-level={e.level} style={{ verticalAlign: 'top', color: color(e.level) }}>
                <td className="selectable" style={{ fontVariantNumeric: 'tabular-nums', color: 'inherit' }}>{e.time}</td>
                <td style={{ color: 'inherit' }}>{types[e.level] ?? e.level}</td>
                <td style={{ color: 'inherit' }}><Detail entry={e} /></td>
              </tr>
            ))}
            {data && data.entries.length === 0 && <tr><td colSpan={3} className="p-empty">No entries</td></tr>}
          </tbody>
        </table>
      </Box>
      {pager && <Box sx={{ display: 'flex', justifyContent: 'flex-end', mt: 2 }}>{pager}</Box>}
      <ConfirmDialog open={clearing} title="Clear logs?" body="Every entry of the log file is deleted." confirm="Clear" danger
        onClose={() => setClearing(false)}
        onConfirm={() => void api.logs.clear().then(() => notify('Logs cleared'), fail)} />
    </Box>
  )
}
