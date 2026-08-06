# ThemeForge

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="src/Themearr.Web/public/themeforge-logo.svg">
    <source media="(prefers-color-scheme: light)" srcset="src/Themearr.Web/public/themeforge-logo-dark.svg">
    <img src="src/Themearr.Web/public/themeforge-logo.svg" alt="ThemeForge" height="56" />
  </picture>
</p>

<p align="center">
  <strong>Movie and TV theme automation by ChrisFlix Labs</strong>
</p>

<p align="center">
  Automatically discover, download, organize, and maintain theme music for Plex, Jellyfin, Emby, and Kodi libraries.
</p>

<p align="center">
  <a href="https://github.com/Themearr/themearr/releases">Releases</a> ·
  <a href="https://github.com/Themearr/ProxmoxVE">Proxmox Scripts</a>
</p>

---

## What it does

ThemeForge reads movie and TV libraries and helps add and maintain `theme.mp3` files — the format Plex, Jellyfin, Emby, and Kodi use for background music while browsing. ThemeForge is created by **ChrisFlix Labs**.

It reads movie libraries from **Plex or Radarr** and TV libraries from **Plex or Sonarr**. The arr integrations are first-class sources, so a Plex account is optional.

- Read your library from **Plex** (OAuth sign-in) **or Radarr** (URL + API key)
- Browse your whole library as a poster grid
- Auto-search YouTube for each movie's theme
- One-click download to `theme.mp3`
- Automatic background downloading across the whole library
- Paste any video URL to use a custom source
- Downloaded status tracked per movie, verified against what's on disk
- System page with health checks and scheduled tasks, arr-style
- An API key plus a **Radarr webhook**, so a theme is fetched the moment a movie is imported

## Local YouTube downloader

YouTube search and scoring still use YoutubeExplode. Once a result is selected, ThemeForge downloads it locally with **yt-dlp**, converts it to MP3 with **FFmpeg**, validates the result in an isolated temporary directory, and atomically writes `theme.mp3`. The Docker image and native installer include yt-dlp, FFmpeg, ffprobe, and Deno; normal users need no hosted-converter account or monthly request allowance.

Video availability is not guaranteed. Region restrictions, sign-in requirements, removed videos, and YouTube extractor changes can still prevent a download. A Netscape-format cookies file is optional for videos your account can access; public videos normally do not need one. The Docker installation also runs an internal automatic PO-token provider for short-lived YouTube playback verification tokens.

### YouTube cookies

Open **Settings → Local YouTube Downloader** and choose **Upload cookies.txt**. ThemeForge validates the Netscape header and tab-delimited YouTube/Google records, then atomically stores the file at `<DB_PATH parent>/secrets/youtube-cookies.txt` (normally `/opt/themearr/data/secrets/youtube-cookies.txt`). In Docker this is inside the existing `themearr-data` volume, not the image, web root, database, or media library. A valid upload is used by the next download without restarting ThemeForge. Use **Replace cookies.txt** to refresh a session and **Delete cookies** to remove it.

The file must be a UTF-8/ASCII-compatible Netscape export beginning with `# Netscape HTTP Cookie File` or `# HTTP Cookie File`; JSON browser exports and browser databases are not accepted. An administrator can instead mount a read-only file and set `YTDLP_COOKIES_FILE`. That environment setting takes precedence over any uploaded file and makes cookie management read-only in Settings.

> **Security:** A cookies.txt file can provide access to the associated YouTube session. Keep it private, never post it in an issue, and consider using a dedicated account. Export a fresh file and replace the upload when YouTube rejects or expires the session.

### Automatic PO tokens

Proof-of-Origin (PO) tokens are short-lived playback-verification tokens. They are separate from account cookies: cookies authenticate a YouTube session, while the provider supplies tokens required by some player/media requests. A video may need neither, either one, or both. Neither capability guarantees every private, restricted, removed, or region-blocked video is accessible.

