# Modrinth Integration Reference (Spawner)

Last updated: 2026-02-26

Purpose:
- Working reference for implementing Modrinth support in Spawner.
- Scope is the subset needed for Fabric server instance management:
  - mods
  - modpacks (`.mrpack`)
  - resource packs
  - update checks
- This is not a full reproduction of the entire Modrinth API.

## 1) What Modrinth pieces we actually need

We do NOT need the entire API surface (teams, notifications, threads, payouts, etc.).

We DO need:
- `projects`
  - search projects
  - get project
- `versions`
  - list project versions
  - get version (optional, mostly debugging/detail views)
- `version-files`
  - identify installed files by hash
  - bulk identify files by hashes
  - bulk update resolution from hashes + loader/game version
- `tags`
  - loaders
  - game versions
  - categories (optional UI metadata)
  - side types (for validation/UI labels)
- Modrinth modpack format (`.mrpack`) spec

## 2) Core Modrinth API behavior (important constraints)

Base / versioning:
- Production API host: `https://api.modrinth.com`
- API version is path-based (`/v2/...`)
- Docs currently show Labrinth `v2.7.0` on the overview page.

Auth:
- Most read-only requests do not require auth.
- PAT / OAuth only needed for writes or private data.
- For Spawner install/search/update features, public unauthenticated requests are enough.

Rate limits:
- IP-based rate limit, currently documented as `300 requests/min`.
- Response headers include:
  - `X-Ratelimit-Limit`
  - `X-Ratelimit-Remaining`
  - `X-Ratelimit-Reset`

User-Agent:
- Modrinth recommends a unique identifying `User-Agent`.
- Spawner backend client should always send one.

Identifiers:
- Projects/versions use base62 IDs.
- Slugs/usernames are friendly but mutable.
- Persist IDs when possible.

## 3) API endpoints we should implement against (subset)

### 3.1 Search projects

Route:
- `GET /v2/search`

Use cases:
- Search Fabric mods
- Search Modrinth modpacks
- Search resource packs

Key query params:
- `query`
- `facets` (string; nested arrays represent AND/OR)
- `index` (`relevance`, `downloads`, `follows`, `newest`, `updated`)
- `offset`
- `limit` (<= 100)

Important search facet notes:
- Common facets include:
  - `project_type`
  - `categories` (loaders are included here for search)
  - `versions`
  - `client_side`
  - `server_side`
  - `open_source`

Examples we will use:
- Fabric server mods:
  - `project_type:mod`
  - `categories:fabric`
  - `versions:<mcVersion>`
  - `server_side:required OR server_side:optional`
- Modpacks:
  - `project_type:modpack`
  - optionally `categories:fabric` (if desired)
- Resource packs:
  - `project_type:resourcepack`

### 3.2 Get project details

Route:
- `GET /v2/project/{id|slug}`

Use cases:
- Metadata page/details in UI
- License / support / side compatibility display

Useful fields (documented in schema):
- `id`, `slug`, `title`, `description`
- `categories`
- `client_side`, `server_side`
- `project_type`
- `icon_url`
- `downloads`
- `published`, `updated`

### 3.3 List project versions

Route:
- `GET /v2/project/{id|slug}/version`

Use cases:
- Manual version selection for a project
- Compatibility filtering before install

Key query params:
- `loaders` (string representation of array, e.g. `["fabric"]`)
- `game_versions` (string representation of array)
- `featured` (optional bool)
- `include_changelog` (default true; set false for list views)

Useful version fields:
- `id`, `project_id`, `version_number`, `name`
- `game_versions`
- `loaders`
- `version_type` (`release`, `beta`, `alpha`)
- `featured`
- `status`
- `dependencies[]` (`required`, `optional`, `incompatible`, `embedded`)
- `files[]`
  - `hashes` (`sha1`, `sha512`)
  - `url`
  - `filename`
  - `primary`
  - `size`
  - `file_type` (nullable; includes resource-pack-related values)

