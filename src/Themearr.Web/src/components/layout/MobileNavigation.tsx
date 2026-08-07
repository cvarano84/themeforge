import { useEffect, useRef, type RefObject } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { APP_BRAND, brandAsset } from '@/lib/brand'
import { useAuth } from '@/lib/auth'
import { NavigationLinks } from './navigation'
import type { NavigationMeta } from './useNavigationMeta'

export function MobileHeader({ title, onMenu, menuRef }: {
  title?: string
  onMenu: () => void
  menuRef: RefObject<HTMLButtonElement | null>
}) {
  const navigate = useNavigate()
  const location = useLocation()
  const canGoBack = location.pathname.split('/').filter(Boolean).length > 1 && window.history.length > 1

  return (
    <header className="tf-mobile-header">
      <div className="tf-mobile-header-row">
        <a href="#main-content" className="tf-skip-link">Skip to content</a>
        {canGoBack && (
          <button className="tf-icon-button" type="button" onClick={() => navigate(-1)} aria-label="Go back">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
          </button>
        )}
        <img src={brandAsset('themeforge-logo.svg')} alt={APP_BRAND.name} className="tf-mobile-wordmark" />
        {title && <p className="tf-mobile-title">{title}</p>}
        <button
          ref={menuRef}
          type="button"
          className="tf-icon-button ml-auto"
          onClick={onMenu}
          aria-label="Open navigation menu"
          aria-haspopup="dialog"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h16" /></svg>
        </button>
      </div>
    </header>
  )
}

export function MobileNavigationDrawer({ open, onClose, triggerRef, meta }: {
  open: boolean
  onClose: () => void
  triggerRef: RefObject<HTMLButtonElement | null>
  meta: NavigationMeta
}) {
  const panelRef = useRef<HTMLDivElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)
  const pathname = useLocation().pathname
  const { accountName, logout } = useAuth()

  useEffect(() => {
    if (!open) return
    const trigger = triggerRef.current
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    closeRef.current?.focus()

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        onClose()
        return
      }
      if (event.key !== 'Tab' || !panelRef.current) return
      const focusable = [...panelRef.current.querySelectorAll<HTMLElement>('a[href], button:not([disabled])')]
      if (!focusable.length) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
    }

    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.body.style.overflow = previousOverflow
      document.removeEventListener('keydown', onKeyDown)
      trigger?.focus()
    }
  }, [open, onClose, triggerRef])

  if (!open) return null

  return (
    <div className="tf-mobile-drawer-root" role="presentation">
      <button className="tf-drawer-backdrop" type="button" onClick={onClose} aria-label="Close navigation menu" />
      <div ref={panelRef} className="tf-mobile-drawer" role="dialog" aria-modal="true" aria-label="Main navigation">
        <div className="tf-drawer-header">
          <img src={brandAsset('themeforge-logo.svg')} alt={APP_BRAND.name} className="h-9 w-auto max-w-[180px]" />
          <button ref={closeRef} type="button" className="tf-icon-button" onClick={onClose} aria-label="Close navigation menu">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12" /></svg>
          </button>
        </div>
        <nav className="tf-drawer-nav" aria-label="Primary">
          <NavigationLinks pathname={pathname} meta={meta} onNavigate={onClose} />
        </nav>
        <div className="tf-drawer-footer">
          {accountName && <p className="truncate text-sm text-[#D0D5DD]">Signed in as <span className="font-semibold text-[#F9FAFB]">{accountName}</span></p>}
          <p className="text-xs text-[#98A2B3]">{meta.version?.current ? `v${meta.version.current.replace(/^v/, '')}` : 'Version unavailable'}</p>
          <button type="button" className="tf-nav-link w-full" onClick={logout}>
            <span className="tf-nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9" /></svg></span>
            Sign out
          </button>
        </div>
      </div>
    </div>
  )
}
