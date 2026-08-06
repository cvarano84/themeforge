# Changelog

## v1.48.0 — ThemeForge Rebrand & App Icons

- Rebranded the user-facing application from Themearr to **ThemeForge** by ChrisFlix Labs.
- Added an original forge-flame and audio-waveform icon with SVG source, multi-size favicons, and navigation branding.
- Added a standalone web app manifest, maskable PWA icons, and a dedicated 180×180 iPhone home-screen icon.
- Added Open Graph, social, theme-color, Apple web-app, and route-aware browser title metadata.
- Preserved existing databases, `/opt/themearr` paths, Docker image/service/volume names, API paths, assembly names, and external integrations.
- Added preferred `THEMEFORGE_*` environment variables with deprecated `THEMEARR_*` aliases; new names take precedence and legacy usage produces value-free warnings.
- Added automatic migration of the legacy browser auth-token storage key.
- Added automated metadata, manifest, icon-dimension, branding, environment-precedence, and persistent-data compatibility tests.

## v1.47.2 — YouTube Authentication & PO Tokens

- Added secure cookies.txt upload, replacement, status, and deletion.
- Added persistent managed-cookie storage in the Themearr data volume.
- Preserved environment-managed cookie-file support.
- Added automatic yt-dlp PO-token provider integration.
- Added PO-token provider diagnostics and health status.
- Improved YouTube authentication, bot-check, and playback-verification errors.
- Added hardened credential-file validation and permissions.
- Added comprehensive backend and frontend tests.

## v1.47.1 — Container Path Resolution Fix

- Fixed Plex and Radarr source paths being retained instead of mapped container paths.
- Added stale library-path repair during full synchronization.
- Improved path-mapping validation and diagnostics.
- Preserved strict library-root containment for theme writes and deletes.
- Added regression coverage for Docker bind mounts and Unicode media folders.

## v1.47.0 — Local YouTube Downloader

- Replaced RapidAPI `youtube-mp36` downloads with local yt-dlp and FFmpeg processing.
- Removed RapidAPI credentials and quota requirements.
- Added local downloader diagnostics and health checks.
- Added optional cookies-file support.
- Added configurable audio quality, timeout, and concurrency.
- Added hardened temporary-file and external-process handling.

Legacy `rapidapi_key` and `rapidapi_username` database values are ignored and never returned.
