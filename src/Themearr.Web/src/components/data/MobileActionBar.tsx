import { Button } from '@/components/ui'

export function MobileActionBar({ count, itemLabel, primaryLabel, onPrimary, onClear, busy = false }: {
  count: number
  itemLabel: string
  primaryLabel: string
  onPrimary: () => void
  onClear: () => void
  busy?: boolean
}) {
  if (count === 0) return null
  return (
    <div className="tf-mobile-action-bar" role="region" aria-label="Bulk actions" aria-live="polite">
      <p className="min-w-0 flex-1 text-sm font-semibold text-[#F9FAFB]"><span className="tabular-nums">{count}</span> {itemLabel}{count === 1 ? '' : 's'} selected</p>
      <Button variant="ghost" onClick={onClear} disabled={busy}>Clear</Button>
      <Button variant="secondary" onClick={onPrimary} loading={busy}>{primaryLabel}</Button>
    </div>
  )
}
