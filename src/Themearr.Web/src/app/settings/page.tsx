import { useCallback, useEffect, useRef, useState } from 'react'
import { apiKeyApi, arrInstancesApi, downloaderApi, plexApi, radarrApi, settingsApi, setupApi, sonarrApi, versionApi, youtubeCookiesApi } from '@/lib/api'
import type { ArrInstance, DownloaderDiagnostics, PathMappingTestResult, PlexLibrary, Settings, VersionInfo } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Button, EmptyState, ErrorIcon, Input, Spinner } from '@/components/ui'
import { useResource } from '@/lib/useResource'
import { ArrInstancesSection } from '@/components/settings/ArrInstancesSection'
import { APP_BRAND, brandAsset } from '@/lib/brand'

const LIBRARY_SOURCE_OPTIONS: { value: 'plex' | 'radarr'; label: string }[] = [
  { value: 'plex', label: 'Plex' },
  { value: 'radarr', label: 'Radarr' },
]
const MOVIE_SOURCE_OPTIONS = [...LIBRARY_SOURCE_OPTIONS, { value: 'disabled' as const, label: 'Disabled (movies)' }]
const SHOW_SOURCE_OPTIONS: { value: 'plex' | 'sonarr' | 'disabled'; label: string }[] = [
  { value: 'plex', label: 'Plex' },
  { value: 'sonarr', label: 'Sonarr' },
  { value: 'disabled', label: 'Disabled (shows)' },
]