The official Compose setup runs the pinned `brainicism/bgutil-ytdlp-pot-provider:1.3.1` server on the internal Docker network and the ThemeForge image contains the matching pinned yt-dlp plugin. Port 4416 is exposed only to sibling containers and is not published on the host. Manual static PO tokens are not offered because current tokens can be video-bound and short-lived.

## Install

### Proxmox LXC (one-line)

Run this on your Proxmox host:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/Themearr/ProxmoxVE/main/ct/themearr.sh)"
```

The installer generates an access token, prints it at the end, and saves a copy to `/root/themearr.creds`. Open `http://<container-ip>:8080`, enter the token, then pick your library source — sign in with Plex, or choose **"I don't use Plex"** to connect Radarr instead.

### Docker

A multi-arch image (`amd64` / `arm64`) is published to GHCR on every release.

```bash
# 1. Get the compose file
curl -fsSL https://raw.githubusercontent.com/Themearr/themearr/main/docker-compose.yml -o docker-compose.yml

# 2. Generate the required access token
echo "THEMEFORGE_AUTH_TOKEN=$(openssl rand -hex 32)" > .env

# 3. Edit docker-compose.yml — point the movie volume at your library:
#      - /path/to/your/movies:/movies
#    It must be WRITABLE (no ":ro") — ThemeForge writes theme.mp3 into movie folders.

docker compose up -d
```

Open `http://127.0.0.1:8080` and enter the token from `.env`.

> The compose file publishes the port to `127.0.0.1` only. For remote access, put a reverse proxy (Caddy/nginx) in front with its own TLS and auth.

## Configuration

### Access token (required)

The API refuses to start without `THEMEFORGE_AUTH_TOKEN` (or the deprecated `THEMEARR_AUTH_TOKEN` alias) — there is no unauthenticated mode. The Proxmox installer generates one for you; for Docker you set it yourself. Every client enters this token once.

### Downloader environment variables

| Variable | Default | Notes |
|---|---:|---|
| `YTDLP_PATH` | `yt-dlp` | Optional executable path. |
| `FFMPEG_PATH` | `ffmpeg` | Optional executable or directory path; passed explicitly to yt-dlp. |
| `YTDLP_COOKIES_FILE` | unset | Optional read-only Netscape cookies file. Missing files produce a warning without exposing the path. |
| `YTDLP_PO_TOKEN_MODE` | `auto` | `auto`, `disabled`, or `required`. Auto degrades safely; required blocks downloads if unavailable. |
| `YTDLP_PO_TOKEN_PROVIDER_URL` | unset | Absolute HTTP(S) provider base URL without credentials/query parameters. Compose sets the internal sidecar URL. |
| `YTDLP_AUDIO_QUALITY` | `192K` | One of `128K`, `192K`, `256K`, or `320K`. |
| `YTDLP_DOWNLOAD_TIMEOUT_SECONDS` | `300` | 30–1800 seconds. |
| `YTDLP_CONCURRENT_DOWNLOADS` | `1` | 1–3 local yt-dlp/FFmpeg processes. |

Environment values override Settings-page values. Invalid environment values are reported as configuration errors; they are never silently clamped.

For normal Docker installs, upload cookies in Settings; no extra volume is needed and the file persists in `themearr-data`. For an environment-managed file, mount it read-only:

```yaml
environment:
  - YTDLP_AUDIO_QUALITY=192K
  - YTDLP_DOWNLOAD_TIMEOUT_SECONDS=300
  - YTDLP_CONCURRENT_DOWNLOADS=1
  - YTDLP_PO_TOKEN_MODE=auto
  - YTDLP_PO_TOKEN_PROVIDER_URL=http://themearr-pot-provider:4416
  # - YTDLP_COOKIES_FILE=/opt/themearr/config/youtube-cookies.txt
volumes:
  - /path/to/your/movies:/movies
  - /path/to/your/tv:/shows
  # - ./youtube-cookies.txt:/opt/themearr/config/youtube-cookies.txt:ro
```

The recommended PO provider service is:

