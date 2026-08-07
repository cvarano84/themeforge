import { Link } from 'react-router-dom'

export interface ResponsiveStat {
  label: string
  value: number
  color: string
  href: string
  description: string
}

export function ResponsiveStatGrid({ stats }: { stats: ResponsiveStat[] }) {
  return (
    <section aria-labelledby="dashboard-status-heading">
      <h2 id="dashboard-status-heading" className="sr-only">Library status</h2>
      <div className="grid grid-cols-1 gap-3 min-[360px]:grid-cols-2 sm:grid-cols-4">
        {stats.map(({ label, value, color, href, description }) => (
          <Link key={label} to={href} aria-label={`${label}: ${value}. ${description}`} className="flex min-h-24 flex-col justify-center rounded-xl border border-[#1D2939] bg-[#101828] px-4 py-4 transition-colors hover:border-[#475467] focus-visible:border-[#E07777]">
            <p className="mb-1 text-sm font-medium leading-snug text-[#98A2B3]">{label}</p>
            <p className="text-2xl font-bold tabular-nums" style={{ color }}>{value.toLocaleString()}</p>
            <p className="mt-1 text-xs text-[#98A2B3]">{description}</p>
          </Link>
        ))}
      </div>
    </section>
  )
}