### 3.4 Version-files: identify installed files and check updates

These endpoints are the key to robust update detection from local mod jars.

Get version from a hash:
- `GET /v2/version_file/{hash}?algorithm=sha1|sha512`
- Optional `multiple` flag exists (rarely needed for our v1)

Bulk get versions from hashes:
- `POST /v2/version_files`
- Body:
  - `hashes[]`
  - `algorithm` (`sha1` or `sha512`)
- Returns map `{ hash -> version }`

Get updated version for one hash (compatibility-aware):
- `POST /v2/version_file/{hash}/update?algorithm=sha1|sha512`
- Body:
  - `loaders[]`
  - `game_versions[]`

Bulk get updated versions for many hashes (preferred):
- `POST /v2/version_files/update`
- Body:
  - `hashes[]`
  - `algorithm`
  - `loaders[]`
  - `game_versions[]`
- Returns map `{ hash -> version }`

Why these matter:
- Lets us identify mod jars already in `mods/`
- Lets us ask Modrinth for compatible upgrades for the current server loader/version
- Avoids relying on filename parsing

### 3.5 Tags endpoints (for UI + validation)

Loaders:
- `GET /v2/tag/loader`
- Returns loaders + supported project types

Game versions:
- `GET /v2/tag/game_version`
- Returns version + `version_type` + `date` + `major`

Categories:
- `GET /v2/tag/category`
- Returns category + icon + applicable `project_type`

Side types:
- `GET /v2/tag/side_type`
- Returns valid side types:
  - `required`
  - `optional`
  - `unsupported`
  - `unknown`

## 4) Modrinth modpack format (`.mrpack`) essentials

Storage:
- `.mrpack` is a ZIP file
- Root metadata file is `modrinth.index.json` (UTF-8)

Key `modrinth.index.json` fields:
- `formatVersion` (currently `1`)
- `game` (`minecraft`)
- `versionId`
- `name`
- `summary` (optional)
- `files[]`
  - `path` (destination path relative to instance dir)
  - `hashes` (MUST include `sha1` and `sha512`)
  - `env` (optional: `client` / `server` = `required|optional|unsupported`)
  - `downloads[]` (HTTPS URLs)
  - `fileSize` (optional but useful)
- `dependencies`
  - includes `minecraft`, `fabric-loader`, etc.

Overrides:
- `overrides/` copied to instance root
- `server-overrides/` copied after `overrides/` and can overwrite it
- `client-overrides/` exists but should be ignored for server installs

Important `.mrpack` safety requirements for Spawner:
- Reject path traversal in `files[].path`
  - no `..`
  - no absolute paths / drive prefixes
- Verify downloaded file hashes before placing
- Follow redirects (spec recommends at least 3)
- Restrict or validate download domains (spec names common allowed domains)

## 5) How this maps to Spawner (high-level architecture)

Spawner already has:
- ASP.NET backend (`Spawner`)
- React frontend (`Frontend/spawner`)
- Instance model and per-instance file management

Implementation should be backend-first:
- Frontend should call Spawner backend endpoints only
- Backend handles Modrinth API calls, downloads, verification, file placement

Why backend-first:
- We need files written to server instance directories
- We need hash verification and path validation server-side
- We need centralized rate-limit handling and caching
- We can hide future auth tokens if ever needed

## 6) Proposed Spawner backend modules (new, planned)

### 6.1 `Services/ModrinthClient.cs`

Responsibilities:
- HTTP wrapper for Modrinth API (`/v2`)
- Adds `User-Agent`
- Handles errors and rate-limit headers
- Typed methods for endpoint subset