```yaml
services:
  themearr:
    depends_on:
      - themearr-pot-provider
  themearr-pot-provider:
    image: brainicism/bgutil-ytdlp-pot-provider:1.3.1
    init: true
    restart: unless-stopped
    expose:
      - "4416" # internal only; do not add a host ports mapping
```

`depends_on` orders container startup but does not guarantee readiness. In `auto` mode ThemeForge reports a degraded provider and keeps normal yt-dlp behavior while the provider starts or is temporarily unavailable. In `required` mode YouTube downloads are blocked until both the plugin and provider are ready. Do not route the provider through Traefik, Cloudflare Tunnel, or another public ingress.

### Library paths & path mappings

For a Plex host library at `/mnt/plex/Movies`, use this Docker mount:

```yaml
services:
  themearr:
    volumes:
      - /mnt/plex/Movies:/movies
```

ThemeForge library root:

```text
/movies
```

Path mapping:

```yaml
source: /mnt/plex/Movies
target: /movies
```

The left side of the Docker mount is the host path; the right side is the path visible inside the ThemeForge container. Plex may report the left-side path, but ThemeForge must store and write to the right-side path. The mapping performs that translation. The media mount must be writable because ThemeForge creates `theme.mp3` in each media folder.

After changing a path mapping, library root, or Docker mount destination, run a full sync or use the authenticated path-repair operation. ThemeForge validates that every mapping target exists and is beneath a configured local root; it never treats a mapping source or target as write authority by itself. Do not configure the host-side path as a ThemeForge root unless that exact path is genuinely mounted inside the container.

This is the setting people most often get wrong, and it's what causes **`Skipping <title> — unresolved path`** during sync.

ThemeForge writes `theme.mp3` **into your movie folders**, so it has to reach your files at a path *it* can see — which is usually **not** the path Plex reports.

- **Local Library Paths** — where your movie folders live *as ThemeForge sees them* (e.g. `/movies` in Docker, or `/mnt/media/Movies` in an LXC).
- **Path Mappings** — translate the path **Plex reports** into the path **ThemeForge sees**.

Example — Plex on Windows, ThemeForge in Docker:

| Plex reports | ThemeForge sees | Mapping to add |
|---|---|---|
| `P:\Movies\Heat (1995)\heat.mkv` | `/movies/Heat (1995)` | `P:\Movies` → `/movies` |

If sync logs `Skipping <title> — unresolved path: <path>`, that logged path is exactly what Plex reported — map its parent folder to wherever it's mounted in ThemeForge. Windows-style (`\`) paths are handled automatically.

Also make sure the movie mount is **writable** — a read-only mount resolves fine but silently fails every download.

## Health checks

**System → Health** flags the things that silently break downloads: a library path
that is missing or read-only, a Plex server that is unreachable or has rejected its
token, missing local downloader tools, an invalid optional cookies mount, and a
stalled auto-download worker. Downloader checks run only local version commands
and a temporary-directory write probe; they never contact YouTube. Only problems
are listed, so an empty page means everything is fine.

**System → Tasks** shows when the library last synced and lets you trigger a sync
immediately with *Run now*.

For external monitoring, ThemeForge exposes an unauthenticated `/health` endpoint
that returns `{"status":"Healthy"}` and nothing else — enough for Uptime Kuma or
Gatus, without leaking any configuration.

## Independent movie and show sources

Movie and TV-show sources are configured independently:

- Movies: **Plex**, **Radarr**, or **Disabled**
- Shows: **Plex**, **Sonarr**, or **Disabled**

This supports Plex movies with Sonarr shows, Radarr movies with Plex shows, Radarr
movies with Sonarr shows, and TV-only installations. Disabling a source stops its sync
and auto-download work without deleting existing records or theme files.

Sonarr uses its v3 API and imports only series with downloaded episodes. It syncs every
15 minutes. Monitored series with no downloaded media are skipped. API keys for Radarr
and Sonarr, and Plex tokens, are write-only and are never returned to the browser.

### Sonarr paths and writable TV mounts

Plex or Sonarr may report `/tv/Show` while ThemeForge sees `/shows/Show`. Add a Path
Mapping from the reported prefix to the path mounted inside ThemeForge. `/shows` is only
an example; any configured container path is supported.