export default function SettingsPage() {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [arrMappingInstances, setArrMappingInstances] = useState<ArrInstance[]>([])

  // ── Show libraries (opt-in: nothing selected means shows stay off) ──────────
  const [plexLibraries, setPlexLibraries] = useState<Record<string, PlexLibrary[]>>({})
  const [showLibs,      setShowLibs]      = useState<Record<string, string[]>>({})
  const [savingShowLibs, setSavingShowLibs] = useState(false)
  const [showLibsSaved,  setShowLibsSaved]  = useState(false)
  const [showLibsError,  setShowLibsError]  = useState('')
  const [showLibsLoading, setShowLibsLoading] = useState(false)
  const [version,  setVersion]  = useState<VersionInfo | null>(null)
  // Set when the initial version fetch fails. Supplementary -- unlike
  // settingsApi.get() below, nothing else on the page depends on the
  // version, so this only ever drives a small note in the Updates section
  // rather than gating the page.
  const [versionLoadError, setVersionLoadError] = useState('')
  const [saving,         setSaving]         = useState(false)
  const [saved,          setSaved]          = useState(false)
  const [error,          setError]          = useState('')
  const [mappingSample, setMappingSample] = useState('')
  const [mappingTesting, setMappingTesting] = useState(false)
  const [mappingTest, setMappingTest] = useState<PathMappingTestResult | null>(null)
  const [mappingTestError, setMappingTestError] = useState('')
  // Manual Plex server URL override (per-server, keyed by server id) -- lets
  // a server's stored URL be edited, test-connected, and saved independently
  // of the rest of Settings, mirroring the Radarr connect state below.
  const [plexUrls,    setPlexUrls]    = useState<Record<string, string>>({})
  const [plexTest,    setPlexTest]    = useState<{ ok: boolean; detail: string } | null>(null)
  const [plexTesting, setPlexTesting] = useState(false)
  const [plexSaving,  setPlexSaving]  = useState(false)
  const [plexSaved,   setPlexSaved]   = useState(false)
  const [plexError,   setPlexError]   = useState('')
  const [downloader,       setDownloader]       = useState<DownloaderDiagnostics | null>(null)
  const [downloaderError,  setDownloaderError]  = useState('')
  const [downloaderSaving, setDownloaderSaving] = useState(false)
  const [downloaderTesting, setDownloaderTesting] = useState(false)
  const [downloaderResult, setDownloaderResult] = useState<{ ok: boolean; detail: string } | null>(null)
  const [cookieBusy,       setCookieBusy]       = useState(false)
  const [cookieError,      setCookieError]      = useState('')
  const [cookieSuccess,    setCookieSuccess]    = useState('')
  const cookieInputRef = useRef<HTMLInputElement>(null)
  const [librarySource,    setLibrarySource]    = useState<'plex' | 'radarr' | 'disabled'>('plex')
  const [radarrUrl,        setRadarrUrl]        = useState('')
  const [radarrApiKey,     setRadarrApiKey]     = useState('')
  const [radarrConfigured, setRadarrConfigured] = useState(false)
  const [radarrSaving,     setRadarrSaving]     = useState(false)
  const [radarrSaved,      setRadarrSaved]      = useState(false)
  const [radarrTesting,    setRadarrTesting]    = useState(false)
  const [radarrTestResult, setRadarrTestResult] = useState<{ ok: boolean; detail: string } | null>(null)
  const [radarrError,      setRadarrError]      = useState('')
  const [radarrLoaded,     setRadarrLoaded]     = useState(false)
  const [radarrLoadError,  setRadarrLoadError]  = useState('')
  const [showSource,       setShowSource]        = useState<'plex' | 'sonarr' | 'disabled'>('disabled')
  const [sonarrUrl,        setSonarrUrl]         = useState('')
  const [sonarrApiKey,     setSonarrApiKey]      = useState('')
  const [sonarrConfigured, setSonarrConfigured] = useState(false)
  const [sonarrSaving,     setSonarrSaving]      = useState(false)
  const [sonarrSaved,      setSonarrSaved]       = useState(false)
  const [sonarrTesting,    setSonarrTesting]     = useState(false)
  const [sonarrTestResult, setSonarrTestResult] = useState<{ ok: boolean; detail: string } | null>(null)
  const [sonarrError,      setSonarrError]       = useState('')
  const [sonarrLoaded,     setSonarrLoaded]      = useState(false)
  const [sonarrLoadError,  setSonarrLoadError]  = useState('')
  const [apiKey,             setApiKey]             = useState('')
  const [apiKeyLoaded,       setApiKeyLoaded]       = useState(false)
  const [apiKeyLoadError,    setApiKeyLoadError]    = useState('')
  const [apiKeyRegenerating, setApiKeyRegenerating] = useState(false)
  const [apiKeyRegenerated,  setApiKeyRegenerated]  = useState(false)
  const [apiKeyError,        setApiKeyError]        = useState('')
  const [keyCopied,          setKeyCopied]          = useState(false)
  const [webhookCopied,      setWebhookCopied]      = useState(false)
  const keyFieldRef     = useRef<HTMLDivElement>(null)
  const webhookFieldRef = useRef<HTMLDivElement>(null)

  // Update modal state
  const [updateOpen,    setUpdateOpen]    = useState(false)
  const [updating,      setUpdating]      = useState(false)
  const [updateDone,    setUpdateDone]    = useState(false)
  const [updateError,   setUpdateError]   = useState('')
  const [updateLogs,    setUpdateLogs]    = useState<string[]>([])
  const [checking,      setChecking]      = useState(false)
  // Set when a "Check for updates" click fails. Distinct from versionLoadError
  // (the initial/retry load): this is the action's own failure, shown next to
  // the button that triggered it, same as radarrError/apiKeyError elsewhere.
  const [checkUpdatesError, setCheckUpdatesError] = useState('')
  const logEndRef = useRef<HTMLDivElement>(null)

  const loadShowLibraries = useCallback(async (servers: Settings['selectedServers']) => {
    setShowLibsError('')
    setPlexLibraries({})
    if (servers.length === 0) return
    setShowLibsLoading(true)
    try {
      const response = await setupApi.plexLibraries(servers, 'show')
      setPlexLibraries(response.libraries)
    } catch (e) {
      setShowLibsError((e as Error)?.message || 'Plex TV-library discovery failed.')
    } finally {
      setShowLibsLoading(false)
    }
  }, [])

  // Loads settings -- the data the rest of the page (Library Source, API Key
  // and library-source sections) can't function without. Routed through
  // useResource so a failed request surfaces as an error screen with a
  // retry, rather than leaving the page spinning forever.
  const loadSettings = useCallback(async () => {
    const s = await settingsApi.get()
    setSettings(s)
    setPlexUrls(Object.fromEntries(s.selectedServers.map(srv => [srv.id, srv.url])))
    setShowLibs(s.selectedShowLibraries ?? {})

    // The show-library picker needs the server's library list, which only the setup
    // endpoint returns. Supplementary: a failure here leaves the picker empty with a
    // notice rather than gating the whole settings page.
    void loadShowLibraries(s.selectedServers)
    return s
  }, [loadShowLibraries])
  const { error: settingsError, retry: retrySettings } = useResource(loadSettings)

  useEffect(() => {
    // Version and local downloader status are supplementary: nothing else on the
    // page depends on them, so their failures stay local to their own small
    // areas (the Updates section / downloader section below) instead of
    // gating the whole page the way a failed settingsApi.get() does.
    versionApi.get().then(v => {
      setVersion(v)
      setVersionLoadError('')
    }).catch(e => {
      setVersionLoadError((e as Error)?.message || 'Failed to load version info.')
    })
    downloaderApi.get().then(s => {
      setDownloader(s)
      setDownloaderError('')
    }).catch(e => {
      setDownloaderError((e as Error)?.message || 'Failed to check the local downloader.')
    })
    radarrApi.get().then(s => {
      setLibrarySource(s.source)
      setRadarrUrl(s.url)
      setRadarrConfigured(s.configured)
      setRadarrLoaded(true)
      setRadarrLoadError('')
    }).catch(e => {
      setRadarrLoaded(false)
      setRadarrLoadError((e as Error)?.message || 'Failed to load the current library source.')
    })
    arrInstancesApi.list().then(setArrMappingInstances).catch(() => setArrMappingInstances([]))
    sonarrApi.get().then(s => {
      setShowSource(s.source)
      setSonarrUrl(s.url)
      setSonarrConfigured(s.configured)
      setSonarrLoaded(true)
      setSonarrLoadError('')
    }).catch(e => {
      setSonarrLoaded(false)
      setSonarrLoadError((e as Error)?.message || 'Failed to load the current show source.')
    })
    apiKeyApi.get().then(k => {
      setApiKey(k.key)
      setApiKeyLoaded(true)
      setApiKeyLoadError('')
    }).catch(e => {
      setApiKeyLoaded(false)
      setApiKeyLoadError((e as Error)?.message || 'Failed to load the API key.')
    })
  }, [])

  // Auto-scroll logs
  useEffect(() => {
    logEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [updateLogs])

  // Poll update status while in progress
  useEffect(() => {
    if (!updating) return
    const id = setInterval(async () => {
      try {
        const st = await versionApi.updateStatus()
        if (st.logs.length) setUpdateLogs(st.logs)
        if (st.finished) {
          setUpdating(false)
          setUpdateDone(true)
          if (st.error) setUpdateError(st.error)
        }
      } catch { /* ignore */ }
    }, 1000)
    return () => clearInterval(id)
  }, [updating])

  async function save() {
    if (!settings) return
    setSaving(true)
    setError('')
    try {
      await settingsApi.save(settings)
      setSaved(true)
      setTimeout(() => setSaved(false), 2000)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  function toggleShowLib(serverId: string, key: string) {
    setShowLibsSaved(false)
    setShowLibs(prev => {
      const cur = prev[serverId] ?? []
      return { ...prev, [serverId]: cur.includes(key) ? cur.filter(k => k !== key) : [...cur, key] }
    })
  }

  // Sends the whole settings object with the show selection replaced. The endpoint takes
  // one payload and writes the other collections unconditionally, so a partial object
  // would clear them. The key is always sent — the server reads an absent
  // selectedShowLibraries as "leave unchanged", so omitting it when everything is
  // unticked would look like a successful save that quietly kept the old selection.
  async function saveShowLibraries() {
    if (!settings) return
    setSavingShowLibs(true)
    setShowLibsError('')
    try {
      const next = { ...settings, selectedShowLibraries: showLibs }
      await settingsApi.save(next)
      setSettings(next)
      setShowLibsSaved(true)
    } catch (e) {
      setShowLibsError((e as Error)?.message || 'Could not save the show libraries.')
    } finally {
      setSavingShowLibs(false)
    }
  }

  async function testPlexUrl(serverId: string) {
    setPlexTesting(true)
    setPlexTest(null)
    setPlexError('')
    try {
      setPlexTest(await plexApi.test(serverId, plexUrls[serverId] ?? ''))
    } catch (e) {
      setPlexError((e as Error).message) // surface, never swallow
    } finally {
      setPlexTesting(false)
    }
  }

  async function savePlexUrl(serverId: string) {
    setPlexSaving(true)
    setPlexSaved(false)
    setPlexError('')
    try {
      const res = await plexApi.saveUrl(serverId, plexUrls[serverId] ?? '')
      // Sync to the response rather than trusting what was typed: the backend
      // normalises the URL (adds a scheme, trims a trailing slash), and
      // saveUrl() -- unlike radarrApi.save() -- already echoes the
      // normalised value back, so there's no need for a separate re-fetch.
      // Only the saved server's own entry is touched -- res.selectedServers
      // is the full list, and overwriting every entry would silently discard
      // any unsaved edit the user has typed into another server's field.
      setSettings(s => s ? { ...s, selectedServers: res.selectedServers } : s)
      setPlexUrls(p => ({ ...p, [serverId]: res.selectedServers.find(srv => srv.id === serverId)?.url ?? p[serverId] }))
      setPlexSaved(true)
      setTimeout(() => setPlexSaved(false), 2000)
    } catch (e) {
      setPlexError((e as Error).message)
    } finally {
      setPlexSaving(false)
    }
  }

  async function startUpdate() {
    setUpdateOpen(true)
    setUpdating(true)
    setUpdateDone(false)
    setUpdateError('')
    setUpdateLogs([])
    try {
      await versionApi.update()
    } catch (e) {
      setUpdating(false)
      setUpdateDone(true)
      setUpdateError((e as Error).message)
    }
  }

  async function checkForUpdates() {
    setChecking(true)
    setCheckUpdatesError('')
    try {
      const v = await versionApi.refresh()
      setVersion(v)
    } catch (e) {
      setCheckUpdatesError(`Couldn't check for updates: ${(e as Error)?.message || 'unknown error'}`)
    } finally {
      setChecking(false)
    }
  }

  async function saveDownloader() {
    if (!downloader || downloaderSaving) return
    if (downloader.timeoutSeconds < 30 || downloader.timeoutSeconds > 1800) {
      setDownloaderError('Download timeout must be between 30 and 1800 seconds.')
      return
    }
    if (downloader.concurrentDownloads < 1 || downloader.concurrentDownloads > 3) {
      setDownloaderError('Concurrent downloads must be between 1 and 3.')
      return
    }
    setDownloaderSaving(true)
    setDownloaderError('')
    setDownloaderResult(null)
    try {
      setDownloader(await downloaderApi.save(
        downloader.audioQuality, downloader.timeoutSeconds, downloader.concurrentDownloads))
      setDownloaderResult({ ok: true, detail: 'Downloader settings saved.' })
    } catch (e) {
      setDownloaderError((e as Error)?.message || 'Could not save downloader settings.')
    } finally {
      setDownloaderSaving(false)
    }
  }

  async function testPathMapping() {
    if (!settings || !mappingSample.trim()) return
    setMappingTesting(true)
    setMappingTest(null)
    setMappingTestError('')
    try {
      setMappingTest(await settingsApi.testPathMapping(
        mappingSample.trim(), librarySource === 'radarr' || showSource === 'sonarr', settings.pathMappings, settings.libraryPaths))
    } catch (e) {
      setMappingTestError((e as Error)?.message || 'Could not test this mapping.')
    } finally {
      setMappingTesting(false)
    }
  }

  async function testDownloader() {
    if (downloaderTesting) return
    setDownloaderTesting(true)
    setDownloaderError('')
    setDownloaderResult(null)
    try {
      const result = await downloaderApi.test()
      setDownloader(result.diagnostics)
      setDownloaderResult({ ok: result.ok, detail: result.detail })
    } catch (e) {
      setDownloaderError((e as Error)?.message || 'Local downloader test failed.')
    } finally {
      setDownloaderTesting(false)
    }
  }

  async function uploadCookies(file: File | undefined) {
    if (!file || !downloader || !downloader.cookies.canUpload || cookieBusy) return
    if (downloader.cookies.configured &&
        !confirm('Replace the current uploaded cookies.txt file?')) {
      if (cookieInputRef.current) cookieInputRef.current.value = ''
      return
    }
    setCookieBusy(true)
    setCookieError('')
    setCookieSuccess('')
    try {
      const status = await youtubeCookiesApi.upload(file)
      setDownloader(current => current ? { ...current, cookies: status } : current)
      setCookieSuccess(status.youtubeRecordCount > 0
        ? `Cookies uploaded and validated (${status.youtubeRecordCount} relevant records).`
        : 'Cookies uploaded and validated.')
    } catch (e) {
      setCookieError((e as Error)?.message || 'Could not upload cookies.txt.')
    } finally {
      setCookieBusy(false)
      if (cookieInputRef.current) cookieInputRef.current.value = ''
    }
  }

  async function deleteCookies() {
    if (!downloader?.cookies.canDelete || cookieBusy ||
        !confirm('Delete the uploaded YouTube cookies? Restricted videos may stop working.')) return
    setCookieBusy(true)
    setCookieError('')
    setCookieSuccess('')
    try {
      const status = await youtubeCookiesApi.delete()
      setDownloader(current => current ? { ...current, cookies: status } : current)
      setCookieSuccess('Uploaded cookies deleted.')
    } catch (e) {
      setCookieError((e as Error)?.message || 'Could not delete the uploaded cookies.')
    } finally {
      setCookieBusy(false)
    }
  }

  // Loads the stored library source. Reused as a retry action after a failed
  // load, and re-run after a successful save so the URL reflects any
  // server-side normalisation (e.g. a trimmed trailing slash).
  async function loadLibrarySource() {
    try {
      const s = await radarrApi.get()
      setLibrarySource(s.source)
      setRadarrUrl(s.url)
      setRadarrConfigured(s.configured)
      setRadarrLoaded(true)
      setRadarrLoadError('')
    } catch (e) {
      setRadarrLoaded(false)
      setRadarrLoadError((e as Error)?.message || 'Failed to load the current library source.')
    }
  }

  async function loadApiKey() {
    try {
      const k = await apiKeyApi.get()
      setApiKey(k.key)
      setApiKeyLoaded(true)
      setApiKeyLoadError('')
    } catch (e) {
      setApiKeyLoaded(false)
      setApiKeyLoadError((e as Error)?.message || 'Failed to load the API key.')
    }
  }

  // Retry action for the supplementary version load above.
  async function loadVersion() {
    try {
      const v = await versionApi.get()
      setVersion(v)
      setVersionLoadError('')
    } catch (e) {
      setVersionLoadError((e as Error)?.message || 'Failed to load version info.')
    }
  }

  async function loadDownloader() {
    try {
      setDownloader(await downloaderApi.get())
      setDownloaderError('')
    } catch (e) {
      setDownloaderError((e as Error)?.message || 'Failed to check the local downloader.')
    }
  }

  async function loadShowSource() {
    try {
      const s = await sonarrApi.get()
      setShowSource(s.source)
      setSonarrUrl(s.url)
      setSonarrConfigured(s.configured)
      setSonarrLoaded(true)
      setSonarrLoadError('')
    } catch (e) {
      setSonarrLoaded(false)
      setSonarrLoadError((e as Error)?.message || 'Failed to load the current show source.')
    }
  }

  async function regenerateApiKey() {
    if (!confirm('Regenerate the API key? Any Radarr connection using the current key will stop working until you update it there.')) return
    setApiKeyRegenerating(true)
    setApiKeyError('')
    try {
      const k = await apiKeyApi.regenerate()
      setApiKey(k.key)
      setApiKeyRegenerated(true)
      setTimeout(() => setApiKeyRegenerated(false), 2000)
    } catch (e) {
      setApiKeyError(`Couldn't regenerate the API key: ${(e as Error).message}`)
    } finally {
      setApiKeyRegenerating(false)
    }
  }

  // Copies `text` to the clipboard when running in a secure context (HTTPS or
  // localhost). ThemeForge is normally reached over plain HTTP on a LAN, where
  // navigator.clipboard doesn't exist at all — so this always feature-detects
  // first rather than relying on a thrown error. When the clipboard API is
  // unavailable or the write itself fails, it selects the field's text and
  // falls back to document.execCommand('copy'). That API is deprecated but
  // still implemented by every current browser and, unlike the Clipboard API,
  // it works on insecure origins — so on a plain-HTTP LAN install it's the
  // path that actually copies. Only if it also fails do we ask the user to
  // copy manually, leaving the text selected so that instruction is
  // actionable.
  async function copyToClipboard(text: string, fieldRef: React.RefObject<HTMLDivElement | null>, setCopied: (v: boolean) => void) {
    setApiKeyError('')
    if (window.isSecureContext && navigator.clipboard) {
      try {
        await navigator.clipboard.writeText(text)
        setCopied(true)
        setTimeout(() => setCopied(false), 2000)
        return
      } catch {
        // Fall through to the manual-selection fallback below.
      }
    }
    const input = fieldRef.current?.querySelector('input')
    input?.focus()
    input?.select()
    let copiedViaExecCommand = false
    try {
      copiedViaExecCommand = document.execCommand('copy')
    } catch {
      copiedViaExecCommand = false
    }
    if (copiedViaExecCommand) {
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
      return
    }
    setApiKeyError('Clipboard access needs HTTPS. The text has been selected — press Ctrl/Cmd+C to copy it.')
  }

  async function copyApiKey() {
    await copyToClipboard(apiKey, keyFieldRef, setKeyCopied)
  }

  async function copyWebhookUrl() {
    await copyToClipboard(webhookUrl, webhookFieldRef, setWebhookCopied)
  }

  async function testRadarrConnection() {
    setRadarrTesting(true)
    setRadarrTestResult(null)
    setRadarrError('')
    try {
      const res = await radarrApi.test(radarrUrl.trim(), radarrApiKey.trim())
      setRadarrTestResult(res)
    } catch (e) {
      setRadarrError((e as Error).message)
    } finally {
      setRadarrTesting(false)
    }
  }

  async function saveLibrarySource() {
    setRadarrSaving(true)
    setRadarrError('')
    try {
      await radarrApi.save(librarySource, radarrUrl.trim(), radarrApiKey.trim())
      setRadarrApiKey('')
      setRadarrTestResult(null)
      // Re-read from the server rather than trusting the save response, since
      // the backend normalises the URL (e.g. trims a trailing slash) and that
      // isn't reflected in what save() returns.
      await loadLibrarySource()
      setRadarrSaved(true)
      setTimeout(() => setRadarrSaved(false), 2000)
    } catch (e) {
      setRadarrError((e as Error).message)
    } finally {
      setRadarrSaving(false)
    }
  }

  async function testSonarrConnection() {
    setSonarrTesting(true)
    setSonarrTestResult(null)
    setSonarrError('')
    try {
      setSonarrTestResult(await sonarrApi.test(sonarrUrl.trim(), sonarrApiKey.trim()))
    } catch (e) {
      setSonarrError((e as Error).message)
    } finally {
      setSonarrTesting(false)
    }
  }

  async function saveShowSource() {
    setSonarrSaving(true)
    setSonarrError('')
    try {
      await sonarrApi.save(showSource, sonarrUrl.trim(), sonarrApiKey.trim())
      setSonarrApiKey('')
      setSonarrTestResult(null)
      await loadShowSource()
      setSonarrSaved(true)
      setTimeout(() => setSonarrSaved(false), 2000)
    } catch (e) {
      setSonarrError((e as Error).message)
    } finally {
      setSonarrSaving(false)
    }
  }

  function closeUpdateModal() {
    if (updating) return
    setUpdateOpen(false)
    if (updateDone && !updateError) {
      // Refresh version info after successful update
      versionApi.get().then(setVersion).catch(() => null)
    }
  }


  async function resetSetup() {
    if (!confirm('Reset all settings and data? This cannot be undone.')) return
    try {
      await setupApi.reset()
      window.location.href = '/setup'
    } catch (e) {
      setError((e as Error).message)
    }
  }

  // Settings genuinely gates the page -- Library Source, API Key and
  // other settings sections sit behind it -- so a failure here is the one load on this
  // page that shows a full error screen with a retry, rather than a small
  // in-place notice.
  if (settings === null && settingsError) {
    return (
      <AppShell title="Settings">
        <EmptyState
          icon={<ErrorIcon />}
          title="Couldn&apos;t load settings"
          description={settingsError}
          action={<Button variant="secondary" size="sm" onClick={retrySettings}>Retry</Button>}
        />
      </AppShell>
    )
  }

  if (!settings) {
    return (
      <AppShell title="Settings">
        <div className="flex justify-center py-24">
          <Spinner size={28} className="text-[#BB0000]" />
        </div>
      </AppShell>
    )
  }

  const paths  = settings.libraryPaths.length ? settings.libraryPaths : ['']
  const setPaths = (fn: (p: string[]) => string[]) =>
    setSettings(s => s ? { ...s, libraryPaths: fn(s.libraryPaths.length ? s.libraryPaths : ['']) } : s)

  const radarrUrlMissing = librarySource === 'radarr' && !radarrUrl.trim()
  const webhookUrl = `${window.location.origin}/api/webhook/radarr`

  return (
    <AppShell title="Settings" actions={
      <Button onClick={save} loading={saving} size="sm">
        {saved ? 'Saved ✓' : 'Save changes'}
      </Button>
    }>
      <div className="max-w-2xl space-y-6">

        {error && (
          <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
            <p className="text-sm text-[#FDA29B]">{error}</p>
          </div>
        )}

        <ArrInstancesSection />

        {/* Plex connection */}
        <Section title="Plex Connection" hint="Override a server's URL if Plex's own address for it doesn't work (e.g. behind a reverse proxy or on a different LAN path).">
          <div className="space-y-3">
            {settings.selectedServers.map(srv => (
              <div key={srv.id} className="space-y-3 rounded-lg border border-[#1D2939] px-4 py-3">
                <p className="text-sm font-medium text-[#F9FAFB]">{srv.name}</p>
                <Input
                  label="Server URL"
                  placeholder="http://192.168.1.50:32400"
                  value={plexUrls[srv.id] ?? srv.url}
                  onChange={e => { setPlexUrls(p => ({ ...p, [srv.id]: e.target.value })); setPlexTest(null) }}
                  className="font-mono text-xs"
                />
                {plexTest && (
                  <div className={`rounded-lg border px-3.5 py-2.5 text-sm ${
                    plexTest.ok
                      ? 'border-[#12B76A]/30 bg-[#12B76A]/5 text-[#D0D5DD]'
                      : 'border-[#B42318]/30 bg-[#FEF3F2]/5 text-[#FDA29B]'
                  }`}>
                    {plexTest.detail}
                  </div>
                )}
                <div className="flex gap-2">
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => testPlexUrl(srv.id)}
                    loading={plexTesting}
                    disabled={!(plexUrls[srv.id] ?? srv.url).trim()}
                  >
                    Test connection
                  </Button>
                  <Button
                    size="sm"
                    onClick={() => savePlexUrl(srv.id)}
                    loading={plexSaving}
                    disabled={!(plexUrls[srv.id] ?? srv.url).trim()}
                  >
                    {plexSaved ? 'Saved ✓' : 'Save'}
                  </Button>
                </div>
                {plexError && <p className="text-xs text-[#FDA29B]">{plexError}</p>}
              </div>
            ))}
            {settings.selectedServers.length === 0 && (
              <p className="text-sm text-[#667085]">No server connected.</p>
            )}
          </div>
        </Section>

        {/* Show libraries — opt-in, and separate from the movie library selection */}
        {showSource === 'plex' && <Section title="Plex TV Show Libraries" hint="ThemeForge only looks for show themes in the Plex libraries you pick here. TV support remains opt-in.">
          <div className="space-y-3">
            {showLibsLoading && <div className="flex items-center gap-2 text-sm text-[#98A2B3]"><Spinner size={14} /> Discovering Plex TV libraries…</div>}

            {!showLibsLoading && Object.entries(plexLibraries).flatMap(([serverId, libs]) =>
              libs.filter(l => l.type === 'show').map(l => (
                <label key={`${serverId}:${l.key}`} className="flex items-center gap-2 text-sm text-[#D0D5DD]">
                  <input
                    type="checkbox"
                    checked={(showLibs[serverId] ?? []).includes(l.key)}
                    onChange={() => toggleShowLib(serverId, l.key)}
                  />
                  {l.title}
                </label>
              )))}

            {!showLibsLoading && !showLibsError && settings.selectedServers.length === 0 && (
              <p className="text-sm text-[#667085]">No Plex server is selected.</p>
            )}

            {!showLibsLoading && !showLibsError && settings.selectedServers.length > 0 &&
             Object.values(plexLibraries).every(libs => !libs.some(l => l.type === 'show')) && (
              <p className="text-sm text-[#667085]">Plex returned zero TV show libraries.</p>
            )}

            <div className="flex items-center gap-3">
              <Button size="sm" onClick={saveShowLibraries} loading={savingShowLibs}>
                Save show libraries
              </Button>
              {showLibsSaved && <p className="text-xs text-[#12B76A]">Saved ✓</p>}
            </div>
            {showLibsError && (
              <div className="flex flex-wrap items-center gap-3">
                <p className="text-xs text-[#FDA29B]">Plex TV-library discovery failed: {showLibsError}</p>
                <Button variant="secondary" size="sm" onClick={() => loadShowLibraries(settings.selectedServers)}>Retry</Button>
              </div>
            )}
            <p className="text-xs text-[#98A2B3]">Themes are written as <code>theme.mp3</code> into the series root. The TV mount must be writable.</p>
          </div>
        </Section>}

        {/* Independent movie source */}
        <Section title="Movie Source" hint="Choose where ThemeForge reads movies from. This does not change the TV-show source.">
          {radarrLoadError && (
            <div className="flex items-center justify-between gap-3 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
              <p className="text-sm text-[#FDA29B]">Couldn&apos;t load the current library source: {radarrLoadError}</p>
              <Button variant="secondary" size="sm" onClick={loadLibrarySource}>Retry</Button>
            </div>
          )}

          <div className="flex flex-wrap gap-2">
            {MOVIE_SOURCE_OPTIONS.map(opt => (
              <button
                key={opt.value}
                onClick={() => { setLibrarySource(opt.value); setRadarrTestResult(null) }}
                className={`flex-1 rounded-lg border px-4 py-2.5 text-sm font-medium transition-colors ${
                  librarySource === opt.value
                    ? 'border-[#BB0000] bg-[#BB0000]/10 text-[#F9FAFB]'
                    : 'border-[#344054] text-[#98A2B3] hover:border-[#475467]'
                }`}
              >
                {opt.label}
              </button>
            ))}
          </div>

          {librarySource === 'radarr' && (
            <div className="space-y-3">
              <Input
                label="Radarr URL"
                placeholder="http://localhost:7878"
                value={radarrUrl}
                onChange={e => { setRadarrUrl(e.target.value); setRadarrTestResult(null) }}
              />
              <Input
                label="API key"
                type="password"
                placeholder={radarrConfigured ? 'Leave blank to keep the current key' : 'Radarr API key…'}
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
              <div className="flex gap-2">
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={testRadarrConnection}
                  loading={radarrTesting}
                  disabled={radarrUrlMissing}
                >
                  Test connection
                </Button>
                <Button size="sm" onClick={saveLibrarySource} loading={radarrSaving} disabled={radarrUrlMissing || !radarrLoaded}>
                  {radarrSaved ? 'Saved ✓' : 'Save'}
                </Button>
              </div>
              {radarrError && <p className="text-xs text-[#FDA29B]">{radarrError}</p>}
            </div>
          )}

          {librarySource === 'plex' && (
            <div className="space-y-3">
              <Button size="sm" onClick={saveLibrarySource} loading={radarrSaving} disabled={!radarrLoaded}>
                {radarrSaved ? 'Saved ✓' : 'Save'}
              </Button>
              {radarrError && <p className="text-xs text-[#FDA29B]">{radarrError}</p>}
            </div>
          )}

          {librarySource === 'disabled' && (
            <div className="space-y-3">
              <p className="text-sm text-[#98A2B3]">Movie sync is disabled. Existing movie records and themes are kept.</p>
              <Button size="sm" onClick={saveLibrarySource} loading={radarrSaving} disabled={!radarrLoaded}>
                {radarrSaved ? 'Saved ✓' : 'Save'}
              </Button>
              {radarrError && <p className="text-xs text-[#FDA29B]">{radarrError}</p>}
            </div>
          )}
        </Section>

        {/* Independent TV-show source */}
        <Section title="Show Source" hint="Choose where ThemeForge reads TV series from. This does not change the movie source.">
          {sonarrLoadError && (
            <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
              <p className="text-sm text-[#FDA29B]">Couldn&apos;t load the current show source: {sonarrLoadError}</p>
              <Button variant="secondary" size="sm" onClick={loadShowSource}>Retry</Button>
            </div>
          )}

          <div className="flex flex-wrap gap-2">
            {SHOW_SOURCE_OPTIONS.map(option => (
              <button key={option.value}
                onClick={() => { setShowSource(option.value); setSonarrTestResult(null); setSonarrError('') }}
                className={`min-w-[7rem] flex-1 rounded-lg border px-4 py-2.5 text-sm font-medium transition-colors ${
                  showSource === option.value
                    ? 'border-[#BB0000] bg-[#BB0000]/10 text-[#F9FAFB]'
                    : 'border-[#344054] text-[#98A2B3] hover:border-[#475467]'}`}>
                {option.label}
              </button>
            ))}
          </div>

          {showSource === 'plex' && (
            <div className="space-y-3">
              <p className="text-sm text-[#98A2B3]">
                Uses the connected Plex server and the TV-library checkboxes above. Plex shows without a local theme are eligible only when Plex does not already provide one.
              </p>
              <Button size="sm" onClick={saveShowSource} loading={sonarrSaving} disabled={!sonarrLoaded}>
                {sonarrSaved ? 'Saved ✓' : 'Save'}
              </Button>
              {sonarrError && <p className="text-xs text-[#FDA29B]">{sonarrError}</p>}
            </div>
          )}

          {showSource === 'sonarr' && (
            <div className="space-y-3">
              <Input label="Sonarr URL" placeholder="http://localhost:8989" value={sonarrUrl}
                onChange={e => { setSonarrUrl(e.target.value); setSonarrTestResult(null) }} />
              <Input label="API key" type="password"
                placeholder={sonarrConfigured ? 'Leave blank to keep the current key' : 'Sonarr API key…'}
                value={sonarrApiKey}
                onChange={e => { setSonarrApiKey(e.target.value); setSonarrTestResult(null) }}
                className="font-mono text-xs" />
              <p className="text-xs text-[#98A2B3]">
                {sonarrConfigured ? 'Configured. The stored key is write-only and is never loaded into this page.' : 'Not configured.'}
                {' '}If Sonarr reports paths such as <code>/tv/Show</code> while ThemeForge sees another mount, add a Path Mapping below.
              </p>
              {sonarrTestResult && (
                <div className={`rounded-lg border px-3.5 py-2.5 text-sm ${sonarrTestResult.ok
                  ? 'border-[#12B76A]/30 bg-[#12B76A]/5 text-[#D0D5DD]'
                  : 'border-[#B42318]/30 bg-[#FEF3F2]/5 text-[#FDA29B]'}`}>
                  {sonarrTestResult.detail}
                </div>
              )}
              <div className="flex flex-wrap gap-2">
                <Button variant="secondary" size="sm" onClick={testSonarrConnection}
                  loading={sonarrTesting} disabled={!sonarrUrl.trim()}>Test connection</Button>
                <Button size="sm" onClick={saveShowSource} loading={sonarrSaving}
                  disabled={!sonarrUrl.trim() || !sonarrLoaded}>{sonarrSaved ? 'Saved ✓' : 'Save'}</Button>
              </div>
              {sonarrError && <p className="text-xs text-[#FDA29B]">{sonarrError}</p>}
            </div>
          )}

          {showSource === 'disabled' && (
            <div className="space-y-3">
              <p className="text-sm text-[#98A2B3]">Show sync and show auto-download are disabled. Existing show records and theme files are not deleted.</p>
              <Button size="sm" onClick={saveShowSource} loading={sonarrSaving} disabled={!sonarrLoaded}>
                {sonarrSaved ? 'Saved ✓' : 'Save'}
              </Button>
              {sonarrError && <p className="text-xs text-[#FDA29B]">{sonarrError}</p>}
            </div>
          )}
        </Section>

        {/* API key */}
        <Section title="API Key" hint="Used by Radarr and scripts to authenticate with ThemeForge. This is not the access token you sign in with.">
          {apiKeyLoadError && (
            <div className="flex items-center justify-between gap-3 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
              <p className="text-sm text-[#FDA29B]">Couldn&apos;t load the API key: {apiKeyLoadError}</p>
              <Button variant="secondary" size="sm" onClick={loadApiKey}>Retry</Button>
            </div>
          )}

          {!apiKeyLoaded && !apiKeyLoadError && (
            <div className="flex items-center gap-2 text-sm text-[#475467]"><Spinner size={13} className="text-[#BB0000]" /> Loading…</div>
          )}

          {apiKeyLoaded && (
            <div className="space-y-3">
              <div className="flex gap-2 items-end">
                <div ref={keyFieldRef} className="flex-1">
                  <Input label="Key" readOnly value={apiKey} className="flex-1 font-mono text-xs" />
                </div>
                <Button variant="secondary" size="sm" onClick={copyApiKey}>{keyCopied ? 'Copied ✓' : 'Copy'}</Button>
              </div>
              <div className="flex gap-2 items-end">
                <div ref={webhookFieldRef} className="flex-1">
                  <Input label="Radarr webhook URL" readOnly value={webhookUrl} className="flex-1 font-mono text-xs" />
                </div>
                <Button variant="secondary" size="sm" onClick={copyWebhookUrl}>{webhookCopied ? 'Copied ✓' : 'Copy'}</Button>
              </div>
              <Button variant="danger" size="sm" onClick={regenerateApiKey} loading={apiKeyRegenerating}>
                {apiKeyRegenerated ? 'Regenerated ✓' : 'Regenerate'}
              </Button>
              {apiKeyError && <p className="text-xs text-[#FDA29B]">{apiKeyError}</p>}
            </div>
          )}
        </Section>

        {/* Library paths */}
        <Section title="Local Library Paths" hint="Writable directories as ThemeForge sees them inside this container (for example /movies).">
          <div className="space-y-2">
            {paths.map((p, i) => (
              <div key={i} className="flex gap-2">
                <Input
                  placeholder="/mnt/movies"
                  value={p}
                  onChange={e => setPaths(prev => { const n = [...prev]; n[i] = e.target.value; return n })}
                  className="flex-1"
                />
                <button
                  onClick={() => setPaths(prev => prev.filter((_, j) => j !== i))}
                  className="px-2 text-[#667085] hover:text-[#FDA29B] transition-colors"
                  aria-label="Remove"
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M18 6 6 18M6 6l12 12" /></svg>
                </button>
              </div>
            ))}
            <Button variant="ghost" size="sm" onClick={() => setPaths(p => [...p, ''])}>
              + Add path
            </Button>
          </div>
        </Section>

        {/* Path mappings */}
        <Section
          title="Path Mappings"
          hint="Map Plex, Radarr, or Sonarr paths to the writable paths mounted inside the ThemeForge container."
        >
          <div className="space-y-4">
            <div className="rounded-lg border border-[#1D2939] bg-[#0C111D] p-3 text-xs text-[#98A2B3]">
              <p><span className="text-[#D0D5DD]">Plex/Radarr/Sonarr source path:</span> <code>/mnt/media/TV/Show</code></p>
              <p><span className="text-[#D0D5DD]">ThemeForge container path:</span> <code>/movies</code></p>
              <p><span className="text-[#D0D5DD]">Docker mount:</span> <code>/mnt/plex/Movies:/movies</code></p>
              <p className="mt-2">ThemeForge stores and writes only the right-side container path. Run a full sync or path repair after changing mappings.</p>
            </div>
            <div className="space-y-2">
            {settings.pathMappings.map((m, i) => {
              const scope = m.instanceId ? `instance:${m.instanceId}` : m.serviceType ? `service:${m.serviceType}` : 'global'
              return (
              <div key={i} className="grid gap-2 sm:grid-cols-[10rem_1fr_auto_1fr_auto] sm:items-center">
                <select aria-label={`Mapping ${i + 1} scope`} value={scope} onChange={e => setSettings(s => s ? {
                  ...s, pathMappings: s.pathMappings.map((pm, j) => {
                    if (j !== i) return pm
                    if (e.target.value === 'global') return { source: pm.source, target: pm.target }
                    const [kind, value] = e.target.value.split(':', 2)
                    return kind === 'instance' ? { source: pm.source, target: pm.target, instanceId: value }
                      : { source: pm.source, target: pm.target, serviceType: value as 'radarr' | 'sonarr' }
                  }),
                } : s)} className="rounded-lg border border-[#344054] bg-[#101828] px-2 py-2 text-xs text-[#D0D5DD]">
                  <option value="global">Global</option>
                  <option value="service:radarr">All Radarr</option><option value="service:sonarr">All Sonarr</option>
                  {arrMappingInstances.map(instance => <option key={instance.id} value={`instance:${instance.id}`}>{instance.name}</option>)}
                </select>
                <Input placeholder="/remote/movies" value={m.source}
                  onChange={e => setSettings(s => s ? { ...s, pathMappings: s.pathMappings.map((pm, j) => j === i ? { ...pm, source: e.target.value } : pm) } : s)}
                  className="flex-1" />
                <span className="text-[#475467] flex-shrink-0">→</span>
                <Input placeholder="/local/movies" value={m.target}
                  onChange={e => setSettings(s => s ? { ...s, pathMappings: s.pathMappings.map((pm, j) => j === i ? { ...pm, target: e.target.value } : pm) } : s)}
                  className="flex-1" />
                <button
                  onClick={() => setSettings(s => s ? { ...s, pathMappings: s.pathMappings.filter((_, j) => j !== i) } : s)}
                  className="px-2 text-[#667085] hover:text-[#FDA29B] transition-colors" aria-label="Remove">
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M18 6 6 18M6 6l12 12" /></svg>
                </button>
              </div>
            )})}
            <Button variant="ghost" size="sm" onClick={() => setSettings(s => s ? { ...s, pathMappings: [...s.pathMappings, { source: '', target: '' }] } : s)}>
              + Add mapping
            </Button>
            </div>
            <div className="border-t border-[#1D2939] pt-4 space-y-2">
              <Input
                label="Representative source media path"
                placeholder={showSource === 'sonarr'
                  ? '/tv/Show Name'
                  : librarySource === 'radarr'
                    ? '/movies/Movie Name'
                    : '/mnt/plex/Movies/Movie/Movie.mkv'}
                value={mappingSample}
                onChange={e => { setMappingSample(e.target.value); setMappingTest(null); setMappingTestError('') }}
                className="font-mono text-xs"
              />
              <Button variant="secondary" size="sm" onClick={testPathMapping}
                loading={mappingTesting} disabled={!mappingSample.trim()}>
                Test mapping
              </Button>
              {mappingTestError && <p className="text-xs text-[#FDA29B]">{mappingTestError}</p>}
              {mappingTest && (
                <div className={`rounded-lg border p-3 text-xs ${mappingTest.resolvedFolderPath ? 'border-[#12B76A]/30' : 'border-[#B42318]/40'}`}>
                  <p>Source folder: <code>{mappingTest.sourceFolderPath || '(none)'}</code></p>
                  <p>Matched mapping: <code>{mappingTest.matchedMapping ? `${mappingTest.matchedMapping.source} → ${mappingTest.matchedMapping.target}` : '(none)'}</code></p>
                  <p>Mapped candidate: <code>{mappingTest.mappedCandidate || '(none)'}</code> ({mappingTest.candidateExists ? 'exists' : 'missing'}, {mappingTest.candidateWithinRoots ? 'inside root' : 'outside root'})</p>
                  <p>Resolution: <strong>{mappingTest.resolutionMode}</strong> → <code>{mappingTest.resolvedFolderPath || '(unresolved)'}</code></p>
                  {mappingTest.failureReason && <p className="mt-1 text-[#FDA29B]">{mappingTest.failureReason}</p>}
                </div>
              )}
            </div>
          </div>
        </Section>

        {/* Queue behaviour */}
        <Section title="Queue">
          <div className="space-y-4">
            <ToggleRow
              label="Auto-download mode"
              hint="Automatically download the best match for each movie without confirmation."
              checked={settings.autoDownload}
              onChange={() => setSettings(s => s ? { ...s, autoDownload: !s.autoDownload } : s)}
            />
            <div className="border-t border-[#1D2939]" />
            <ToggleRow
              label="Auto-sync enabled sources"
              hint={`Movies and shows follow their independent source cadence (Radarr/Sonarr every 15 minutes; Plex daily).${settings.lastAutoSyncAt ? ` Last movie sync: ${formatUnix(settings.lastAutoSyncAt)}` : ''}`}
              checked={settings.autoSync}
              onChange={() => setSettings(s => s ? { ...s, autoSync: !s.autoSync } : s)}
            />
          </div>
        </Section>

        {/* Local YouTube downloader */}
        <Section title="Local YouTube Downloader" hint="Downloads and converts audio locally with yt-dlp and FFmpeg. No hosted converter account is required.">
          {downloader ? (
            <div className="space-y-4">
              <div className={`rounded-lg border px-3.5 py-3 ${downloader.ready ? 'border-[#12B76A]/30 bg-[#12B76A]/5' : 'border-[#B42318]/40 bg-[#FEF3F2]/5'}`}>
                <p className="text-sm font-medium text-[#D0D5DD]">{downloader.ready ? (downloader.degraded ? 'Ready with warnings' : 'Ready') : 'Not ready'}</p>
                <p className="mt-1 text-xs text-[#98A2B3]">{downloader.summary}</p>
              </div>

              <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                <DownloaderStatus label="yt-dlp" available={downloader.ytDlp.available} version={downloader.ytDlp.version} />
                <DownloaderStatus label="FFmpeg" available={downloader.ffmpeg.available} version={downloader.ffmpeg.version} />
                <DownloaderStatus label="FFprobe" available={downloader.ffprobe.available} version={downloader.ffprobe.version} />
                <DownloaderStatus label="JavaScript runtime" available={downloader.javaScriptRuntime.available} version={downloader.javaScriptRuntime.version} optional />
                <DownloaderStatus
                  label="PO-token provider"
                  available={downloader.poTokenProvider.status === 'ready' || downloader.poTokenProvider.status === 'disabled' || downloader.poTokenProvider.status === 'notConfigured'}
                  version={downloader.poTokenProvider.status === 'ready'
                    ? `Ready${downloader.poTokenProvider.version ? ` · ${downloader.poTokenProvider.version}` : ''}`
                    : downloader.poTokenProvider.status === 'disabled' ? 'Disabled'
                    : downloader.poTokenProvider.status === 'notConfigured' ? 'Not configured'
                    : downloader.poTokenProvider.status === 'requiredUnavailable' ? 'Required but unavailable' : 'Unavailable'}
                  optional={downloader.poTokenProvider.mode !== 'required'} />
              </div>

              <div className="space-y-3 rounded-lg border border-[#1D2939] bg-[#0C111D] px-3.5 py-3">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div>
                    <div className="flex flex-wrap items-center gap-2">
                      <p className="text-sm font-medium text-[#D0D5DD]">Cookies</p>
                      {downloader.cookies.managedByEnvironment && (
                        <span className="rounded-full bg-[#344054] px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-[#D0D5DD]">Read only</span>
                      )}
                    </div>
                    <p className={`mt-1 text-xs ${downloader.cookies.configured && !downloader.cookies.valid ? 'text-[#FDA29B]' : 'text-[#98A2B3]'}`}>
                      {!downloader.cookies.configured ? 'Not configured'
                        : downloader.cookies.source === 'environment' ? 'Configured by environment'
                        : downloader.cookies.valid ? 'Configured — uploaded file' : 'Uploaded file is invalid'}
                    </p>
                    {downloader.cookies.source === 'managed' && downloader.cookies.uploadedAtUtc && (
                      <p className="mt-1 text-xs text-[#667085]">
                        Uploaded {new Date(downloader.cookies.uploadedAtUtc).toLocaleString()} · {downloader.cookies.recordCount} validated records
                      </p>
                    )}
                    {downloader.cookies.managedByEnvironment && (
                      <p className="mt-1 text-xs text-[#667085]">YTDLP_COOKIES_FILE controls this credential file. Upload, replacement, and deletion are disabled.</p>
                    )}
                    {downloader.cookies.detail && <p className="mt-1 text-xs text-[#FDA29B]">{downloader.cookies.detail}</p>}
                  </div>
                  {downloader.cookies.canUpload && (
                    <div className="flex flex-wrap gap-2">
                      <input
                        ref={cookieInputRef}
                        className="sr-only"
                        type="file"
                        accept=".txt,text/plain"
                        aria-label="Choose YouTube cookies.txt"
                        onChange={event => uploadCookies(event.target.files?.[0])} />
                      <Button variant="secondary" size="sm" loading={cookieBusy}
                        onClick={() => cookieInputRef.current?.click()}>
                        {downloader.cookies.configured ? 'Replace cookies.txt' : 'Upload cookies.txt'}
                      </Button>
                      {downloader.cookies.canDelete && (
                        <Button variant="secondary" size="sm" disabled={cookieBusy} onClick={deleteCookies}>Delete cookies</Button>
                      )}
                    </div>
                  )}
                </div>
                <p className="text-xs text-[#667085]">Optional. Used for age-restricted, account-required, or YouTube bot-check responses.</p>
                <p className="text-xs text-[#FEC84B]">Cookie files grant access to the associated YouTube session. Keep them private and consider using a dedicated account.</p>
                {cookieSuccess && <p role="status" className="text-xs text-[#6CE9A6]">{cookieSuccess}</p>}
                {cookieError && <p role="alert" className="text-xs text-[#FDA29B]">{cookieError}</p>}
              </div>

              <p className="text-xs text-[#667085]">PO tokens and cookies solve different YouTube checks. The provider automatically supplies short-lived playback tokens; it does not replace account cookies.</p>

              <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
                <label className="flex flex-col gap-1.5 text-sm font-medium text-[#D0D5DD]">
                  Audio quality
                  <select
                    aria-label="Audio quality"
                    value={downloader.audioQuality}
                    disabled={downloader.audioQualityManagedByEnvironment}
                    onChange={e => setDownloader(d => d ? { ...d, audioQuality: e.target.value as DownloaderDiagnostics['audioQuality'] } : d)}
                    className="rounded-lg border border-[#344054] bg-[#101828] px-3.5 py-2.5 text-sm text-[#F9FAFB] outline-none focus:border-[#BB0000] disabled:cursor-not-allowed disabled:opacity-50">
                    {['128K', '192K', '256K', '320K'].map(q => <option key={q}>{q}</option>)}
                  </select>
                  {downloader.audioQualityManagedByEnvironment && <span className="text-xs font-normal text-[#667085]">Managed by environment</span>}
                </label>
                <Input label="Timeout (seconds)" type="number" min={30} max={1800}
                  value={downloader.timeoutSeconds} disabled={downloader.timeoutManagedByEnvironment}
                  error={downloader.timeoutSeconds < 30 || downloader.timeoutSeconds > 1800 ? 'Use 30–1800 seconds.' : undefined}
                  hint={downloader.timeoutManagedByEnvironment ? 'Managed by environment' : undefined}
                  onChange={e => setDownloader(d => d ? { ...d, timeoutSeconds: Number(e.target.value) } : d)} />
                <Input label="Concurrent downloads" type="number" min={1} max={3}
                  value={downloader.concurrentDownloads} disabled={downloader.concurrencyManagedByEnvironment}
                  error={downloader.concurrentDownloads < 1 || downloader.concurrentDownloads > 3 ? 'Use 1–3 downloads.' : undefined}
                  hint={downloader.concurrencyManagedByEnvironment ? 'Managed by environment' : undefined}
                  onChange={e => setDownloader(d => d ? { ...d, concurrentDownloads: Number(e.target.value) } : d)} />
              </div>

              <div className="flex flex-wrap items-center gap-2">
                <Button onClick={saveDownloader} loading={downloaderSaving} size="sm"
                  disabled={downloader.timeoutSeconds < 30 || downloader.timeoutSeconds > 1800 || downloader.concurrentDownloads < 1 || downloader.concurrentDownloads > 3}>Save settings</Button>
                <Button variant="secondary" onClick={testDownloader} loading={downloaderTesting} disabled={downloaderTesting} size="sm">Test downloader</Button>
              </div>
              {downloaderResult && (
                <div className={`space-y-1 text-xs ${downloaderResult.ok ? 'text-[#6CE9A6]' : 'text-[#FDA29B]'}`}>
                  <p>{downloaderResult.detail}</p>
                  <p>Cookies: {downloader.cookies.configured ? (downloader.cookies.valid ? `${downloader.cookies.source} · valid` : `${downloader.cookies.source} · invalid`) : 'not configured'}</p>
                  <p>PO-token provider: {downloader.poTokenProvider.status}</p>
                </div>
              )}

              <div className="rounded-lg border border-[#1D2939] bg-[#0C111D] px-3.5 py-3 text-xs text-[#667085]">
                Container images include yt-dlp, the pinned PO-token plugin, FFmpeg, FFprobe, and Deno. Environment-managed cookies remain available through <span className="font-mono text-[#98A2B3]">YTDLP_COOKIES_FILE</span>.
              </div>
            </div>
          ) : downloaderError ? (
            <div className="flex items-center justify-between gap-3 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-3.5 py-2.5">
              <p className="text-sm text-[#FDA29B]">{downloaderError}</p>
              <Button variant="secondary" size="sm" onClick={loadDownloader}>Retry</Button>
            </div>
          ) : (
            <div className="flex items-center gap-2 text-sm text-[#475467]"><Spinner size={13} className="text-[#BB0000]" /> Checking…</div>
          )}
          {downloader && downloaderError && <p className="text-xs text-[#FDA29B]">{downloaderError}</p>}
        </Section>

        {/* Advanced */}
        <Section title="Advanced">
          <div className="grid grid-cols-2 gap-4">
            <Input
              label="Max search directories"
              type="number"
              value={settings.advanced.maxSearchDirs}
              onChange={e => setSettings(s => s ? { ...s, advanced: { ...s.advanced, maxSearchDirs: +e.target.value } } : s)}
            />
            <Input
              label="Search depth"
              type="number"
              value={settings.advanced.searchDepth}
              onChange={e => setSettings(s => s ? { ...s, advanced: { ...s.advanced, searchDepth: +e.target.value } } : s)}
            />
          </div>
        </Section>

        {/* About / version / update */}
        <Section title={`About ${APP_BRAND.name}`}>
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center">
            <img src={brandAsset('themeforge-icon.svg')} alt={`${APP_BRAND.name} icon`} width={64} height={64} className="h-16 w-16 flex-none" />
            <div className="min-w-0">
              <img src={brandAsset('themeforge-logo.svg')} alt={APP_BRAND.name} width={220} height={44} className="h-10 max-w-full object-contain object-left" />
              <p className="mt-1 text-sm text-[#D0D5DD]">{APP_BRAND.tagline}</p>
              <p className="mt-1 max-w-2xl text-xs leading-relaxed text-[#667085]">{APP_BRAND.description}</p>
            </div>
          </div>
        </Section>

        {/* Version / update */}
        {version && (
          <Section title="Updates">
            <div className="flex items-center justify-between">
              <div className="space-y-0.5">
                <p className="text-sm text-[#D0D5DD]">
                  Current: <span className="font-mono text-[#F9FAFB]">{version.current}</span>
                </p>
                {version.latest && (
                  <p className="text-sm text-[#667085]">
                    Latest: <span className="font-mono">{version.latest}</span>
                    {version.updateAvailable && (
                      <span className="ml-2 text-[#FEC84B]">● Update available</span>
                    )}
                  </p>
                )}
              </div>
              <div className="flex items-center gap-2">
                <Button variant="ghost" size="sm" onClick={checkForUpdates} loading={checking}>
                  Check for updates
                </Button>
                {version.updateAvailable && (
                  <Button onClick={startUpdate} size="sm">
                    Update now
                  </Button>
                )}
              </div>
            </div>
            {checkUpdatesError && <p className="text-xs text-[#FDA29B]">{checkUpdatesError}</p>}
          </Section>
        )}
        {!version && versionLoadError && (
          // Supplementary: the version check failing shouldn't strand the
          // rest of Settings, so this is just a small note rather than an
          // error screen -- and it doesn't reuse "Check for updates" (that
          // action belongs to a working version load, not a failed one).
          <Section title="Updates">
            <div className="flex items-center justify-between gap-3">
              <p className="text-sm text-[#667085]">Couldn&apos;t check the current version: {versionLoadError}</p>
              <Button variant="secondary" size="sm" onClick={loadVersion}>Retry</Button>
            </div>
          </Section>
        )}

        {/* Update modal */}
        {updateOpen && (
          <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
            <div className="absolute inset-0 bg-black/60 backdrop-blur-sm" onClick={closeUpdateModal} />
            <div className="relative w-full max-w-lg rounded-xl border border-[#1D2939] bg-[#101828] shadow-2xl">
              {/* Header */}
              <div className="flex items-center justify-between border-b border-[#1D2939] px-5 py-4">
                <div className="flex items-center gap-2.5">
                  {updating && <Spinner size={16} className="text-[#BB0000]" />}
                  {updateDone && !updateError && (
                    <div className="flex h-5 w-5 items-center justify-center rounded-full bg-[#12B76A]">
                      <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round">
                        <path d="M2 6l3 3 5-5" />
                      </svg>
                    </div>
                  )}
                  {updateDone && updateError && (
                    <div className="flex h-5 w-5 items-center justify-center rounded-full bg-[#F04438]">
                      <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round">
                        <path d="M3 3l6 6M9 3l-6 6" />
                      </svg>
                    </div>
                  )}
                  <h2 className="text-sm font-semibold text-[#F9FAFB]">
                    {updating ? `Updating ${APP_BRAND.name}…` : updateError ? 'Update failed' : 'Update complete'}
                  </h2>
                </div>
                <button
                  onClick={closeUpdateModal}
                  disabled={updating}
                  className="text-[#667085] hover:text-[#D0D5DD] transition-colors disabled:opacity-30"
                  aria-label="Close"
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                    <path d="M18 6 6 18M6 6l12 12" />
                  </svg>
                </button>
              </div>

              {/* Log output */}
              <div className="h-72 overflow-y-auto bg-[#0C111D] px-4 py-3">
                {updateLogs.length === 0 && updating && (
                  <p className="font-mono text-xs text-[#475467]">Starting update…</p>
                )}
                {updateLogs.map((line, i) => (
                  <p key={i} className="font-mono text-xs leading-relaxed text-[#667085] whitespace-pre-wrap">{line}</p>
                ))}
                {updateDone && !updateError && (
                  <p className="mt-1 font-mono text-xs text-[#12B76A]">✓ Update applied successfully. The service will restart shortly.</p>
                )}
                {updateError && (
                  <p className="mt-1 font-mono text-xs text-[#FDA29B]">✗ {updateError}</p>
                )}
                <div ref={logEndRef} />
              </div>

              {/* Footer */}
              <div className="flex justify-end border-t border-[#1D2939] px-5 py-3">
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={closeUpdateModal}
                  disabled={updating}
                >
                  {updating ? 'Please wait…' : 'Close'}
                </Button>
              </div>
            </div>
          </div>
        )}

        {/* Danger zone */}
        <Section title="Danger zone">
          <div className="flex items-center justify-between rounded-lg border border-[#B42318]/30 px-4 py-3">
            <div>
              <p className="text-sm font-medium text-[#F9FAFB]">Reset {APP_BRAND.name}</p>
              <p className="text-xs text-[#667085]">Wipes all settings and movie data</p>
            </div>
            <Button variant="danger" size="sm" onClick={resetSetup}>Reset</Button>
          </div>
        </Section>
      </div>
    </AppShell>
  )
}

function ToggleRow({ label, hint, checked, onChange }: {
  label: string; hint?: string; checked: boolean; onChange: () => void
}) {
  return (
    <div className="flex items-center justify-between gap-4">
      <div className="space-y-0.5">
        <p className="text-sm font-medium text-[#F9FAFB]">{label}</p>
        {hint && <p className="text-xs text-[#667085]">{hint}</p>}
      </div>
      <button
        role="switch"
        aria-checked={checked}
        onClick={onChange}
        className={`relative inline-flex h-6 w-11 flex-shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors focus:outline-none ${checked ? 'bg-[#BB0000]' : 'bg-[#344054]'}`}
      >
        <span className={`pointer-events-none inline-block h-5 w-5 transform rounded-full bg-white shadow transition-transform ${checked ? 'translate-x-5' : 'translate-x-0'}`} />
      </button>
    </div>
  )
}

function formatUnix(unix: string): string {
  try {
    const d = new Date(parseInt(unix, 10) * 1000)
    return d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  } catch { return '' }
}

function DownloaderStatus({ label, available, version, optional = false }: {
  label: string; available: boolean; version: string | null; optional?: boolean
}) {
  return (
    <div className="flex items-center justify-between gap-3 rounded-lg border border-[#1D2939] bg-[#0C111D] px-3.5 py-2.5">
      <span className="text-xs text-[#98A2B3]">{label}{optional ? ' (optional)' : ''}</span>
      <span className={`text-xs font-medium ${available ? 'text-[#6CE9A6]' : optional ? 'text-[#FEC84B]' : 'text-[#FDA29B]'}`}>
        {version || (available ? 'Available' : 'Unavailable')}
      </span>
    </div>
  )
}

function Section({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-5 space-y-4">
      <div>
        <h2 className="text-sm font-semibold text-[#F9FAFB]">{title}</h2>
        {hint && <p className="mt-0.5 text-xs text-[#667085]">{hint}</p>}
      </div>
      {children}
    </div>
  )
}
