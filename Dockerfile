# ── Stage 1: Build Vite/React frontend ─────────────────────────────────────────
# Pin build stages to the native BUILDPLATFORM: the outputs (static SPA bundle +
# portable .NET IL) are architecture-independent, so multi-arch images build
# fast without emulating npm/dotnet under QEMU for arm64.
FROM --platform=$BUILDPLATFORM node:22-slim AS frontend-build
WORKDIR /frontend

COPY src/Themearr.Web/package.json src/Themearr.Web/package-lock.json* ./
RUN npm ci

COPY src/Themearr.Web/ .
RUN npm run build
# Output is in /frontend/out (static SPA bundle)

# ── Stage 2: Build .NET API ───────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src

COPY src/Themearr.API/ ./
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Stage 3: Pinned local-downloader tools ───────────────────────────────────
FROM --platform=$BUILDPLATFORM debian:bookworm-slim AS downloader-tools
ARG TARGETARCH
ARG YTDLP_VERSION=2026.07.04
ARG DENO_VERSION=2.9.4
ARG BGUTIL_PROVIDER_VERSION=1.3.1
ARG BGUTIL_PLUGIN_SHA256=b8ceec7f76143da172aaf5ebeec0c2d218e5680c063b931586bca48567069b38
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl unzip \
    && rm -rf /var/lib/apt/lists/* \
    && case "$TARGETARCH" in \
         amd64) YTDLP_ASSET=yt-dlp_linux; DENO_ARCH=x86_64 ;; \
         arm64) YTDLP_ASSET=yt-dlp_linux_aarch64; DENO_ARCH=aarch64 ;; \
         *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
       esac \
    && curl -fsSLo /tmp/yt-dlp "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/${YTDLP_ASSET}" \
    && curl -fsSLo /tmp/yt-dlp-sums "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/SHA2-256SUMS" \
    && grep "  ${YTDLP_ASSET}$" /tmp/yt-dlp-sums | sed "s#  ${YTDLP_ASSET}\$#  /tmp/yt-dlp#" | sha256sum -c - \
    && install -m 0755 /tmp/yt-dlp /usr/local/bin/yt-dlp \
    && mkdir -p /usr/local/share/yt-dlp-plugins \
    && curl -fsSLo /usr/local/share/yt-dlp-plugins/bgutil-ytdlp-pot-provider.zip \
         "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/${BGUTIL_PROVIDER_VERSION}/bgutil-ytdlp-pot-provider.zip" \
    && echo "${BGUTIL_PLUGIN_SHA256}  /usr/local/share/yt-dlp-plugins/bgutil-ytdlp-pot-provider.zip" | sha256sum -c - \
    && DENO_ASSET="deno-${DENO_ARCH}-unknown-linux-gnu.zip" \
    && curl -fsSLo "/tmp/${DENO_ASSET}" "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/${DENO_ASSET}" \
    && curl -fsSLo "/tmp/${DENO_ASSET}.sha256sum" "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/${DENO_ASSET}.sha256sum" \
    && cd /tmp \
    && sha256sum -c "${DENO_ASSET}.sha256sum" \
    && unzip -q "${DENO_ASSET}" \
    && install -m 0755 deno /usr/local/bin/deno \
    && yt-dlp --ignore-config --no-plugin-dirs \
         --plugin-dirs /usr/local/share/yt-dlp-plugins --verbose --list-extractors \
         >/tmp/yt-dlp-extractors 2>/tmp/yt-dlp-plugin-debug \
    && (yt-dlp --ignore-config --no-plugin-dirs \
         --plugin-dirs /usr/local/share/yt-dlp-plugins --verbose --simulate \
         --proxy http://127.0.0.1:9 --socket-timeout 1 --extractor-retries 0 --retries 0 \
         "https://www.youtube.com/watch?v=abc12345678" \
         >/tmp/yt-dlp-plugin-probe 2>&1 || true) \
    && grep -F "PO Token Providers: bgutil:http-${BGUTIL_PROVIDER_VERSION}" /tmp/yt-dlp-plugin-probe

# ── Stage 4: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ARG APP_VERSION=dev
LABEL org.opencontainers.image.title="ThemeForge" \
      org.opencontainers.image.description="Movie and TV theme automation by ChrisFlix Labs" \
      org.opencontainers.image.vendor="ChrisFlix Labs" \
      org.opencontainers.image.version="${APP_VERSION}"

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=downloader-tools /usr/local/bin/yt-dlp /usr/local/bin/yt-dlp
COPY --from=downloader-tools /usr/local/bin/deno /usr/local/bin/deno
COPY --from=downloader-tools /usr/local/share/yt-dlp-plugins /usr/local/share/yt-dlp-plugins

WORKDIR /app

# Copy .NET publish output
COPY --from=api-build /app/publish ./

# Copy the static SPA bundle into wwwroot (served by .NET, with SPA fallback)
COPY --from=frontend-build /frontend/out ./wwwroot/

# Non-root user — yt-dlp, FFmpeg, ffprobe, and Deno are globally executable,
# while runtime downloads and caches remain confined to per-job /tmp directories.
RUN groupadd -r themearr && useradd -r -g themearr -d /opt/themearr -s /sbin/nologin themearr \
    && mkdir -p /opt/themearr/data/secrets \
    && chown -R themearr:themearr /app /opt/themearr \
    && chmod 700 /opt/themearr/data /opt/themearr/data/secrets

USER themearr

ENV APP_VERSION=${APP_VERSION}
# Bind to all interfaces INSIDE the container — this is required, since Docker's
# port publishing can't reach a container that only listens on its own loopback.
# Host exposure is restricted separately by docker-compose publishing to
# 127.0.0.1:8080 only; remote access needs a reverse proxy with its own auth/TLS.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

ENTRYPOINT ["dotnet", "Themearr.API.dll"]