Use separate writable mounts, for example:

```yaml
volumes:
  - /host/movies:/movies
  - /host/tv:/shows
```

Do not add `:ro` to the TV mount. ThemeForge writes `theme.mp3` directly into each series
root, never into a season folder.

### Movie sources: Plex or Radarr

ThemeForge can read your movie list from either **Plex** or **Radarr**, chosen during
setup and changeable later under **Settings → Library source**.

Radarr is what makes ThemeForge useful without Plex. It already knows every movie's
folder, title, year and whether the film is downloaded — and because `theme.mp3` is
read by Jellyfin, Emby and Kodi as well as Plex, a Radarr-sourced library serves all
of them. You need a Radarr URL and API key (Radarr → Settings → General → API Key).

Radarr is local and cheap to poll, so it syncs every 15 minutes rather than daily —
a newly imported movie usually has its theme within minutes.

Movies Radarr is monitoring but has not downloaded yet are skipped: there is no film
for a theme to accompany. Path Mappings work exactly as they do for Plex, and are
often needed, since Radarr in a container reports its own paths.

### If you already have the app set up

> **Note for existing installs (v1.42.0).** You don't need to do anything. ThemeForge
> stays on Plex unless you change it — the library source defaults to Plex, your setup
> stays complete, and you won't be asked to run the wizard again.

**Only read on if you plan to switch an existing library from Plex to Radarr.**

ThemeForge identifies a movie by the folder its theme lives in, and both sources describe
the same folders on disk, so anything **both** of them report keeps its downloaded
status and its place in your history.

The catch is anything Radarr *doesn't* report. Radarr only knows about movies it
manages, so hand-added rips, a second library, or anything imported before you started
using Radarr are invisible to it — and the first Radarr sync removes those rows.
Concretely:

- Movies you've **ignored** are kept, whichever source you're on.
- A removed movie's **downloaded status is not lost in practice** — status is read from
  whether a `theme.mp3` is actually on disk, so if the file is still there it comes back
  as downloaded the moment the movie reappears.
- Its **history entries are orphaned**, though. They still show the film's title and
  year on the History page, they just no longer link to a movie.

So switching is safe if Radarr manages your whole library, and lossy at the edges if it
doesn't. Check Radarr's movie count against ThemeForge's before you switch.

## API key and Radarr webhook

**Settings → API key** shows a key that external tools can use to talk to ThemeForge.
Send it as an `X-Api-Key` header on any `/api/…` request:

```bash
curl -H "X-Api-Key: <your key>" http://themearr:8080/api/system/tasks
```

> **Warning: this is a full-access credential, not a read-only one.** It authenticates
> exactly like the access token you sign in with, on every `/api/*` endpoint except the
> two that manage the key itself. Whoever holds it can reset setup, trigger an update
> that restarts the service, and overwrite your stored Plex token and Radarr API key —
> the only thing it can't do is read or regenerate itself. Handle it with
> the same care as the access token, and don't paste it anywhere you wouldn't paste that.

It is separate from the access token you sign in with, so you can regenerate it
without logging anyone out — and regenerating immediately stops the old one working.

### Fetching themes the moment Radarr imports

Instead of waiting for the next sync, have Radarr tell ThemeForge directly. In Radarr:
**Settings → Connect → Add → Webhook**, then set:

| Field | Value |
|---|---|
| Notification Triggers | **On Import** (also tick On Upgrade if you want) |
| URL | `http://themearr:8080/api/webhook/radarr` |
| Method | `POST` |
| Headers | `X-Api-Key` = your key from Settings → API key |

Press **Test** — ThemeForge answers, so a wrong URL or key shows up immediately rather
than at the next import.

Importing several movies at once is fine: ThemeForge collapses the burst into a single
sync rather than one per movie.

Two caveats:

- This is most useful when **Radarr is your library source**. If you use Plex as the
  source and Radarr only downloads, the webhook still fires — but Plex may not have
  scanned the new file yet, so the theme may still wait for a later sync.
- Radarr builds from before custom webhook headers were added (upstream, late 2024)
  cannot send `X-Api-Key`.

## Updating

- **In-app:** Settings → Updates. Downloads the latest release, preserves your data, and restarts.
- **Docker:** `docker compose pull && docker compose up -d`
- **Proxmox / bare metal:** the in-app updater, or re-run the community install script.

> **Upgrading a pre-.NET-10 install:** releases from v1.39.10 onward need the **ASP.NET Core 10** runtime. Containers created earlier were provisioned with .NET 9, so install the runtime first:
> ```bash
> curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
> bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet
> ```
> If you forget, nothing breaks — the updater checks for the runtime *before* changing any files and aborts with these instructions, leaving your running install untouched. Docker is unaffected (the runtime ships in the image).

## Upgrading from Themearr to ThemeForge

ThemeForge is the new product name beginning with v1.48.0. The web interface, metadata, API display messages, and native installer output now use ThemeForge. Existing installations upgrade in place; no database or configuration reset is required.

Compatibility-sensitive identifiers deliberately retain their established names:

- The published Docker image remains `ghcr.io/themearr/themearr`; a new registry package has not been assumed.
- The Compose services (`themearr` and `themearr-pot-provider`), container names, and `themearr-data` volume remain unchanged. Do not rename or recreate that volume.
- Native installs continue using `/opt/themearr`, the `themearr` system account/service, `/usr/local/bin/themearr-update`, and the existing `Themearr.API.dll` assembly.
- Existing `DB_PATH` values and the default `/opt/themearr/data/themearr.db` are unchanged. Do not move or rename the database.
- Existing API paths, database tables, API keys, cookies, and settings are unchanged.
- The browser automatically migrates the legacy `themearr_token` local-storage entry to `themeforge_token`, preserving practical sign-in continuity.

New environment-variable names take precedence while the old names remain supported as deprecated aliases:

| Preferred | Deprecated alias |
|---|---|
| `THEMEFORGE_AUTH_TOKEN` | `THEMEARR_AUTH_TOKEN` |
| `THEMEFORGE_VERSION_FILE` | `THEMEARR_VERSION_FILE` |
| `THEMEFORGE_UPDATER_CMD` | `THEMEARR_UPDATER_CMD` |
| `THEMEFORGE_DOWNLOAD_TIMEOUT_SECONDS` | `THEMEARR_DOWNLOAD_TIMEOUT_SECONDS` |
| `THEMEFORGE_DOWNLOAD_WATCHDOG_GRACE_SECONDS` | `THEMEARR_DOWNLOAD_WATCHDOG_GRACE_SECONDS` |
| `THEMEFORGE_BIND` | `THEMEARR_BIND` |

Legacy environment variables emit a warning without logging their values. `THEMEFORGE_DB_PATH` is also accepted, but existing `DB_PATH` remains fully supported and is not deprecated. A fresh install writes the new names; existing `auth.env` files continue to load without modification.

Browsers and iOS may retain the old icon after an upgrade. Close old tabs and remove/re-add a saved home-screen shortcut if needed; desktop browsers may require clearing site icon data. Never delete the database, Docker volume, or `/opt/themearr/data` to refresh an icon.

### v1.47.0 migration

v1.47.0 removes the legacy RapidAPI `youtube-mp36` integration, its credential routes, and quota handling. Existing `rapidapi_key` and `rapidapi_username` database rows are ignored and are never read or returned. Docker users only need to pull the new image. Native installs run a dependency preflight and install the pinned local tools before switching the application release.

## Local downloader troubleshooting

