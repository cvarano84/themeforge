import { useCallback, useState } from 'react'
import { historyApi } from '@/lib/api'
import { AppShell } from '@/components/layout/AppShell'
import { Button, EmptyState, ErrorIcon, Spinner } from '@/components/ui'
import { useResource } from '@/lib/useResource'

type DateFilter = 'all' | 'today' | 'week' | 'month'

export default function HistoryPage() {
  const [search,     setSearch]     = useState('')
  const [dateFilter, setDateFilter] = useState<DateFilter>('all')

  const { data: entries, error, loading, retry } = useResource(useCallback(() => historyApi.get(), []))

  const filtered = (entries ?? []).filter(e => {
    if (search.trim()) {
      const q = search.toLowerCase()
      if (!e.movieTitle.toLowerCase().includes(q) && !(e.themeTitle ?? '').toLowerCase().includes(q))
        return false
    }
    if (dateFilter !== 'all') {
      const now  = new Date()
      const date = new Date(e.downloadedAt)
      if (dateFilter === 'today') {
        if (date.toDateString() !== now.toDateString()) return false
      } else if (dateFilter === 'week') {
        if (date < new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000)) return false
      } else if (dateFilter === 'month') {
        if (date < new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000)) return false
      }
    }
    return true
  })

  return (
    <AppShell title="History" actions={
      <Button variant="ghost" size="sm" onClick={retry} loading={loading}>
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
          <path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8" />
          <path d="M21 3v5h-5" />
          <path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16" />
          <path d="M3 21v-5h5" />
        </svg>
        Refresh
      </Button>
    }>
      {entries === null && error ? (
        <EmptyState
          icon={<ErrorIcon />}
          title="Couldn&apos;t load your history"
          description={error}
          action={<Button variant="secondary" size="sm" onClick={retry}>Retry</Button>}
        />
      ) : entries === null ? (
        <div className="flex justify-center py-24">
          <Spinner size={28} className="text-[#BB0000]" />
        </div>
      ) : entries.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-24 gap-3 text-center">
          <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="#344054" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="10" />
            <polyline points="12 6 12 12 16 14" />
          </svg>
          <p className="text-sm font-semibold text-[#D0D5DD]">No downloads yet</p>
          <p className="text-sm text-[#667085]">Themes will appear here once downloaded</p>
        </div>
      ) : (
        <div className="w-full space-y-4">
          {error && (
            <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
              <p className="text-sm text-[#FDA29B]">Couldn&apos;t refresh history: {error}</p>
            </div>
          )}
          {/* Search + filter toolbar */}
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="relative w-full sm:w-64">
              <svg className="absolute left-3 top-1/2 -translate-y-1/2 text-[#475467]" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <circle cx="11" cy="11" r="8" /><path d="m21 21-4.35-4.35" />
              </svg>
              <input
                value={search}
                onChange={(e: { target: { value: string } }) => setSearch(e.target.value)}
                placeholder="Search history…"
                aria-label="Search history"
                inputMode="search"
                className="min-h-11 w-full rounded-lg border border-[#344054] bg-[#101828] py-2 pl-9 pr-3.5 text-base text-[#F9FAFB] placeholder:text-[#667085] outline-none focus:border-[#BB0000] focus:ring-1 focus:ring-[#BB0000]/40 sm:text-sm"
              />
            </div>
            <div className="-mx-1 overflow-x-auto px-1 pb-1"><div className="flex w-max items-center gap-1 rounded-lg border border-[#1D2939] bg-[#101828] p-1">
              {(['all', 'today', 'week', 'month'] as DateFilter[]).map(f => (
                <button
                  key={f}
                  onClick={() => setDateFilter(f)}
                  aria-pressed={dateFilter === f}
                  className={`min-h-11 rounded-md px-3 py-1.5 text-xs font-medium transition-all capitalize
                    ${dateFilter === f
                      ? 'bg-[#1D2939] text-[#F9FAFB] shadow-sm'
                      : 'text-[#667085] hover:text-[#D0D5DD]'}`}
                >
                  {f}
                </button>
              ))}
            </div></div>
          </div>

          <p className="text-sm text-[#98A2B3]">
            {filtered.length}{filtered.length !== entries.length ? ` of ${entries.length}` : ''} theme{entries.length !== 1 ? 's' : ''}
          </p>

          {filtered.length === 0 ? (
            <p className="py-8 text-center text-sm text-[#98A2B3]">No results match your filters.</p>
          ) : (
          <div className="rounded-xl border border-[#1D2939] overflow-hidden">
            {filtered.map((entry, i) => (
              <div
                key={entry.id}
                className={`flex min-h-20 items-start gap-3 px-4 py-4 sm:gap-4 sm:px-5 ${i < filtered.length - 1 ? 'border-b border-[#1D2939]' : ''}`}
              >
                {/* Icon */}
                <div className="flex h-8 w-8 flex-shrink-0 items-center justify-center rounded-full bg-[#12B76A]/15 mt-0.5">
                  <svg width="14" height="14" viewBox="0 0 12 12" fill="none" stroke="#12B76A" strokeWidth="2.5" strokeLinecap="round">
                    <path d="M2 6l3 3 5-5" />
                  </svg>
                </div>

                {/* Content */}
                <div className="flex-1 min-w-0 space-y-0.5">
                  {/* Movie */}
                  <p className="line-clamp-2 text-sm font-medium leading-snug text-[#F9FAFB]">
                    {entry.movieTitle}
                    {entry.movieYear && (
                      <span className="ml-1.5 font-normal text-[#98A2B3]">({entry.movieYear})</span>
                    )}
                  </p>

                  {/* Theme song */}
                  {entry.themeTitle && (
                    <p className="mt-1 flex items-start gap-1 text-xs leading-snug text-[#D0D5DD]">
                      <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className="flex-shrink-0 text-[#475467]">
                        <path d="M9 18V5l12-2v13" />
                        <circle cx="6" cy="18" r="3" /><circle cx="18" cy="16" r="3" />
                      </svg>
                      {entry.sourceUrl ? (
                        <a
                          href={entry.sourceUrl}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="line-clamp-2 min-h-11 py-1 text-[#F4AAAA] transition-colors hover:text-white"
                        >
                          {entry.themeTitle}
                        </a>
                      ) : (
                        <span className="line-clamp-2">{entry.themeTitle}</span>
                      )}
                    </p>
                  )}

                  {/* Date */}
                  <p className="mt-1 text-xs text-[#98A2B3]">Downloaded · {formatDate(entry.downloadedAt)}</p>
                </div>
              </div>
            ))}
          </div>
          )}
        </div>
      )}
    </AppShell>
  )
}

function formatDate(iso: string): string {
  try {
    const d = new Date(iso)
    return d.toLocaleString(undefined, {
      year: 'numeric', month: 'short', day: 'numeric',
      hour: '2-digit', minute: '2-digit',
    })
  } catch {
    return iso
  }
}
