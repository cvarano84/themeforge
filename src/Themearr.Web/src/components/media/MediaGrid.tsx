import { useEffect, useRef, useState } from 'react'
import type { MediaItem, MediaStatus } from '@/lib/types'
import type { MediaAdapter } from '@/lib/media/adapter'
import { Button, EmptyState, Modal, Spinner } from '@/components/ui'
import { SearchModal } from './SearchModal'
import { MobileActionBar } from '@/components/data/MobileActionBar'

interface MediaGridProps {
  items: MediaItem[]
  adapter: MediaAdapter
  onUpdated: (id: string, status: MediaStatus) => void
  /** Context-dependent empty-state copy — the page knows the source, the grid doesn't. */
  emptyDescription: string
}

type Filter = 'all' | MediaStatus | 'partial' | 'missing' | 'unavailable'

const STATUS_LABEL: Record<Exclude<Filter, 'all'>, string> = {
  pending:    'Pending',
  downloaded: 'Downloaded',
  plexTheme:  'Plex theme',
  unresolved: 'Unresolved path',
  ignored:    'Ignored',
  partial:    'Partial',
  missing:    'Missing',
  unavailable:'Unavailable',
}

export function MediaGrid({ items, adapter, onUpdated, emptyDescription }: MediaGridProps) {
  const [filter,   setFilter]   = useState<Filter>('all')
  const [search,   setSearch]   = useState('')
  const [quality,  setQuality]  = useState('all')
  const [instance, setInstance] = useState('all')
  const [selected, setSelected] = useState<MediaItem | null>(null)
  const [selectionMode, setSelectionMode] = useState(false)
  const [selectedIds, setSelectedIds] = useState<Set<string>>(() => new Set())
  const [bulkBusy, setBulkBusy] = useState(false)
  const [bulkError, setBulkError] = useState('')

  const effectiveStatus = (item: MediaItem) => item.aggregateStatus ?? item.status
  const countOf = (s: Exclude<Filter, 'all'>) => items.filter(i => effectiveStatus(i) === s).length
  const ignored = countOf('ignored')

  const visible = items.filter(i => {
    if (filter !== 'all' && effectiveStatus(i) !== filter) return false
    if (filter === 'all' && i.status === 'ignored') return false
    if (search.trim()) {
      const q = search.toLowerCase()
      return i.title.toLowerCase().includes(q) || String(i.year ?? '').includes(q)
    }
    if (quality !== 'all' && !(i.locations ?? []).some(l => l.qualityLabel === quality)) return false
    if (instance !== 'all' && !(i.locations ?? []).some(l => l.instanceId === instance)) return false
    return true
  })
  const qualities = [...new Set(items.flatMap(i => (i.locations ?? []).map(l => l.qualityLabel).filter((x): x is string => !!x)))].sort()
  const instances = [...new Map(items.flatMap(i => (i.locations ?? []).filter(l => l.instanceId).map(l => [l.instanceId!, l.instanceName ?? l.instanceId!] as const))).entries()]

  function toggleSelected(id: string) {
    setSelectedIds(current => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id); else next.add(id)
      return next
    })
  }

  function clearSelection() {
    setSelectedIds(new Set())
    setSelectionMode(false)
    setBulkError('')
  }

  async function ignoreSelected() {
    setBulkBusy(true)
    setBulkError('')
    const ids = [...selectedIds]
    const results = await Promise.allSettled(ids.map(id => adapter.ignore(id)))
    results.forEach((result, index) => { if (result.status === 'fulfilled') onUpdated(ids[index], 'ignored') })
    const failed = results.filter(result => result.status === 'rejected').length
    if (failed) setBulkError(`${failed} ${adapter.labels.plural.slice(0, -1)}${failed === 1 ? '' : 's'} could not be ignored.`)
    else clearSelection()
    setBulkBusy(false)
  }

  // Render the grid in windows so a large library (1000s of movies) never mounts
  // every card at once — that DOM/layout cost, not the data, is what made these
  // pages slow. Grow the window as a bottom sentinel scrolls into view.
  const BATCH = 120
  const [limit, setLimit] = useState(BATCH)
  const sentinelRef = useRef<HTMLDivElement | null>(null)

  // Reset paging whenever the filtered set changes. Done during render (React's
  // "adjusting state when props change" pattern) rather than in an effect, which
  // would trigger an extra cascading render.
  const [pagedFor, setPagedFor] = useState({ filter, search })
  if (pagedFor.filter !== filter || pagedFor.search !== search) {
    setPagedFor({ filter, search })
    setLimit(BATCH)
  }

  const shown   = visible.slice(0, limit)
  const hasMore = limit < visible.length

  useEffect(() => {
    if (!hasMore) return
    const el = sentinelRef.current
    if (!el) return
    const io = new IntersectionObserver(
      entries => { if (entries[0].isIntersecting) setLimit(l => l + BATCH) },
      { rootMargin: '800px' },   // preload before the user reaches the bottom
    )
    io.observe(el)
    return () => io.disconnect()
  }, [hasMore, visible.length])

  return (
    <>
      {/* Toolbar */}
      <div className="mb-5 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="-mx-1 overflow-x-auto px-1 pb-1" aria-label="Status filters">
        <div className="flex w-max items-center gap-1 rounded-lg border border-[#1D2939] bg-[#101828] p-1">
          {([
            ['all', `All (${items.length - ignored})`],
            // Driven by the adapter, so shows get a Plex theme chip and movies don't.
            // Ignored still only appears once something is ignored.
            ...([...adapter.statuses,
              ...(items.some(i => i.aggregateStatus === 'partial') ? ['partial' as const] : []),
              ...(items.some(i => i.aggregateStatus === 'missing') ? ['missing' as const] : []),
              ...(items.some(i => i.aggregateStatus === 'unavailable') ? ['unavailable' as const] : []),
            ] as Exclude<Filter, 'all'>[])
              .filter(s => s !== 'ignored' || ignored > 0)
              .map(s => [s, `${STATUS_LABEL[s]} (${countOf(s)})`] as [Filter, string]),
          ] as [Filter, string][]).map(([val, label]) => (
            <button
              key={val}
              onClick={() => setFilter(val)}
              aria-pressed={filter === val}
              className={`min-h-11 whitespace-nowrap rounded-md px-3 py-1.5 text-xs font-medium transition-all
                ${filter === val
                  ? 'bg-[#1D2939] text-[#F9FAFB] shadow-sm'
                  : 'text-[#667085] hover:text-[#D0D5DD]'}`}
            >
              {label}
            </button>
          ))}
        </div></div>

        <div className="flex w-full flex-col gap-2 sm:w-auto sm:flex-row sm:flex-wrap">
          <button type="button" aria-pressed={selectionMode} onClick={() => { setSelectionMode(value => !value); setSelectedIds(new Set()); setBulkError('') }} className="min-h-11 rounded-lg border border-[#344054] bg-[#101828] px-3 text-sm font-semibold text-[#D0D5DD] hover:border-[#667085] hover:text-white">{selectionMode ? 'Cancel selection' : 'Select'}</button>
          {instances.length > 0 && <select aria-label="Filter by instance" value={instance} onChange={e => setInstance(e.target.value)} className="min-h-11 rounded-lg border border-[#344054] bg-[#101828] px-3 py-2 text-base text-[#D0D5DD] sm:text-xs">
            <option value="all">All instances</option>{instances.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
          </select>}
          {qualities.length > 0 && <select aria-label="Filter by quality" value={quality} onChange={e => setQuality(e.target.value)} className="min-h-11 rounded-lg border border-[#344054] bg-[#101828] px-3 py-2 text-base text-[#D0D5DD] sm:text-xs">
            <option value="all">All qualities</option>{qualities.map(label => <option key={label}>{label}</option>)}
          </select>}
        <div className="relative min-w-0 flex-1 sm:w-56">
          <svg className="absolute left-3 top-1/2 -translate-y-1/2 text-[#475467]" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <circle cx="11" cy="11" r="8" /><path d="m21 21-4.35-4.35" />
          </svg>
          <input
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder={adapter.labels.searchPlaceholder}
            aria-label={`Search ${adapter.labels.plural}`}
            inputMode="search"
            className="min-h-11 w-full rounded-lg border border-[#344054] bg-[#101828] py-2 pl-9 pr-3.5 text-base text-[#F9FAFB] placeholder:text-[#667085] outline-none focus:border-[#BB0000] focus:ring-1 focus:ring-[#BB0000]/40 sm:text-sm"
          />
        </div>
        </div>
      </div>

      {/* Grid */}
      {visible.length === 0 ? (
        <EmptyState
          icon={
            <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round">
              <rect x="2" y="2" width="20" height="20" rx="2" />
              <path d="M7 2v20M17 2v20M2 12h20" />
            </svg>
          }
          title={search ? `No ${adapter.labels.plural} match your search` : adapter.labels.emptyTitle}
          description={search ? 'Try a different search term' : emptyDescription}
        />
      ) : (
        <>
          <div className="grid grid-cols-2 gap-4 min-[430px]:grid-cols-3 sm:grid-cols-4 md:grid-cols-5 lg:grid-cols-6 xl:grid-cols-8 2xl:grid-cols-10">
            {shown.map(item => (
              <MediaCard
                key={item.id}
                item={item}
                selectionMode={selectionMode}
                checked={selectedIds.has(item.id)}
                onClick={() => selectionMode ? toggleSelected(item.id) : setSelected(item)}
              />
            ))}
          </div>
          {hasMore && (
            <div ref={sentinelRef} className="flex justify-center py-8 text-xs text-[#475467]">
              Loading more… ({shown.length} of {visible.length})
            </div>
          )}
        </>
      )}

      {selected && (
        <MediaActionModal
          item={selected}
          adapter={adapter}
          onClose={() => setSelected(null)}
          onUpdated={(id, status) => { onUpdated(id, status); setSelected(null) }}
        />
      )}
      {bulkError && <p role="alert" className="mt-4 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3 text-sm text-[#FDA29B]">{bulkError}</p>}
      <MobileActionBar count={selectedIds.size} itemLabel={adapter.labels.plural.slice(0, -1)} primaryLabel="Ignore selected" onPrimary={() => void ignoreSelected()} onClear={clearSelection} busy={bulkBusy} />
    </>
  )
}

