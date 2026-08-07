import type { ReactNode } from 'react'

export interface MobileDataField {
  label: string
  value: ReactNode
}

export function MobileDataCard({ title, subtitle, fields, status, actions }: {
  title: ReactNode
  subtitle?: ReactNode
  fields: MobileDataField[]
  status?: ReactNode
  actions?: ReactNode
}) {
  return (
    <article className="rounded-xl border border-[#1D2939] bg-[#101828] p-4">
      <div className="flex min-w-0 items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <h3 className="text-sm font-semibold leading-snug text-[#F9FAFB]">{title}</h3>
          {subtitle && <div className="mt-1 text-xs leading-relaxed text-[#98A2B3]">{subtitle}</div>}
        </div>
        {status && <div className="flex-shrink-0">{status}</div>}
      </div>
      <dl className="mt-4 grid grid-cols-2 gap-x-4 gap-y-3">
        {fields.map(field => <div key={field.label} className="min-w-0"><dt className="text-xs font-medium text-[#98A2B3]">{field.label}</dt><dd className="mt-0.5 break-words text-sm text-[#D0D5DD]">{field.value}</dd></div>)}
      </dl>
      {actions && <div className="mt-4 flex flex-col gap-2 border-t border-[#1D2939] pt-4 [&>*]:min-h-11">{actions}</div>}
    </article>
  )
}
