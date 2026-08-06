export const APP_BRAND = {
  name: 'ThemeForge',
  shortName: 'ThemeForge',
  tagline: 'Movie and TV theme automation by ChrisFlix Labs',
  organization: 'ChrisFlix Labs',
  description: 'Automatically discover, download, organize, and manage movie and TV theme music.',
} as const

export function brandAsset(path: string): string {
  return `${import.meta.env.BASE_URL}${path.replace(/^\//, '')}`
}
