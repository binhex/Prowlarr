# Indexer Automation (auto add / remove) — Design

- **Status:** Approved (design), not implemented
- **Date:** 2026-11-11
- **Repository:** `/data/forks/Prowlarr` (fork of Prowlarr, branch `develop`, v3.0.2)
- **Scope:** Prowlarr core (`.NET`), API v1, React settings UI

## 1. Overview and scope

An **opt-in, single global rule** that keeps Prowlarr's indexer list self-maintaining:

- **Add path** — discover available indexers matching the user's criteria (protocol, language, privacy,
  categories), probe each one, keep the ones that pass the probe, and retry failures after `RetryDays`.
- **Remove path** — delete any enabled indexer that has been in Prowlarr's *own* recorded failure state for at
  least `RemovalDays`, then let the add path re-admit it after `RetryDays`.

### Decisions taken during design

| Decision | Choice |
| --- | --- |
| Credentials | Only **credential-free** indexers are auto-added; everything else is skipped |
| Rule model | **Single global rule** (one settings section) |
| Failure signal | Existing Prowlarr failures recorded by `IndexerStatusService` (no extra probing of healthy sites) |
| Day-count state | **New automation state table** (not `IndexerStatus.InitialFailure`, which is never cleared) |
| Removal scope | **All enabled indexers**, manual or auto-added |
| UI surface | New **Settings → Indexer Automation** page |
| Test timeout | Applies to the **automation probe only** (default 60 s) |
| Manual deletes | **Suppressed** — auto-add never re-adds an indexer the user deleted |

### Non-goals

- No credential vault or per-rule credential templates.
- No per-indexer rule overrides or multiple named rules.
- No change to search/RSS-sync HTTP timeouts.
- No Sonarr/Radarr-side automation; Prowlarr's existing app sync still pushes added/removed indexers.
- No retroactive backfill of historical failures.

## 2. Components

All new core code lives under `src/NzbDrone.Core/Indexers/Automation/`:

| Component | Responsibility |
| --- | --- |
| `IndexerAutomationCommand : Command` | Scheduled trigger, registered in `TaskManager.Handle(ApplicationStartedEvent)` defaults at **60 minutes** |
| `IndexerAutomationService` | Orchestrates one reconciliation pass; also `IHandle<ProviderDeletedEvent<IIndexer>>` to record manual deletes |
| `IndexerAutomationSettingsService` | Reads/writes settings via `IConfigService`, JSON (de)serialises list values, clamps ranges |
| `IIndexerCandidateProvider` / `IndexerCandidateProvider` | Turns `IIndexerFactory.GetDefaultDefinitions()` into `IndexerCandidate` records |
| `IndexerCriteriaMatcher` | Pure matching and credential detection; no I/O |
| `IndexerAutomationProbe` | Wraps `IIndexerFactory.Test(definition)` in a wall-clock timeout |
| `IndexerAutomationState` + `IIndexerAutomationStateRepository` | Persistence for failure streaks, retry times, suppression |
| Migration `045_indexer_automation_states` | Creates the state table |

Reused unchanged: `IndexerFactory` (`GetDefaultDefinitions`, `Create`, `Test`, `Delete`), `IndexerStatusService`,
`AppProfileService`, `TaskManager`/`Scheduler`, `IConfigService`.

New API and UI:

- `src/Prowlarr.Api.V1/Indexers/IndexerAutomationController.cs` (+ resources)
- `frontend/src/Settings/Indexers/Automation/` page, route, and a link in `frontend/src/Settings/Settings.js`

### Pass flow

`IndexerAutomationService.Execute(IndexerAutomationCommand)`:

1. Take the pass lock (no overlapping passes); load settings, no-op when disabled.
2. **Phase 1** — refresh failure streaks from `IndexerStatusService`.
3. **Phase 2** — remove indexers whose failed streak reached the threshold.
4. **Phase 3** — reconcile suppression (clear it when the key exists again).
5. **Phase 4** — enumerate candidates, match, filter to due ones, probe and add (bounded per pass).

