import { useEffect, useMemo, useRef, useState } from 'react'
import { Box, ButtonBase, CssBaseline, ThemeProvider, Typography } from '@mui/material'
import { alpha } from '@mui/material/styles'
import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider'
import { AdapterDayjs } from '@mui/x-date-pickers/AdapterDayjs'
import DonutLarge from '@mui/icons-material/DonutLargeRounded'
import DonutLargeOutlined from '@mui/icons-material/DonutLargeOutlined'
import CalendarMonth from '@mui/icons-material/CalendarMonthRounded'
import CalendarMonthOutlined from '@mui/icons-material/CalendarMonthOutlined'
import UploadFile from '@mui/icons-material/UploadFileRounded'
import UploadFileOutlined from '@mui/icons-material/UploadFileOutlined'
import Storefront from '@mui/icons-material/StorefrontRounded'
import StorefrontOutlined from '@mui/icons-material/StorefrontOutlined'
import Settings from '@mui/icons-material/SettingsRounded'
import SettingsOutlined from '@mui/icons-material/SettingsOutlined'
import { api, type InitInfo } from './api'
import { AppProvider, useApp, type Page } from './AppContext'
import { makeTheme } from './theme'
import { ResizeEdges, TitleBar, useMaximized } from './components/WindowChrome'
import { PeriodsPage } from './pages/PeriodsPage'
import { CalendarPage } from './pages/CalendarPage'
import { ImportPage } from './pages/ImportPage'
import { BrandsPage } from './pages/BrandsPage'
import { SettingsPage } from './pages/SettingsPage'

const nav: { page: Page; label: string; icon: React.ReactNode; active: React.ReactNode }[] = [
  { page: 'periods', label: 'Periods', icon: <DonutLargeOutlined />, active: <DonutLarge /> },
  { page: 'calendar', label: 'Calendar', icon: <CalendarMonthOutlined />, active: <CalendarMonth /> },
  { page: 'import', label: 'Import', icon: <UploadFileOutlined />, active: <UploadFile /> },
  { page: 'brands', label: 'Brands', icon: <StorefrontOutlined />, active: <Storefront /> },
]

function RailItem({ label, icon, selected, onClick }: { label: string; icon: React.ReactNode; selected: boolean; onClick: () => void }) {
  return (
    <ButtonBase
      onClick={onClick}
      aria-current={selected ? 'page' : undefined}
      sx={{ display: 'flex', flexDirection: 'column', gap: 0.5, width: 72, py: 0.5, borderRadius: 3, '&:hover .pill': { bgcolor: selected ? undefined : 'action.hover' } }}
    >
      <Box
        className="pill"
        sx={(t) => ({
          width: 56, height: 32, borderRadius: 999, display: 'grid', placeItems: 'center', transition: 'background-color 140ms',
          bgcolor: selected ? alpha(t.palette.primary.main, t.palette.mode === 'dark' ? 0.22 : 0.13) : 'transparent',
          color: selected ? 'primary.main' : 'text.secondary',
          '& svg': { fontSize: 22 },
        })}
      >
        {icon}
      </Box>
      <Typography sx={{ fontSize: 11.5, fontWeight: selected ? 650 : 520, color: selected ? 'text.primary' : 'text.secondary' }}>{label}</Typography>
    </ButtonBase>
  )
}

function Shell() {
  const { page, go, info } = useApp()
  const maximized = useMaximized(info.maximized)
  const scroller = useRef<HTMLDivElement>(null)

  useEffect(() => { scroller.current?.scrollTo({ top: 0 }) }, [page])

  // Files dropped outside the import area must never make the window open them.
  useEffect(() => {
    const stop = (e: DragEvent) => e.preventDefault()
    window.addEventListener('dragover', stop)
    window.addEventListener('drop', stop)
    return () => { window.removeEventListener('dragover', stop); window.removeEventListener('drop', stop) }
  }, [])

  useEffect(() => { void api.ready() }, [])

  return (
    <Box sx={{ height: '100vh', display: 'flex', flexDirection: 'column', bgcolor: 'background.default' }}>
      <ResizeEdges maximized={maximized} />
      <TitleBar maximized={maximized} />
      <Box sx={{ flex: 1, display: 'flex', minHeight: 0 }}>
        <Box component="nav" sx={{ width: 88, flex: '0 0 88px', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 1.5, pt: 1.5, pb: 2 }}>
          {nav.map((n) => (
            <RailItem key={n.page} label={n.label} icon={page === n.page ? n.active : n.icon} selected={page === n.page} onClick={() => go(n.page)} />
          ))}
          <Box sx={{ flex: 1 }} />
          <RailItem label="Settings" icon={page === 'settings' ? <Settings /> : <SettingsOutlined />} selected={page === 'settings'} onClick={() => go('settings')} />
        </Box>
        <Box
          ref={scroller}
          component="main"
          sx={{ flex: 1, minWidth: 0, overflow: 'auto', px: { xs: 2, md: 4 }, borderTopLeftRadius: 20, bgcolor: 'background.default' }}
        >
          {page === 'periods' && <PeriodsPage />}
          {page === 'calendar' && <CalendarPage />}
          {page === 'import' && <ImportPage />}
          {page === 'brands' && <BrandsPage />}
          {page === 'settings' && <SettingsPage />}
        </Box>
      </Box>
    </Box>
  )
}

export default function App() {
  const [info, setInfo] = useState<InitInfo | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [mode, setMode] = useState<'light' | 'dark'>('light')

  useEffect(() => {
    api.init().then((i) => { setMode(i.settings.theme === 'dark' ? 'dark' : 'light'); setInfo(i) }, (e) => setError(String(e?.message ?? e)))
  }, [])

  useEffect(() => { document.documentElement.dataset.theme = mode }, [mode])

  const theme = useMemo(() => makeTheme(mode), [mode])

  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <LocalizationProvider dateAdapter={AdapterDayjs} adapterLocale="en">
        {info ? (
          <AppProvider info={info} onTheme={setMode}>
            <Shell />
          </AppProvider>
        ) : (
          <Box sx={{ height: '100vh', display: 'grid', placeItems: 'center', color: 'text.secondary' }}>{error}</Box>
        )}
      </LocalizationProvider>
    </ThemeProvider>
  )
}
