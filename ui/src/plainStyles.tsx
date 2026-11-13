import { useMemo } from 'react'
import { alpha, useTheme, type Theme } from '@mui/material/styles'

/**
 * The look of what a period draws many times over (its tables, their lines, the pills, the icons, the boxes of
 * totals), written once as a style sheet made from the theme: each element then only carries a class name, instead of
 * having its look worked out again every time it is drawn. The values are the theme's own, so it looks the same.
 */
function css(t: Theme): string {
  const ty = t.typography
  const p = t.palette
  const dark = p.mode === 'dark'
  const font = (v: 'h5' | 'h6' | 'body1' | 'body2' | 'caption') => {
    const s = ty[v] as { fontFamily?: string; fontWeight?: number | string; fontSize?: string | number; lineHeight?: string | number; letterSpacing?: string | number }
    return `font-family:${s.fontFamily};font-weight:${s.fontWeight};font-size:${s.fontSize};line-height:${s.lineHeight};letter-spacing:${s.letterSpacing ?? 'normal'};`
  }
  const rowHover = dark ? alpha('#ffffff', 0.035) : alpha('#000000', 0.022)
  const headBg = dark ? '#1c1c1b' : '#ffffff'
  return `
.p-h5{margin:0;${font('h5')}}
.p-h6{margin:0;${font('h6')}}
.p-body1{margin:0;${font('body1')}}
.p-body2{margin:0;${font('body2')}}
.p-caption{margin:0;${font('caption')}}
.p-muted{color:${p.text.secondary}}
.p-nowrap{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.p-icon{user-select:none;width:1em;height:1em;display:inline-block;flex-shrink:0;fill:currentColor;transition:${t.transitions.create('fill', { duration: t.transitions.duration.shorter })};font-size:${ty.pxToRem(24)}}
.p-mark{user-select:none;width:1em;height:1em;display:inline-block;flex-shrink:0;background:center/100% 100% no-repeat;font-size:${ty.pxToRem(24)}}
.p-icon-small{font-size:${ty.pxToRem(20)}}
@keyframes p-pulse{0%{opacity:1}50%{opacity:.4}100%{opacity:1}}
.p-skeleton{display:block;background-color:${alpha(p.text.primary, dark ? 0.13 : 0.11)};height:1.2em;animation:p-pulse 2s ease-in-out .5s infinite}
.p-skeleton-text{margin-top:0;margin-bottom:0;height:auto;transform-origin:0 55%;transform:scale(1,.6);border-radius:12px/20px}
.p-skeleton-text:empty:before{content:"\\00a0"}
.p-period{border:1px solid ${p.divider};border-radius:24px;background-color:${p.background.paper};padding:${t.spacing(2.5)};min-width:0;contain-intrinsic-height:auto 2400px}
@media (min-width:${t.breakpoints.values.md}px){.p-period{padding:${t.spacing(3.5)}}}
.p-period-head{display:flex;align-items:baseline;gap:${t.spacing(2)}}
.p-stat{flex:1 1 180px;border-radius:18px;padding:${t.spacing(2)} ${t.spacing(2.5)}}
.p-stat-label{font-weight:520}
.p-stat-value{font-size:26px;font-weight:650;letter-spacing:-0.02em;margin-top:${t.spacing(0.25)}}
.p-stat-note{margin-top:${t.spacing(0.25)}}
.p-row{display:flex;flex-wrap:wrap;gap:${t.spacing(1.5)}}
.p-section{margin-top:${t.spacing(3)}}
.p-section-head{display:flex;align-items:baseline;gap:${t.spacing(1.25)};margin-bottom:${t.spacing(1)};flex-wrap:wrap}
.p-section-icon{align-self:center;display:flex;color:${p.text.secondary}}
.p-section-total{font-weight:650;border-radius:10px;padding:${t.spacing(0.4)} ${t.spacing(1.25)}}
[data-section],.p-lines{content-visibility:auto;contain-intrinsic-height:auto 800px}
.p-part{margin-top:${t.spacing(2.5)};border:1px solid ${p.divider};border-radius:20px;padding:${t.spacing(1)} ${t.spacing(2)} ${t.spacing(2)};min-width:0}
@media (min-width:${t.breakpoints.values.md}px){.p-part{padding:${t.spacing(1)} ${t.spacing(2.5)} ${t.spacing(2.5)}}}
.p-part-head{position:sticky;top:var(--sticky-top,0px);z-index:3;display:flex;align-items:center;gap:${t.spacing(1.25)};margin-bottom:${t.spacing(1)}}
.p-part-icon{display:flex;color:${p.text.secondary}}
.p-part-name{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.p-part-currency{font-weight:650;border:1px solid ${p.divider};border-radius:10px;padding:${t.spacing(0.25)} ${t.spacing(1)};font-size:13px;white-space:nowrap}
.p-table{display:table;width:100%;border-collapse:collapse;border-spacing:0;table-layout:fixed}
.p-table th,.p-table td{${font('body2')}display:table-cell;vertical-align:inherit;border-bottom:1px solid ${p.divider};text-align:left;padding:6px 12px;font-size:13.5px;color:${p.text.primary}}
.p-table.p-tight th,.p-table.p-tight td{padding-left:10px;padding-right:10px}
.p-table tr>*:first-of-type{padding-left:4px}
.p-table thead{position:sticky;top:calc(var(--sticky-top, 0px) + var(--part-head, 0px));z-index:2}
.p-table th{line-height:${ty.pxToRem(24)};font-weight:600;color:${p.text.secondary};font-size:12.5px;background-color:${headBg}}
.p-table .p-right{text-align:right;flex-direction:row-reverse}
.p-table .p-none{padding:0}
.p-table tbody tr.row:hover{background-color:${rowHover}}
.p-table tbody tr.row>td{font-size:13.5px;padding-top:${t.spacing(0.9)};padding-bottom:${t.spacing(0.9)}}
.p-table td.p-empty{padding-top:${t.spacing(4)};padding-bottom:${t.spacing(4)};text-align:center;color:${p.text.secondary};border-bottom:0}
.p-head-button{display:inline-flex;align-items:center;gap:${t.spacing(0.25)};border:0px solid;background:none;padding:2px 6px;margin-left:-6px;border-radius:18px;font:inherit;color:inherit;font-weight:600;cursor:pointer}
.p-head-button:hover{background-color:${p.action.hover}}
.p-head-button.p-on{color:${p.primary.main}}
.p-money{white-space:nowrap;font-weight:520}
.p-original{font-size:12px;color:${p.text.secondary};line-height:1.3}
.p-pill-box{display:inline-flex;max-width:100%}
.p-pill{display:inline-flex;align-items:center;gap:${t.spacing(0.75)};border:0px solid;border-radius:11988px;padding:${t.spacing(0.4)} ${t.spacing(1.25)} ${t.spacing(0.4)} ${t.spacing(0.9)};max-width:100%;font:inherit;font-size:13px;font-weight:520;color:${p.text.primary};background-color:var(--pill-bg);cursor:default;transition:background-color 120ms}
.p-pill.p-colored{font-weight:620}
.p-pill.p-quiet{color:${p.text.secondary}}
.p-pill.p-active{cursor:pointer}
.p-pill:hover:not(:disabled){background-color:var(--pill-hover)}
.p-pill:focus-visible{outline:2px solid ${p.primary.main}}
.p-pill>.p-icon,.p-pill>.p-mark{font-size:16px;color:var(--pill-icon,${p.text.secondary})}
.p-brand-box{display:flex;align-items:center;gap:${t.spacing(1)};min-width:0}
.p-brand{font-weight:520;cursor:text;border-radius:18px;padding:${t.spacing(0.25)} ${t.spacing(1)};margin:0 -${t.spacing(1)};overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.p-brand:hover{background-color:${p.action.hover}}
.p-tags{display:flex;flex-wrap:wrap;gap:${t.spacing(0.5)};align-items:center;min-height:26px;width:100%;border:0px solid;background:none;padding:${t.spacing(0.25)};margin:0 -${t.spacing(0.25)};border-radius:18px;cursor:pointer;font:inherit;text-align:left}
.p-tags:hover{background-color:${p.action.hover}}
.p-tags .tag-add{font-size:16px;color:${p.text.disabled};opacity:0;transition:opacity 120ms}
.p-tags:hover .tag-add,.p-tags:focus-visible .tag-add{opacity:1}
.p-tag{display:inline-flex;align-items:center;max-width:100%;padding:${t.spacing(0.15)} ${t.spacing(0.9)};border-radius:999px;font-size:12px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;line-height:1.6;color:var(--tag-color,${p.text.secondary});background-color:var(--tag-bg);border:1px solid transparent}
.p-tag.p-inherited{border:1px dashed var(--tag-border,${p.divider})}
.p-actions{display:inline-flex;align-items:center;justify-content:flex-end;gap:${t.spacing(0.25)};vertical-align:middle}
.p-icon-button{display:inline-flex;align-items:center;justify-content:center;box-sizing:border-box;-webkit-tap-highlight-color:transparent;background-color:transparent;outline:0;border:0;margin:0;padding:5px;cursor:pointer;user-select:none;vertical-align:middle;appearance:none;text-decoration:none;text-align:center;flex:0 0 auto;font-size:${ty.pxToRem(18)};border-radius:8px;color:${p.text.secondary};transition:color 120ms}
.p-icon-button:focus-visible{outline:2px solid ${p.primary.main};outline-offset:1px}
.p-icon-button.p-edit:hover{color:${p.primary.main}}
.p-icon-button.p-delete:hover{color:${p.error.main}}
.p-pager{display:flex;align-items:center;justify-content:space-between;margin-top:${t.spacing(1.5)}}
.p-place{min-width:0}
.p-place-name{overflow-wrap:anywhere;line-height:1.3}
.p-place-type{display:block;line-height:1.2}
`
}

/** The style sheet of the plain elements, made again only when the theme changes (light or dark). */
export function PlainStyles() {
  const theme = useTheme()
  const text = useMemo(() => css(theme), [theme])
  return <style data-plain-styles>{text}</style>
}
