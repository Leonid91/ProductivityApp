import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react'
import { PlainStyles } from './plainStyles'
import { Box, ButtonBase, CssBaseline, IconButton, Menu, MenuItem, ThemeProvider, Tooltip, Typography } from '@mui/material'
import { alpha } from '@mui/material/styles'
import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider'
import { AdapterDayjs } from '@mui/x-date-pickers/AdapterDayjs'
import Article from '@mui/icons-material/ArticleRounded'
import ArticleOutlined from '@mui/icons-material/ArticleOutlined'
import DonutLarge from '@mui/icons-material/DonutLargeRounded'
import DonutLargeOutlined from '@mui/icons-material/DonutLargeOutlined'
import CalendarMonth from '@mui/icons-material/CalendarMonthRounded'
import CalendarMonthOutlined from '@mui/icons-material/CalendarMonthOutlined'
import UploadFile from '@mui/icons-material/UploadFileRounded'
import UploadFileOutlined from '@mui/icons-material/UploadFileOutlined'
import LightMode from '@mui/icons-material/LightModeOutlined'
import DarkMode from '@mui/icons-material/DarkModeOutlined'
import Visibility from '@mui/icons-material/VisibilityOutlined'
import VisibilityOff from '@mui/icons-material/VisibilityOffOutlined'
import Settings from '@mui/icons-material/SettingsRounded'
import SettingsOutlined from '@mui/icons-material/SettingsOutlined'
import { api, currencies, type InitInfo } from './api'
import { AppProvider, useApp, type Page } from './AppContext'
import { makeTheme } from './theme'
import { AmountsHiddenProvider, setAmountsHidden, useAmountsHidden } from './mask'
import { ResizeEdges, TitleBar, useMaximized } from './components/WindowChrome'
import { PeriodsPage } from './pages/PeriodsPage'
// The pages other than Statements and Analytics are loaded when first opened: the app starts with less to read.
const CalendarPage = lazy(() => import('./pages/CalendarPage').then((m) => ({ default: m.CalendarPage })))
const ImportPage = lazy(() => import('./pages/ImportPage').then((m) => ({ default: m.ImportPage })))
const SettingsPage = lazy(() => import('./pages/SettingsPage').then((m) => ({ default: m.SettingsPage })))
import { FindBar } from './components/FindBar'
const LogsPage = lazy(() => import('./pages/LogsPage').then((m) => ({ default: m.LogsPage })))

const nav: { page: Page; label: string; icon: React.ReactNode; active: React.ReactNode }[] = [
  { page: 'statements', label: 'Statements', icon: <ArticleOutlined />, active: <Article /> },
  { page: 'analytics', label: 'Analytics', icon: <DonutLargeOutlined />, active: <DonutLarge /> },
  { page: 'calendar', label: 'Calendar', icon: <CalendarMonthOutlined />, active: <CalendarMonth /> },
  { page: 'import', label: 'Import', icon: <UploadFileOutlined />, active: <UploadFile /> },
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

/** The round buttons of the menu (amounts hidden, display currency, light or dark mode): one size. */
const railButton = { width: 40, height: 40, color: 'text.secondary' } as const

/**
 * The global display currency (every bank together is converted to it and shown in it): one button of the size of the
 * others of the menu, showing the currency in the page's font (a button of Safari's engine has its own); clicked, the
 * currencies drop down from it.
 */
function CurrencyButton() {
  const { settings, setSetting } = useApp()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  return (
    <>
      <Tooltip title="Display currency" placement="right">
        <IconButton
          data-global-currency
          aria-label="Display currency"
          aria-haspopup="menu"
          aria-expanded={anchor !== null}
          onClick={(e) => setAnchor(e.currentTarget)}
          sx={{ ...railButton, fontFamily: 'inherit', fontSize: 12, fontWeight: 650, letterSpacing: 0.2 }}
        >
          {settings.currency}
        </IconButton>
      </Tooltip>
      <Menu
        anchorEl={anchor}
        open={anchor !== null}
        onClose={() => setAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'bottom', horizontal: 'left' }}
        slotProps={{ list: { dense: true, 'aria-label': 'Display currency' } as object }}
      >
        {[...new Set([settings.currency, ...currencies])].map((c) => (
          <MenuItem key={c} selected={c === settings.currency} onClick={() => { setAnchor(null); if (c !== settings.currency) void setSetting('currency', c) }}>{c}</MenuItem>
        ))}
      </Menu>
    </>
  )
}

/**
 * The eye of the menu: hides every amount of the pages (totals, operations, charts, salary, calendar), and shows them
 * again. Kept for the next start.
 */
