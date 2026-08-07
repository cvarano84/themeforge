import { useCallback, useState } from 'react'
import { Link } from 'react-router-dom'
import { statsApi } from '@/lib/api'
import { AppShell } from '@/components/layout/AppShell'
import { Button, EmptyState, ErrorIcon, Spinner } from '@/components/ui'
import { ResponsiveStatGrid } from '@/components/dashboard/ResponsiveStatGrid'
import { useResource } from '@/lib/useResource'

export default function DashboardPage() {
  const [imgErrors, setImgErrors] = useState<Record<string, boolean>>({})
  const { data: stats, error, retry } = useResource(useCallback(() => statsApi.get(), []))

  if (stats === null && error) {
    return <AppShell title="Dashboard"><EmptyState icon={<ErrorIcon />} title="Couldn't load the dashboard" description={error} action={<Button variant="secondary" onClick={retry}>Retry</Button>} /></AppShell>
  }

  if (!stats) {
    return <AppShell title="Dashboard"><div className="flex min-h-48 items-center justify-center" role="status" aria-label="Loading dashboard"><Spinner size={28} className="text-[#BB0000]" /></div></AppShell>
  }

  const coverageColor = stats.coverage >= 80 ? '#12B76A' : stats.coverage >= 40 ? '#F79009' : '#E07777'

  return (
    <AppShell title="Dashboard">
      <div className="space-y-6">
        <section className="rounded-xl border border-[#1D2939] bg-[#101828] p-5" aria-labelledby="library-coverage-heading">
          <div className="mb-4">
            <h2 id="library-coverage-heading" className="mb-1 text-xs font-semibold uppercase tracking-wider text-[#98A2B3]">Library coverage</h2>
            <p className="text-4xl font-bold tabular-nums sm:text-5xl" style={{ color: coverageColor }}>{stats.coverage}%</p>
            <p className="mt-1 text-sm text-[#D0D5DD]"><span className="tabular-nums">{stats.downloaded.toLocaleString()} of {stats.total.toLocaleString()}</span> movies</p>
          </div>
          <div className="h-2 w-full overflow-hidden rounded-full bg-[#1D2939]" role="progressbar" aria-label="Movie theme library coverage" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.min(stats.coverage, 100)} aria-valuetext={`${stats.downloaded.toLocaleString()} of ${stats.total.toLocaleString()} movies have themes`}>
            <div className="h-full rounded-full transition-all duration-700" style={{ width: `${Math.min(stats.coverage, 100)}%`, backgroundColor: coverageColor }} />
          </div>
        </section>

        <ResponsiveStatGrid stats={[
          { label: 'Pending', value: stats.pending, color: '#FEC84B', href: '/queue', description: 'Needs a theme' },
          { label: 'Downloaded', value: stats.downloaded, color: '#6CE9A6', href: '/movies', description: 'Themes available' },
          { label: 'This week', value: stats.addedThisWeek, color: '#6CE9A6', href: '/history', description: 'Recent downloads' },
          { label: 'Ignored', value: stats.ignored, color: '#D0D5DD', href: '/movies', description: 'Excluded items' },
        ]} />

        <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
          <section className="overflow-hidden rounded-xl border border-[#1D2939] bg-[#101828]" aria-labelledby="recent-downloads-heading">
            <div className="tf-card-header flex min-h-14 items-center justify-between gap-3 border-b border-[#1D2939] px-4 py-2">
              <h2 id="recent-downloads-heading" className="text-xs font-semibold uppercase tracking-wider text-[#98A2B3]">Recent downloads</h2>
              <Link to="/history" className="inline-flex min-h-11 items-center rounded-lg px-3 text-sm font-semibold text-[#F4AAAA] transition-colors hover:bg-[#1D2939] hover:text-white">View all <span aria-hidden="true" className="ml-1">→</span></Link>
            </div>
            {stats.recentActivity.length === 0 ? (
              <div className="flex min-h-32 items-center justify-center px-4 py-8 text-center"><p className="text-sm text-[#98A2B3]">No themes downloaded yet.</p></div>
            ) : (
              <div className="divide-y divide-[#1D2939]">
                {stats.recentActivity.map(entry => (
                  <div key={entry.id} className="flex min-h-16 items-start gap-3 px-4 py-3">
                    <div className="mt-0.5 flex h-9 w-9 flex-shrink-0 items-center justify-center rounded-full bg-[#12B76A]/15" aria-label="Downloaded successfully" role="img">
                      <svg width="14" height="14" viewBox="0 0 12 12" fill="none" stroke="#6CE9A6" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true"><path d="M2 6l3 3 5-5" /></svg>
                    </div>
                    <div className="min-w-0 flex-1">
                      <p className="line-clamp-2 text-sm font-medium leading-snug text-[#F9FAFB]">{entry.movieTitle}{entry.movieYear && <span className="ml-1.5 font-normal text-[#98A2B3]">({entry.movieYear})</span>}</p>
                      {entry.themeTitle && <p className="mt-1 flex items-start gap-1 text-xs leading-snug text-[#98A2B3]"><svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className="mt-0.5 flex-shrink-0" aria-hidden="true"><path d="M9 18V5l12-2v13" /><circle cx="6" cy="18" r="3" /><circle cx="18" cy="16" r="3" /></svg><span className="line-clamp-2">{entry.themeTitle}</span></p>}
                      <p className="mt-1 text-xs text-[#98A2B3]">Downloaded · {formatDate(entry.downloadedAt)}</p>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </section>

          <section className="overflow-hidden rounded-xl border border-[#1D2939] bg-[#101828]" aria-labelledby="recently-added-heading">
            <div className="tf-card-header flex min-h-14 items-center justify-between gap-3 border-b border-[#1D2939] px-4 py-2">
              <h2 id="recently-added-heading" className="text-xs font-semibold uppercase tracking-wider text-[#98A2B3]">Recently added</h2>
              <Link to="/queue" className="inline-flex min-h-11 items-center rounded-lg px-3 text-sm font-semibold text-[#F4AAAA] transition-colors hover:bg-[#1D2939] hover:text-white">Go to queue <span aria-hidden="true" className="ml-1">→</span></Link>
            </div>
            {stats.recentlyAdded.length === 0 ? (
              <div className="flex min-h-32 items-center justify-center px-4 py-8 text-center"><p className="text-sm text-[#98A2B3]">{stats.pending === 0 ? 'All movies have themes!' : 'Sync your library to populate.'}</p></div>
            ) : (
              <div className="divide-y divide-[#1D2939]">
                {stats.recentlyAdded.map(movie => (
                  <div key={movie.id} className="flex min-h-16 items-center gap-3 px-4 py-2.5">
                    <div className="relative h-12 w-8 flex-shrink-0 overflow-hidden rounded bg-[#1D2939]">
                      {movie.posterUrl && !imgErrors[movie.id] ? <img src={movie.posterUrl} alt="" className="absolute inset-0 h-full w-full object-cover" onError={() => setImgErrors(e => ({ ...e, [movie.id]: true }))} loading="lazy" /> : <div className="flex h-full w-full items-center justify-center"><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#667085" strokeWidth="1.5" aria-hidden="true"><rect x="2" y="2" width="20" height="20" rx="2" /><path d="M7 2v20M17 2v20M2 12h20" /></svg></div>}
                    </div>
                    <div className="min-w-0 flex-1">
                      <p className="line-clamp-2 text-sm leading-snug text-[#D0D5DD]">{movie.title}</p>
                      <p className="mt-1 text-xs text-[#98A2B3]">{movie.year ? `${movie.year} · ` : ''}<span className="text-[#FEC84B]">Pending theme</span></p>
                    </div>
                    <div className="h-2 w-2 flex-shrink-0 rounded-full bg-[#F79009]" aria-hidden="true" />
                  </div>
                ))}
              </div>
            )}
          </section>
        </div>
      </div>
    </AppShell>
  )
}

function formatDate(iso: string): string {
  try {
    const d = new Date(iso)
    const now = new Date()
    const diffMs = now.getTime() - d.getTime()
    const diffH = Math.floor(diffMs / (1000 * 60 * 60))
    const diffD = Math.floor(diffH / 24)
    if (diffH < 1) return 'Just now'
    if (diffH < 24) return `${diffH}h ago`
    if (diffD < 7) return `${diffD}d ago`
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
  } catch { return iso }
}
