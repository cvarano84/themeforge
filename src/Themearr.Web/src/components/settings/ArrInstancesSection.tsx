import { useEffect, useState, type Dispatch, type ReactNode, type SetStateAction } from 'react'
import { arrInstancesApi } from '@/lib/api'
import type { ArrInstance, ArrInstanceInput, ArrServiceType } from '@/lib/types'
import { Button, Spinner } from '@/components/ui'

const empty = (serviceType: ArrServiceType): ArrInstanceInput => ({
  serviceType,
  name: serviceType === 'radarr' ? 'Movies' : 'TV',
  url: '', apiKey: '', enabled: true, qualityLabel: '', priority: 0, tags: [],
})

export function ArrInstancesSection() {
  const [instances, setInstances] = useState<ArrInstance[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [editing, setEditing] = useState<ArrInstance | null | 'new'>(null)
  const [form, setForm] = useState<ArrInstanceInput>(empty('radarr'))
  const [busy, setBusy] = useState('')
  const [notice, setNotice] = useState('')

  async function load() {
    setLoading(true); setError('')
    try { setInstances(await arrInstancesApi.list()) }
    catch (e) { setError((e as Error).message) }
    finally { setLoading(false) }
  }
  useEffect(() => { void load() }, [])

  function add(serviceType: ArrServiceType) {
    setForm(empty(serviceType)); setEditing('new'); setNotice(''); setError('')
  }
  function edit(instance: ArrInstance) {
    setForm({ serviceType: instance.serviceType, name: instance.name, url: instance.url,
      apiKey: '', enabled: instance.enabled, qualityLabel: instance.qualityLabel ?? '',
      priority: instance.priority, tags: instance.tags })
    setEditing(instance); setNotice(''); setError('')
  }
  async function save() {
    setBusy('save'); setError('')
    try {
      const saved = editing === 'new'
        ? await arrInstancesApi.create(form)
        : await arrInstancesApi.update((editing as ArrInstance).id, form)
      setInstances(current => editing === 'new'
        ? [...current, saved]
        : current.map(item => item.id === saved.id ? saved : item))
      setEditing(null); setNotice(`${saved.name} saved.`)
    } catch (e) { setError((e as Error).message) }
    finally { setBusy('') }
  }
  async function test() {
    setBusy('test'); setError(''); setNotice('')
    try {
      const result = await arrInstancesApi.test({ serviceType: form.serviceType, url: form.url,
        apiKey: form.apiKey, instanceId: editing === 'new' ? undefined : (editing as ArrInstance).id })
      setNotice(result.detail)
    } catch (e) { setError((e as Error).message) }
    finally { setBusy('') }
  }
  async function toggle(instance: ArrInstance) {
    setBusy(instance.id); setError('')
    try {
      const updated = await arrInstancesApi.update(instance.id, {
        serviceType: instance.serviceType, name: instance.name, url: instance.url, apiKey: '',
        enabled: !instance.enabled, qualityLabel: instance.qualityLabel ?? '',
        priority: instance.priority, tags: instance.tags,
      })
      setInstances(current => current.map(item => item.id === updated.id ? updated : item))
    } catch (e) { setError((e as Error).message) }
    finally { setBusy('') }
  }
  async function sync(instance: ArrInstance) {
    setBusy(`sync:${instance.id}`); setError('')
    try {
      const result = await arrInstancesApi.sync(instance.id)
      setNotice(`${instance.name}: ${result.synced} item${result.synced === 1 ? '' : 's'} synced.`)
      await load()
    } catch (e) { setError((e as Error).message) }
    finally { setBusy('') }
  }
  async function remove(instance: ArrInstance) {
    if (!confirm(`Delete ${instance.name}? Media records and theme.mp3 files will not be deleted.`)) return
    setBusy(`delete:${instance.id}`); setError('')
    try {
      await arrInstancesApi.delete(instance.id)
      setInstances(current => current.filter(item => item.id !== instance.id))
    } catch (e) { setError((e as Error).message) }
    finally { setBusy('') }
  }

  return (
    <section className="rounded-xl border border-[#1D2939] bg-[#101828] p-4 sm:p-6 space-y-5">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div><h2 className="text-base font-semibold text-[#F9FAFB]">Arr Instances</h2>
          <p className="mt-1 text-xs text-[#667085]">Configure each quality or category independently. Lower priority numbers are preferred.</p></div>
        <div className="flex flex-col gap-2 min-[430px]:flex-row [&>*]:flex-1"><Button size="sm" onClick={() => add('radarr')}>Add Radarr</Button>
          <Button size="sm" variant="secondary" onClick={() => add('sonarr')}>Add Sonarr</Button></div>
      </div>
      {loading && <div className="py-6 flex justify-center"><Spinner size={20} /></div>}
      {(['radarr', 'sonarr'] as ArrServiceType[]).map(service => (
        <div key={service} className="space-y-2">
          <h3 className="text-xs font-semibold uppercase tracking-wider text-[#98A2B3]">{service}</h3>
          <div className="grid gap-3 lg:grid-cols-2">
            {instances.filter(i => i.serviceType === service).map(instance => (
              <div key={instance.id} className="rounded-lg border border-[#344054] bg-[#0C111D] p-4 space-y-3">
                <div className="flex items-start justify-between gap-3"><div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2"><span className="font-medium text-[#F9FAFB]">{instance.name}</span>
                    {instance.qualityLabel && <Badge>{instance.qualityLabel}</Badge>}
                    <Badge>{instance.enabled ? 'Enabled' : 'Disabled'}</Badge></div>
                  <p className="mt-1 truncate text-xs text-[#667085]">{instance.url}</p></div>
                  <span className={`text-xs ${instance.health === 'healthy' ? 'text-[#32D583]' : instance.health === 'error' ? 'text-[#FDA29B]' : 'text-[#667085]'}`}>
                    {instance.health}</span></div>
                <div className="grid grid-cols-1 gap-2 text-xs text-[#98A2B3] min-[360px]:grid-cols-2">
                  <span>Priority <b className="text-[#D0D5DD]">{instance.priority}</b></span>
                  <span>{instance.configured ? 'Configured' : 'API key required'}</span>
                  <span>Unresolved <b className="text-[#D0D5DD]">{instance.unresolvedPathCount}</b></span>
                  <span>Last sync <b className="text-[#D0D5DD]">{instance.lastSuccessfulSync ? new Date(instance.lastSuccessfulSync).toLocaleString() : 'Never'}</b></span>
                </div>
                {instance.healthDetail && <p className="text-xs text-[#FDA29B]">{instance.healthDetail}</p>}
                <div className="flex flex-wrap gap-2">
                  <Button size="sm" variant="secondary" onClick={() => edit(instance)}>Edit / Test</Button>
                  <Button size="sm" variant="secondary" loading={busy === instance.id} onClick={() => void toggle(instance)}>{instance.enabled ? 'Disable' : 'Enable'}</Button>
                  <Button size="sm" variant="secondary" loading={busy === `sync:${instance.id}`} disabled={!instance.enabled} onClick={() => void sync(instance)}>Sync now</Button>
                  <Button size="sm" variant="ghost" loading={busy === `delete:${instance.id}`} onClick={() => void remove(instance)}>Delete</Button>
                </div>
              </div>
            ))}
          </div>
        </div>
      ))}
      {editing && <Editor form={form} setForm={setForm} configured={editing === 'new' ? false : editing.configured}
        saving={busy === 'save'} testing={busy === 'test'} onSave={() => void save()} onTest={() => void test()} onCancel={() => setEditing(null)} />}
      {notice && <p className="text-xs text-[#32D583]">{notice}</p>}
      {error && <p className="text-xs text-[#FDA29B]">{error}</p>}
    </section>
  )
}

function Badge({ children }: { children: ReactNode }) {
  return <span className="rounded-full bg-[#1D2939] px-2 py-0.5 text-[10px] text-[#D0D5DD]">{children}</span>
}

function Editor({ form, setForm, configured, saving, testing, onSave, onTest, onCancel }: {
  form: ArrInstanceInput; setForm: Dispatch<SetStateAction<ArrInstanceInput>>; configured: boolean
  saving: boolean; testing: boolean; onSave: () => void; onTest: () => void; onCancel: () => void
}) {
  const field = 'min-h-11 rounded-lg border border-[#344054] bg-[#101828] px-3 py-2 text-base text-[#F9FAFB] outline-none focus:border-[#BB0000] sm:text-sm'
  return <div className="rounded-lg border border-[#344054] bg-[#0C111D] p-4 space-y-4">
    <h3 className="font-medium text-[#F9FAFB]">{configured ? `Edit ${form.name}` : `Add ${form.serviceType === 'radarr' ? 'Radarr' : 'Sonarr'}`}</h3>
    <div className="grid gap-3 sm:grid-cols-2">
      <label className="flex flex-col gap-1 text-xs text-[#98A2B3]">Instance name<input className={field} value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} /></label>
      <label className="flex flex-col gap-1 text-xs text-[#98A2B3]">URL<input className={field} type="url" inputMode="url" autoCapitalize="none" placeholder={form.serviceType === 'radarr' ? 'http://radarr:7878' : 'http://sonarr:8989'} value={form.url} onChange={e => setForm(f => ({ ...f, url: e.target.value }))} /></label>
      <label className="flex flex-col gap-1 text-xs text-[#98A2B3]">API key<input className={field} type="password" autoComplete="new-password" placeholder={configured ? 'Leave blank to keep current key' : 'API key'} value={form.apiKey} onChange={e => setForm(f => ({ ...f, apiKey: e.target.value }))} /></label>
      <label className="flex flex-col gap-1 text-xs text-[#98A2B3]">Quality label<input className={field} placeholder="1080p, 4K, Anime…" value={form.qualityLabel} onChange={e => setForm(f => ({ ...f, qualityLabel: e.target.value }))} /></label>
      <label className="flex flex-col gap-1 text-xs text-[#98A2B3]">Priority<input className={field} type="number" value={form.priority} onChange={e => setForm(f => ({ ...f, priority: Number(e.target.value) }))} /></label>
      <label className="flex flex-col gap-1 text-xs text-[#98A2B3]">Tags (comma separated)<input className={field} value={form.tags.join(', ')} onChange={e => setForm(f => ({ ...f, tags: e.target.value.split(',').map(x => x.trim()).filter(Boolean) }))} /></label>
    </div>
    <label className="flex items-center gap-2 text-sm text-[#D0D5DD]"><input type="checkbox" checked={form.enabled} onChange={e => setForm(f => ({ ...f, enabled: e.target.checked }))} />Enabled</label>
    <div className="flex flex-wrap gap-2"><Button size="sm" variant="secondary" loading={testing} onClick={onTest}>Test</Button>
      <Button size="sm" loading={saving} onClick={onSave}>Save</Button><Button size="sm" variant="ghost" onClick={onCancel}>Cancel</Button></div>
  </div>
}