Methods (planned):
- `SearchProjectsAsync(...)`
- `GetProjectAsync(projectIdOrSlug)`
- `GetProjectVersionsAsync(projectIdOrSlug, loaders, gameVersions, ...)`
- `GetVersionsFromHashesAsync(hashes, algorithm)`
- `GetLatestVersionsFromHashesAsync(hashes, algorithm, loaders, gameVersions)`
- `GetLoadersAsync()`
- `GetGameVersionsAsync()`
- `GetCategoriesAsync()`
- `GetSideTypesAsync()`

### 6.2 `Services/ModrinthPackageInstaller.cs`

Responsibilities:
- Install mod/version file into a specific instance
- Validate compatibility (Fabric + game version)
- Download file, verify hash, place file atomically

Planned operations:
- Install mod version file -> `mods/`
- Install resource pack version file -> `resourcepacks/` (local library)
- Remove installed file
- Update installed file
- Enable/disable file

### 6.3 `Services/MrpackInstaller.cs`

Responsibilities:
- Parse `.mrpack`
- Validate `modrinth.index.json`
- Filter server-applicable files via `env.server`
- Download + verify files
- Apply `overrides/` then `server-overrides/`

Planned behavior (v1):
- Fabric-only pack install support
- Reject pack if dependencies do not include compatible `minecraft` + `fabric-loader`
- Ignore `client-overrides/`
- Optional file selection support can be added later

### 6.4 `Services/InstanceModCatalog.cs` (Spawner metadata)

Purpose:
- Track installed Modrinth-managed assets per instance for reliable UI/update/rollback

Suggested file:
- `<instance>/.spawner/modrinth-state.json`

Store entries for:
- project ID / slug
- version ID
- file hash(es)
- local path
- asset kind (`mod`, `resourcepack`, `modpack-file`)
- enabled/disabled
- source (`search install`, `mrpack`, `manual import recognized`)
- installed timestamp

Reason:
- We cannot rely only on filenames.
- Local state simplifies updates and UI status.

## 7) Enable/disable semantics (planned)

### 7.1 Mods (Fabric)

Recommended v1 behavior:
- Enabled: file exists in `mods/`
- Disabled: move file to `mods/.disabled/` (preferred) OR `mods-disabled/`

Why move instead of rename extension:
- Clearer UI
- Less ambiguity for tooling
- Easier to restore exact filename

Rules:
- Stop server before mutating `mods/`
- Use atomic moves when possible
- Keep state metadata in sync

### 7.2 Resource packs

Important distinction:
- Installing a resource pack file locally is not the same as enabling it for clients.
- Minecraft server resource packs are usually enabled via `server.properties` fields (`resource-pack`, `resource-pack-sha1`, etc.) and require a URL clients can download.

Planned v1:
- Install/remove/manage resource pack files in instance storage
- Mark one pack as “selected for server use” in Spawner metadata
- Optional later: expose a hosted file URL via Spawner and write `server.properties` automatically

Planned v2 (optional):
- Backend-hosted resource pack URL endpoint per instance
- Auto-compute SHA1 and update:
  - `resource-pack`
  - `resource-pack-sha1`
  - `require-resource-pack`
  - `resource-pack-prompt`

## 8) Compatibility rules (what we enforce)

For Fabric mods:
- Project version must support:
  - loader `fabric`
  - instance Minecraft version
- Project should be server-compatible:
  - `server_side` = `required` or `optional`
- Reject obvious client-only (`server_side=unsupported`) by default

For resource packs:
- No Fabric loader requirement
- Project type must be `resourcepack`
- File extension should typically be `.zip`

For modpacks (`.mrpack`):
- `game` must be `minecraft`
- `dependencies` must include `minecraft`
- `dependencies["fabric-loader"]` required for Fabric support (v1)
- `files[].env.server == "unsupported"` should not be installed on server
- `files[].env.server == "optional"` may be skipped or user-selected (phase 2 UI)

## 9) Security and reliability requirements (non-optional)

### 9.1 Download / file safety
- Verify hash (`sha512` preferred if available; otherwise `sha1` only when endpoint/pack dictates)
- Write to temp file, verify, then move into place
- Enforce max file size and max total bytes (especially for `.mrpack`)
- Timeout + retry policy
- Follow redirects with cap

