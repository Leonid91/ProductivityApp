import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Alert, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, Snackbar, Typography } from '@mui/material'
import { api, type Account, type Bank, type Category, type InitInfo, type Settings, type Tag, type TagData } from './api'
import { categoryTree, savedOrder, type TreeEntry } from './components/order'

export type Page = 'periods' | 'calendar' | 'import' | 'settings'

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
  setCategories: (list: Category[]) => void
  /** Every category as a tree, in the person's order. */
  tree: TreeEntry[]
  category: (id: string) => Category
  /** Every tag, in the person's order, and the tags of each category itself. */
  tags: Tag[]
  categoryTags: Record<string, number[]>
  tag: (id: number) => Tag | undefined
  /** Takes the tags the engine sends back after a change, and lets every view reload. */
  setTagData: (data: TagData) => void
  accounts: Account[]
  refreshAccounts: () => Promise<void>
  banks: Bank[]
  refreshBanks: () => Promise<void>
  /** Folder of the database (it can be moved). */
  dataFolder: string
  setDataFolder: (folder: string) => void
  /** Increases whenever stored data changes, so every view reloads what it shows. */
  dataVersion: number
  dataChanged: () => void
  notify: (message: string, severity?: Toast['severity'], actions?: Toast['actions']) => void
  fail: (error: unknown) => void
  page: Page
  go: (page: Page) => void
  /**
   * Asks whether a new category is for this operation only (false) or for every operation of the brand, past and
   * future (true). Null when the person cancels.
   */
  askScope: (brand: string, categoryName: string) => Promise<boolean | null>
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
  const [banks, setBanks] = useState<Bank[]>([])
  const [categories, setCategories] = useState<Category[]>(info.categories)
  const [tagData, setTagDataState] = useState<TagData>(info.tags)
  const [dataFolder, setDataFolder] = useState(info.dataFolder)
  const [dataVersion, setDataVersion] = useState(0)
  const [page, setPage] = useState<Page>('periods')
  const [toast, setToast] = useState<Toast | null>(null)
  const counter = useRef(0)
  const [scope, setScope] = useState<{ brand: string; category: string; resolve: (v: boolean | null) => void } | null>(null)

  const askScope = useCallback((brand: string, categoryName: string) => new Promise<boolean | null>((resolve) => {
    setScope({ brand, category: categoryName, resolve })
  }), [])
  const answer = (v: boolean | null) => { scope?.resolve(v); setScope(null) }

  const byId = useMemo(() => new Map(categories.map((c) => [c.id, c])), [categories])
  const tagById = useMemo(() => new Map(tagData.tags.map((t) => [t.id, t])), [tagData])
  const setTagData = useCallback((data: TagData) => { setTagDataState(data); setDataVersion((v) => v + 1) }, [])
  const tree = useMemo(() => categoryTree(categories, savedOrder(settings, 'categories')), [categories, settings])

  const notify = useCallback((message: string, severity: Toast['severity'] = 'success', actions?: Toast['actions']) => {
    setToast({ key: ++counter.current, message, severity, actions })
  }, [])

  const fail = useCallback((error: unknown) => {
    notify(error instanceof Error ? error.message : String(error), 'error')
  }, [notify])

  const refreshAccounts = useCallback(async () => {
    setAccounts(await api.accounts.list())
  }, [])

  const refreshBanks = useCallback(async () => {
    setBanks(await api.banks.list())
  }, [])

  const setSetting = useCallback(async (key: string, value: string) => {
    try {
      const next = await api.setSetting(key, value)
      setSettings(next)
      if (key === 'theme') onTheme(value === 'dark' ? 'dark' : 'light')
    } catch (e) {
      fail(e)
    }
  }, [fail, onTheme])

  const dataChanged = useCallback(() => {
    setDataVersion((v) => v + 1)
    void refreshAccounts()
    void refreshBanks()
  }, [refreshAccounts, refreshBanks])

  // Exchange rates of the chosen bank: downloaded again every day (checked at start, every hour, and when the bank is
  // changed); the periods are drawn again with them. Another bank converts at once with the copy it already has.
  const lastSource = useRef(settings.ratesSource)
  useEffect(() => {
    const changed = lastSource.current !== settings.ratesSource
    lastSource.current = settings.ratesSource
    const check = () => api.rates.ensure().then((r) => { if (r.updated) setDataVersion((v) => v + 1) }, () => undefined)
    if (changed) setDataVersion((v) => v + 1)
    void check()
    const hourly = setInterval(() => void check(), 3600_000)
    return () => clearInterval(hourly)
  }, [settings.ratesSource])

  const value: AppState = {
    info,
    settings,
    setSetting,
    categories,
    setCategories,
    tree,
    category: (id) => byId.get(id) ?? byId.get('other') ?? categories[0],
    tags: tagData.tags,
    categoryTags: tagData.categoryTags,
    tag: (id) => tagById.get(id),
    setTagData,
    accounts,
    refreshAccounts,
    banks,
    refreshBanks,
    dataFolder,
    setDataFolder,
    dataVersion,
    dataChanged,
    notify,
    fail,
    page,
    go: setPage,
    askScope,
  }

  return (
    <Ctx.Provider value={value}>
      {children}
      <Dialog open={scope !== null} onClose={() => answer(null)} maxWidth="xs" fullWidth data-scope-dialog>
        <DialogTitle>Change the category to “{scope?.category}”</DialogTitle>
        <DialogContent>
          <Typography color="text.secondary">
            For this operation only, or for every “{scope?.brand}” operation? Every operation also covers the ones already saved and the
            ones you will import later.
          </Typography>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2.5, flexWrap: 'wrap', gap: 1 }}>
          <Button onClick={() => answer(null)}>Cancel</Button>
          <Box sx={{ flex: 1 }} />
          <Button variant="outlined" onClick={() => answer(false)}>Only this one</Button>
          <Button variant="contained" onClick={() => answer(true)} sx={{ ml: '0 !important' }}>All “{scope?.brand}”</Button>
        </DialogActions>
      </Dialog>
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