## 3. Settings model

Stored in the existing `Config` key/value table through `IConfigService`; list values are JSON strings.

| Key | Type | Default | Range |
| --- | --- | --- | --- |
| `IndexerAutomationEnabled` | bool | `false` | — |
| `IndexerAutomationProtocols` | JSON `["torrent","usenet"]` | empty (= any) | — |
| `IndexerAutomationLanguages` | JSON `["en-US", ...]` | empty (= any) | — |
| `IndexerAutomationPrivacy` | JSON `["public","semiPrivate","private"]` | empty (= any) | — |
| `IndexerAutomationCategories` | JSON `[2000, 5000, ...]` (Newznab ids) | empty (= any) | — |
| `IndexerAutomationTestTimeoutSeconds` | int | `60` | 10–600 |
| `IndexerAutomationRetryDays` | int | `7` | 1–365 |
| `IndexerAutomationRemovalDays` | int | `14` | 1–365 |
| `IndexerAutomationMaxAddsPerPass` | int | `3` | 1–20 |

An **empty dimension is unconstrained**, so enabling with only categories selected is valid.

## 4. State table

`IndexerAutomationStates`, one row per candidate key:

| Column | Notes |
| --- | --- |
| `Key` | Unique. Native: `Implementation` (e.g. `Anidex`). Cardigann: `Cardigann:<definition file>` |
| `IndexerId` | Current `Indexers.Id` when the indexer exists, otherwise null |
| `FailureSince` | Start of the current failed streak; cleared on recovery |
| `LastFailureAt` | Most recent pass that observed a failed state |
| `LastAttemptAt` | Last add/probe attempt |
| `NextAttemptAt` | Earliest time the candidate may be probed again |
| `LastAttemptResult` | `Passed` / `Failed` / `Removed` / `Suppressed` |
| `RemovedAt` | When the automation deleted the indexer |
| `Suppressed` / `SuppressedAt` | Manual-delete suppression and its timestamp |

## 5. Behaviour

### 5.1 Failure tracking (removal input)

Each pass reads `IndexerStatusService` for every enabled indexer and upserts a state row for it (keyed by its
candidate key, with `IndexerId`). Failed means `EscalationLevel > 0`; recovered means `EscalationLevel == 0`. On the
first pass that observes failure, `FailureSince = now`; on recovery it is cleared. This is deliberately independent of
`InitialFailure`, which Prowlarr never clears on success.

Accepted consequence: an indexer that is never queried never records failures and is therefore never removed.

### 5.2 Removal

When `now - FailureSince >= RemovalDays`, the indexer is deleted through `IIndexerFactory.Delete` (its status row is
removed by the existing `ProviderDeletedEvent` handler). The state row records `RemovedAt`,
`NextAttemptAt = now + RetryDays`, `LastAttemptResult = Removed`, and clears `FailureSince`.

### 5.3 Discovery and add

Candidates come from `GetDefaultDefinitions()` and must be: credential-free, not already present (key match — an
existing indexer counts as present whether enabled or disabled), not suppressed, due (`NextAttemptAt` null or in the
past), and matching the criteria. Candidates are processed in key order, at most `MaxAddsPerPass` per pass:

1. Clone the default definition; set `Enable = true`,
   `AppProfileId = AppProfileService.GetDefaultProfile("Standard").Id`, priority 25.
2. **Probe the unsaved definition** (`Id = 0`) — the same path as the manual "Test" button, so nothing is persisted
   and no add/remove churn is pushed to apps.
3. Pass → `Create(definition)`, record `Passed` with the new `IndexerId`, clear failure fields.
4. Fail or timeout → record `Failed` and `NextAttemptAt = now + RetryDays`. Nothing is created.

### 5.4 Matching rules

- Protocol: candidate protocol must be in the selected set.
- Privacy: candidate privacy must be in the selected set.
- Language: case-insensitive comparison against the candidate's language; `null`/`"Any"` matches only when no
  languages are selected.
- Categories: **ANY** semantics — the intersection of the candidate's capability category ids with the selection must
  be non-empty.

