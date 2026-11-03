import { alpha, createTheme, type Theme } from '@mui/material/styles'

/** Chart colours, validated for colour-vision deficiencies (same values as the PDF reports). */
export const series = {
  light: ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#6250d6', '#e34948'],
  dark: ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9', '#e66767'],
}
export const otherSlice = { light: '#a3a29c', dark: '#6f6e69' }

declare module '@mui/material/styles' {
  interface Palette {
    surface: { base: string; raised: string; sunken: string; hover: string }
    chart: { series: string[]; other: string; grid: string; axis: string }
    money: { positive: string; negative: string; zero: string; expenseSoft: string; incomeSoft: string; positiveStrong: string; negativeStrong: string }
    /** Money going to or coming from outside the imported accounts. */
    outside: string
  }
  interface PaletteOptions {
    surface?: { base: string; raised: string; sunken: string; hover: string }
    chart?: { series: string[]; other: string; grid: string; axis: string }
    money?: { positive: string; negative: string; zero: string; expenseSoft: string; incomeSoft: string; positiveStrong: string; negativeStrong: string }
    outside?: string
  }
}

export function makeTheme(mode: 'light' | 'dark'): Theme {
  const dark = mode === 'dark'
  const text = dark
    ? { primary: '#f0efec', secondary: '#c3c2b7', disabled: '#7d7c76' }
    : { primary: '#1b1b1a', secondary: '#52514e', disabled: '#a3a29c' }
  const primary = dark ? '#6aa5ec' : '#2468c0'
  const divider = dark ? '#2f2f2d' : '#e6e5e1'

  return createTheme({
    palette: {
      mode,
      primary: { main: primary, contrastText: dark ? '#0b1a2e' : '#ffffff' },
      secondary: { main: dark ? '#a59cf0' : '#5444c4' },
      success: { main: dark ? '#4cc26a' : '#1f8a3b' },
      error: { main: dark ? '#ef6b6b' : '#d03b3b' },
      warning: { main: dark ? '#f2b64a' : '#b86e00' },
      background: { default: dark ? '#121211' : '#f6f5f2', paper: dark ? '#1c1c1b' : '#ffffff' },
      text,
      divider,
      surface: dark
        ? { base: '#121211', raised: '#1c1c1b', sunken: '#171716', hover: '#262624' }
        : { base: '#f6f5f2', raised: '#ffffff', sunken: '#f1f0ec', hover: '#f3f2ee' },
      chart: {
        series: dark ? series.dark : series.light,
        other: dark ? otherSlice.dark : otherSlice.light,
        grid: dark ? '#2b2b29' : '#ecebe7',
        axis: dark ? '#8f8e88' : '#7a7974',
      },
      // Summary boxes: expenses faintly red, earnings faintly green, the balance more strongly in the colour of its sign.
      money: dark
        ? { positive: '#4cc26a', negative: '#ef6b6b', zero: text.primary, expenseSoft: 'rgba(239,107,107,.07)', incomeSoft: 'rgba(76,194,106,.07)', positiveStrong: 'rgba(76,194,106,.2)', negativeStrong: 'rgba(239,107,107,.2)' }
        : { positive: '#006300', negative: '#d03b3b', zero: '#000000', expenseSoft: '#fcf3f2', incomeSoft: '#f1f8f1', positiveStrong: '#d5ead5', negativeStrong: '#f6d9d6' },
      outside: dark ? '#e8834f' : '#c4551f',
    },
    shape: { borderRadius: 12 },
    typography: {
      fontFamily: '"Inter", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif',
      fontSize: 13.5,
      h4: { fontWeight: 650, letterSpacing: '-0.02em', fontSize: '1.65rem' },
      h5: { fontWeight: 620, letterSpacing: '-0.015em', fontSize: '1.32rem' },
      h6: { fontWeight: 600, letterSpacing: '-0.01em', fontSize: '1.02rem' },
      subtitle1: { fontWeight: 560 },
      subtitle2: { fontWeight: 600, fontSize: '0.82rem' },
      button: { textTransform: 'none', fontWeight: 580, letterSpacing: 0 },
      overline: { fontWeight: 600, letterSpacing: '0.08em', fontSize: '0.68rem' },
    },
    components: {
      MuiCssBaseline: {
        styleOverrides: {
          body: { backgroundColor: dark ? '#121211' : '#f6f5f2' },
        },
      },
      MuiPaper: {
        defaultProps: { elevation: 0 },
        styleOverrides: {
          root: { backgroundImage: 'none' },
          outlined: { borderColor: divider },
        },
      },
      MuiCard: {
        defaultProps: { variant: 'outlined' },
        styleOverrides: { root: { borderRadius: 20, borderColor: divider } },
      },
      MuiButton: {
        defaultProps: { disableElevation: true },
        styleOverrides: {
          root: { borderRadius: 999, paddingInline: 18, minHeight: 36 },
          sizeSmall: { minHeight: 30, paddingInline: 12 },
          outlined: { borderColor: divider },
        },
      },
      MuiIconButton: { styleOverrides: { root: { borderRadius: 10 } } },
      MuiToggleButton: {
        styleOverrides: {
          root: {
            textTransform: 'none',
            border: 0,
            borderRadius: 999,
            paddingInline: 16,
            paddingBlock: 6,
            fontWeight: 560,
            color: text.secondary,
            '&.Mui-selected': {
              backgroundColor: dark ? '#2b2b29' : '#ffffff',
              color: text.primary,
              boxShadow: dark ? 'none' : '0 1px 2px rgba(0,0,0,.08), 0 0 0 1px rgba(0,0,0,.04)',
            },
            '&.Mui-selected:hover': { backgroundColor: dark ? '#323230' : '#ffffff' },
          },
        },
      },
      MuiToggleButtonGroup: {
        styleOverrides: {
          root: { backgroundColor: dark ? '#1f1f1e' : '#ebeae6', borderRadius: 999, padding: 3, gap: 2 },
          grouped: { border: 0, '&:not(:first-of-type)': { borderRadius: 999, marginLeft: 0 }, '&:first-of-type': { borderRadius: 999 } },
        },
      },
      MuiTooltip: {
        defaultProps: { enterDelay: 500, disableInteractive: true },
        styleOverrides: {
          tooltip: {
            backgroundColor: dark ? '#f0efec' : '#262624',
            color: dark ? '#1b1b1a' : '#f6f5f2',
            fontSize: 12,
            fontWeight: 500,
            borderRadius: 8,
            padding: '6px 10px',
          },
        },
      },
      MuiMenu: {
        styleOverrides: {
          paper: {
            borderRadius: 14,
            border: `1px solid ${divider}`,
            boxShadow: dark ? '0 12px 32px rgba(0,0,0,.5)' : '0 12px 32px rgba(20,20,10,.12), 0 2px 6px rgba(20,20,10,.06)',
          },
          list: { padding: 6 },
        },
      },
      MuiMenuItem: {
        styleOverrides: {
          root: { borderRadius: 8, minHeight: 34, fontSize: 13.5 },
        },
      },
      MuiPopover: {
        styleOverrides: {
          paper: { borderRadius: 14 },
        },
      },
      MuiDialog: {
        styleOverrides: {
          paper: { borderRadius: 24, border: `1px solid ${divider}`, backgroundColor: dark ? '#1c1c1b' : '#ffffff' },
        },
      },
      MuiDialogTitle: { styleOverrides: { root: { fontWeight: 620, fontSize: '1.15rem', paddingTop: 22 } } },
      MuiOutlinedInput: {
        styleOverrides: {
          root: {
            borderRadius: 12,
            backgroundColor: dark ? '#171716' : '#ffffff',
            '& .MuiOutlinedInput-notchedOutline': { borderColor: divider },
          },
        },
      },
      MuiChip: { styleOverrides: { root: { fontWeight: 560, borderRadius: 999 } } },
      MuiTableCell: {
        styleOverrides: {
          root: { borderBottomColor: divider, fontSize: 13.5 },
          // Table headers stay visible while a long table scrolls (below the page's own sticky bar, if any).
          head: { fontWeight: 600, color: text.secondary, fontSize: 12.5, position: 'sticky', top: 'var(--sticky-top, 0px)', zIndex: 2, backgroundColor: dark ? '#1c1c1b' : '#ffffff' },
        },
      },
      MuiTableRow: {
        styleOverrides: {
          root: { '&.MuiTableRow-hover:hover': { backgroundColor: dark ? alpha('#ffffff', 0.035) : alpha('#000000', 0.022) } },
        },
      },
      MuiAlert: { styleOverrides: { root: { borderRadius: 14 } } },
      MuiSnackbarContent: { styleOverrides: { root: { borderRadius: 14 } } },
      MuiLinearProgress: { styleOverrides: { root: { borderRadius: 999, height: 4 } } },
      MuiSwitch: { defaultProps: { disableRipple: true } },
      MuiListItemButton: { styleOverrides: { root: { borderRadius: 10 } } },
      MuiSelect: { defaultProps: { MenuProps: { slotProps: { paper: { sx: { maxHeight: 420 } } } } } },
    },
  })
}