function HideAmountsButton() {
  const { setSetting } = useApp()
  const hidden = useAmountsHidden()
  const label = hidden ? 'Show amounts' : 'Hide amounts'
  return (
    <Tooltip title={label} placement="right">
      <IconButton
        data-hide-amounts
        aria-label={label}
        aria-pressed={hidden}
        onClick={() => { setAmountsHidden(!hidden); void setSetting('hideAmounts', hidden ? 'false' : 'true') }}
        sx={railButton}
      >
        {hidden ? <VisibilityOff /> : <Visibility />}
      </IconButton>
    </Tooltip>
  )
}

const periodPage = (p: Page) => p === 'statements' || p === 'analytics'

function Shell() {
  const { page, go, info, settings, setSetting, refreshAccounts, refreshBanks } = useApp()
  const dark = settings.theme === 'dark'
  const maximized = useMaximized(info.maximized)
  const scroller = useRef<HTMLDivElement>(null)

  // Another page starts at its top. Statements and Analytics show the same periods: the period read stays on the
  // screen from one to the other (see PeriodsPage).
  const lastPage = useRef(page)
  useEffect(() => {
    if (!(periodPage(lastPage.current) && periodPage(page))) scroller.current?.scrollTo({ top: 0 })
    lastPage.current = page
  }, [page])

  // Files dropped outside the import area must never make the window open them.
  useEffect(() => {
    const stop = (e: DragEvent) => e.preventDefault()
    window.addEventListener('dragover', stop)
    window.addEventListener('drop', stop)
    return () => { window.removeEventListener('dragover', stop); window.removeEventListener('drop', stop) }
  }, [])

  useEffect(() => { void api.ready() }, [])
  // First launch of this build: what was drawn a few seconds after the start, for its first-launch log.
  useEffect(() => {
    if (!info.firstLaunch) return
    const t = window.setTimeout(() => void api.drawn({
      menu: document.querySelector('nav') !== null,
      elements: document.querySelectorAll('#root *').length,
      characters: document.body.innerText.length,
      window: `${window.innerWidth}x${window.innerHeight}`,
      browser: navigator.userAgent,
    }), 3000)
    return () => window.clearTimeout(t)
  }, [info.firstLaunch])
  useEffect(() => { void refreshAccounts(); void refreshBanks() }, [refreshAccounts, refreshBanks])

  return (
    <Box sx={{ height: '100vh', display: 'flex', flexDirection: 'column', bgcolor: 'background.default' }}>
      {/* macOS and Linux: the system's own title bar and edges. */}
      {!info.nativeFrame && <ResizeEdges maximized={maximized} />}
      {!info.nativeFrame && <TitleBar maximized={maximized} />}
      <Box sx={{ flex: 1, display: 'flex', minHeight: 0 }}>
        <Box component="nav" sx={{ width: 88, flex: '0 0 88px', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 1.5, pt: 1.5, pb: 2 }}>
          {nav.map((n) => (
            <RailItem key={n.page} label={n.label} icon={page === n.page ? n.active : n.icon} selected={page === n.page} onClick={() => go(n.page)} />
          ))}
          <Box sx={{ flex: 1 }} />
          <HideAmountsButton />
          <CurrencyButton />
          <Tooltip title={dark ? 'Light mode' : 'Dark mode'} placement="right">
            <IconButton aria-label={dark ? 'Light mode' : 'Dark mode'} onClick={() => void setSetting('theme', dark ? 'light' : 'dark')} sx={railButton}>
              {dark ? <LightMode /> : <DarkMode />}
            </IconButton>
          </Tooltip>
          {/* The logs are opened from Settings: Settings stays marked while they are shown. */}
          <RailItem label="Settings" icon={page === 'settings' || page === 'logs' ? <Settings /> : <SettingsOutlined />} selected={page === 'settings' || page === 'logs'} onClick={() => go('settings')} />
        </Box>
        <Box
          ref={scroller}
          component="main"
          sx={{ flex: 1, minWidth: 0, overflow: 'auto', px: { xs: 2, md: 4 }, borderTopLeftRadius: 20, bgcolor: 'background.default' }}
        >
          {/* One page for both: the kind of period chosen, the dates and the place read are kept from one to the other. */}
          {periodPage(page) && <PeriodsPage view={page === 'analytics' ? 'analytics' : 'statements'} />}
          <Suspense fallback={null}>
            {page === 'logs' && <LogsPage />}
            {page === 'calendar' && <CalendarPage />}
            {page === 'import' && <ImportPage />}
            {page === 'settings' && <SettingsPage />}
          </Suspense>
        </Box>
      </Box>
      <FindBar />
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
      <PlainStyles />
      <LocalizationProvider dateAdapter={AdapterDayjs} adapterLocale="en">
        {info ? (
          <AppProvider info={info} onTheme={setMode}>
            <AmountsHiddenProvider>
              <Shell />
            </AmountsHiddenProvider>
          </AppProvider>
        ) : (
          <Box sx={{ height: '100vh', display: 'grid', placeItems: 'center', color: 'text.secondary' }}>{error}</Box>
        )}
      </LocalizationProvider>
    </ThemeProvider>
  )
}
