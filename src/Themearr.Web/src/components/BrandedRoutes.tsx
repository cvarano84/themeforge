import { useEffect } from 'react'
import { Route, Routes, useLocation } from 'react-router-dom'
import { APP_BRAND } from '@/lib/brand'

import RootPage from '@/app/page'
import DashboardPage from '@/app/dashboard/page'
import HistoryPage from '@/app/history/page'
import LoginPage from '@/app/login/page'
import MoviesPage from '@/app/movies/page'
import QueuePage from '@/app/queue/page'
import SettingsPage from '@/app/settings/page'
import ShowsPage from '@/app/shows/page'
import SetupPage from '@/app/setup/page'
import SystemPage from '@/app/system/page'

const ROUTE_TITLES: Record<string, string> = {
  '/dashboard': 'Dashboard',
  '/history': 'History',
  '/login': 'Sign in',
  '/movies': 'Movies',
  '/queue': 'Queue',
  '/settings': 'Settings',
  '/shows': 'Shows',
  '/setup': 'Setup',
  '/system': 'System',
}

export function BrandedRoutes() {
  const { pathname } = useLocation()

  useEffect(() => {
    const page = ROUTE_TITLES[pathname]
    document.title = page ? `${page} · ${APP_BRAND.name}` : APP_BRAND.name
  }, [pathname])

  return (
    <Routes>
      <Route path="/" element={<RootPage />} />
      <Route path="/dashboard" element={<DashboardPage />} />
      <Route path="/history" element={<HistoryPage />} />
      <Route path="/login" element={<LoginPage />} />
      <Route path="/movies" element={<MoviesPage />} />
      <Route path="/queue" element={<QueuePage />} />
      <Route path="/settings" element={<SettingsPage />} />
      <Route path="/shows" element={<ShowsPage />} />
      <Route path="/setup" element={<SetupPage />} />
      <Route path="/system" element={<SystemPage />} />
      {/* Unknown paths fall back to the root redirect, which routes by auth state. */}
      <Route path="*" element={<RootPage />} />
    </Routes>
  )
}
