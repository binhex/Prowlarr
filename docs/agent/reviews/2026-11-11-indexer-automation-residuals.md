# Indexer Automation — Review Residuals (Round Cap)

- **Branch:** `develop` (BASE_REF `origin/develop`)
- **Commit at review:** `7b8e44bc5` (work uncommitted in the working tree at the time of the review)
- **Round reached:** 10 of 10 (`CIRCUIT_BREAKER_ROUNDS`)
- **Reviewer models:** Reviewer A `opencode-go/deepseek-v4.1-flash:max`, Reviewer B `opencode-go/glm-5.3-flash:max`
- **Scope reviewed:** indexer automation (new `Indexers/Automation/` core, migration 045, `/api/v1/indexer/automation`
  endpoints, settings page)

These are the findings reported by the **cap round** (round 10). Each fix was applied after the last reviewer pass and
therefore **has not been independently re-reviewed**. The review loop ended here by design; the ledger is the durable
record that travels with the branch.

## Findings

| Sev | Location | Finding | Status |
| --- | --- | --- | --- |
| MEDIUM | `IndexerAutomationService.cs` `TryAddCandidate` | Suppression TOCTOU: the suppression re-read, the presence query and `Create` wer. | applied, not re-reviewed: final re-read |
| MEDIUM | `IndexerAutomationService.cs` `RemoveStaleIndexers` | Failed removal left a lie: `RemovedAt`/`NextAttemptAt` persisted and the streak. | applied, not re-reviewed: streak cleared |
| MEDIUM | `IndexerCredentialDetector.cs` | Over-broad Cardigann skip: any definition without a settings block was treated a. | applied, not re-reviewed: only a `Login` |
| MEDIUM | `IndexerAutomationService.cs` disabled-row branch | The "skip unchanged writes" guard was a no-op (`TrackingSince != now` always tru. | applied, not re-reviewed: guard tests pr |
| MEDIUM | `IndexerCredentialDetector.cs` | The property-name heuristic ignored explicit non-secret `Privacy` metadata. | applied, not re-reviewed: heuristic gate |
| MEDIUM | `docs/agent/specs/2026-11-11-indexer-automation-design.md` §5.1/§5.2/§5.5 | Spec contradicted shipped behaviour (anchoring, re-anchor after a decrease, Card. | applied, not re-reviewed: spec updated t |
| LOW | `frontend/src/Store/Actions/indexerAutomationActions.js` | "Run Now" refetches state on the 202 and never again, so the table shows pre-run. | not fixed: needs command-completion sign |
| LOW | `src/coverlet.runsettings` | Unrelated test-infra change (collector friendly name, stale in-proc collector re. | not fixed, kept deliberately: tech-debt |

## Verification gap

The cap round's fixes were applied **after** the last reviewer pass, so they were never re-reviewed. They were,
however, compiled and exercised: `dotnet build src/Prowlarr.sln -c Debug` (0 warnings, 0 errors), 83/83 automation
unit tests, 11/11 API tests, `yarn eslint` clean on every changed frontend file, and `yarn build`
(webpack + fork-ts-checker) clean.

## Test evidence at the cap

- `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj` with the filter
  `FullyQualifiedName~IndexerTests.Automation|FullyQualifiedName~IndexerAutomationStateRepositoryFixture` → 83 passed.
- `dotnet test src/Prowlarr.Api.V1.Test/Prowlarr.Api.V1.Test.csproj` → 11 passed.
- Known pre-existing failures elsewhere in the Core suite (5, reproduced identically on clean `HEAD`, scoped out by the
  user during tech-debt remediation): system-time health check, two newznab/torznab log assertions, two update checks.