### 5.5 Credential detection

- **Native definitions** — reflect `FieldDefinitionAttribute` over the settings contract; any `PrivacyLevel` of
  `UserName`, `Password` or `ApiKey`, or any `FieldType.Password`, disqualifies the candidate.
- **Cardigann definitions** — disqualify when the YML has a `Login` block, or has a settings field whose `Type` is
  `password` or whose name is one of `username`, `password`, `apikey`, `cookie`, `rsskey`, `token`. A definition with
  no settings at all is credential-free when it has no `Login` block.

### 5.6 Suppression

A manual delete sets `Suppressed = true` for that key. The service records its own in-flight deletions so the
`ProviderDeletedEvent` handler can distinguish automation deletions from manual ones. Suppression is cleared by the
explicit UI action, or automatically when an indexer with that key exists again (re-added by hand).

### 5.7 Idempotency and ordering

Phase order is fixed (remove before add). Every phase tolerates an indexer or candidate disappearing mid-pass. The
pass lock prevents overlap with a manual "Run now" trigger.

## 6. API

| Route | Purpose |
| --- | --- |
| `GET /api/v1/indexer/automation` | Settings plus option lists (languages/privacy/protocols derived from candidates, category tree from the Newznab standard categories) |
| `PUT /api/v1/indexer/automation` | Validate and save settings |
| `GET /api/v1/indexer/automation/state` | State rows for display (name, key, status, failureSince, nextAttemptAt, suppressed) |
| `POST /api/v1/indexer/automation/run` | Enqueue the command manually |
| `DELETE /api/v1/indexer/automation/state/{key}` | Clear a suppression |

## 7. UI

New page **Settings → Indexer Automation** at `frontend/src/Settings/Indexers/Automation/`, linked from
`frontend/src/Settings/Settings.js` with a matching route.

Contents:

- Enable toggle.
- Protocol, privacy, language and category multi-selects, reusing the components already used by
  `frontend/src/Indexer/Add/`.
- Numeric fields for timeout, retry days, removal days and max adds per pass, with the ranges above.
- "Run now" button.
- Read-only state table with a per-row "Clear suppression" action.

## 8. Error handling

- **Probe timeout** — hard wall-clock cap via `Task.WhenAny(probe, Task.Delay(timeout))`. Because the definition is
  unsaved (`Id = 0`), an abandoned probe cannot write status rows or cookies: both `UpdateCookies` and
  `RecordFailure` guard on `id > 0`.
- **Probe exception / validation failure / create failure** — logged and recorded as `Failed` with the message; never
  rethrown out of the pass.
- **Delete failure** — logged and left unmodified so the next pass retries it.
- **Missing default app profile** — the candidate is skipped with a warning rather than creating an indexer without a
  sync profile.
- **Empty Cardigann definition cache** — only native candidates are considered; logged at debug level.

## 9. Testing

- `IndexerCriteriaMatcher`: credential detection for native `NoAuth`/`UserPass`/`Cookie` contracts and Cardigann
  definitions with and without a `Login` block; ANY-category semantics; empty dimension = unconstrained; key
  derivation for both implementations.
- `IndexerAutomationService` with fake factory/status/settings: streak start and clear, threshold boundary (just
  under, exactly at, over), retry gating, `MaxAddsPerPass` cap, suppression on manual delete, no suppression on
  automation delete, complete no-op when disabled.
- `IndexerAutomationProbe`: a slow fake indexer asserts `Failed` and that `Create` was never called.
- Controller tests for `GET`/`PUT`, including range clamping.
- Frontend: manual verification only — the repository has no frontend test suite.

## 10. Accepted trade-offs

- Deleting a manually configured indexer discards its credentials; the user must re-enter them if it returns.
- Indexers that are never queried never accumulate failures and are never removed.
- Settings live as JSON strings in the `Config` table rather than typed columns.
- A pass can take up to `MaxAddsPerPass × TestTimeoutSeconds`.
- The pass interval is fixed at 60 minutes in this version and is not user-configurable.