// ── Media action modal ─────────────────────────────────────────────────────────

function MediaActionModal({ item, adapter, onClose, onUpdated }: {
  item: MediaItem
  adapter: MediaAdapter
  onClose: () => void
  onUpdated: (id: string, status: MediaStatus) => void
}) {
  // 'plexTheme' deliberately does NOT auto-open search — the whole point is that ThemeForge
  // does not fill a show Plex already covers unless the operator asks for it.
  const [view,      setView]      = useState<'default' | 'search'>(item.status === 'pending' ? 'search' : 'default')
  const [replacing, setReplacing] = useState(false)
  const [ignoring,  setIgnoring]  = useState(false)
  const [error,     setError]     = useState('')

  if (view === 'search') {
    return (
      <SearchModal
        item={item}
        adapter={adapter}
        onClose={onClose}
        onDownloaded={id => onUpdated(id, 'downloaded')}
      />
    )
  }

  async function replaceTheme() {
    setReplacing(true)
    setError('')
    try {
      await adapter.deleteTheme(item.id)
      onUpdated(item.id, 'pending')
    } catch (e) {
      setError((e as Error).message)
      setReplacing(false)
    }
  }

  async function unignore() {
    setIgnoring(true)
    try {
      await adapter.unignore(item.id)
      onUpdated(item.id, 'pending')
    } catch (e) {
      setError((e as Error).message)
      setIgnoring(false)
    }
  }

  return (
    <Modal open onClose={onClose} title={`${item.title}${item.year ? ` (${item.year})` : ''}`} size="sm">
        <div className="space-y-4">
          {(item.locations?.length ?? 0) > 1 && <div className="space-y-2">
            <p className="text-xs font-semibold uppercase tracking-wider text-[#667085]">Quality locations</p>
            {item.locations!.map(location => <div key={location.id} className="flex items-center justify-between gap-3 rounded-lg border border-[#1D2939] px-3 py-2 text-xs">
              <div><span className="text-[#D0D5DD]">{location.instanceName ?? location.instanceId}</span>
                {location.qualityLabel && <span className="ml-2 rounded-full bg-[#1D2939] px-2 py-0.5 text-[#98A2B3]">{location.qualityLabel}</span>}
                <p className="mt-1 text-[#667085]">{location.status === 'pending' ? 'Missing' : location.status}</p></div>
              {location.status === 'downloaded' && <Button size="sm" variant="ghost" onClick={async () => {
                await adapter.deleteTheme(location.id, 'location'); onUpdated(item.id, 'pending')
              }}>Delete this copy</Button>}
            </div>)}
            {item.locations!.some(l => l.status === 'downloaded') && <Button size="sm" variant="ghost" className="w-full" onClick={async () => {
              if (!confirm('Delete the theme from every quality location?')) return
              await adapter.deleteTheme(item.id, 'all'); onUpdated(item.id, 'pending')
            }}>Delete from all quality locations</Button>}
            {item.aggregateStatus === 'partial' && <Button size="sm" variant="secondary" className="w-full" onClick={() => setView('search')}>Fill missing quality locations</Button>}
          </div>}
          {item.status === 'downloaded' && (
            <>
              {/* Audio preview */}
              <div className="space-y-1.5">
                <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider">Theme preview</p>
                <ThemeAudioPreview id={item.id} adapter={adapter} />
              </div>
              <div className="border-t border-[#1D2939]" />
              <Button variant="secondary" size="sm" className="w-full" onClick={() => setView('search')} loading={replacing}>
                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                  <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><polyline points="17 8 12 3 7 8" /><line x1="12" y1="3" x2="12" y2="15" />
                </svg>
                Replace theme
              </Button>
              <Button variant="ghost" size="sm" className="w-full" onClick={replaceTheme} loading={replacing}>
                Delete theme file
              </Button>
            </>
          )}

          {item.status === 'plexTheme' && (
            <div className="space-y-3">
              <p className="text-sm text-[#667085]">
                Plex already has a theme for this show, so ThemeForge skips it. Downloading one
                writes a <code className="text-[#98A2B3]">theme.mp3</code> into the show folder,
                which takes priority over Plex&apos;s own.
              </p>
              <Button variant="secondary" className="w-full" size="sm" onClick={() => setView('search')}>
                Download anyway
              </Button>
            </div>
          )}

          {item.status === 'unresolved' && (
            <div className="space-y-3">
              <p className="text-sm text-[#FDA29B]">
                ThemeForge cannot translate this source path into an existing writable folder beneath a configured local root.
              </p>
              {item.sourcePath && (
                <p className="break-all text-xs text-[#667085]">
                  Source path: <code className="text-[#98A2B3]">{item.sourcePath}</code>
                </p>
              )}
              <p className="text-xs text-[#667085]">
                Check the Docker mount and path mapping in Settings, then run a full sync or path repair.
              </p>
            </div>
          )}

          {item.status === 'ignored' && (
            <div className="space-y-3">
              <p className="text-sm text-[#667085]">
                This {adapter.labels.plural === 'shows' ? 'show' : 'movie'} is ignored and won&apos;t appear in the queue.
              </p>
              <Button className="w-full" size="sm" onClick={unignore} loading={ignoring}>
                Remove from ignore list
              </Button>
            </div>
          )}

          {error && (
            <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-3 py-2">
              <p className="text-xs text-[#FDA29B]">{error}</p>
            </div>
          )}
        </div>
    </Modal>
  )
}

// ── Media card ─────────────────────────────────────────────────────────────────

function MediaCard({ item, onClick, selectionMode, checked }: { item: MediaItem; onClick: () => void; selectionMode: boolean; checked: boolean }) {
  const [imgError, setImgError] = useState(false)
  const isPending   = item.status === 'pending' || item.aggregateStatus === 'missing'
  const isIgnored   = item.status === 'ignored'
  const isPlexTheme = item.status === 'plexTheme'
  const isUnresolved = item.status === 'unresolved'
  const isPartial = item.aggregateStatus === 'partial'

  return (
    <button
      onClick={onClick}
      aria-pressed={selectionMode ? checked : undefined}
      aria-label={`${checked ? 'Deselect' : selectionMode ? 'Select' : 'Open actions for'} ${item.title}${item.year ? `, ${item.year}` : ''}`}
      className="group relative flex min-h-11 flex-col rounded-lg text-left cursor-pointer focus-visible:ring-2 focus-visible:ring-[#F4AAAA] focus-visible:ring-offset-2 focus-visible:ring-offset-[#0C111D]"
    >
      {/* Poster */}
      <div className={`relative w-full overflow-hidden rounded-lg bg-[#1D2939] ${isIgnored ? 'opacity-40' : ''}`} style={{ aspectRatio: '2/3' }}>
        {selectionMode && <span className={`absolute left-2 top-2 z-10 flex h-7 w-7 items-center justify-center rounded-full border-2 ${checked ? 'border-[#F4AAAA] bg-[#BB0000] text-white' : 'border-[#D0D5DD] bg-[#101828]/90 text-transparent'}`} aria-hidden="true"><svg width="14" height="14" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round"><path d="M2 6l3 3 5-5" /></svg></span>}
        {item.posterUrl && !imgError ? (
          <img
            src={item.posterUrl}
            alt={item.title}
            className="absolute inset-0 h-full w-full object-cover transition-transform duration-200 group-hover:scale-105"
            onError={() => setImgError(true)}
            loading="lazy"
          />
        ) : (
          <div className="flex h-full w-full flex-col items-center justify-center gap-2 p-2">
            <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="#344054" strokeWidth="1.5" strokeLinecap="round">
              <rect x="2" y="2" width="20" height="20" rx="2" />
              <path d="M7 2v20M17 2v20M2 12h20" />
            </svg>
            <span className="text-center text-[10px] leading-tight text-[#475467] line-clamp-3">{item.title}</span>
          </div>
        )}

        {/* Hover overlay */}
        <div className="absolute inset-0 hidden items-center justify-center rounded-lg bg-black/60 opacity-0 transition-opacity duration-200 group-hover:opacity-100 group-focus-visible:opacity-100 sm:flex">
          <div className={`flex items-center gap-1.5 rounded-md px-3 py-1.5 text-xs font-medium text-white ${isPending ? 'bg-[#BB0000]' : isIgnored ? 'bg-[#344054]' : 'bg-[#1D2939]'}`}>
            {isPending   && <><svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round"><circle cx="11" cy="11" r="8" /><path d="m21 21-4.35-4.35" /></svg>Get theme</>}
            {isIgnored   && <>Ignored</>}
            {isPlexTheme && <>Plex theme</>}
            {isUnresolved && <>Fix path mapping</>}
            {!isPending && !isIgnored && !isPlexTheme && !isUnresolved && <><svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M9 18V5l12-2v13" /><circle cx="6" cy="18" r="3" /><circle cx="18" cy="16" r="3" /></svg>Preview / Replace</>}
          </div>
        </div>

        {/* Downloaded badge */}
        {item.status === 'downloaded' && (
          <div className="absolute bottom-1.5 right-1.5 flex h-5 w-5 items-center justify-center rounded-full bg-[#12B76A]">
            <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round">
              <path d="M2 6l3 3 5-5" />
            </svg>
          </div>
        )}

        {/* Plex-theme badge. Deliberately NOT the green tick — "Plex has this covered" is
            a different claim from "we fetched this", and they must not read the same. */}
        {isPlexTheme && (
          <div
            title="Plex already has a theme"
            className="absolute bottom-1.5 right-1.5 flex h-5 items-center rounded-full bg-[#344054] px-1.5 text-[9px] font-semibold text-[#D0D5DD]"
          >
            PLEX
          </div>
        )}
        {isPartial && <div className="absolute bottom-1.5 left-1.5 rounded-full bg-[#F79009] px-1.5 py-0.5 text-[9px] font-semibold text-white">PARTIAL</div>}

        {isUnresolved && (
          <div
            title="Source path is not resolved inside a configured local root"
            className="absolute bottom-1.5 right-1.5 rounded-full bg-[#B42318] px-1.5 py-0.5 text-[9px] font-semibold text-white"
          >
            PATH
          </div>
        )}
      </div>

      {/* Title + year */}
      <div className="mt-1.5 px-0.5">
        <p className={`line-clamp-2 text-sm font-medium leading-snug sm:text-xs ${isIgnored ? 'text-[#98A2B3]' : 'text-[#D0D5DD]'}`}>{item.title}</p>
        {item.year && <p className="mt-1 text-xs text-[#98A2B3]">{item.year}</p>}
        {(item.qualityLabels?.length ?? 0) > 0 && <div className="mt-1 flex flex-wrap gap-1">{item.qualityLabels!.map(label => <span key={label} className="rounded bg-[#1D2939] px-1 text-[9px] text-[#98A2B3]">{label}</span>)}</div>}
      </div>
    </button>
  )
}

// ── Theme audio preview (fetches via bearer auth, plays from object URL) ─────

function ThemeAudioPreview({ id, adapter }: { id: string; adapter: MediaAdapter }) {
  const [src, setSrc] = useState<string>('')
  const [error, setError] = useState('')

  useEffect(() => {
    let revoked = false
    let objectUrl = ''
    adapter.themeAudioObjectUrl(id)
      .then(url => {
        if (revoked) { URL.revokeObjectURL(url); return }
        objectUrl = url
        setSrc(url)
      })
      .catch(e => setError((e as Error).message))
    return () => {
      revoked = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [id, adapter])

  if (error) return <p className="text-xs text-[#FDA29B]">{error}</p>
  if (!src)  return <div className="h-9 flex items-center"><Spinner size={16} /></div>
  return (
    <audio
      controls
      src={src}
      className="w-full h-9"
      style={{ colorScheme: 'dark' }}
    />
  )
}