### 9.2 Path safety (`.mrpack` + overrides)
- Normalize and validate all destination paths
- Reject:
  - absolute paths
  - drive-letter paths (`C:\...`)
  - `..` traversal
  - UNC/network paths
- Prevent writes outside the instance directory root

### 9.3 ZIP safety
- Limit extracted file count
- Limit decompressed size
- Reject suspicious compression ratios if needed

### 9.4 Runtime safety
- Require instance offline before install/remove/enable/disable/update
- Optional override later for advanced users, but default should be strict

## 10) API design for Spawner (planned internal endpoints)

All below are proposed `Spawner` endpoints (not implemented yet).

Discovery/search:
- `GET /api/v1/modrinth/search?projectType=mod|modpack|resourcepack&query=...&mcVersion=...&loader=fabric&offset=...&limit=...`
- `GET /api/v1/modrinth/projects/{idOrSlug}`
- `GET /api/v1/modrinth/projects/{idOrSlug}/versions?...`
- `GET /api/v1/modrinth/tags/loaders`
- `GET /api/v1/modrinth/tags/game-versions`

Install/manage (instance-scoped):
- `GET /api/v1/servers/{serverId}/mods` (managed + discovered local files)
- `POST /api/v1/servers/{serverId}/mods:install-modrinth-version`
- `POST /api/v1/servers/{serverId}/mods/{entryId}:disable`
- `POST /api/v1/servers/{serverId}/mods/{entryId}:enable`
- `DELETE /api/v1/servers/{serverId}/mods/{entryId}`
- `POST /api/v1/servers/{serverId}/mods:check-updates`
- `POST /api/v1/servers/{serverId}/mods:upgrade`

Modpacks:
- `POST /api/v1/servers/{serverId}/modpack:install-upload` (upload `.mrpack` to existing instance)
- `POST /api/v1/servers/{serverId}/modpack:install-url`
- `POST /api/v1/servers/{serverId}/modpack:preview` (parse only, no writes)
- `POST /api/v1/servers:import-modrinth-modpack` (create new instance from `.mrpack`)

Resource packs:
- `GET /api/v1/servers/{serverId}/resourcepacks`
- `POST /api/v1/servers/{serverId}/resourcepacks:install-modrinth-version`
- `POST /api/v1/servers/{serverId}/resourcepacks/{entryId}:select-server-pack`
- `POST /api/v1/servers/{serverId}/resourcepacks/{entryId}:unselect-server-pack`

## 11) Frontend plan (Spawner UI)

### 11.1 Server Page additions (new tabs/sections)

Add tabs (or sub-tabs under Files/Mods):
- `Mods`
- `Modpacks`
- `Resource Packs`

### 11.2 Mods tab (v1)

Views:
- Installed mods list (enabled/disabled/update available)
- Search drawer/panel (Modrinth search)
- Install modal/version picker

Actions:
- Install
- Enable / Disable
- Remove
- Check updates
- Update selected/all

Status UX:
- Show compatibility tags (`fabric`, mc versions, server-side support)
- Show source (`Modrinth`, `MrPack`, `Unknown local`)
- Show progress for downloads and hash verification

### 11.3 Modpacks tab (v1 -> v2)

v1:
- Upload `.mrpack`
- Preview pack metadata + dependencies
- Install into existing stopped Fabric instance

v2:
- Create new instance from `.mrpack`
- Optional file selection for `env.server=optional`
- Conflict handling / overwrite preview

### 11.4 Resource Packs tab

v1:
- Search/install/remove local resource packs
- Mark selected server pack in Spawner metadata

v2:
- “Enable on server” writes `server.properties` and optionally hosts file via Spawner

## 12) Update detection strategy (detailed)

Goal:
- Detect updates for installed mods/resource packs that came from Modrinth, even if filenames differ.

