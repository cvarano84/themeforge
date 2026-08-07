import { Link, useLocation } from 'react-router-dom'
import { useAuth } from '@/lib/auth'
import { APP_BRAND, brandAsset } from '@/lib/brand'
import { NavigationLinks } from './navigation'
import type { NavigationMeta } from './useNavigationMeta'

export function Sidebar({ meta }: { meta: NavigationMeta }) {
  const pathname = useLocation().pathname
  const { accountName, logout } = useAuth()

  return (
    <aside className="tf-desktop-sidebar" aria-label="Application sidebar">
      <div className="flex h-16 items-center border-b border-[#1D2939] px-4">
        <img src={brandAsset('themeforge-logo.svg')} alt={APP_BRAND.name} width={180} height={36} className="h-9 max-w-full object-contain object-left" />
      </div>
      <nav className="flex-1 space-y-0.5 overflow-y-auto px-3 py-4" aria-label="Primary">
        <NavigationLinks pathname={pathname} meta={meta} />
      </nav>
      <div className="space-y-1 border-t border-[#1D2939] px-3 py-3">
        {meta.version?.updateAvailable && (
          <Link to="/settings" className="flex min-h-11 items-center gap-2 rounded-lg px-3 text-xs text-[#FEC84B] hover:bg-[#1D2939]">
            <span className="h-1.5 w-1.5 rounded-full bg-[#F79009]" aria-hidden="true" /> Update available
          </Link>
        )}
        <p className="px-3 text-xs text-[#98A2B3]">{meta.version?.current ? `v${meta.version.current.replace(/^v/, '')}` : '—'}</p>
        {accountName && (
          <div className="flex items-center gap-2.5 px-3 py-2">
            <div className="flex h-7 w-7 flex-shrink-0 items-center justify-center rounded-full bg-[#BB0000]/20 text-xs font-semibold text-[#F4AAAA]" aria-hidden="true">{accountName[0]?.toUpperCase()}</div>
            <span className="min-w-0 flex-1 truncate text-xs text-[#D0D5DD]">{accountName}</span>
          </div>
        )}
        <button onClick={logout} className="tf-nav-link w-full">
          <span className="tf-nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9" /></svg></span>
          Sign out
        </button>
      </div>
    </aside>
  )
}
