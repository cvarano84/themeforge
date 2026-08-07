import { useEffect, useRef, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import { Sidebar } from './Sidebar'
import { MobileHeader, MobileNavigationDrawer } from './MobileNavigation'
import { useNavigationMeta } from './useNavigationMeta'
import { useAuth } from '@/lib/auth'
import { Spinner } from '@/components/ui'

interface AppShellProps {
  children: ReactNode
  title?: string
  actions?: ReactNode
}

export function AppShell({ children, title, actions }: AppShellProps) {
  const navigate = useNavigate()
  const { loading, authorized } = useAuth()
  const [navigationOpen, setNavigationOpen] = useState(false)
  const menuRef = useRef<HTMLButtonElement>(null)
  const navigationMeta = useNavigationMeta(authorized)

  // Route guard: kick anyone without a valid bearer token back to /login.
  // The api.ts 401 handler catches expired tokens mid-session; this handles
  // the cold-load case (user navigates directly to /queue, /movies, etc).
  useEffect(() => {
    if (!loading && !authorized) navigate('/login', { replace: true })
  }, [loading, authorized, navigate])

  if (loading || !authorized) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-[#0C111D]">
        <Spinner size={32} className="text-[#BB0000]" />
      </div>
    )
  }

  return (
    <div className="tf-app-shell">
      <Sidebar meta={navigationMeta} />
      <div className="tf-content-shell">
        <MobileHeader title={title} onMenu={() => setNavigationOpen(true)} menuRef={menuRef} />
        {(title || actions) && (
          <header className="tf-page-header">
            {title && (
              <h1 className="tf-desktop-page-title">{title}</h1>
            )}
            {actions && <div className="tf-page-actions">{actions}</div>}
          </header>
        )}
        <main id="main-content" tabIndex={-1} className="tf-main-content">
          <div className="mx-auto w-full max-w-[1024px]">
            {children}
          </div>
        </main>
      </div>
      <MobileNavigationDrawer open={navigationOpen} onClose={() => setNavigationOpen(false)} triggerRef={menuRef} meta={navigationMeta} />
    </div>
  )
}