Process:
1. Scan managed entries from `modrinth-state.json`.
2. For entries missing hashes (or imported manually), compute `sha1` and `sha512`.
3. Call `POST /v2/version_files/update` in batches with:
   - `hashes`
   - `algorithm` (prefer `sha512`; fallback to `sha1`)
   - `loaders: ["fabric"]`
   - `game_versions: [instance.GameVersion]`
4. Compare returned version IDs to installed version IDs.
5. Mark update availability in UI.

Fallback behavior:
- If hash not found on Modrinth, mark as `unmanaged/unknown`.
- Keep local file untouched.

## 13) Phased implementation plan (recommended)

### Phase 0: Foundation
- Add `ModrinthClient`
- Add tag caching (`loader`, `game_version`, `side_type`)
- Add basic search + project/version proxy endpoints

### Phase 1: Mods (Fabric only)
- Install mod from selected Modrinth version file
- Local metadata tracking (`modrinth-state.json`)
- Enable/disable/remove
- Instance offline enforcement

### Phase 2: Update checks
- Hash scan + bulk identify/update endpoints
- UI “check updates” and “update all”

### Phase 3: Modpacks (`.mrpack`) import/install
- Parse `.mrpack`, validate paths and hashes
- Apply `overrides` and `server-overrides`
- Download server-applicable files
- Fabric-only enforcement

### Phase 4: Resource packs
- Install/manage local packs
- Optional server activation via `server.properties`
- Optional hosted resource pack endpoint

### Phase 5: Quality / advanced
- Conflict previews
- Rollback support
- Optional file selection in modpacks
- Parallel downloads with bounded concurrency
- Resume/retry downloads

## 14) Key implementation decisions (to avoid scope creep)

Recommended defaults for first implementation:
- Backend-only Modrinth API access (frontend calls Spawner only)
- Unauthenticated public Modrinth reads only
- Fabric-only for mod install/update
- Fabric-only `.mrpack` support initially
- Server must be offline for install/remove/enable/disable/update
- Track state in `.spawner/modrinth-state.json`
- Use `sha512` where possible

Deferred intentionally:
- OAuth/PAT support
- Forge/NeoForge/Quilt pack installation
- Full Modrinth account features
- Automatic dependency solver beyond what Modrinth version metadata exposes
- Live install while server running

## 15) Open questions before coding

These should be answered before implementation starts:
- Do you want modpack installs into:
  - existing instance only
  - new instance creation flow
  - both
- For disabled mods, prefer:
  - `mods/.disabled/`
  - `mods-disabled/`
  - filename suffix rename
- For resource packs “enable”, should Spawner:
  - only manage local files
  - also host the selected pack and write `server.properties`
- Should optional `.mrpack` server files be:
  - installed by default
  - skipped by default
  - user-selected in preview
- Should updates include `beta/alpha` or `release` only by default?

## 16) Sources (official docs used)

Modrinth docs:
- https://docs.modrinth.com/
- https://docs.modrinth.com/api/
- https://docs.modrinth.com/api/operations/searchprojects/
- https://docs.modrinth.com/api/operations/getproject/
- https://docs.modrinth.com/api/operations/getprojectversions/
- https://docs.modrinth.com/api/operations/versionfromhash/
- https://docs.modrinth.com/api/operations/getlatestversionfromhash/
- https://docs.modrinth.com/api/operations/versionsfromhashes/
- https://docs.modrinth.com/api/operations/getlatestversionsfromhashes/
- https://docs.modrinth.com/api/operations/categorylist/
- https://docs.modrinth.com/api/operations/loaderlist/
- https://docs.modrinth.com/api/operations/versionlist/
- https://docs.modrinth.com/api/operations/sidetypelist/
- https://docs.modrinth.com/guide/oauth/ (not needed for v1, but referenced for future auth)

Modrinth `.mrpack` spec/help article:
- https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack

