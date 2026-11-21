import { useEffect, useRef, useState } from 'react'
import { Box, IconButton, InputBase, Typography } from '@mui/material'
import Search from '@mui/icons-material/SearchRounded'
import KeyboardArrowUp from '@mui/icons-material/KeyboardArrowUpRounded'
import KeyboardArrowDown from '@mui/icons-material/KeyboardArrowDownRounded'
import Close from '@mui/icons-material/CloseRounded'

// Safari before 17.4 (macOS 14.0 to 14.3) has neither checkVisibility nor highlights: the text's box tells whether it
// is shown, and the current match's element is outlined instead of its words being highlighted.
const visible = (e: Element) => (typeof e.checkVisibility === 'function' ? e.checkVisibility() : e.getClientRects().length > 0)
const canHighlight = typeof CSS !== 'undefined' && 'highlights' in CSS && typeof Highlight === 'function'

function mark(ranges: Range[], current: number) {
  for (const e of document.querySelectorAll('[data-find-current]')) e.removeAttribute('data-find-current')
  if (canHighlight) {
    if (ranges.length) {
      CSS.highlights.set('find', new Highlight(...ranges))
      CSS.highlights.set('find-current', new Highlight(ranges[current]))
    } else {
      CSS.highlights.delete('find')
      CSS.highlights.delete('find-current')
    }
  } else if (ranges.length) ranges[current].startContainer.parentElement?.setAttribute('data-find-current', '')
}

/** Every visible piece of text on screen that contains the query (case ignored), in reading order. */
function findRanges(query: string, skip: Element | null): Range[] {
  const q = query.toLowerCase()
  if (!q) return []
  const ranges: Range[] = []
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    const parent = n.parentElement
    if (!parent || skip?.contains(parent) || parent.closest('style, script') || !visible(parent)) continue
    const text = (n.textContent ?? '').toLowerCase()
    for (let at = text.indexOf(q); at >= 0; at = text.indexOf(q, at + q.length)) {
      const r = new Range()
      r.setStart(n, at)
      r.setEnd(n, at + q.length)
      ranges.push(r)
    }
  }
  return ranges
}

/** Ctrl+F: finds text anywhere in the window, highlights every match and goes from one to the next. */
export function FindBar() {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [count, setCount] = useState(0)
  const [index, setIndex] = useState(0)
  const bar = useRef<HTMLDivElement>(null)
  const input = useRef<HTMLInputElement>(null)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f') {
        e.preventDefault()
        setOpen(true)
        setTimeout(() => input.current?.select(), 0)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  // The page changes while the bar is open (periods load as they scroll in): matches are looked for again each time.
  const show = (q: string, i: number) => {
    const ranges = findRanges(q, bar.current)
    const n = ranges.length === 0 ? 0 : ((i % ranges.length) + ranges.length) % ranges.length
    mark(ranges, n)
    if (ranges.length) ranges[n].startContainer.parentElement?.scrollIntoView({ block: 'center' })
    setCount(ranges.length)
    setIndex(n)
  }

  const close = () => {
    setOpen(false)
    mark([], 0)
  }

  if (!open) return null
  return (
    <Box
      ref={bar}
      data-findbar
      sx={{
        position: 'fixed', top: 46, right: 24, zIndex: 1400, display: 'flex', alignItems: 'center', gap: 0.5, pl: 1.5, pr: 0.5, height: 44, width: 360,
        bgcolor: 'background.paper', border: 1, borderColor: 'divider', borderRadius: 999, boxShadow: '0 10px 30px rgba(0,0,0,.18)',
      }}
    >
      <Search sx={{ fontSize: 19, color: 'text.secondary' }} />
      <InputBase
        inputRef={input}
        autoFocus
        placeholder="Find"
        value={query}
        onChange={(e) => { setQuery(e.target.value); show(e.target.value, 0) }}
        onKeyDown={(e) => {
          if (e.key === 'Enter') show(query, index + (e.shiftKey ? -1 : 1))
          if (e.key === 'Escape') close()
        }}
        sx={{ flex: 1, fontSize: 14 }}
        slotProps={{ input: { 'aria-label': 'Find' } }}
      />
      <Typography variant="body2" color="text.secondary" className="tnum" sx={{ px: 0.5, whiteSpace: 'nowrap' }}>
        {query ? `${count ? index + 1 : 0}/${count}` : ''}
      </Typography>
      <IconButton size="small" aria-label="Previous" disabled={count === 0} onClick={() => show(query, index - 1)}><KeyboardArrowUp fontSize="small" /></IconButton>
      <IconButton size="small" aria-label="Next" disabled={count === 0} onClick={() => show(query, index + 1)}><KeyboardArrowDown fontSize="small" /></IconButton>
      <IconButton size="small" aria-label="Close" onClick={close}><Close fontSize="small" /></IconButton>
    </Box>
  )
}
