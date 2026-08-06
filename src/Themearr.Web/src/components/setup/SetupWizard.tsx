import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { radarrApi, setupApi, settingsApi, sonarrApi } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import type { PathMapping, PlexLibrary, PlexServer } from '@/lib/types'
import { Button, Input, Spinner } from '@/components/ui'

type Step = 'source-select' | 'server-select' | 'library-select' | 'radarr-connect' | 'sonarr-connect' | 'path-config'
type Source = 'plex' | 'radarr' | 'sonarr'

export function SetupWizard() {
  const navigate = useNavigate()
  const { connected } = useAuth()
  const [step, setStep]     = useState<Step>('source-select')
  const [source, setSource] = useState<Source>('plex')
  const [error, setError]   = useState('')

  // Server select
  const [servers, setServers]                 = useState<PlexServer[]>([])
  const [loadingServers, setLoadingServers]   = useState(false)
  const [selectedServers, setSelectedServers] = useState<PlexServer[]>([])

  // Library select
  const [libraries, setLibraries]               = useState<Record<string, PlexLibrary[]>>({})
  const [showLibraries, setShowLibraries]       = useState<Record<string, PlexLibrary[]>>({})
  const [loadingLibs, setLoadingLibs]           = useState(false)
  const [selectedLibs, setSelectedLibs]         = useState<Record<string, string[]>>({})
  const [selectedShowLibs, setSelectedShowLibs] = useState<Record<string, string[]>>({})

  // Radarr connect
  const [radarrUrl, setRadarrUrl]               = useState('')
  const [radarrApiKey, setRadarrApiKey]         = useState('')
  const [testingRadarr, setTestingRadarr]       = useState(false)
  const [radarrTestResult, setRadarrTestResult] = useState<{ ok: boolean; detail: string } | null>(null)
  const [savingRadarr, setSavingRadarr]         = useState(false)
  const [sonarrUrl, setSonarrUrl]               = useState('')
  const [sonarrApiKey, setSonarrApiKey]         = useState('')
  const [testingSonarr, setTestingSonarr]       = useState(false)
  const [sonarrTestResult, setSonarrTestResult] = useState<{ ok: boolean; detail: string } | null>(null)
  const [savingSonarr, setSavingSonarr]         = useState(false)

  // Path config
  const [libraryPaths, setLibraryPaths]         = useState<string[]>([''])
  const [pathMappings, setPathMappings]         = useState<PathMapping[]>([])
  const [saving, setSaving]                     = useState(false)

  // Reopening an unfinished (or completed) setup should never reset choices the
  // operator already made. Credentials remain redacted; discovered servers replace
  // these display-only copies before a Plex selection is submitted.
  useEffect(() => {
    Promise.resolve(setupApi.status()).then(status => {
      if (!status) return
      setSelectedServers(status.selectedServers)
      setSelectedLibs(status.selectedLibraries)
      setSelectedShowLibs(status.selectedShowLibraries ?? {})
      if (status.libraryPaths.length) setLibraryPaths(status.libraryPaths)
      setPathMappings(status.pathMappings)
      if (status.showLibrarySource === 'sonarr' && status.movieLibrarySource === 'disabled') setSource('sonarr')
      else if (status.movieLibrarySource === 'radarr') setSource('radarr')
    }).catch(() => { /* the page-level auth flow surfaces status failures */ })
  }, [])

  // ── Source select ─────────────────────────────────────────────────────────

  // Plex servers are fetched here, on choosing Plex, rather than on mount —
  // so a Radarr user never triggers a Plex API call or sees a stray Plex error.
  function chooseSource(src: Source) {
    setSource(src)
    setError('')
    if (src === 'plex') {
      // A user can reach the wizard with a valid token but no Plex sign-in yet
      // (e.g. arriving via the Radarr entry point on /login, or an unfinished
      // Plex OAuth). The server-select step has nothing to fetch without a
      // Plex connection, so send them to sign in instead of showing an empty
      // or broken list.
      if (!connected) {
        navigate('/login')
        return
      }
      setStep('server-select')
      setLoadingServers(true)
      setupApi.plexServers()
        .then(data => {
          setServers(data.servers)
          setSelectedServers(previous => {
            const ids = new Set(previous.map(s => s.id))
            return data.servers.filter(s => ids.has(s.id))
          })
          setLoadingServers(false)
        })
        .catch(e => { setError((e as Error).message); setLoadingServers(false) })
    } else if (src === 'radarr') {
      setStep('radarr-connect')
    } else {
      setStep('sonarr-connect')
    }
  }

  // ── Server select ──────────────────────────────────────────────────────────

  function toggleServer(srv: PlexServer) {
    setSelectedServers(prev =>
      prev.find(s => s.id === srv.id)
        ? prev.filter(s => s.id !== srv.id)
        : [...prev, srv])
  }

  async function confirmServers() {
    if (selectedServers.length === 0) { setError('Select at least one server'); return }
    setLoadingLibs(true)
    setError('')
    try {
      const [movies, shows] = await Promise.all([
        setupApi.plexLibraries(selectedServers, 'movie'),
        setupApi.plexLibraries(selectedServers, 'show'),
      ])
      setLibraries(movies.libraries)
      setShowLibraries(shows.libraries)
      setStep('library-select')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoadingLibs(false)
    }
  }

  // ── Library select ─────────────────────────────────────────────────────────

  function toggleLib(serverId: string, key: string) {
    setSelectedLibs(prev => {
      const cur = prev[serverId] ?? []
      return {
        ...prev,
        [serverId]: cur.includes(key) ? cur.filter(k => k !== key) : [...cur, key],
      }
    })
  }

  function toggleShowLib(serverId: string, key: string) {
    setSelectedShowLibs(prev => {
      const cur = prev[serverId] ?? []
      return {
        ...prev,
        [serverId]: cur.includes(key) ? cur.filter(k => k !== key) : [...cur, key],
      }
    })
  }

  function confirmLibraries() {
    const total = Object.values(selectedLibs).flat().length + Object.values(selectedShowLibs).flat().length
    if (total === 0) { setError('Select at least one movie or TV show library'); return }
    setError('')
    setStep('path-config')
  }

  // ── Radarr connect ────────────────────────────────────────────────────────

  async function testRadarrConnection() {
    setTestingRadarr(true)
    setError('')
    setRadarrTestResult(null)
    try {
      const result = await radarrApi.test(radarrUrl.trim(), radarrApiKey.trim())
      setRadarrTestResult(result)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setTestingRadarr(false)
    }
  }

  async function confirmRadarr() {
    // A wrong key discovered at first sync is far worse than one discovered
    // here, so advancing requires a successful test of these exact values.
    if (!radarrTestResult?.ok) { setError('Test the connection before continuing'); return }
    setSavingRadarr(true)
    setError('')
    try {
      await radarrApi.save('radarr', radarrUrl.trim(), radarrApiKey.trim())
      setStep('path-config')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSavingRadarr(false)
    }
  }

  async function testSonarrConnection() {
    setTestingSonarr(true)
    setError('')
    setSonarrTestResult(null)
    try {
      setSonarrTestResult(await sonarrApi.test(sonarrUrl.trim(), sonarrApiKey.trim()))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setTestingSonarr(false)
    }
  }

  async function confirmSonarr() {
    if (!sonarrTestResult?.ok) { setError('Test the connection before continuing'); return }
    setSavingSonarr(true)
    setError('')
    try {
      await sonarrApi.save('sonarr', sonarrUrl.trim(), sonarrApiKey.trim())
      await radarrApi.save('disabled', '', '')
      setStep('path-config')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSavingSonarr(false)
    }
  }

  // ── Path config + save ─────────────────────────────────────────────────────

  async function save() {
    setSaving(true)
    setError('')
    try {
      const paths = libraryPaths.map(p => p.trim()).filter(Boolean)
      const mappings = pathMappings.filter(m => m.source.trim() || m.target.trim())
      if (source === 'radarr' || source === 'sonarr') {
        // The Radarr branch never touches plex/selection (Plex-only); library
        // paths go through the ordinary settings endpoint, then setup completes
        // via its own non-Plex endpoint. /setup is reachable at any time by an
        // already-configured user, so this must only write the library paths —
        // fetch the current settings first and round-trip everything else
        // unchanged, rather than posting a blank slate that would wipe out an
        // existing Plex server/library selection and reset auto-download,
        // auto-sync and the advanced search settings.
        const current = await settingsApi.get()
        await settingsApi.save({ ...current, libraryPaths: paths, pathMappings: mappings })
        await setupApi.complete()
      } else {
        await setupApi.saveSelection({
          servers: selectedServers,
          selectedLibraries: selectedLibs,
          selectedShowLibraries: selectedShowLibs,
          pathMappings: mappings,
          libraryPaths: paths,
        })
      }
      navigate(source === 'sonarr' ? '/shows' : '/movies')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  // ── Header ─────────────────────────────────────────────────────────────────

  const header = (() => {
    if (step === 'source-select')
      return { title: 'Set up ThemeForge', subtitle: 'Choose where ThemeForge should read your movies or TV shows from' }
    if (step === 'radarr-connect')
      return { title: 'Connect your Radarr instance', subtitle: 'No Plex account needed — ThemeForge will read your movie list straight from Radarr' }
    if (step === 'sonarr-connect')
      return { title: 'Connect your Sonarr instance', subtitle: 'Set up a TV-only installation without Plex' }
    if (step === 'path-config' && source !== 'plex')
      return { title: 'Local library paths', subtitle: `Where are your ${source === 'sonarr' ? 'TV shows' : 'movies'} mounted for ThemeForge?` }
    return { title: 'Connect your Plex server', subtitle: 'Choose which server and libraries ThemeForge should manage' }
  })()

  // ── Render ─────────────────────────────────────────────────────────────────

  return (
    <div className="mx-auto max-w-lg space-y-8">
      {/* Header */}
      <div>
        <div className="mb-2 flex h-12 w-12 items-center justify-center rounded-xl bg-[#BB0000]">
          <svg width="24" height="24" viewBox="0 0 24 24" fill="white">
            <circle cx="12" cy="12" r="9" fill="none" stroke="white" strokeWidth="1.5" />
            <path d="M9 9l6 3-6 3V9z" fill="white" />
          </svg>
        </div>
        <h1 className="text-2xl font-bold text-[#F9FAFB]">{header.title}</h1>
        <p className="mt-1 text-sm text-[#667085]">{header.subtitle}</p>
      </div>

      <StepIndicator current={step} source={source} />

      {error && (
        <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
          <p className="text-sm text-[#FDA29B]">{error}</p>
        </div>
      )}

      {/* ── Source select ── */}
      {step === 'source-select' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
            <h2 className="font-semibold text-[#F9FAFB]">How does ThemeForge find your libraries?</h2>
          <div className="space-y-2">
            <button
              onClick={() => chooseSource('plex')}
              className="flex w-full items-center gap-3 rounded-lg border border-[#1D2939] px-4 py-3 text-left transition-all hover:border-[#344054]"
            >
              <div className="min-w-0">
                <p className="text-sm font-medium text-[#F9FAFB]">Plex</p>
                <p className="text-xs text-[#667085]">Sign in and pick your Plex server and libraries</p>
              </div>
            </button>
            <button
              onClick={() => chooseSource('radarr')}
              className="flex w-full items-center gap-3 rounded-lg border border-[#1D2939] px-4 py-3 text-left transition-all hover:border-[#344054]"
            >
              <div className="min-w-0">
                <p className="text-sm font-medium text-[#F9FAFB]">Radarr</p>
                <p className="text-xs text-[#667085]">Connect directly to Radarr — no Plex account required</p>
              </div>
            </button>
            <button
              onClick={() => chooseSource('sonarr')}
              className="flex w-full items-center gap-3 rounded-lg border border-[#1D2939] px-4 py-3 text-left transition-all hover:border-[#344054]"
            >
              <div className="min-w-0">
                <p className="text-sm font-medium text-[#F9FAFB]">Sonarr (TV only)</p>
                <p className="text-xs text-[#667085]">Connect directly to Sonarr with movies disabled</p>
              </div>
            </button>
          </div>
        </div>
      )}

      {/* ── Server select ── */}
      {step === 'server-select' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          <h2 className="font-semibold text-[#F9FAFB]">Select your Plex server</h2>
          {loadingServers ? (
            <div className="flex items-center gap-3 text-sm text-[#98A2B3]">
              <Spinner size={18} /> Loading servers…
            </div>
          ) : servers.length === 0 ? (
            <p className="text-sm text-[#667085]">No servers found on your account.</p>
          ) : (
            <div className="space-y-2">
              {servers.map(srv => (
                <button
                  key={srv.id}
                  onClick={() => toggleServer(srv)}
                  className={`flex w-full items-center gap-3 rounded-lg border px-4 py-3 text-left transition-all
                    ${selectedServers.find(s => s.id === srv.id)
                      ? 'border-[#BB0000] bg-[#BB0000]/10'
                      : 'border-[#1D2939] hover:border-[#344054]'}`}
                >
                  <span className={`h-4 w-4 rounded border flex-shrink-0 flex items-center justify-center
                    ${selectedServers.find(s => s.id === srv.id) ? 'bg-[#BB0000] border-[#BB0000]' : 'border-[#344054]'}`}>
                    {selectedServers.find(s => s.id === srv.id) && (
                      <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round">
                        <path d="M2 6l3 3 5-5" />
                      </svg>
                    )}
                  </span>
                  <div className="min-w-0">
                    <p className="text-sm font-medium text-[#F9FAFB] truncate">{srv.name}</p>
                    <p className="text-xs text-[#667085] truncate">{srv.url}</p>
                  </div>
                  {srv.owned && <span className="ml-auto text-xs text-[#6CE9A6] flex-shrink-0">Owned</span>}
                </button>
              ))}
            </div>
          )}
          <Button onClick={confirmServers} loading={loadingLibs} disabled={selectedServers.length === 0 || loadingServers} className="w-full">
            Continue
          </Button>
        </div>
      )}

      {/* ── Library select ── */}
      {step === 'library-select' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          <h2 className="font-semibold text-[#F9FAFB]">Select libraries</h2>

          <LibraryChoices
            title="Movie Libraries"
            libraries={libraries}
            selected={selectedLibs}
            selectedServers={selectedServers}
            onToggle={toggleLib}
            empty="No movie libraries were returned by Plex."
          />

          <LibraryChoices
            title="TV Show Libraries"
            libraries={showLibraries}
            selected={selectedShowLibs}
            selectedServers={selectedServers}
            onToggle={toggleShowLib}
            empty="No TV show libraries were returned by Plex."
          />

          <p className="rounded-lg border border-[#344054] bg-[#0C111D] p-3 text-xs text-[#98A2B3]">
            Show themes are written as <code>theme.mp3</code> directly into each series root. Your TV library mount must be writable.
          </p>
          <div className="flex gap-2 pt-2">
            <Button variant="ghost" size="sm" onClick={() => setStep('server-select')}>Back</Button>
            <Button onClick={confirmLibraries} className="flex-1">Continue</Button>
          </div>
        </div>
      )}

      {/* ── Radarr connect ── */}
      {step === 'radarr-connect' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          <h2 className="font-semibold text-[#F9FAFB]">Connect to Radarr</h2>

          <Input
            label="Radarr URL"
            placeholder="http://localhost:7878"
            value={radarrUrl}
            onChange={e => { setRadarrUrl(e.target.value); setRadarrTestResult(null) }}
          />
          <Input
            label="API key"
            type="password"
            placeholder="Radarr API key…"
            value={radarrApiKey}
            onChange={e => { setRadarrApiKey(e.target.value); setRadarrTestResult(null) }}
            className="font-mono text-xs"
          />

          {radarrTestResult && (
            <div className={`rounded-lg border px-3.5 py-2.5 text-sm ${
              radarrTestResult.ok
                ? 'border-[#12B76A]/30 bg-[#12B76A]/5 text-[#D0D5DD]'
                : 'border-[#B42318]/30 bg-[#FEF3F2]/5 text-[#FDA29B]'
            }`}>
              {radarrTestResult.detail}
            </div>
          )}

          <div className="flex gap-2 pt-2">
            <Button variant="ghost" size="sm" onClick={() => setStep('source-select')}>Back</Button>
            <Button
              variant="secondary"
              onClick={testRadarrConnection}
              loading={testingRadarr}
              disabled={!radarrUrl.trim() || !radarrApiKey.trim()}
            >
              Test connection
            </Button>
            <Button
              onClick={confirmRadarr}
              loading={savingRadarr}
              disabled={!radarrTestResult?.ok}
              className="flex-1"
            >
              Continue
            </Button>
          </div>
        </div>
      )}

      {/* ── Path config ── */}
      {step === 'sonarr-connect' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          <h2 className="font-semibold text-[#F9FAFB]">Connect to Sonarr</h2>
          <Input label="Sonarr URL" placeholder="http://localhost:8989" value={sonarrUrl}
            onChange={e => { setSonarrUrl(e.target.value); setSonarrTestResult(null) }} />
          <Input label="API key" type="password" placeholder="Sonarr API key…" value={sonarrApiKey}
            onChange={e => { setSonarrApiKey(e.target.value); setSonarrTestResult(null) }} className="font-mono text-xs" />
          {sonarrTestResult && (
            <div className={`rounded-lg border px-3.5 py-2.5 text-sm ${sonarrTestResult.ok
              ? 'border-[#12B76A]/30 bg-[#12B76A]/5 text-[#D0D5DD]'
              : 'border-[#B42318]/30 bg-[#FEF3F2]/5 text-[#FDA29B]'}`}>
              {sonarrTestResult.detail}
            </div>
          )}
          <div className="flex flex-wrap gap-2 pt-2">
            <Button variant="ghost" size="sm" onClick={() => setStep('source-select')}>Back</Button>
            <Button variant="secondary" onClick={testSonarrConnection} loading={testingSonarr}
              disabled={!sonarrUrl.trim() || !sonarrApiKey.trim()}>Test connection</Button>
            <Button onClick={confirmSonarr} loading={savingSonarr} disabled={!sonarrTestResult?.ok} className="flex-1">Continue</Button>
          </div>
        </div>
      )}

      {step === 'path-config' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-5">
          <div>
            <h2 className="font-semibold text-[#F9FAFB]">Local library paths</h2>
            <p className="mt-1 text-sm text-[#667085]">
              Enter each writable container path, such as /movies or /shows. Show themes are written as theme.mp3 into the series root, so TV mounts cannot be read-only.
            </p>
          </div>

          <div className="space-y-2">
            {libraryPaths.map((p, i) => (
              <div key={i} className="flex gap-2">
                <Input
                  placeholder={source === 'sonarr' ? '/shows' : '/mnt/movies'}
                  value={p}
                  onChange={e => {
                    const next = [...libraryPaths]
                    next[i] = e.target.value
                    setLibraryPaths(next)
                  }}
                  className="flex-1"
                />
                {libraryPaths.length > 1 && (
                  <button
                    onClick={() => setLibraryPaths(prev => prev.filter((_, j) => j !== i))}
                    className="px-2 text-[#667085] hover:text-[#FDA29B] transition-colors"
                    aria-label="Remove"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                      <path d="M18 6 6 18M6 6l12 12" />
                    </svg>
                  </button>
                )}
              </div>
            ))}
            <Button variant="ghost" size="sm" onClick={() => setLibraryPaths(p => [...p, ''])}>
              + Add path
            </Button>
          </div>

          <div className="space-y-3 border-t border-[#1D2939] pt-4">
            <div>
              <h3 className="text-sm font-medium text-[#F9FAFB]">Path mappings</h3>
              <p className="mt-1 text-xs text-[#667085]">If {source === 'plex' ? 'Plex' : source === 'radarr' ? 'Radarr' : 'Sonarr'} reports a different path, translate it to the container path.</p>
            </div>
            <div className="rounded-lg border border-[#1D2939] bg-[#0C111D] p-3 text-xs text-[#98A2B3]">
              <p>Source: <code>/mnt/plex/Movies</code></p>
              <p>Target: <code>/movies</code></p>
              <p>Docker mount: <code>/mnt/plex/Movies:/movies</code></p>
            </div>
            {pathMappings.map((mapping, i) => (
              <div key={i} className="flex items-center gap-2">
                <Input placeholder="/mnt/plex/Movies" value={mapping.source}
                  onChange={e => setPathMappings(ms => ms.map((m, j) => j === i ? { ...m, source: e.target.value } : m))}
                  className="flex-1 font-mono text-xs" />
                <span className="text-[#475467]">→</span>
                <Input placeholder="/movies" value={mapping.target}
                  onChange={e => setPathMappings(ms => ms.map((m, j) => j === i ? { ...m, target: e.target.value } : m))}
                  className="flex-1 font-mono text-xs" />
                <button onClick={() => setPathMappings(ms => ms.filter((_, j) => j !== i))}
                  className="px-2 text-[#667085] hover:text-[#FDA29B]" aria-label="Remove mapping">×</button>
              </div>
            ))}
            <Button variant="ghost" size="sm" onClick={() => setPathMappings(ms => [...ms, { source: '', target: '' }])}>
              + Add mapping
            </Button>
          </div>

          <div className="flex gap-2 pt-2">
            <Button variant="ghost" size="sm" onClick={() => setStep(source === 'plex' ? 'library-select' : source === 'radarr' ? 'radarr-connect' : 'sonarr-connect')}>Back</Button>
            <Button onClick={save} loading={saving} className="flex-1">Save & continue</Button>
          </div>
        </div>
      )}
    </div>
  )
}

