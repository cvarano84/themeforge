import { type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Spinner } from '@/components/ui'
import type { NavigationMeta } from './useNavigationMeta'

interface NavItem {
  href: string
  label: string
  icon: ReactNode
}

const NAV_ITEMS: NavItem[] = [
  { href: '/dashboard', label: 'Dashboard', icon: <svg viewBox="0 0 24 24"><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></svg> },
  { href: '/queue', label: 'Queue', icon: <svg viewBox="0 0 24 24"><path d="M3 6h18M3 12h14M3 18h9" /><circle cx="19" cy="18" r="3" /><path d="M18 17.3l2 .7-2 .7v-1.4z" fill="currentColor" stroke="none" /></svg> },
  { href: '/movies', label: 'Movies', icon: <svg viewBox="0 0 24 24"><rect x="2" y="2" width="20" height="20" rx="2.18" /><path d="M7 2v20M17 2v20M2 12h20M2 7h5M2 17h5M17 17h5M17 7h5" /></svg> },
  { href: '/shows', label: 'Shows', icon: <svg viewBox="0 0 24 24"><rect x="2" y="7" width="20" height="13" rx="2" /><path d="m8 3 4 4 4-4" /></svg> },
  { href: '/history', label: 'History', icon: <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="10" /><polyline points="12 6 12 12 16 14" /></svg> },
  { href: '/settings', label: 'Settings', icon: <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3" /><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" /></svg> },
  { href: '/system', label: 'System', icon: <svg viewBox="0 0 24 24"><rect x="2" y="4" width="20" height="14" rx="2" /><path d="M8 20h8M12 18v2M6 9h4M6 12h7" /></svg> },
]

export function NavigationLinks({ pathname, meta, onNavigate }: {
  pathname: string
  meta: NavigationMeta
  onNavigate?: () => void
}) {
  return NAV_ITEMS.map(({ href, label, icon }) => {
    const active = pathname === href || pathname.startsWith(`${href}/`)
    const showSyncBadge = label === 'Movies' && meta.syncing
    const showHealthBadge = label === 'System' && meta.healthIssues > 0
    return (
      <Link
        key={href}
        to={href}
        onClick={onNavigate}
        aria-current={active ? 'page' : undefined}
        className={`tf-nav-link ${active ? 'tf-nav-link-active' : ''}`}
      >
        <span className="tf-nav-icon" aria-hidden="true">{icon}</span>
        <span className="flex-1">{label}</span>
        {showSyncBadge && <Spinner size={14} className="text-[#FEC84B]" />}
        {showHealthBadge && (
          <span className="tf-nav-badge" aria-label={`${meta.healthIssues} system warnings`}>
            {meta.healthIssues}
          </span>
        )}
      </Link>
    )
  })
}
