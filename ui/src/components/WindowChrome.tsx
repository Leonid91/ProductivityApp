import { useEffect, useRef, useState } from 'react'
import { Box, Typography, useTheme } from '@mui/material'
import { api } from '../api'
import { on } from '../bridge'
import { useApp } from '../AppContext'

/** Win11-style caption buttons drawn in the interface (the native frame is removed). */
function CaptionButton({ kind, onClick, maximized }: { kind: 'min' | 'max' | 'close'; onClick: () => void; maximized?: boolean }) {
  const theme = useTheme()
  const dark = theme.palette.mode === 'dark'
  const stroke = theme.palette.text.primary
  return (
    <Box
      component="button"
      className="no-drag"
      onClick={onClick}
      aria-label={kind === 'min' ? 'Minimize' : kind === 'max' ? (maximized ? 'Restore' : 'Maximize') : 'Close'}
      sx={{
        width: 46,
        height: '100%',
        border: 0,
        p: 0,
        background: 'transparent',
        color: stroke,
        display: 'grid',
        placeItems: 'center',
        cursor: 'default',
        transition: 'background-color 90ms',
        '&:hover': kind === 'close'
          ? { backgroundColor: '#c42b1c', color: '#ffffff' }
          : { backgroundColor: dark ? 'rgba(255,255,255,.07)' : 'rgba(0,0,0,.045)' },
        '&:active': kind === 'close' ? { backgroundColor: '#b3271a' } : { backgroundColor: dark ? 'rgba(255,255,255,.04)' : 'rgba(0,0,0,.03)' },
        '&:focus-visible': { outline: `2px solid ${theme.palette.primary.main}`, outlineOffset: -2 },
      }}
    >
      <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="1">
        {kind === 'min' && <path d="M0 5.5h10" />}
        {kind === 'max' && !maximized && <rect x="0.5" y="0.5" width="9" height="9" rx="1.5" />}
        {kind === 'max' && maximized && (
          <>
            <rect x="0.5" y="2.5" width="7" height="7" rx="1.3" />
            <path d="M2.5 2.5V1.8c0-.7.6-1.3 1.3-1.3h4.4c.7 0 1.3.6 1.3 1.3v4.4c0 .7-.6 1.3-1.3 1.3h-.7" />
          </>
        )}
        {kind === 'close' && <path d="M0.5 0.5l9 9M9.5 0.5l-9 9" />}
      </svg>
    </Box>
  )
}

export function useMaximized(initial: boolean) {
  const [maximized, setMaximized] = useState(initial)
  useEffect(() => on<{ maximized: boolean }>('window.state', (s) => setMaximized(s.maximized)), [])
  return maximized
}

export function TitleBar({ maximized }: { maximized: boolean }) {
  const { info } = useApp()
  const last = useRef(0)

  // When WebView2 cannot mark the bar as a native caption, drag and double-click are handled here.
  const onPointerDown = (e: React.PointerEvent) => {
    if (info.nonClientRegions || e.button !== 0) return
    const now = performance.now()
    if (now - last.current < 420) {
      last.current = 0
      void api.window.toggleMaximize()
      return
    }
    last.current = now
    void api.window.drag()
  }

  return (
    <Box
      className="drag"
      onPointerDown={onPointerDown}
      sx={{ height: 40, flex: '0 0 40px', display: 'flex', alignItems: 'stretch', pl: 2, gap: 1.25, zIndex: 3 }}
    >
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.1 }}>
        <AppMark size={18} />
        <Typography variant="body2" sx={{ fontWeight: 600, letterSpacing: '-0.005em' }}>Finances</Typography>
      </Box>
      <Box sx={{ flex: 1 }} />
      <Box sx={{ display: 'flex', height: '100%' }}>
        <CaptionButton kind="min" onClick={() => void api.window.minimize()} />
        <CaptionButton kind="max" maximized={maximized} onClick={() => void api.window.toggleMaximize()} />
        <CaptionButton kind="close" onClick={() => void api.window.close()} />
      </Box>
    </Box>
  )
}

export function AppMark({ size = 20 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 64 64" aria-hidden>
      <defs>
        <linearGradient id="appmark" x1="0" y1="0" x2="0.35" y2="1">
          <stop offset="0" stopColor="#3a8be8" />
          <stop offset="1" stopColor="#523fc9" />
        </linearGradient>
      </defs>
      <rect x="2.5" y="2.5" width="59" height="59" rx="15" fill="url(#appmark)" />
      <rect x="14" y="31" width="9.4" height="16" rx="3" fill="#fff" opacity=".85" />
      <rect x="27.3" y="23.5" width="9.4" height="23.5" rx="3" fill="#fff" opacity=".85" />
      <rect x="40.6" y="15.5" width="9.4" height="31.5" rx="3" fill="#fff" />
      <rect x="13.4" y="50.3" width="37.2" height="2.6" rx="1.3" fill="#fff" opacity=".66" />
    </svg>
  )
}

const EDGE = 5
const CORNER = 10

/** Invisible resize handles along the window border (hidden while maximised). */
export function ResizeEdges({ maximized }: { maximized: boolean }) {
  if (maximized) return null
  const start = (edge: string) => (e: React.PointerEvent) => {
    if (e.button !== 0) return
    e.preventDefault()
    void api.window.resize(edge)
  }
  const base = { position: 'fixed', zIndex: 3000 } as const
  return (
    <>
      <Box onPointerDown={start('top')} sx={{ ...base, top: 0, left: CORNER, right: CORNER, height: EDGE, cursor: 'ns-resize' }} />
      <Box onPointerDown={start('bottom')} sx={{ ...base, bottom: 0, left: CORNER, right: CORNER, height: EDGE, cursor: 'ns-resize' }} />
      <Box onPointerDown={start('left')} sx={{ ...base, left: 0, top: CORNER, bottom: CORNER, width: EDGE, cursor: 'ew-resize' }} />
      <Box onPointerDown={start('right')} sx={{ ...base, right: 0, top: CORNER, bottom: CORNER, width: EDGE, cursor: 'ew-resize' }} />
      <Box onPointerDown={start('top-left')} sx={{ ...base, top: 0, left: 0, width: CORNER, height: CORNER, cursor: 'nwse-resize' }} />
      <Box onPointerDown={start('top-right')} sx={{ ...base, top: 0, right: 0, width: CORNER, height: CORNER, cursor: 'nesw-resize' }} />
      <Box onPointerDown={start('bottom-left')} sx={{ ...base, bottom: 0, left: 0, width: CORNER, height: CORNER, cursor: 'nesw-resize' }} />
      <Box onPointerDown={start('bottom-right')} sx={{ ...base, bottom: 0, right: 0, width: CORNER, height: CORNER, cursor: 'nwse-resize' }} />
    </>
  )
}