- **yt-dlp executable unavailable** — use the official image/installer, or set `YTDLP_PATH` to an executable file. Check **Settings → Local YouTube Downloader**.
- **FFmpeg or ffprobe unavailable** — install the full `ffmpeg` package or correct `FFMPEG_PATH`.
- **JavaScript runtime unavailable** — install Deno. Downloads remain enabled but YouTube extraction may fail for some videos.
- **Extraction failure / outdated yt-dlp** — YouTube changes frequently. Upgrade to the tested ThemeForge image/release; native custom installs should update yt-dlp from official release assets.
- **Sign in to confirm you’re not a bot** — upload a fresh YouTube cookies.txt in Settings, verify its status, keep concurrency at 1, and check the PO-token provider if the failure persists.
- **Cookie file invalid** — export in Netscape format and verify the first meaningful line. Do not upload HTML, JSON browser exports, ZIP files, or browser SQLite databases.
- **Cookie session rejected** — export a fresh session and use **Replace cookies.txt**. Cookies expire and YouTube can invalidate a session.
- **Restricted or authenticated video** — use another public result or upload a Netscape cookies file. Generic failures do not mean cookies are required, and the account must actually have access.
- **Cookies file missing** — correct the `YTDLP_COOKIES_FILE` mount/path or unset it. The UI intentionally never displays the path.
- **PO-token plugin missing** — rebuild or update using the supported ThemeForge image so the pinned plugin and yt-dlp version match.
- **PO-token provider unavailable** — inspect `docker compose logs themearr-pot-provider`, verify internal Docker DNS and port 4416, and confirm the plugin/server versions match. Do not expose this service publicly.
- **Media folder not writable** — the library mount must be writable by the non-root `themearr` user so the final atomic rename can create `theme.mp3`.
- **Timeout** — raise the bounded timeout in Settings or `YTDLP_DOWNLOAD_TIMEOUT_SECONDS`; slow or unavailable videos may still fail.
- **Output exceeds the maximum size** — select a shorter result. Oversized temporary output is rejected before the library is changed.
- **Unsupported architecture** — published container and native assets support Linux amd64/x86_64 and arm64/aarch64.

## Tech stack

| Layer | Technology |
|---|---|
| API | .NET 10 Web API (ASP.NET Core, LTS) |
| Frontend | React 19 + Vite (static SPA, served by .NET) |
| Routing | React Router |
| Database | SQLite via `Microsoft.Data.Sqlite` |
| YouTube search | `YoutubeExplode` |
| Theme download | yt-dlp + FFmpeg, with Deno for YouTube extraction |
| Tests | xUnit |

## Local development

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22+](https://nodejs.org/)
- yt-dlp, FFmpeg/ffprobe, and Deno on `PATH` for local download development

### Run

```bash
# Terminal 1 — API (set any token you like for local dev)
THEMEFORGE_AUTH_TOKEN=dev-token-at-least-16-chars dotnet run --project src/Themearr.API

# Terminal 2 — Frontend (dev server with proxy to API)
cd src/Themearr.Web
npm install
npm run dev   # proxies /api to the .NET backend on :5000
```

Open `http://localhost:3000`. The frontend is a static SPA — in production it's built to `src/Themearr.Web/out/` and served by the .NET app from `wwwroot` (with an SPA fallback, so deep links like `/movies` work).

### Checks

```bash
dotnet test                  # .NET test suite

cd src/Themearr.Web
npm run lint                 # ESLint
npx tsc --noEmit             # typecheck
npm run build                # production build -> out/
```

## Building a release

Push to `main` — GitHub Actions will automatically:

1. Detect the semver bump from commit messages (`feat:` → minor, `major:` → major, else patch)
2. Build the frontend (Vite) and publish .NET for `linux-x64` and `linux-arm64`
3. Bundle the frontend into each publish output
4. Create a GitHub release with both tarballs **plus SHA-256 checksums** (verified by `install.sh` / `deploy.sh`)
5. Build and push the multi-arch Docker image to `ghcr.io/themearr/themearr` (`:latest` and `:vX.Y.Z`)

Changes that don't affect the shipped app (docs, `.gitignore`, workflows) don't cut a release.

## Versioning

Releases follow semantic versioning driven by commit message prefixes:

| Prefix | Bump |
|---|---|
| `feat:` | minor |
| `major:` / `BREAKING CHANGE` | major |
| anything else | patch |

## License

MIT
