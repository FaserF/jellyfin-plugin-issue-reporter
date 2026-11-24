# Jellyfin Issue Reporter Plugin

[![CI](https://github.com/FaserF/jellyfin-plugin-issue-reporter/actions/workflows/ci.yml/badge.svg)](https://github.com/FaserF/jellyfin-plugin-issue-reporter/actions/workflows/ci.yml)
[![Linter](https://github.com/FaserF/jellyfin-plugin-issue-reporter/actions/workflows/linter.yml/badge.svg)](https://github.com/FaserF/jellyfin-plugin-issue-reporter/actions/workflows/linter.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A **resource-efficient Jellyfin plugin** that monitors installed plugin logs via an O(1) `ILoggerProvider` + bounded async `Channel<LogEvent>`, filters out local/transient errors, masks secrets & PII across 4 security layers, deduplicates GitHub issues with ETag caching & version intelligence, and auto-fills target repositories' `.github/ISSUE_TEMPLATE` forms.

> [!NOTE]
> **Target Audience — Jellyfin Plugin Developers:**
> This plugin is primarily designed for **Jellyfin plugin developers and maintainers** monitoring their own plugins, `-dev`/`beta` builds, and staging servers. Automated submission should be restricted to repositories you maintain or actively test.

---

## Features

- **Resource-Efficient O(1) Log Monitoring (`ResourceEfficientLogMonitor`)**
  - Hooks directly into Jellyfin's `ILoggerFactory` via `ILoggerProvider` — zero log-file polling or disk tailing.
  - O(1) severity & category prefix filtering on the hot path, pushing into a bounded `Channel<LogEvent>(250, DropOldest)` drained asynchronously by a `BackgroundService` with 2-second burst coalescing.
  - Out-of-the-box **self-monitoring** for `Jellyfin.Plugin.IssueReporter` protected by `AsyncLocal<bool>` re-entrancy & loop-breaker guards.
- **Dynamic Plugin Discovery & Universal Repo Mapping**
  - Discovers installed plugins via `IPluginManager`, `meta.json` (`owner`, `sourceUrl`), and assembly `RepositoryUrl` metadata.
  - Built-in support for `FaserF` Jellyfin plugins (`youtube-jellyfin`, `emby-jellyfin`, `crunchyroll-jellyfin`, `animekai-jellyfin`, `jellyfin-ai-companion`, `jellyfin-plugin-MediathekViewDL`, `jellyfin-plugin-ai-search`, `jellyfin-plugin-ratings`, `jellyfin-plugin-issue-reporter`) plus **universal custom mappings** (`PluginNameOrAssembly=owner/repo`) and owner allowlist filtering (`self`, `custom`, `manual`).
- **4-Layer Zero-Leak Privacy & Redaction Pipeline**
  1. **Dynamic Config Harvester:** Extracts secrets from Jellyfin plugin XML configuration files (`*Token*`, `*Secret*`, `*Key*`, `*Password*`, `*Cookie*`, etc.).
  2. **Built-in Compiled Regex Scrubbers:** Masks GitHub/JWT/Bearer/API tokens, PEM keys, URL credentials, IPv4, MAC, Tailnet/local domains, emails, UUIDs, and OS user paths.
  3. **Custom User Rules:** Add custom domains/words and regexes.
  4. **Fail-Closed Pre-Flight Leak Guard:** Scans every rendered issue title & body (including Shannon entropy checks) and aborts if any secret survives.
- **GitHub API Efficiency, Anti-Spam & Persistent Retry Outbox**
  - **ETag (`If-None-Match` / `304 Not Modified`)** caching for repo issues, releases, templates, and comments, plus stale-on-error fallback.
  - Local ETag-cached issue index avoids GitHub's `30 req/min` Search API limit.
  - Proactive `core`/`search` rate-limit reserve checks, 3-failure circuit breaker, 30s write spacing, hourly/daily/per-repo caps, and a 60m per-target flood breaker.
  - Persistent `state.json` retry outbox (`QueuedSubmission`) with exponential backoff (`2m -> 10m -> 30m -> 2h -> 6h`) on network or rate-limit errors.

---

## Installation

1. In Jellyfin Dashboard -> **Plugins** -> **Repositories** -> **Add**, add:
   `https://raw.githubusercontent.com/FaserF/jellyfin-plugin-issue-reporter/main/manifest.json`
2. Install **Issue Reporter** from the Catalog and restart Jellyfin.
3. Open **Dashboard -> Plugins -> Issue Reporter** to configure authentication (GitHub App PEM, OAuth Device Flow, or PAT) and owner filtering.

## License
Distributed under the [MIT License](LICENSE).
