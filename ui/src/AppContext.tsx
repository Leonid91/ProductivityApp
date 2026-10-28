import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react'
import { Alert, Button, Snackbar } from '@mui/material'
import { api, type Account, type Category, type InitInfo, type Settings } from './api'

export type Page = 'periods' | 'calendar' | 'import' | 'brands' | 'settings'

interface Toast {
  key: number
  message: string
  severity: 'success' | 'info' | 'warning' | 'error'
  actions?: { label: string; run: () => void }[]
}

interface AppState {
  info: InitInfo
  settings: Settings
  setSetting: (key: string, value: string) => Promise<void>
  categories: Category[]
  category: (id: string) => Category
  accounts: Account[]
  refreshAccounts: () => Promise<void>
  /** Increases whenever stored data changes, so every view reloads what it shows. */
  dataVersion: number
  dataChanged: () => void
  notify: (message: string, severity?: Toast['severity'], actions?: Toast['actions']) => void
  fail: (error: unknown) => void
  page: Page
  go: (page: Page) => void
}

const Ctx = createContext<AppState | null>(null)

export function useApp(): AppState {
  const v = useContext(Ctx)
  if (!v) throw new Error('useApp outside provider')
  return v
}

export function AppProvider({ info, children, onTheme }: { info: InitInfo; children: ReactNode; onTheme: (t: 'light' | 'dark') => void }) {
  const [settings, setSettings] = useState<Settings>(info.settings)
  const [accounts, setAccounts] = useState<Account[]>([])
  const [dataVersion, setDataVersion] = useState(0)
  const [page, setPage] = useState<Page>('periods')
  const [toast, setToast] = useState<Toast | null>(null)
  const counter = useRef(0)

  const byId = useMemo(() => new Map(info.categories.map((c) => [c.id, c])), [info.categories])

  const notify = useCallback((message: string, severity: Toast['severity'] = 'success', actions?: Toast['actions']) => {
    setToast({ key: ++counter.current, message, severity, actions })
  }, [])

  const fail = useCallback((error: unknown) => {
    notify(error instanceof Error ? error.message : String(error), 'error')
  }, [notify])

  const refreshAccounts = useCallback(async () => {
    setAccounts(await api.accounts.list())
  }, [])

  const setSetting = useCallback(async (key: string, value: string) => {
    try {
      const next = await api.setSetting(key, value)
      setSettings(next)
      if (key === 'theme') onTheme(value === 'dark' ? 'dark' : 'light')
      if (key === 'currency') setDataVersion((v) => v + 1)
    } catch (e) {
      fail(e)
    }
  }, [fail, onTheme])

  const dataChanged = useCallback(() => {
    setDataVersion((v) => v + 1)
    void refreshAccounts()
  }, [refreshAccounts])

  const value: AppState = {
    info,
    settings,
    setSetting,
    categories: info.categories,
    category: (id) => byId.get(id) ?? info.categories[0],
    accounts,
    refreshAccounts,
    dataVersion,
    dataChanged,
    notify,
    fail,
    page,
    go: setPage,
  }

  return (
    <Ctx.Provider value={value}>
      {children}
      <Snackbar
        key={toast?.key}
        open={toast !== null}
        autoHideDuration={toast?.actions ? 9000 : 4500}
        onClose={(_, reason) => { if (reason !== 'clickaway') setToast(null) }}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      >
        {toast ? (
          <Alert
            severity={toast.severity}
            variant="filled"
            onClose={() => setToast(null)}
            sx={{ alignItems: 'center', minWidth: 320, boxShadow: '0 10px 30px rgba(0,0,0,.18)' }}
            action={toast.actions?.map((a) => (
              <Button key={a.label} color="inherit" size="small" onClick={() => { a.run(); setToast(null) }} sx={{ fontWeight: 650 }}>
                {a.label}
              </Button>
            ))}
          >
            {toast.message}
          </Alert>
        ) : undefined}
      </Snackbar>
    </Ctx.Provider>
  )
}
