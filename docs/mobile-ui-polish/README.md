# ThemeForge vNext — Mobile UI Polish

## Root causes

The original shell reserved a fixed 260px sidebar and content margin at every viewport width. On a 390px phone, the remaining content width was smaller than the page padding. Desktop flex rows, fixed-width searches, minimum-width tables, one-line truncation, short controls, and hover-only media actions then compounded the problem.

## Layout breakpoints

Breakpoints are based on available content space, not device names:

- Below 360px: single-column statistic cards and the most compact header treatment.
- 360px–429px: two-column statistic cards with stacked page controls.
- 430px–639px: two-column cards; wider controls may share a row.
- 640px–1023px: tablet/mobile shell with wider grids and no permanent navigation rail.
- 1024px and above: the 260px desktop sidebar and desktop toolbar are enabled because at least 764px remains for content.

The permanent sidebar intentionally stays off at 768px. This prevents a tablet-sized viewport from inheriting the same narrow-content failure as a phone.

## Responsive patterns

- `AppShell` owns the sticky mobile header, page actions, desktop rail, main landmark, safe-area padding, and drawer state.
- The mobile drawer shares navigation definitions and live badges with the desktop sidebar.
- Dense scheduled-task rows become `MobileDataCard` instances below the desktop breakpoint.
- Media libraries retain their visual poster browsing model, but use two columns at small widths, touch-visible card actions, horizontally scrollable status chips, and stacked filters.
- Form controls use a 44px minimum target and 16px mobile font size to avoid iOS input zoom.

## Manual viewport matrix

Responsive QA covers 320, 375, 390, 430, 768, and 1440px widths. Each viewport is checked for `scrollWidth <= clientWidth`, usable navigation, readable dashboard content, and keyboard-operable primary actions.

`scripts/mobile-preview-api.mjs` provides deterministic local dashboard fixtures for visual QA when the .NET SDK or a configured media server is unavailable.