// ── Step indicator ────────────────────────────────────────────────────────────

function LibraryChoices({ title, libraries, selected, selectedServers, onToggle, empty }: {
  title: string
  libraries: Record<string, PlexLibrary[]>
  selected: Record<string, string[]>
  selectedServers: PlexServer[]
  onToggle: (serverId: string, key: string) => void
  empty: string
}) {
  const count = Object.values(libraries).reduce((total, values) => total + values.length, 0)
  return (
    <div className="space-y-3 border-t border-[#1D2939] pt-4 first:border-0 first:pt-0">
      <h3 className="text-sm font-semibold text-[#D0D5DD]">{title}</h3>
      {count === 0 && <p className="text-sm text-[#667085]">{empty}</p>}
      {Object.entries(libraries).map(([serverId, libs]) => {
        const server = selectedServers.find(s => s.id === serverId)
        return (
          <div key={serverId} className="space-y-2">
            <p className="text-xs font-medium uppercase tracking-wider text-[#667085]">{server?.name ?? serverId}</p>
            {libs.map(lib => {
              const checked = (selected[serverId] ?? []).includes(lib.key)
              return (
                <button key={lib.key} onClick={() => onToggle(serverId, lib.key)}
                  className={`flex w-full items-center gap-3 rounded-lg border px-4 py-3 text-left transition-all ${
                    checked ? 'border-[#BB0000] bg-[#BB0000]/10' : 'border-[#1D2939] hover:border-[#344054]'}`}>
                  <span className={`flex h-4 w-4 flex-shrink-0 items-center justify-center rounded border ${
                    checked ? 'border-[#BB0000] bg-[#BB0000]' : 'border-[#344054]'}`}>
                    {checked && <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round"><path d="M2 6l3 3 5-5" /></svg>}
                  </span>
                  <p className="text-sm font-medium text-[#F9FAFB]">{lib.title}</p>
                </button>
              )
            })}
          </div>
        )
      })}
    </div>
  )
}

const PLEX_STEPS: { id: Step; label: string }[] = [
  { id: 'source-select',  label: 'Source' },
  { id: 'server-select',  label: 'Server' },
  { id: 'library-select', label: 'Libraries' },
  { id: 'path-config',    label: 'Paths' },
]

const RADARR_STEPS: { id: Step; label: string }[] = [
  { id: 'source-select',  label: 'Source' },
  { id: 'radarr-connect', label: 'Connect' },
  { id: 'path-config',    label: 'Paths' },
]

const SONARR_STEPS: { id: Step; label: string }[] = [
  { id: 'source-select',  label: 'Source' },
  { id: 'sonarr-connect', label: 'Connect' },
  { id: 'path-config',    label: 'Paths' },
]

function StepIndicator({ current, source }: { current: Step; source: Source }) {
  // Only the steps on the chosen branch — a Radarr user must not see "Select
  // server" as a pending step they will never reach. Before a choice is made
  // (still on source-select) this falls back to the Plex list, matching the
  // wizard's original step count for the flow most installs still use.
  const steps = source === 'radarr' ? RADARR_STEPS : source === 'sonarr' ? SONARR_STEPS : PLEX_STEPS
  const idx = steps.findIndex(s => s.id === current)
  return (
    <div className="flex items-center gap-2">
      {steps.map((step, i) => (
        <div key={step.id} className="flex items-center gap-2">
          <div className={`flex h-7 w-7 items-center justify-center rounded-full text-xs font-medium transition-colors
            ${i < idx  ? 'bg-[#BB0000] text-white' :
              i === idx ? 'bg-[#BB0000]/20 border border-[#BB0000] text-[#E07777]' :
                          'bg-[#1D2939] text-[#475467]'}`}>
            {i < idx
              ? <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round"><path d="M2 6l3 3 5-5" /></svg>
              : i + 1}
          </div>
          <span className={`text-xs ${i === idx ? 'text-[#D0D5DD]' : 'text-[#475467]'}`}>{step.label}</span>
          {i < steps.length - 1 && <div className="h-px w-4 bg-[#1D2939] flex-shrink-0" />}
        </div>
      ))}
    </div>
  )
}
