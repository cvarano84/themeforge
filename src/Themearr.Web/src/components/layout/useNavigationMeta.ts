import { useEffect, useState } from 'react'
import { syncApi, systemApi, versionApi } from '@/lib/api'
import type { VersionInfo } from '@/lib/types'

export interface NavigationMeta {
  version: VersionInfo | null
  syncing: boolean
  healthIssues: number
}

export function useNavigationMeta(enabled = true): NavigationMeta {
  const [version, setVersion] = useState<VersionInfo | null>(null)
  const [syncing, setSyncing] = useState(false)
  const [healthIssues, setHealthIssues] = useState(0)

  useEffect(() => { if (enabled) Promise.resolve(versionApi.get()).then(value => { if (value) setVersion(value) }).catch(() => null) }, [enabled])
  useEffect(() => {
    if (!enabled) return
    const check = () => Promise.resolve(syncApi.status()).then(s => setSyncing(Boolean(s?.inProgress))).catch(() => null)
    const id = window.setInterval(check, 3000)
    return () => window.clearInterval(id)
  }, [enabled])
  useEffect(() => {
    if (!enabled) return
    const check = () => Promise.resolve(systemApi.health()).then(h => setHealthIssues(h?.checks?.length ?? 0)).catch(() => null)
    check()
    const id = window.setInterval(check, 60000)
    return () => window.clearInterval(id)
  }, [enabled])

  return { version, syncing, healthIssues }
}
