const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']

/** "2025-10-03" -> parts, without going through Date (no time zone surprises). */
export function parts(iso: string): { y: number; m: number; d: number } {
  const [y, m, d] = iso.split('-').map(Number)
  return { y, m, d }
}

export function iso(y: number, m: number, d: number): string {
  return `${y.toString().padStart(4, '0')}-${m.toString().padStart(2, '0')}-${d.toString().padStart(2, '0')}`
}

export function daysInMonth(y: number, m: number): number {
  return new Date(Date.UTC(y, m, 0)).getUTCDate()
}

export function monthName(m: number): string {
  return MONTHS[m - 1]
}

/** The five formats of the Date column menu. */
export function formatDate(isoDate: string, format: string): string {
  const { y, m, d } = parts(isoDate)
  const dd = d.toString().padStart(2, '0')
  const mm = m.toString().padStart(2, '0')
  switch (format) {
    case 'YYYY-MM-DD': return `${y}-${mm}-${dd}`
    case 'DD-MM-YYYY': return `${dd}-${mm}-${y}`
    case 'DD/MM/YYYY': return `${dd}/${mm}/${y}`
    case 'MM/DD/YYYY': return `${mm}/${dd}/${y}`
    default: return `${dd} ${monthName(m)} ${y}`
  }
}

/** Example shown next to each format in the menu. */
export const formatExample = (format: string) => formatDate('2025-10-03', format)

const moneyFormats = new Map<string, Intl.NumberFormat>()

function numberFormat(currency: string, compact: boolean, whole = false): Intl.NumberFormat {
  const key = currency + (compact ? ':c' : '') + (whole ? ':w' : '')
  let f = moneyFormats.get(key)
  if (!f) {
    try {
      f = new Intl.NumberFormat('en-CA', {
        style: 'currency',
        currency,
        currencyDisplay: 'narrowSymbol',
        ...(compact ? { notation: 'compact', maximumFractionDigits: 1 } : whole ? { minimumFractionDigits: 0, maximumFractionDigits: 0 } : { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
      })
    } catch {
      f = new Intl.NumberFormat('en-CA', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
    }
    moneyFormats.set(key, f)
  }
  return f
}

/** −$45.20, +$2,140.55 (with sign) or $45.20. Uses a true minus sign. */
export function money(value: number, currency: string, opts: { sign?: boolean; compact?: boolean; whole?: boolean } = {}): string {
  const abs = numberFormat(currency, opts.compact ?? false, opts.whole ?? false).format(Math.abs(value))
  if (value < 0 && abs.replace(/[^1-9]/g, '') !== '') return '−' + abs
  if (opts.sign && value > 0) return '+' + abs
  return abs
}

/** A share with two decimals (0.29%, 88.50%): a small share never reads as 0%. */
export function percent(share: number): string {
  return `${(share * 100).toFixed(2)}%`
}

export function today(): string {
  const d = new Date()
  return iso(d.getFullYear(), d.getMonth() + 1, d.getDate())
}

export function fileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

export function shortDate(isoDate: string): string {
  const { y, m, d } = parts(isoDate.slice(0, 10))
  return `${monthName(m).slice(0, 3)} ${d}, ${y}`
}

export function plural(n: number, word: string, many = word + 's'): string {
  return `${n.toLocaleString('en-CA')} ${n === 1 ? word : many}`
}
