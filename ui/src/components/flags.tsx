// The flags of the countries whose banks are listed (Settings > Information), drawn here: Windows shows no flag emoji.
// Every flag takes the same box (3 by 2): a wider flag is cut evenly at its two sides.

const box = { display: 'block', borderRadius: 3, boxShadow: '0 0 0 1px rgba(0,0,0,.12)' } as const

function Canada({ height }: { height: number }) {
  return (
    <svg viewBox="0 0 9600 4800" height={height} width={height * 1.5} preserveAspectRatio="xMidYMid slice" style={box} aria-hidden>
      <path fill="#d52b1e" d="m0 0h2400l99 99h4602l99-99h2400v4800h-2400l-99-99h-4602l-99 99H0z" />
      <path fill="#fff" d="m2400 0h4800v4800h-4800zm2490 4430-45-863a95 95 0 0 1 111-98l859 151-116-320a65 65 0 0 1 20-73l941-762-212-99a65 65 0 0 1-34-79l186-572-542 115a65 65 0 0 1-73-38l-105-247-423 454a65 65 0 0 1-111-57l204-1052-327 189a65 65 0 0 1-91-27l-332-652-332 652a65 65 0 0 1-91 27l-327-189 204 1052a65 65 0 0 1-111 57l-423-454-105 247a65 65 0 0 1-73 38l-542-115 186 572a65 65 0 0 1-34 79l-212 99 941 762a65 65 0 0 1 20 73l-116 320 859-151a95 95 0 0 1 111 98l-45 863z" />
    </svg>
  )
}

function France({ height }: { height: number }) {
  return (
    <svg viewBox="0 0 3 2" height={height} width={height * 1.5} style={box} aria-hidden>
      <rect width="1" height="2" fill="#002654" />
      <rect x="1" width="1" height="2" fill="#fff" />
      <rect x="2" width="1" height="2" fill="#ce1126" />
    </svg>
  )
}

function UnitedKingdom({ height }: { height: number }) {
  return (
    <svg viewBox="0 0 60 30" height={height} width={height * 1.5} preserveAspectRatio="xMidYMid slice" style={box} aria-hidden>
      <clipPath id="flag-gb-t"><path d="M30,15 h30 v15 z v15 h-30 z h-30 v-15 z v-15 h30 z" /></clipPath>
      <rect width="60" height="30" fill="#012169" />
      <path d="M0,0 L60,30 M60,0 L0,30" stroke="#fff" strokeWidth="6" />
      <path d="M0,0 L60,30 M60,0 L0,30" clipPath="url(#flag-gb-t)" stroke="#C8102E" strokeWidth="4" />
      <path d="M30,0 v30 M0,15 h60" stroke="#fff" strokeWidth="10" />
      <path d="M30,0 v30 M0,15 h60" stroke="#C8102E" strokeWidth="6" />
    </svg>
  )
}

const flags: Record<string, (p: { height: number }) => React.ReactElement> = { CA: Canada, FR: France, GB: UnitedKingdom }

/** The flag of a country by its ISO 3166 code (nothing for one not drawn here). */
export function Flag({ code, height = 16 }: { code: string; height?: number }) {
  const F = flags[code]
  return F ? <F height={height} /> : null
}
