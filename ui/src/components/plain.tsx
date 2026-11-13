import type { CSSProperties, ReactNode } from 'react'
import type { SvgIconProps } from '@mui/material'

/**
 * The drawing of an icon of the icon set (its paths), read once: a plain <svg> with it looks the same as the icon
 * component, without the work of a styled component each time it is drawn (a table draws hundreds of them).
 */
const paths = new WeakMap<object, ReactNode>()
function pathOf(icon: React.ComponentType<SvgIconProps>): ReactNode {
  let d = paths.get(icon)
  if (d === undefined) {
    // An icon of the set is memo(forwardRef(render)), its render giving the icon with its paths as children.
    const render = (icon as unknown as { type?: { render?: (props: object, ref: null) => { props?: { children?: ReactNode } } } }).type?.render
    d = render?.({}, null)?.props?.children ?? null
    paths.set(icon, d)
  }
  return d
}

/** An icon of the set as a plain <svg> (same classes of look as the icon component: size of the text around it, or `size`). */
export function Glyph({ icon, size, className, style }: { icon: React.ComponentType<SvgIconProps>; size?: number | 'small'; className?: string; style?: CSSProperties }) {
  const d = pathOf(icon)
  const Icon = icon
  // Not an icon of the set (no paths found): the component itself.
  if (d === null) return <Icon className={className} style={style} fontSize={size === 'small' ? 'small' : undefined} sx={typeof size === 'number' ? { fontSize: size } : undefined} />
  return (
    <svg
      className={`MuiSvgIcon-root p-icon${size === 'small' ? ' p-icon-small' : ''}${className ? ' ' + className : ''}`}
      focusable="false"
      aria-hidden="true"
      viewBox="0 0 24 24"
      style={typeof size === 'number' ? { fontSize: size, ...style } : style}
    >
      {d}
    </svg>
  )
}

/** The drawing of an icon as an image in a colour (an SVG file in an address), made once per icon and colour. */
const images = new WeakMap<object, Map<string, string>>()
function svgOf(node: ReactNode): string {
  if (Array.isArray(node)) return node.map(svgOf).join('')
  if (node === null || node === undefined || typeof node === 'boolean') return ''
  if (typeof node === 'string' || typeof node === 'number') return String(node)
  const el = node as { type?: unknown; props?: Record<string, unknown> }
  if (typeof el.type !== 'string' || !el.props) return ''
  const attrs = Object.entries(el.props).filter(([k, v]) => k !== 'children' && (typeof v === 'string' || typeof v === 'number'))
    .map(([k, v]) => ` ${k.replace(/[A-Z]/g, (c) => '-' + c.toLowerCase())}="${String(v).replace(/"/g, '&quot;')}"`).join('')
  return `<${el.type}${attrs}>${svgOf(el.props.children as ReactNode)}</${el.type}>`
}
function imageOf(icon: React.ComponentType<SvgIconProps>, color: string): string | null {
  let byColor = images.get(icon)
  if (!byColor) images.set(icon, (byColor = new Map()))
  let url = byColor.get(color)
  if (url === undefined) {
    const d = pathOf(icon)
    url = d === null ? '' : `url("data:image/svg+xml,${encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="${color}">${svgOf(d)}</svg>`)}")`
    byColor.set(color, url)
  }
  return url || null
}

/**
 * An icon of the set in a colour, as a single plain element showing it as an image (for the icons of the lines of a
 * table: a long table draws hundreds of them, and one element is much less to lay out and paint than a drawing).
 */
export function Mark({ icon, color, size, className, style }: { icon: React.ComponentType<SvgIconProps>; color: string; size?: number; className?: string; style?: CSSProperties }) {
  const url = imageOf(icon, color)
  if (!url) return <Glyph icon={icon} size={size} className={className} style={{ color, ...style }} />
  return <span aria-hidden="true" className={`p-mark${className ? ' ' + className : ''}`} style={{ backgroundImage: url, ...(size ? { fontSize: size } : {}), ...style }} />
}
