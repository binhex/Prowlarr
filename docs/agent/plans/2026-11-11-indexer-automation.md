# Indexer Automation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use sub-agents (recommended) to implement this plan
task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in, single global rule to Prowlarr that auto-adds credential-free indexers matching user
criteria (with a probe), and auto-removes indexers whose recorded failure streak exceeds a day threshold.

**Architecture:** One hourly `IndexerAutomationCommand` drives `IndexerAutomationService`, which runs four fixed
phases per pass: refresh failure streaks, remove stale indexers, reconcile suppression, then discover/probe/add due
candidates. Candidates come from `IIndexerFactory.GetDefaultDefinitions()`; probes run against unsaved definitions
(`Id = 0`) so nothing is persisted unless the probe passes. State (streaks, retry times, suppression) lives in a new
`IndexerAutomationStates` table. Settings live in the existing `Config` table via new `IConfigService` properties. A
new settings page and `/api/v1/indexer/automation` endpoints expose configuration and state.

**Tech Stack:** C# / .NET 10 (SDK 10.0.403 per `global.json`), DryIoc DI (convention-based registration),
Dapper + FluentMigrator, NUnit + Moq + FluentAssertions (`AutoMoq`/`Mocker`), React + Redux (frontend),
FluentValidation.

**Design spec:** `docs/agent/specs/2026-11-11-indexer-automation-design.md`

---

## Scope check

The spec covers **one** subsystem (indexer automation) with three layers (core, API v1, React settings UI).
It is a single coherent plan; no split required.

## Toolchain prerequisite (read before Task 1)

`dotnet` is **not installed** in this environment and `global.json` pins SDK `10.0.403`.

- If `dotnet --version` fails, install SDK 10.0.403 **before** starting, or run the backend verification steps and
  report them as BLOCKED. Never report tests as passing without a real run.
- `yarn` and `node_modules` are also absent. Frontend verification requires `yarn install` (or `npm install`) in the
  repo root. If that cannot be done, mark the frontend lint/build gate as BLOCKED and say so explicitly in the final
  report.

## File map

### Create — core

| File | Responsibility |
| --- | --- |
| `IndexerAutomationCommand.cs` | Scheduled command marker |
| `IndexerAutomationState.cs` | Persisted per-candidate state model + attempt-result constants |
| `IndexerAutomationStateRepository.cs` | Repository with `FindByKey` |
| `IndexerAutomationSettings.cs` | Settings DTO |
| `IndexerAutomationSettingsService.cs` | Read/write/clamp settings through `IConfigService` |
| `IndexerCandidate.cs` | Candidate record + stable key derivation |
| `IndexerCredentialDetector.cs` | Pure credential-free detection |
| `IndexerCriteriaMatcher.cs` | Pure criteria matching |
| `IndexerCandidateProvider.cs` | Enumerates candidates from `GetDefaultDefinitions()` |
| `IndexerAutomationProbe.cs` | Wall-clock-timeout probe wrapper |
| `IndexerAutomationService.cs` | Orchestrates the four phases + delete-event suppression |

### Create — other

| File | Responsibility |
| --- | --- |
| `src/NzbDrone.Core/Datastore/Migration/045_indexer_automation_states.cs` | Creates the state table |
| `src/Prowlarr.Api.V1/Indexers/IndexerAutomationController.cs` | HTTP endpoints |
| `src/Prowlarr.Api.V1/Indexers/IndexerAutomationResource.cs` | Settings resource + mapper |
| `src/Prowlarr.Api.V1/Indexers/IndexerAutomationStateResource.cs` | State resource + mapper |
| `frontend/src/Store/Actions/Settings/indexerAutomation.js` | Settings redux section |
| `frontend/src/Store/Actions/indexerAutomationActions.js` | State-rows redux section |
| `frontend/src/Settings/Indexers/Automation/IndexerAutomationSettings.js` | Page component |
| `frontend/src/Settings/Indexers/Automation/IndexerAutomationSettingsConnector.js` | Redux connector |
| `frontend/src/Settings/Indexers/Automation/IndexerAutomationStateTable.js` | State table component |

### Create — tests

| File | Responsibility |
| --- | --- |
| `src/NzbDrone.Core.Test/Datastore/IndexerAutomationStateRepositoryFixture.cs` | Repository round-trip |
| `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationSettingsServiceFixture.cs` | Defaults + clamping |
| `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerCredentialDetectorFixture.cs` | Credential detection |
| `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerCriteriaMatcherFixture.cs` | Matching rules |
| `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerCandidateProviderFixture.cs` | Enumeration + keys |
| `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationProbeFixture.cs` | Timeout behaviour |
| `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationServiceFixture.cs` | All four phases |
| `src/Prowlarr.Api.V1.Test/IndexerAutomationTests/IndexerAutomationControllerFixture.cs` | Resource mapping |

### Modify

| File | Change |
| --- | --- |
| `src/NzbDrone.Core/Configuration/IConfigService.cs` | 9 new properties |
| `src/NzbDrone.Core/Configuration/ConfigService.cs` | 9 new property implementations |
| `src/NzbDrone.Core/Datastore/TableMapping.cs` | Register `IndexerAutomationState` |
| `src/NzbDrone.Core/Jobs/TaskManager.cs` | Add 60-minute default task |
| `src/NzbDrone.Core/Localization/Core/en.json` | New UI strings |
| `src/Prowlarr.Api.V1.Test/Prowlarr.Api.V1.Test.csproj` | Reference `Prowlarr.Api.V1` |
| `frontend/src/Store/Actions/settingsActions.js` | Register settings section |
| `frontend/src/Store/Actions/index.js` | Register state-rows section |
| `frontend/src/App/AppRoutes.tsx` | New route |
| `frontend/src/Settings/Settings.js` | New settings link |

---

### Task 1: Persisted state model, repository and migration

**Files:**

- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerAutomationState.cs`
- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerAutomationStateRepository.cs`
- Create: `src/NzbDrone.Core/Datastore/Migration/045_indexer_automation_states.cs`
- Modify: `src/NzbDrone.Core/Datastore/TableMapping.cs:29-40`
- Test: `src/NzbDrone.Core.Test/Datastore/IndexerAutomationStateRepositoryFixture.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore
{
    [TestFixture]
    public class IndexerAutomationStateRepositoryFixture : DbTest<IndexerAutomationStateRepository, IndexerAutomationState>
    {
        [Test]
        public void should_insert_and_find_by_key()
        {
            Subject.Upsert(new IndexerAutomationState
            {
                Key = "Cardigann:anidex.yml",
                IndexerId = 12,
                FailureSince = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                LastAttemptResult = IndexerAutomationAttemptResult.Failed,
                Suppressed = false
            });

            var stored = Subject.FindByKey("Cardigann:anidex.yml");

            stored.Should().NotBeNull();
            stored.IndexerId.Should().Be(12);
            stored.LastAttemptResult.Should().Be(IndexerAutomationAttemptResult.Failed);
            stored.Suppressed.Should().BeFalse();
            stored.FailureSince.Should().BeCloseTo(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(1));
        }

        [Test]
        public void should_return_null_for_unknown_key()
        {
            Subject.FindByKey("nope").Should().BeNull();
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerAutomationStateRepositoryFixture"`
Expected: build error — `IndexerAutomationState` / `IndexerAutomationStateRepository` do not exist.

- [ ] **Step 3: Create the model**

`src/NzbDrone.Core/Indexers/Automation/IndexerAutomationState.cs`:

```csharp
using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Indexers.Automation
{
    public static class IndexerAutomationAttemptResult
    {
        public const string Passed = "Passed";
        public const string Failed = "Failed";
        public const string Removed = "Removed";
        public const string Suppressed = "Suppressed";
    }

    public class IndexerAutomationState : ModelBase
    {
        public string Key { get; set; }
        public int? IndexerId { get; set; }
        public DateTime? FailureSince { get; set; }
        public DateTime? LastFailureAt { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public string LastAttemptResult { get; set; }
        public DateTime? RemovedAt { get; set; }
        public bool Suppressed { get; set; }
        public DateTime? SuppressedAt { get; set; }
    }
}
```

- [ ] **Step 4: Create the repository**

`src/NzbDrone.Core/Indexers/Automation/IndexerAutomationStateRepository.cs`:

```csharp
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Indexers.Automation
{
    public interface IIndexerAutomationStateRepository : IBasicRepository<IndexerAutomationState>
    {
        IndexerAutomationState FindByKey(string key);
    }

    public class IndexerAutomationStateRepository : BasicRepository<IndexerAutomationState>, IIndexerAutomationStateRepository
    {
        public IndexerAutomationStateRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public IndexerAutomationState FindByKey(string key)
        {
            return Query(x => x.Key == key).SingleOrDefault();
        }
    }
}
```

- [ ] **Step 5: Create the migration**

`src/NzbDrone.Core/Datastore/Migration/045_indexer_automation_states.cs`:

```csharp
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(045)]
    public class indexer_automation_states : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.TableForModel("IndexerAutomationStates")
                  .WithColumn("Key").AsString().NotNullable().Unique()
                  .WithColumn("IndexerId").AsInt32().Nullable()
                  .WithColumn("FailureSince").AsDateTimeOffset().Nullable()
                  .WithColumn("LastFailureAt").AsDateTimeOffset().Nullable()
                  .WithColumn("LastAttemptAt").AsDateTimeOffset().Nullable()
                  .WithColumn("NextAttemptAt").AsDateTimeOffset().Nullable()
                  .WithColumn("LastAttemptResult").AsString().Nullable()
                  .WithColumn("RemovedAt").AsDateTimeOffset().Nullable()
                  .WithColumn("Suppressed").AsBoolean().NotNullable().WithDefaultValue(false)
                  .WithColumn("SuppressedAt").AsDateTimeOffset().Nullable();
        }
    }
}
```

- [ ] **Step 6: Register the entity in TableMapping**

In `src/NzbDrone.Core/Datastore/TableMapping.cs`, add the using at the top:

```csharp
using NzbDrone.Core.Indexers.Automation;
```

and register the model directly after the `IndexerDefinition` mapping block (`Mapper.Entity<IndexerDefinition>(...)`):

```csharp
            Mapper.Entity<IndexerAutomationState>("IndexerAutomationStates").RegisterModel();
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerAutomationStateRepositoryFixture"`
Expected: PASS (2 tests).

- [ ] **Step 8: Commit**

```bash
git add src/NzbDrone.Core/Indexers/Automation/IndexerAutomationState.cs \
        src/NzbDrone.Core/Indexers/Automation/IndexerAutomationStateRepository.cs \
        src/NzbDrone.Core/Datastore/Migration/045_indexer_automation_states.cs \
        src/NzbDrone.Core/Datastore/TableMapping.cs \
        src/NzbDrone.Core.Test/Datastore/IndexerAutomationStateRepositoryFixture.cs
git commit -m "New: Add indexer automation state table"
```

---

### Task 2: Settings DTO, config properties and settings service

**Files:**

- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerAutomationSettings.cs`
- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerAutomationSettingsService.cs`
- Modify: `src/NzbDrone.Core/Configuration/IConfigService.cs:46-53`
- Modify: `src/NzbDrone.Core/Configuration/ConfigService.cs:82-88`
- Test: `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationSettingsServiceFixture.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerAutomationSettingsServiceFixture : CoreTest<IndexerAutomationSettingsService>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationEnabled).Returns(false);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationProtocols).Returns("[]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationLanguages).Returns("[]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationPrivacy).Returns("[]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationCategories).Returns("[]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationTestTimeoutSeconds).Returns(60);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationRetryDays).Returns(7);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationRemovalDays).Returns(14);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationMaxAddsPerPass).Returns(3);
        }

        [Test]
        public void should_return_defaults_when_nothing_configured()
        {
            var settings = Subject.Get();

            settings.Enabled.Should().BeFalse();
            settings.Protocols.Should().BeEmpty();
            settings.Languages.Should().BeEmpty();
            settings.Privacy.Should().BeEmpty();
            settings.Categories.Should().BeEmpty();
            settings.TestTimeoutSeconds.Should().Be(60);
            settings.RetryDays.Should().Be(7);
            settings.RemovalDays.Should().Be(14);
            settings.MaxAddsPerPass.Should().Be(3);
        }

        [Test]
        public void should_deserialize_json_list_values()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationProtocols).Returns("[\"torrent\",\"usenet\"]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationPrivacy).Returns("[\"public\",\"semiPrivate\"]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationLanguages).Returns("[\"en-US\"]");
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationCategories).Returns("[2000,5000]");

            var settings = Subject.Get();

            settings.Protocols.Should().BeEquivalentTo(new List<DownloadProtocol> { DownloadProtocol.Torrent, DownloadProtocol.Usenet });
            settings.Privacy.Should().BeEquivalentTo(new List<IndexerPrivacy> { IndexerPrivacy.Public, IndexerPrivacy.SemiPrivate });
            settings.Languages.Should().BeEquivalentTo(new List<string> { "en-US" });
            settings.Categories.Should().BeEquivalentTo(new List<int> { 2000, 5000 });
        }

        [Test]
        public void should_clamp_out_of_range_values_on_read()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationTestTimeoutSeconds).Returns(5);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationRetryDays).Returns(0);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationRemovalDays).Returns(9999);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.IndexerAutomationMaxAddsPerPass).Returns(500);

            var settings = Subject.Get();

            settings.TestTimeoutSeconds.Should().Be(10);
            settings.RetryDays.Should().Be(1);
            settings.RemovalDays.Should().Be(365);
            settings.MaxAddsPerPass.Should().Be(20);
        }

        [Test]
        public void should_clamp_and_serialize_on_save()
        {
            Subject.Save(new IndexerAutomationSettings
            {
                Enabled = true,
                Protocols = new List<DownloadProtocol> { DownloadProtocol.Torrent },
                TestTimeoutSeconds = 1,
                RetryDays = 7,
                RemovalDays = 14,
                MaxAddsPerPass = 3
            });

            Mocker.GetMock<IConfigService>().VerifySet(c => c.IndexerAutomationEnabled = true, Times.Once());
            Mocker.GetMock<IConfigService>().VerifySet(c => c.IndexerAutomationTestTimeoutSeconds = 10, Times.Once());
            Mocker.GetMock<IConfigService>().VerifySet(c => c.IndexerAutomationProtocols = "[\"torrent\"]", Times.Once());
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerAutomationSettingsServiceFixture"`
Expected: build error — settings types and config properties do not exist.

- [ ] **Step 3: Create the settings DTO**

`src/NzbDrone.Core/Indexers/Automation/IndexerAutomationSettings.cs`:

```csharp
using System.Collections.Generic;

namespace NzbDrone.Core.Indexers.Automation
{
    public class IndexerAutomationSettings
    {
        public bool Enabled { get; set; }
        public List<DownloadProtocol> Protocols { get; set; } = new List<DownloadProtocol>();
        public List<string> Languages { get; set; } = new List<string>();
        public List<IndexerPrivacy> Privacy { get; set; } = new List<IndexerPrivacy>();
        public List<int> Categories { get; set; } = new List<int>();
        public int TestTimeoutSeconds { get; set; } = 60;
        public int RetryDays { get; set; } = 7;
        public int RemovalDays { get; set; } = 14;
        public int MaxAddsPerPass { get; set; } = 3;
    }
}
```

- [ ] **Step 4: Add the config properties to the interface**

In `src/NzbDrone.Core/Configuration/IConfigService.cs`, inside the `// Indexers` block, after
`bool LogIndexerResponse { get; set; }`:

```csharp
        // Indexer Automation
        bool IndexerAutomationEnabled { get; set; }
        string IndexerAutomationProtocols { get; set; }
        string IndexerAutomationLanguages { get; set; }
        string IndexerAutomationPrivacy { get; set; }
        string IndexerAutomationCategories { get; set; }
        int IndexerAutomationTestTimeoutSeconds { get; set; }
        int IndexerAutomationRetryDays { get; set; }
        int IndexerAutomationRemovalDays { get; set; }
        int IndexerAutomationMaxAddsPerPass { get; set; }
```

- [ ] **Step 5: Implement them in ConfigService**

In `src/NzbDrone.Core/Configuration/ConfigService.cs`, after the `LogIndexerResponse` property:

```csharp
        public bool IndexerAutomationEnabled
        {
            get { return GetValueBoolean("IndexerAutomationEnabled", false); }
            set { SetValue("IndexerAutomationEnabled", value); }
        }

        public string IndexerAutomationProtocols
        {
            get { return GetValue("IndexerAutomationProtocols", "[]"); }
            set { SetValue("IndexerAutomationProtocols", value); }
        }

        public string IndexerAutomationLanguages
        {
            get { return GetValue("IndexerAutomationLanguages", "[]"); }
            set { SetValue("IndexerAutomationLanguages", value); }
        }

        public string IndexerAutomationPrivacy
        {
            get { return GetValue("IndexerAutomationPrivacy", "[]"); }
            set { SetValue("IndexerAutomationPrivacy", value); }
        }

        public string IndexerAutomationCategories
        {
            get { return GetValue("IndexerAutomationCategories", "[]"); }
            set { SetValue("IndexerAutomationCategories", value); }
        }

        public int IndexerAutomationTestTimeoutSeconds
        {
            get { return GetValueInt("IndexerAutomationTestTimeoutSeconds", 60); }
            set { SetValue("IndexerAutomationTestTimeoutSeconds", value); }
        }

        public int IndexerAutomationRetryDays
        {
            get { return GetValueInt("IndexerAutomationRetryDays", 7); }
            set { SetValue("IndexerAutomationRetryDays", value); }
        }

        public int IndexerAutomationRemovalDays
        {
            get { return GetValueInt("IndexerAutomationRemovalDays", 14); }
            set { SetValue("IndexerAutomationRemovalDays", value); }
        }

        public int IndexerAutomationMaxAddsPerPass
        {
            get { return GetValueInt("IndexerAutomationMaxAddsPerPass", 3); }
            set { SetValue("IndexerAutomationMaxAddsPerPass", value); }
        }
```

- [ ] **Step 6: Create the settings service**

`src/NzbDrone.Core/Indexers/Automation/IndexerAutomationSettingsService.cs`:

```csharp
using System;
using System.Collections.Generic;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Indexers.Automation
{
    public interface IIndexerAutomationSettingsService
    {
        IndexerAutomationSettings Get();
        void Save(IndexerAutomationSettings settings);
    }

    public class IndexerAutomationSettingsService : IIndexerAutomationSettingsService
    {
        public const int MinimumTestTimeoutSeconds = 10;
        public const int MaximumTestTimeoutSeconds = 600;
        public const int MinimumDays = 1;
        public const int MaximumDays = 365;
        public const int MinimumAddsPerPass = 1;
        public const int MaximumAddsPerPass = 20;

        private readonly IConfigService _configService;

        public IndexerAutomationSettingsService(IConfigService configService)
        {
            _configService = configService;
        }

        public IndexerAutomationSettings Get()
        {
            var settings = new IndexerAutomationSettings
            {
                Enabled = _configService.IndexerAutomationEnabled,
                Protocols = DeserializeList<DownloadProtocol>(_configService.IndexerAutomationProtocols),
                Languages = DeserializeList<string>(_configService.IndexerAutomationLanguages),
                Privacy = DeserializeList<IndexerPrivacy>(_configService.IndexerAutomationPrivacy),
                Categories = DeserializeList<int>(_configService.IndexerAutomationCategories),
                TestTimeoutSeconds = _configService.IndexerAutomationTestTimeoutSeconds,
                RetryDays = _configService.IndexerAutomationRetryDays,
                RemovalDays = _configService.IndexerAutomationRemovalDays,
                MaxAddsPerPass = _configService.IndexerAutomationMaxAddsPerPass
            };

            return Clamp(settings);
        }

        public void Save(IndexerAutomationSettings settings)
        {
            var clamped = Clamp(settings);

            _configService.IndexerAutomationEnabled = clamped.Enabled;
            _configService.IndexerAutomationProtocols = STJson.ToJson(clamped.Protocols);
            _configService.IndexerAutomationLanguages = STJson.ToJson(clamped.Languages);
            _configService.IndexerAutomationPrivacy = STJson.ToJson(clamped.Privacy);
            _configService.IndexerAutomationCategories = STJson.ToJson(clamped.Categories);
            _configService.IndexerAutomationTestTimeoutSeconds = clamped.TestTimeoutSeconds;
            _configService.IndexerAutomationRetryDays = clamped.RetryDays;
            _configService.IndexerAutomationRemovalDays = clamped.RemovalDays;
            _configService.IndexerAutomationMaxAddsPerPass = clamped.MaxAddsPerPass;
        }

        public static IndexerAutomationSettings Clamp(IndexerAutomationSettings settings)
        {
            settings.Protocols ??= new List<DownloadProtocol>();
            settings.Languages ??= new List<string>();
            settings.Privacy ??= new List<IndexerPrivacy>();
            settings.Categories ??= new List<int>();

            settings.TestTimeoutSeconds = Math.Clamp(settings.TestTimeoutSeconds, MinimumTestTimeoutSeconds, MaximumTestTimeoutSeconds);
            settings.RetryDays = Math.Clamp(settings.RetryDays, MinimumDays, MaximumDays);
            settings.RemovalDays = Math.Clamp(settings.RemovalDays, MinimumDays, MaximumDays);
            settings.MaxAddsPerPass = Math.Clamp(settings.MaxAddsPerPass, MinimumAddsPerPass, MaximumAddsPerPass);

            return settings;
        }

        private static List<T> DeserializeList<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<T>();
            }

            return STJson.Deserialize<List<T>>(json) ?? new List<T>();
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerAutomationSettingsServiceFixture"`
Expected: PASS (4 tests).

- [ ] **Step 8: Commit**

```bash
git add src/NzbDrone.Core/Indexers/Automation/IndexerAutomationSettings.cs \
        src/NzbDrone.Core/Indexers/Automation/IndexerAutomationSettingsService.cs \
        src/NzbDrone.Core/Configuration/IConfigService.cs \
        src/NzbDrone.Core/Configuration/ConfigService.cs \
        src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationSettingsServiceFixture.cs
git commit -m "New: Add indexer automation settings"
```

---

### Task 3: Credential detector

**Files:**

- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerCredentialDetector.cs`
- Test: `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerCredentialDetectorFixture.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Indexers.Definitions.Cardigann;
using NzbDrone.Core.Indexers.Settings;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerCredentialDetectorFixture
    {
        private class NoAuthSettings : NoAuthTorrentBaseSettings
        {
        }

        private class UserPassSettings : UserPassTorrentBaseSettings
        {
        }

        private class CookieSettings : CookieTorrentBaseSettings
        {
        }

        private class ApiKeySettings : NoAuthTorrentBaseSettings
        {
            [FieldDefinition(4, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey)]
            public string ApiKey { get; set; }
        }

        private static IndexerDefinition Definition(object settings)
        {
            return new IndexerDefinition { Implementation = "Test", Settings = (IProviderConfig)settings };
        }

        [Test]
        public void should_treat_no_auth_settings_as_credential_free()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new NoAuthSettings()), null).Should().BeFalse();
        }

        [Test]
        public void should_detect_username_password_settings()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new UserPassSettings()), null).Should().BeTrue();
        }

        [Test]
        public void should_detect_cookie_settings()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new CookieSettings()), null).Should().BeTrue();
        }

        [Test]
        public void should_detect_api_key_field()
        {
            IndexerCredentialDetector.RequiresCredentials(Definition(new ApiKeySettings()), null).Should().BeTrue();
        }

        [Test]
        public void should_treat_cardigann_without_login_as_credential_free()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "anidex.yml" });
            var cardigann = new CardigannDefinition { Settings = null, Login = null };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeFalse();
        }

        [Test]
        public void should_treat_cardigann_with_login_block_as_requiring_credentials()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "private.yml" });
            var cardigann = new CardigannDefinition { Login = new LoginBlock { Path = "login.php" } };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeTrue();
        }

        [Test]
        public void should_detect_cardigann_password_field()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "apikey.yml" });
            var cardigann = new CardigannDefinition
            {
                Settings = new List<SettingsField> { new SettingsField { Name = "passkey", Type = "password" } }
            };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeTrue();
        }

        [Test]
        public void should_detect_cardigann_auth_shaped_field_name()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "rss.yml" });
            var cardigann = new CardigannDefinition
            {
                Settings = new List<SettingsField> { new SettingsField { Name = "rsskey", Type = "text" } }
            };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeTrue();
        }

        [Test]
        public void should_treat_cardigann_with_only_sort_fields_as_credential_free()
        {
            var definition = Definition(new CardigannSettings { DefinitionFile = "sort.yml" });
            var cardigann = new CardigannDefinition
            {
                Settings = new List<SettingsField> { new SettingsField { Name = "sort", Type = "select" } }
            };

            IndexerCredentialDetector.RequiresCredentials(definition, cardigann).Should().BeFalse();
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerCredentialDetectorFixture"`
Expected: build error — `IndexerCredentialDetector` does not exist.

- [ ] **Step 3: Implement the detector**

`src/NzbDrone.Core/Indexers/Automation/IndexerCredentialDetector.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Reflection;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers.Definitions.Cardigann;

namespace NzbDrone.Core.Indexers.Automation
{
    public static class IndexerCredentialDetector
    {
        private static readonly HashSet<string> CredentialFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "username",
            "password",
            "apikey",
            "cookie",
            "rsskey",
            "token"
        };

        public static bool RequiresCredentials(IndexerDefinition definition, CardigannDefinition cardigannDefinition)
        {
            if (cardigannDefinition != null)
            {
                return RequiresCardigannCredentials(cardigannDefinition);
            }

            if (definition.Settings is CardigannSettings)
            {
                // A Cardigann definition whose YML is not cached cannot be classified, so treat it as credentialed.
                return true;
            }

            return definition.Settings != null && HasCredentialField(definition.Settings.GetType());
        }

        private static bool RequiresCardigannCredentials(CardigannDefinition definition)
        {
            if (definition.Login != null)
            {
                return true;
            }

            return (definition.Settings ?? new List<SettingsField>()).Any(IsCredentialField);
        }

        private static bool IsCredentialField(SettingsField field)
        {
            if (field == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(field.Type) && field.Type.Equals("password", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(field.Name) && CredentialFieldNames.Contains(field.Name);
        }

        private static bool HasCredentialField(Type settingsType)
        {
            foreach (var property in settingsType.GetProperties())
            {
                var field = property.GetAttribute<FieldDefinitionAttribute>(false);

                if (field == null)
                {
                    continue;
                }

                if (field.Type == FieldType.Password)
                {
                    return true;
                }

                if (field.Privacy == PrivacyLevel.UserName ||
                    field.Privacy == PrivacyLevel.Password ||
                    field.Privacy == PrivacyLevel.ApiKey)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerCredentialDetectorFixture"`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NzbDrone.Core/Indexers/Automation/IndexerCredentialDetector.cs \
        src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerCredentialDetectorFixture.cs
git commit -m "New: Add indexer credential detector"
```

---

### Task 4: Candidate model and criteria matcher

**Files:**

- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerCandidate.cs`
- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerCriteriaMatcher.cs`
- Test: `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerCriteriaMatcherFixture.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Indexers.Definitions.Cardigann;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerCriteriaMatcherFixture
    {
        private static IndexerCandidate Candidate(
            DownloadProtocol protocol = DownloadProtocol.Torrent,
            IndexerPrivacy privacy = IndexerPrivacy.Public,
            string language = "en-US",
            params int[] categories)
        {
            return new IndexerCandidate
            {
                Key = "Test",
                Name = "Test",
                Protocol = protocol,
                Privacy = privacy,
                Language = language,
                CategoryIds = new HashSet<int>(categories)
            };
        }

        [Test]
        public void should_match_when_all_dimensions_are_empty()
        {
            IndexerCriteriaMatcher.Matches(Candidate(categories: 2000), new IndexerAutomationSettings()).Should().BeTrue();
        }

        [Test]
        public void should_not_match_when_protocol_is_not_selected()
        {
            var settings = new IndexerAutomationSettings { Protocols = new List<DownloadProtocol> { DownloadProtocol.Usenet } };

            IndexerCriteriaMatcher.Matches(Candidate(), settings).Should().BeFalse();
        }

        [Test]
        public void should_not_match_when_privacy_is_not_selected()
        {
            var settings = new IndexerAutomationSettings { Privacy = new List<IndexerPrivacy> { IndexerPrivacy.Private } };

            IndexerCriteriaMatcher.Matches(Candidate(), settings).Should().BeFalse();
        }

        [Test]
        public void should_match_language_case_insensitively()
        {
            var settings = new IndexerAutomationSettings { Languages = new List<string> { "EN-us" } };

            IndexerCriteriaMatcher.Matches(Candidate(language: "en-US"), settings).Should().BeTrue();
        }

        [Test]
        public void should_not_match_language_when_candidate_has_none_and_languages_are_selected()
        {
            var settings = new IndexerAutomationSettings { Languages = new List<string> { "en-US" } };

            IndexerCriteriaMatcher.Matches(Candidate(language: null), settings).Should().BeFalse();
        }

        [Test]
        public void should_match_any_selected_category()
        {
            var settings = new IndexerAutomationSettings { Categories = new List<int> { 2000, 5000 } };

            IndexerCriteriaMatcher.Matches(Candidate(categories: 5030, 5040), settings).Should().BeTrue();
        }

        [Test]
        public void should_not_match_when_no_selected_category_overlaps()
        {
            var settings = new IndexerAutomationSettings { Categories = new List<int> { 3000 } };

            IndexerCriteriaMatcher.Matches(Candidate(categories: 2000, 5000), settings).Should().BeFalse();
        }

        [Test]
        public void should_derive_key_for_native_definition()
        {
            var definition = new IndexerDefinition { Implementation = "Anidex" };

            IndexerCandidate.GetKey(definition).Should().Be("Anidex");
        }

        [Test]
        public void should_derive_key_for_cardigann_definition()
        {
            var definition = new IndexerDefinition
            {
                Implementation = "Cardigann",
                Settings = new CardigannSettings { DefinitionFile = "anidex.yml" }
            };

            IndexerCandidate.GetKey(definition).Should().Be("Cardigann:anidex.yml");
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerCriteriaMatcherFixture"`
Expected: build error — `IndexerCandidate` / `IndexerCriteriaMatcher` do not exist.

- [ ] **Step 3: Create the candidate model**

`src/NzbDrone.Core/Indexers/Automation/IndexerCandidate.cs`:

```csharp
using System.Collections.Generic;
using NzbDrone.Core.Indexers.Definitions.Cardigann;

namespace NzbDrone.Core.Indexers.Automation
{
    public class IndexerCandidate
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public IndexerPrivacy Privacy { get; set; }
        public string Language { get; set; }
        public HashSet<int> CategoryIds { get; set; } = new HashSet<int>();
        public bool RequiresCredentials { get; set; }
        public IndexerDefinition Definition { get; set; }

        public static string GetKey(IndexerDefinition definition)
        {
            if (definition.Settings is CardigannSettings cardigann)
            {
                return $"Cardigann:{cardigann.DefinitionFile}";
            }

            return definition.Implementation;
        }
    }
}
```

- [ ] **Step 4: Create the matcher**

`src/NzbDrone.Core/Indexers/Automation/IndexerCriteriaMatcher.cs`:

```csharp
using System;
using System.Linq;

namespace NzbDrone.Core.Indexers.Automation
{
    public static class IndexerCriteriaMatcher
    {
        public static bool Matches(IndexerCandidate candidate, IndexerAutomationSettings settings)
        {
            if (settings.Protocols.Count > 0 && !settings.Protocols.Contains(candidate.Protocol))
            {
                return false;
            }

            if (settings.Privacy.Count > 0 && !settings.Privacy.Contains(candidate.Privacy))
            {
                return false;
            }

            if (settings.Languages.Count > 0 && !MatchesLanguage(candidate.Language, settings))
            {
                return false;
            }

            if (settings.Categories.Count > 0 && !candidate.CategoryIds.Overlaps(settings.Categories))
            {
                return false;
            }

            return true;
        }

        private static bool MatchesLanguage(string language, IndexerAutomationSettings settings)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return false;
            }

            return settings.Languages.Any(selected => selected.Equals(language, StringComparison.OrdinalIgnoreCase));
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerCriteriaMatcherFixture"`
Expected: PASS (9 tests).

- [ ] **Step 6: Commit**

```bash
git add src/NzbDrone.Core/Indexers/Automation/IndexerCandidate.cs \
        src/NzbDrone.Core/Indexers/Automation/IndexerCriteriaMatcher.cs \
        src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerCriteriaMatcherFixture.cs
git commit -m "New: Add indexer criteria matcher"
```

---

### Task 5: Candidate provider

**Files:**

- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerCandidateProvider.cs`
- Test: `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerCandidateProviderFixture.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.IndexerVersions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Indexers.Definitions.Cardigann;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerCandidateProviderFixture : CoreTest<IndexerCandidateProvider>
    {
        private IndexerDefinition _native;
        private IndexerDefinition _cardigann;

        [SetUp]
        public void Setup()
        {
            _native = new IndexerDefinition
            {
                Name = "Anidex",
                Implementation = "Anidex",
                Protocol = DownloadProtocol.Torrent,
                Privacy = IndexerPrivacy.Public,
                Language = "en-US",
                Settings = new NzbDrone.Core.Indexers.Settings.NoAuthTorrentBaseSettings(),
                Capabilities = new IndexerCapabilities()
            };

            _cardigann = new IndexerDefinition
            {
                Name = "SomeTracker",
                Implementation = "Cardigann",
                Protocol = DownloadProtocol.Torrent,
                Privacy = IndexerPrivacy.Private,
                Language = "en-US",
                Settings = new CardigannSettings { DefinitionFile = "sometracker.yml" },
                Capabilities = new IndexerCapabilities()
            };

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.GetDefaultDefinitions())
                .Returns(new List<IndexerDefinition> { _native, _cardigann });

            Mocker.GetMock<IIndexerDefinitionUpdateService>()
                .Setup(d => d.GetCachedDefinition("sometracker.yml"))
                .Returns(new CardigannDefinition { Login = new LoginBlock { Path = "login.php" } });
        }

        [Test]
        public void should_map_native_definition_to_credential_free_candidate()
        {
            var candidate = Subject.GetCandidates().Single(c => c.Key == "Anidex");

            candidate.Name.Should().Be("Anidex");
            candidate.RequiresCredentials.Should().BeFalse();
            candidate.Protocol.Should().Be(DownloadProtocol.Torrent);
            candidate.Privacy.Should().Be(IndexerPrivacy.Public);
            candidate.Definition.Should().BeSameAs(_native);
        }

        [Test]
        public void should_map_cardigann_definition_with_login_to_credentialed_candidate()
        {
            var candidate = Subject.GetCandidates().Single(c => c.Key == "Cardigann:sometracker.yml");

            candidate.RequiresCredentials.Should().BeTrue();
        }

        [Test]
        public void should_skip_cardigann_definition_that_is_not_cached()
        {
            _cardigann.Settings = new CardigannSettings { DefinitionFile = "missing.yml" };

            Subject.GetCandidates().Should().HaveCount(1);
        }
    }
}
```

Note: add `using System.Linq;` to the fixture for `.Single(...)`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerCandidateProviderFixture"`
Expected: build error — `IndexerCandidateProvider` does not exist.

- [ ] **Step 3: Implement the provider**

`src/NzbDrone.Core/Indexers/Automation/IndexerCandidateProvider.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.IndexerVersions;
using NzbDrone.Core.Indexers.Definitions.Cardigann;

namespace NzbDrone.Core.Indexers.Automation
{
    public interface IIndexerCandidateProvider
    {
        List<IndexerCandidate> GetCandidates();
    }

    public class IndexerCandidateProvider : IIndexerCandidateProvider
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly IIndexerDefinitionUpdateService _definitionService;
        private readonly Logger _logger;

        public IndexerCandidateProvider(IIndexerFactory indexerFactory,
                                        IIndexerDefinitionUpdateService definitionService,
                                        Logger logger)
        {
            _indexerFactory = indexerFactory;
            _definitionService = definitionService;
            _logger = logger;
        }

        public List<IndexerCandidate> GetCandidates()
        {
            var candidates = new List<IndexerCandidate>();

            foreach (var definition in _indexerFactory.GetDefaultDefinitions())
            {
                var cardigannSettings = definition.Settings as CardigannSettings;
                var cardigannDefinition = cardigannSettings == null
                    ? null
                    : _definitionService.GetCachedDefinition(cardigannSettings.DefinitionFile);

                if (cardigannSettings != null && cardigannDefinition == null)
                {
                    _logger.Debug("Skipping indexer '{0}': definition file '{1}' is not cached", definition.Name, cardigannSettings.DefinitionFile);
                    continue;
                }

                candidates.Add(new IndexerCandidate
                {
                    Key = IndexerCandidate.GetKey(definition),
                    Name = definition.Name,
                    Protocol = definition.Protocol,
                    Privacy = definition.Privacy,
                    Language = definition.Language,
                    CategoryIds = GetCategoryIds(definition),
                    RequiresCredentials = IndexerCredentialDetector.RequiresCredentials(definition, cardigannDefinition),
                    Definition = definition
                });
            }

            return candidates;
        }

        private static HashSet<int> GetCategoryIds(IndexerDefinition definition)
        {
            var categories = definition.Capabilities?.Categories?.GetTorznabCategoryList();

            if (categories == null)
            {
                return new HashSet<int>();
            }

            return categories.Select(c => c.Id).ToHashSet();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerCandidateProviderFixture"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NzbDrone.Core/Indexers/Automation/IndexerCandidateProvider.cs \
        src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerCandidateProviderFixture.cs
git commit -m "New: Add indexer candidate provider"
```

---

### Task 6: Probe with wall-clock timeout

**Files:**

- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerAutomationProbe.cs`
- Test: `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationProbeFixture.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Threading;
using FluentAssertions;
using FluentValidation.Results;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerAutomationProbeFixture : CoreTest<IndexerAutomationProbe>
    {
        private IndexerDefinition _definition;

        [SetUp]
        public void Setup()
        {
            _definition = new IndexerDefinition { Name = "Test", Implementation = "Anidex" };
        }

        [Test]
        public void should_return_true_when_test_passes()
        {
            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Test(It.IsAny<IndexerDefinition>()))
                .Returns(new ValidationResult());

            var success = Subject.TryProbe(_definition, 5, out var failure);

            success.Should().BeTrue();
            failure.Should().BeNull();
        }

        [Test]
        public void should_return_false_with_messages_when_test_fails()
        {
            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Test(It.IsAny<IndexerDefinition>()))
                .Returns(new ValidationResult(new[] { new ValidationFailure(string.Empty, "Unable to connect") }));

            var success = Subject.TryProbe(_definition, 5, out var failure);

            success.Should().BeFalse();
            failure.Should().Contain("Unable to connect");
        }

        [Test]
        public void should_return_false_when_test_times_out()
        {
            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Test(It.IsAny<IndexerDefinition>()))
                .Returns(() =>
                {
                    Thread.Sleep(3000);
                    return new ValidationResult();
                });

            var success = Subject.TryProbe(_definition, 1, out var failure);

            success.Should().BeFalse();
            failure.Should().Contain("timed out");
        }

        [Test]
        public void should_return_false_when_test_throws()
        {
            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Test(It.IsAny<IndexerDefinition>()))
                .Throws(new System.Exception("boom"));

            var success = Subject.TryProbe(_definition, 5, out var failure);

            success.Should().BeFalse();
            failure.Should().Contain("boom");
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerAutomationProbeFixture"`
Expected: build error — `IndexerAutomationProbe` does not exist.

- [ ] **Step 3: Implement the probe**

`src/NzbDrone.Core/Indexers/Automation/IndexerAutomationProbe.cs`:

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Indexers.Automation
{
    public interface IIndexerAutomationProbe
    {
        bool TryProbe(IndexerDefinition definition, int timeoutSeconds, out string failureMessage);
    }

    public class IndexerAutomationProbe : IIndexerAutomationProbe
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly Logger _logger;

        public IndexerAutomationProbe(IIndexerFactory indexerFactory, Logger logger)
        {
            _indexerFactory = indexerFactory;
            _logger = logger;
        }

        public bool TryProbe(IndexerDefinition definition, int timeoutSeconds, out string failureMessage)
        {
            failureMessage = null;

            var task = Task.Run(() => _indexerFactory.Test(definition));

            try
            {
                if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    failureMessage = $"Probe timed out after {timeoutSeconds} seconds";
                    _logger.Debug("Indexer automation probe for '{0}' timed out after {1}s", definition.Name, timeoutSeconds);

                    return false;
                }

                var result = task.Result;

                if (result.IsValid)
                {
                    return true;
                }

                failureMessage = string.Join("; ", result.Errors.Select(e => e.ErrorMessage));

                return false;
            }
            catch (AggregateException ex)
            {
                failureMessage = ex.Flatten().InnerExceptions.FirstOrDefault()?.Message ?? ex.Message;

                return false;
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerAutomationProbeFixture"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NzbDrone.Core/Indexers/Automation/IndexerAutomationProbe.cs \
        src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationProbeFixture.cs
git commit -m "New: Add indexer automation probe with timeout"
```

---

### Task 7: Command and service phases 1-2 (failure tracking and removal)

**Files:**

- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerAutomationCommand.cs`
- Create: `src/NzbDrone.Core/Indexers/Automation/IndexerAutomationService.cs`
- Test: `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationServiceFixture.cs`

- [ ] **Step 1: Write the failing test**

Create `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationServiceFixture.cs` with the removal tests
(the add-phase tests come in Task 8 and are appended to this same file):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerTests.Automation
{
    [TestFixture]
    public class IndexerAutomationServiceFixture : CoreTest<IndexerAutomationService>
    {
        private IndexerDefinition _existing;
        private IndexerAutomationSettings _settings;
        private List<IndexerAutomationState> _states;
        private List<IndexerStatus> _statuses;

        [SetUp]
        public void Setup()
        {
            _existing = new IndexerDefinition
            {
                Id = 1,
                Name = "Existing",
                Implementation = "Anidex",
                Enable = true,
                Settings = new NzbDrone.Core.Indexers.Settings.NoAuthTorrentBaseSettings()
            };

            _settings = new IndexerAutomationSettings
            {
                Enabled = true,
                RetryDays = 7,
                RemovalDays = 14,
                TestTimeoutSeconds = 60,
                MaxAddsPerPass = 3
            };

            _states = new List<IndexerAutomationState>();
            _statuses = new List<IndexerStatus>();

            Mocker.GetMock<IIndexerAutomationSettingsService>().Setup(s => s.Get()).Returns(() => _settings);
            Mocker.GetMock<IIndexerFactory>().Setup(f => f.All()).Returns(() => new List<IndexerDefinition> { _existing });
            Mocker.GetMock<IIndexerStatusRepository>().Setup(r => r.All()).Returns(() => _statuses);
            Mocker.GetMock<IIndexerAutomationStateRepository>().Setup(r => r.All()).Returns(() => _states);
            Mocker.GetMock<IIndexerCandidateProvider>().Setup(p => p.GetCandidates()).Returns(new List<IndexerCandidate>());
            Mocker.GetMock<IAppProfileService>().Setup(s => s.All()).Returns(new List<AppSyncProfile> { new AppSyncProfile { Id = 1, Name = "Standard" } });
        }

        private void WithFailureStreak(int days)
        {
            _states.Add(new IndexerAutomationState
            {
                Key = "Anidex",
                IndexerId = 1,
                FailureSince = DateTime.UtcNow.AddDays(-days)
            });

            _statuses.Add(new IndexerStatus { ProviderId = 1, EscalationLevel = 2, InitialFailure = DateTime.UtcNow.AddDays(-days) });
        }

        [Test]
        public void should_do_nothing_when_disabled()
        {
            _settings.Enabled = false;

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());
            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(r => r.Upsert(It.IsAny<IndexerAutomationState>()), Times.Never());
        }

        [Test]
        public void should_start_failure_streak_when_indexer_is_escalated()
        {
            _statuses.Add(new IndexerStatus { ProviderId = 1, EscalationLevel = 1 });

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.Key == "Anidex" && s.FailureSince != null && s.IndexerId == 1)),
                Times.AtLeastOnce());
        }

        [Test]
        public void should_clear_failure_streak_when_indexer_recovers()
        {
            WithFailureStreak(3);
            _statuses.Clear();
            _statuses.Add(new IndexerStatus { ProviderId = 1, EscalationLevel = 0 });

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.Key == "Anidex" && s.FailureSince == null)),
                Times.AtLeastOnce());
        }

        [Test]
        public void should_remove_indexer_when_streak_reaches_threshold()
        {
            WithFailureStreak(20);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(1), Times.Once());
            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s =>
                    s.Key == "Anidex" &&
                    s.LastAttemptResult == IndexerAutomationAttemptResult.Removed &&
                    s.NextAttemptAt != null &&
                    s.RemovedAt != null)),
                Times.AtLeastOnce());
        }

        [Test]
        public void should_not_remove_indexer_below_threshold()
        {
            WithFailureStreak(13);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_not_remove_disabled_indexer()
        {
            _existing.Enable = false;
            WithFailureStreak(20);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Delete(It.IsAny<int>()), Times.Never());
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerAutomationServiceFixture"`
Expected: build error — `IndexerAutomationCommand` / `IndexerAutomationService` do not exist.

- [ ] **Step 3: Create the command**

`src/NzbDrone.Core/Indexers/Automation/IndexerAutomationCommand.cs`:

```csharp
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Indexers.Automation
{
    public class IndexerAutomationCommand : Command
    {
        public override bool SendUpdatesToClient => true;
    }
}
```

- [ ] **Step 4: Create the service with phases 1 and 2**

`src/NzbDrone.Core/Indexers/Automation/IndexerAutomationService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Indexers.Automation
{
    public interface IIndexerAutomationService
    {
        void Execute(IndexerAutomationCommand message);
    }

    public class IndexerAutomationService : IIndexerAutomationService,
                                           IExecute<IndexerAutomationCommand>,
                                           IHandle<ProviderDeletedEvent<IIndexer>>,
                                           IHandle<ProviderBulkDeletedEvent<IIndexer>>
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly IIndexerStatusRepository _indexerStatusRepository;
        private readonly IIndexerAutomationStateRepository _stateRepository;
        private readonly IIndexerAutomationSettingsService _settingsService;
        private readonly IIndexerCandidateProvider _candidateProvider;
        private readonly IAppProfileService _appProfileService;
        private readonly IIndexerAutomationProbe _probe;
        private readonly Logger _logger;

        private readonly object _passLock = new object();
        private readonly HashSet<int> _deletingIndexerIds = new HashSet<int>();

        public IndexerAutomationService(IIndexerFactory indexerFactory,
                                        IIndexerStatusRepository indexerStatusRepository,
                                        IIndexerAutomationStateRepository stateRepository,
                                        IIndexerAutomationSettingsService settingsService,
                                        IIndexerCandidateProvider candidateProvider,
                                        IAppProfileService appProfileService,
                                        IIndexerAutomationProbe probe,
                                        Logger logger)
        {
            _indexerFactory = indexerFactory;
            _indexerStatusRepository = indexerStatusRepository;
            _stateRepository = stateRepository;
            _settingsService = settingsService;
            _candidateProvider = candidateProvider;
            _appProfileService = appProfileService;
            _probe = probe;
            _logger = logger;
        }

        public void Execute(IndexerAutomationCommand message)
        {
            if (!Monitor.TryEnter(_passLock))
            {
                _logger.Debug("Indexer automation pass already running, skipping this run");

                return;
            }

            try
            {
                var settings = _settingsService.Get();

                if (!settings.Enabled)
                {
                    _logger.Debug("Indexer automation is disabled");
                    return;
                }

                var indexers = _indexerFactory.All();
                var statuses = _indexerStatusRepository.All().ToDictionary(s => s.ProviderId);
                var states = _stateRepository.All().ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

                UpdateFailureState(indexers, statuses, states);
                RemoveStaleIndexers(indexers, states, settings);
                ReconcileSuppression(indexers, states);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Indexer automation pass failed");
            }
            finally
            {
                Monitor.Exit(_passLock);
            }
        }

        private void UpdateFailureState(List<IndexerDefinition> indexers,
                                        Dictionary<int, IndexerStatus> statuses,
                                        Dictionary<string, IndexerAutomationState> states)
        {
            var now = DateTime.UtcNow;

            foreach (var indexer in indexers.Where(i => i.Enable))
            {
                var state = GetOrCreateState(IndexerCandidate.GetKey(indexer), states);
                state.IndexerId = indexer.Id;

                var failed = statuses.TryGetValue(indexer.Id, out var status) && status.EscalationLevel > 0;

                if (failed)
                {
                    state.FailureSince ??= now;
                    state.LastFailureAt = now;
                }
                else
                {
                    state.FailureSince = null;
                }

                Save(state);
            }
        }

        private void RemoveStaleIndexers(List<IndexerDefinition> indexers,
                                         Dictionary<string, IndexerAutomationState> states,
                                         IndexerAutomationSettings settings)
        {
            var now = DateTime.UtcNow;

            foreach (var indexer in indexers.Where(i => i.Enable))
            {
                var key = IndexerCandidate.GetKey(indexer);

                if (!states.TryGetValue(key, out var state) || state.FailureSince == null)
                {
                    continue;
                }

                if ((now - state.FailureSince.Value).TotalDays < settings.RemovalDays)
                {
                    continue;
                }

                try
                {
                    _logger.Info("Indexer automation removing '{0}' after {1} days in a failed state", indexer.Name, settings.RemovalDays);

                    _deletingIndexerIds.Add(indexer.Id);
                    _indexerFactory.Delete(indexer.Id);

                    state.IndexerId = null;
                    state.RemovedAt = now;
                    state.NextAttemptAt = now.AddDays(settings.RetryDays);
                    state.LastAttemptResult = IndexerAutomationAttemptResult.Removed;
                    state.FailureSince = null;
                    Save(state);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to remove indexer '{0}'", indexer.Name);
                }
                finally
                {
                    _deletingIndexerIds.Remove(indexer.Id);
                }
            }
        }

        private void ReconcileSuppression(List<IndexerDefinition> indexers, Dictionary<string, IndexerAutomationState> states)
        {
            foreach (var indexer in indexers)
            {
                var key = IndexerCandidate.GetKey(indexer);

                if (!states.TryGetValue(key, out var state) || !state.Suppressed)
                {
                    continue;
                }

                state.Suppressed = false;
                state.SuppressedAt = null;
                Save(state);
            }
        }

        public void Handle(ProviderDeletedEvent<IIndexer> message)
        {
            SuppressManuallyDeletedIndexer(message.ProviderId);
        }

        public void Handle(ProviderBulkDeletedEvent<IIndexer> message)
        {
            foreach (var id in message.ProviderIds)
            {
                SuppressManuallyDeletedIndexer(id);
            }
        }

        private void SuppressManuallyDeletedIndexer(int indexerId)
        {
            if (_deletingIndexerIds.Contains(indexerId))
            {
                return;
            }

            var state = _stateRepository.All().FirstOrDefault(s => s.IndexerId == indexerId);

            if (state == null)
            {
                _logger.Debug("Indexer {0} was deleted but has no automation state row, nothing to suppress", indexerId);
                return;
            }

            state.IndexerId = null;
            state.Suppressed = true;
            state.SuppressedAt = DateTime.UtcNow;
            state.LastAttemptResult = IndexerAutomationAttemptResult.Suppressed;
            Save(state);

            _logger.Info("Indexer automation suppressed '{0}' after a manual delete", state.Key);
        }

        private IndexerAutomationState GetOrCreateState(string key, Dictionary<string, IndexerAutomationState> states)
        {
            if (states.TryGetValue(key, out var state))
            {
                return state;
            }

            state = new IndexerAutomationState { Key = key };
            states[key] = state;

            return state;
        }

        private void Save(IndexerAutomationState state)
        {
            _stateRepository.Upsert(state);
        }
    }
}
```

Note: `ProviderBulkDeletedEvent<IIndexer>` exposes `IEnumerable<int> ProviderIds` (verified in `src/NzbDrone.Core/ThingiProvider/Events/ProviderBulkDeletedEvent.cs`).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerAutomationServiceFixture"`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/NzbDrone.Core/Indexers/Automation/IndexerAutomationCommand.cs \
        src/NzbDrone.Core/Indexers/Automation/IndexerAutomationService.cs \
        src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationServiceFixture.cs
git commit -m "New: Add indexer automation failure tracking and removal"
```

---

### Task 8: Service phase 4 (discovery and add) and suppression tests

**Files:**

- Modify: `src/NzbDrone.Core/Indexers/Automation/IndexerAutomationService.cs` (`AddCandidates` and helpers)
- Modify: `src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationServiceFixture.cs` (append tests)

- [ ] **Step 1: Write the failing tests**

Append to `IndexerAutomationServiceFixture.cs` (inside the class):

```csharp
        private IndexerCandidate CreateCandidate(string key, bool requiresCredentials = false)
        {
            return new IndexerCandidate
            {
                Key = key,
                Name = key,
                Protocol = DownloadProtocol.Torrent,
                Privacy = IndexerPrivacy.Public,
                Language = "en-US",
                CategoryIds = new HashSet<int> { 2000 },
                RequiresCredentials = requiresCredentials,
                Definition = new IndexerDefinition
                {
                    Name = key,
                    Implementation = key,
                    Protocol = DownloadProtocol.Torrent,
                    Privacy = IndexerPrivacy.Public,
                    Language = "en-US",
                    Settings = new NzbDrone.Core.Indexers.Settings.NoAuthTorrentBaseSettings(),
                    Capabilities = new IndexerCapabilities()
                }
            };
        }

        private void WithCandidates(params IndexerCandidate[] candidates)
        {
            Mocker.GetMock<IIndexerCandidateProvider>()
                .Setup(p => p.GetCandidates())
                .Returns(candidates.ToList());
        }

        private void WithProbeResult(bool success)
        {
            Mocker.GetMock<IIndexerAutomationProbe>()
                .Setup(p => p.TryProbe(It.IsAny<IndexerDefinition>(), It.IsAny<int>(), out It.Ref<string>.IsAny))
                .Returns(new TryProbeCallback((IndexerDefinition d, int t, out string message) =>
                {
                    message = success ? null : "probe failed";
                    return success;
                }).Invoke);
        }

        private delegate bool TryProbeCallback(IndexerDefinition definition, int timeoutSeconds, out string failureMessage);

        [Test]
        public void should_add_matching_credential_free_candidate_when_probe_passes()
        {
            WithCandidates(CreateCandidate("NewTracker"));
            WithProbeResult(true);

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Create(It.IsAny<IndexerDefinition>()))
                .Returns<IndexerDefinition>(d =>
                {
                    d.Id = 42;
                    return d;
                });

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(
                f => f.Create(It.Is<IndexerDefinition>(d => d.Name == "NewTracker" && d.Enable && d.AppProfileId == 1)),
                Times.Once());

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s =>
                    s.Key == "NewTracker" &&
                    s.IndexerId == 42 &&
                    s.LastAttemptResult == IndexerAutomationAttemptResult.Passed &&
                    s.NextAttemptAt == null)),
                Times.AtLeastOnce());
        }

        [Test]
        public void should_not_create_when_probe_fails_and_should_schedule_retry()
        {
            WithCandidates(CreateCandidate("NewTracker"));
            WithProbeResult(false);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Never());
            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s =>
                    s.Key == "NewTracker" &&
                    s.LastAttemptResult == IndexerAutomationAttemptResult.Failed &&
                    s.NextAttemptAt != null)),
                Times.AtLeastOnce());
        }

        [Test]
        public void should_skip_candidates_that_require_credentials()
        {
            WithCandidates(CreateCandidate("PrivateTracker", requiresCredentials: true));
            WithProbeResult(true);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerAutomationProbe>().Verify(
                p => p.TryProbe(It.IsAny<IndexerDefinition>(), It.IsAny<int>(), out It.Ref<string>.IsAny),
                Times.Never());
        }

        [Test]
        public void should_skip_candidates_that_are_already_present()
        {
            WithCandidates(CreateCandidate("Anidex"));
            WithProbeResult(true);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Never());
        }

        [Test]
        public void should_skip_candidates_that_are_suppressed()
        {
            _states.Add(new IndexerAutomationState { Key = "NewTracker", Suppressed = true, SuppressedAt = DateTime.UtcNow });
            WithCandidates(CreateCandidate("NewTracker"));
            WithProbeResult(true);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Never());
        }

        [Test]
        public void should_skip_candidates_that_are_not_due()
        {
            _states.Add(new IndexerAutomationState { Key = "NewTracker", NextAttemptAt = DateTime.UtcNow.AddDays(3) });
            WithCandidates(CreateCandidate("NewTracker"));
            WithProbeResult(true);

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Never());
        }

        [Test]
        public void should_respect_max_adds_per_pass()
        {
            _settings.MaxAddsPerPass = 1;
            WithCandidates(CreateCandidate("A"), CreateCandidate("B"), CreateCandidate("C"));
            WithProbeResult(true);

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Create(It.IsAny<IndexerDefinition>()))
                .Returns<IndexerDefinition>(d =>
                {
                    d.Id = 99;
                    return d;
                });

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerFactory>().Verify(f => f.Create(It.IsAny<IndexerDefinition>()), Times.Once());
        }

        [Test]
        public void should_suppress_indexer_deleted_manually()
        {
            _states.Add(new IndexerAutomationState { Key = "Anidex", IndexerId = 7 });

            Subject.Handle(new NzbDrone.Core.ThingiProvider.Events.ProviderDeletedEvent<IIndexer>(7));

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s =>
                    s.Key == "Anidex" &&
                    s.Suppressed &&
                    s.LastAttemptResult == IndexerAutomationAttemptResult.Suppressed)),
                Times.Once());
        }

        [Test]
        public void should_not_suppress_indexer_deleted_by_automation()
        {
            WithFailureStreak(20);

            Mocker.GetMock<IIndexerFactory>()
                .Setup(f => f.Delete(1))
                .Callback(() => Subject.Handle(new NzbDrone.Core.ThingiProvider.Events.ProviderDeletedEvent<IIndexer>(1)));

            Subject.Execute(new IndexerAutomationCommand());

            Mocker.GetMock<IIndexerAutomationStateRepository>().Verify(
                r => r.Upsert(It.Is<IndexerAutomationState>(s => s.Key == "Anidex" && s.Suppressed)),
                Times.Never());
        }
```

If the Moq `out` parameter setup proves awkward on this Moq version, replace `WithProbeResult` with a mock that
returns via a captured variable:

```csharp
        private string _probeFailure;

        private void WithProbeResult(bool success)
        {
            _probeFailure = success ? null : "probe failed";

            Mocker.GetMock<IIndexerAutomationProbe>()
                .Setup(p => p.TryProbe(It.IsAny<IndexerDefinition>(), It.IsAny<int>(), out _probeFailure))
                .Returns(success);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerAutomationServiceFixture"`
Expected: the new add-phase tests FAIL (nothing is added) while the Task 7 tests still pass.

- [ ] **Step 3: Implement `AddCandidates`**

Add the phase-4 call to `Execute`, so the body reads:

```csharp
                UpdateFailureState(indexers, statuses, states);
                RemoveStaleIndexers(indexers, states, settings);
                ReconcileSuppression(indexers, states);
                AddCandidates(indexers, states, settings);
            }
```

Then add these methods to `IndexerAutomationService.cs`:

```csharp
        private void AddCandidates(List<IndexerDefinition> indexers,
                                   Dictionary<string, IndexerAutomationState> states,
                                   IndexerAutomationSettings settings)
        {
            var profile = _appProfileService.All().FirstOrDefault(p => p.Name == "Standard") ?? _appProfileService.All().FirstOrDefault();

            if (profile == null)
            {
                _logger.Warn("Indexer automation cannot add indexers: no app profile is configured");
                return;
            }

            var present = indexers.Select(IndexerCandidate.GetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;

            var due = _candidateProvider.GetCandidates()
                .Where(c => !c.RequiresCredentials)
                .Where(c => !present.Contains(c.Key))
                .Where(c => !IsSuppressed(states, c.Key))
                .Where(c => IsDue(states, c.Key, now))
                .Where(c => IndexerCriteriaMatcher.Matches(c, settings))
                .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .Take(settings.MaxAddsPerPass)
                .ToList();

            foreach (var candidate in due)
            {
                TryAddCandidate(candidate, profile, settings, states);
            }
        }

        private void TryAddCandidate(IndexerCandidate candidate,
                                     AppSyncProfile profile,
                                     IndexerAutomationSettings settings,
                                     Dictionary<string, IndexerAutomationState> states)
        {
            var state = GetOrCreateState(candidate.Key, states);
            state.LastAttemptAt = DateTime.UtcNow;

            var definition = candidate.Definition;
            definition.Id = 0;
            definition.Enable = true;
            definition.Priority = 25;
            definition.AppProfileId = profile.Id;
            definition.DownloadClientId = 0;
            definition.Added = DateTime.UtcNow;
            definition.Redirect = definition.Protocol == DownloadProtocol.Usenet && definition.SupportsRedirect;

            if (!_probe.TryProbe(definition, settings.TestTimeoutSeconds, out var failureMessage))
            {
                state.LastAttemptResult = IndexerAutomationAttemptResult.Failed;
                state.NextAttemptAt = DateTime.UtcNow.AddDays(settings.RetryDays);
                Save(state);

                _logger.Info("Indexer automation probe failed for '{0}': {1}", candidate.Name, failureMessage);

                return;
            }

            var created = _indexerFactory.Create(definition);

            state.IndexerId = created.Id;
            state.FailureSince = null;
            state.NextAttemptAt = null;
            state.LastAttemptResult = IndexerAutomationAttemptResult.Passed;
            Save(state);

            _logger.Info("Indexer automation added '{0}'", candidate.Name);
        }

        private static bool IsSuppressed(Dictionary<string, IndexerAutomationState> states, string key)
        {
            return states.TryGetValue(key, out var state) && state.Suppressed;
        }

        private static bool IsDue(Dictionary<string, IndexerAutomationState> states, string key, DateTime now)
        {
            return !states.TryGetValue(key, out var state) ||
                   state.NextAttemptAt == null ||
                   state.NextAttemptAt <= now;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerAutomationServiceFixture"`
Expected: PASS (15 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NzbDrone.Core/Indexers/Automation/IndexerAutomationService.cs \
        src/NzbDrone.Core.Test/IndexerTests/Automation/IndexerAutomationServiceFixture.cs
git commit -m "New: Add indexer automation discovery and add phase"
```

---

### Task 9: Schedule the pass

**Files:**

- Modify: `src/NzbDrone.Core/Jobs/TaskManager.cs:96-104`

- [ ] **Step 1: Add the default task**

In `TaskManager.Handle(ApplicationStartedEvent)`, inside `defaultTasks`, after the `IndexerDefinitionUpdateCommand`
entry, add:

```csharp
                    new ScheduledTask
                    {
                        Interval = 60,
                        TypeName = typeof(IndexerAutomationCommand).FullName
                    },
```

and add the using at the top of the file:

```csharp
using NzbDrone.Core.Indexers.Automation;
```

- [ ] **Step 2: Verify the build**

Run: `dotnet build src/Prowlarr.sln -c Debug`
Expected: build succeeds, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/NzbDrone.Core/Jobs/TaskManager.cs
git commit -m "New: Schedule indexer automation every hour"
```

---

### Task 10: API resources and controller

**Files:**

- Create: `src/Prowlarr.Api.V1/Indexers/IndexerAutomationResource.cs`
- Create: `src/Prowlarr.Api.V1/Indexers/IndexerAutomationStateResource.cs`
- Create: `src/Prowlarr.Api.V1/Indexers/IndexerAutomationController.cs`
- Modify: `src/Prowlarr.Api.V1.Test/Prowlarr.Api.V1.Test.csproj`
- Test: `src/Prowlarr.Api.V1.Test/IndexerAutomationTests/IndexerAutomationControllerFixture.cs`

- [ ] **Step 1: Write the failing test**

Add the project reference to `src/Prowlarr.Api.V1.Test/Prowlarr.Api.V1.Test.csproj`:

```xml
    <ProjectReference Include="..\Prowlarr.Api.V1\Prowlarr.Api.V1.csproj" />
```

Create `src/Prowlarr.Api.V1.Test/IndexerAutomationTests/IndexerAutomationControllerFixture.cs`:

```csharp
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Test.Common;
using Prowlarr.Api.V1.Indexers;

namespace NzbDrone.Api.Test.IndexerAutomationTests
{
    [TestFixture]
    public class IndexerAutomationControllerFixture : TestBase
    {
        private IndexerAutomationController _subject;

        [SetUp]
        public void Setup()
        {
            _subject = Mocker.Resolve<IndexerAutomationController>();
        }

        [Test]
        public void should_map_settings_to_resource()
        {
            Mocker.GetMock<IIndexerAutomationSettingsService>()
                .Setup(s => s.Get())
                .Returns(new IndexerAutomationSettings
                {
                    Enabled = true,
                    Protocols = new List<DownloadProtocol> { DownloadProtocol.Torrent },
                    Privacy = new List<IndexerPrivacy> { IndexerPrivacy.Public },
                    Languages = new List<string> { "en-US" },
                    Categories = new List<int> { 2000 },
                    TestTimeoutSeconds = 60,
                    RetryDays = 7,
                    RemovalDays = 14,
                    MaxAddsPerPass = 3
                });

            var resource = _subject.GetSettings();

            resource.Enabled.Should().BeTrue();
            resource.Protocols.Should().BeEquivalentTo(new List<DownloadProtocol> { DownloadProtocol.Torrent });
            resource.Categories.Should().BeEquivalentTo(new List<int> { 2000 });
            resource.RemovalDays.Should().Be(14);
        }

        [Test]
        public void should_save_settings_from_resource()
        {
            var resource = new IndexerAutomationResource
            {
                Enabled = true,
                Protocols = new List<DownloadProtocol> { DownloadProtocol.Usenet },
                Privacy = new List<IndexerPrivacy> { IndexerPrivacy.Private },
                Languages = new List<string>(),
                Categories = new List<int> { 5000 },
                TestTimeoutSeconds = 30,
                RetryDays = 3,
                RemovalDays = 10,
                MaxAddsPerPass = 2
            };

            _subject.SaveSettings(resource);

            Mocker.GetMock<IIndexerAutomationSettingsService>().Verify(
                s => s.Save(It.Is<IndexerAutomationSettings>(m =>
                    m.Enabled &&
                    m.TestTimeoutSeconds == 30 &&
                    m.RemovalDays == 10 &&
                    m.Categories.Contains(5000))),
                Times.Once());
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Prowlarr.Api.V1.Test/Prowlarr.Api.V1.Test.csproj --filter "FullyQualifiedName~IndexerAutomationControllerFixture"`
Expected: build error — `IndexerAutomationController` does not exist.

- [ ] **Step 3: Create the resources**

`src/Prowlarr.Api.V1/Indexers/IndexerAutomationResource.cs`:

```csharp
using System.Collections.Generic;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using Prowlarr.Http.REST;

namespace Prowlarr.Api.V1.Indexers
{
    public class IndexerAutomationResource : RestResource
    {
        public bool Enabled { get; set; }
        public List<DownloadProtocol> Protocols { get; set; } = new List<DownloadProtocol>();
        public List<string> Languages { get; set; } = new List<string>();
        public List<IndexerPrivacy> Privacy { get; set; } = new List<IndexerPrivacy>();
        public List<int> Categories { get; set; } = new List<int>();
        public int TestTimeoutSeconds { get; set; }
        public int RetryDays { get; set; }
        public int RemovalDays { get; set; }
        public int MaxAddsPerPass { get; set; }
    }

    public static class IndexerAutomationResourceMapper
    {
        public static IndexerAutomationResource ToResource(this IndexerAutomationSettings model)
        {
            if (model == null)
            {
                return null;
            }

            return new IndexerAutomationResource
            {
                Id = 1,
                Enabled = model.Enabled,
                Protocols = model.Protocols,
                Languages = model.Languages,
                Privacy = model.Privacy,
                Categories = model.Categories,
                TestTimeoutSeconds = model.TestTimeoutSeconds,
                RetryDays = model.RetryDays,
                RemovalDays = model.RemovalDays,
                MaxAddsPerPass = model.MaxAddsPerPass
            };
        }

        public static IndexerAutomationSettings ToModel(this IndexerAutomationResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            return new IndexerAutomationSettings
            {
                Enabled = resource.Enabled,
                Protocols = resource.Protocols ?? new List<DownloadProtocol>(),
                Languages = resource.Languages ?? new List<string>(),
                Privacy = resource.Privacy ?? new List<IndexerPrivacy>(),
                Categories = resource.Categories ?? new List<int>(),
                TestTimeoutSeconds = resource.TestTimeoutSeconds,
                RetryDays = resource.RetryDays,
                RemovalDays = resource.RemovalDays,
                MaxAddsPerPass = resource.MaxAddsPerPass
            };
        }
    }
}
```

`src/Prowlarr.Api.V1/Indexers/IndexerAutomationStateResource.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Indexers.Automation;
using Prowlarr.Http.REST;

namespace Prowlarr.Api.V1.Indexers
{
    public class IndexerAutomationStateResource : RestResource
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public int? IndexerId { get; set; }
        public DateTime? FailureSince { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public string LastAttemptResult { get; set; }
        public DateTime? RemovedAt { get; set; }
        public bool Suppressed { get; set; }
    }

    public static class IndexerAutomationStateResourceMapper
    {
        public static IndexerAutomationStateResource ToResource(this IndexerAutomationState model, string name)
        {
            if (model == null)
            {
                return null;
            }

            return new IndexerAutomationStateResource
            {
                Id = model.Id,
                Key = model.Key,
                Name = name ?? model.Key,
                IndexerId = model.IndexerId,
                FailureSince = model.FailureSince,
                NextAttemptAt = model.NextAttemptAt,
                LastAttemptResult = model.LastAttemptResult,
                RemovedAt = model.RemovedAt,
                Suppressed = model.Suppressed
            };
        }

        public static List<IndexerAutomationStateResource> ToResource(this IEnumerable<IndexerAutomationState> models, IDictionary<string, string> names)
        {
            return models.Select(m => m.ToResource(names.TryGetValue(m.Key, out var name) ? name : null)).ToList();
        }
    }
}
```

- [ ] **Step 4: Create the controller**

`src/Prowlarr.Api.V1/Indexers/IndexerAutomationController.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Automation;
using NzbDrone.Core.Messaging.Commands;
using Prowlarr.Http;

namespace Prowlarr.Api.V1.Indexers
{
    [V1ApiController("indexer/automation")]
    public class IndexerAutomationController : Controller
    {
        private readonly IIndexerAutomationSettingsService _settingsService;
        private readonly IIndexerAutomationStateRepository _stateRepository;
        private readonly IIndexerCandidateProvider _candidateProvider;
        private readonly IManageCommandQueue _commandQueueManager;

        public IndexerAutomationController(IIndexerAutomationSettingsService settingsService,
                                           IIndexerAutomationStateRepository stateRepository,
                                           IIndexerCandidateProvider candidateProvider,
                                           IManageCommandQueue commandQueueManager)
        {
            _settingsService = settingsService;
            _stateRepository = stateRepository;
            _candidateProvider = candidateProvider;
            _commandQueueManager = commandQueueManager;
        }

        [HttpGet]
        [Produces("application/json")]
        public IndexerAutomationResource GetSettings()
        {
            return _settingsService.Get().ToResource();
        }

        [HttpPut]
        [Consumes("application/json")]
        [Produces("application/json")]
        public ActionResult<IndexerAutomationResource> SaveSettings([FromBody] IndexerAutomationResource resource)
        {
            _settingsService.Save(resource.ToModel());

            return Accepted(resource.ToModel().ToResource());
        }

        [HttpGet("state")]
        [Produces("application/json")]
        public List<IndexerAutomationStateResource> GetState()
        {
            var names = _candidateProvider.GetCandidates()
                .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

            return _stateRepository.All().ToResource(names);
        }

        [HttpPost("run")]
        [Produces("application/json")]
        public IActionResult Run()
        {
            _commandQueueManager.Push(new IndexerAutomationCommand(), CommandPriority.Normal, CommandTrigger.Manual);

            return Accepted();
        }

        [HttpDelete("state/{key}")]
        [Produces("application/json")]
        public IActionResult ClearSuppression(string key)
        {
            var state = _stateRepository.FindByKey(key);

            if (state == null)
            {
                return NotFound();
            }

            state.Suppressed = false;
            state.SuppressedAt = null;
            _stateRepository.Upsert(state);

            return Ok();
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Prowlarr.Api.V1.Test/Prowlarr.Api.V1.Test.csproj --filter "FullyQualifiedName~IndexerAutomationControllerFixture"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Prowlarr.Api.V1/Indexers/IndexerAutomationController.cs \
        src/Prowlarr.Api.V1/Indexers/IndexerAutomationResource.cs \
        src/Prowlarr.Api.V1/Indexers/IndexerAutomationStateResource.cs \
        src/Prowlarr.Api.V1.Test/Prowlarr.Api.V1.Test.csproj \
        src/Prowlarr.Api.V1.Test/IndexerAutomationTests/IndexerAutomationControllerFixture.cs
git commit -m "New: Add indexer automation API"
```

---

### Task 11: Localization keys

**Files:**

- Modify: `src/NzbDrone.Core/Localization/Core/en.json`

- [ ] **Step 1: Add the keys**

Insert these entries in alphabetical position inside the JSON object in `src/NzbDrone.Core/Localization/Core/en.json`:

```json
  "IndexerAutomation": "Indexer Automation",
  "IndexerAutomationCategories": "Categories",
  "IndexerAutomationCategoriesHelpText": "An indexer is added when it supports at least one of the selected categories. Leave empty to match any category.",
  "IndexerAutomationDisabled": "Indexer automation is disabled",
  "IndexerAutomationEnabled": "Enable Indexer Automation",
  "IndexerAutomationEnabledHelpText": "Automatically add indexers that match the criteria below and remove indexers that stay in a failed state.",
  "IndexerAutomationLanguages": "Languages",
  "IndexerAutomationLanguagesHelpText": "Leave empty to match any language.",
  "IndexerAutomationMaxAddsPerPass": "Maximum Adds Per Pass",
  "IndexerAutomationMaxAddsPerPassHelpText": "How many indexers may be probed and added in a single hourly pass (1-20).",
  "IndexerAutomationNextAttempt": "Next Attempt",
  "IndexerAutomationNoState": "No indexers have been evaluated yet.",
  "IndexerAutomationPrivacy": "Privacy",
  "IndexerAutomationPrivacyHelpText": "Leave empty to match any privacy level.",
  "IndexerAutomationProtocols": "Protocols",
  "IndexerAutomationProtocolsHelpText": "Leave empty to match any protocol.",
  "IndexerAutomationRemovalDays": "Remove After Failed Days",
  "IndexerAutomationRemovalDaysHelpText": "An enabled indexer is removed once it has been in a failed state for at least this many days (1-365).",
  "IndexerAutomationRetryDays": "Retry After Days",
  "IndexerAutomationRetryDaysHelpText": "A failed or removed indexer is probed again after this many days (1-365).",
  "IndexerAutomationRunNow": "Run Now",
  "IndexerAutomationSettingsSummary": "Automatically add and remove indexers based on criteria and failure history",
  "IndexerAutomationState": "State",
  "IndexerAutomationStateCleared": "Suppression cleared",
  "IndexerAutomationSuppressed": "Suppressed",
  "IndexerAutomationTestTimeoutSeconds": "Probe Timeout",
  "IndexerAutomationTestTimeoutSecondsHelpText": "Wall-clock limit for the probe that runs before an indexer is added, in seconds (10-600).",
  "IndexerAutomationClearSuppression": "Clear Suppression",
  "IndexerAutomationFailureSince": "Failed Since",
  "IndexerAutomationLastResult": "Last Result"
```

- [ ] **Step 2: Validate the JSON**

Run: `node -e "JSON.parse(require('fs').readFileSync('src/NzbDrone.Core/Localization/Core/en.json','utf8')); console.log('ok')"`
Expected: prints `ok`.

- [ ] **Step 3: Commit**

```bash
git add src/NzbDrone.Core/Localization/Core/en.json
git commit -m "New: Add indexer automation localization strings"
```

---

### Task 12: Frontend settings store section

**Files:**

- Create: `frontend/src/Store/Actions/Settings/indexerAutomation.js`
- Create: `frontend/src/typings/Settings/IndexerAutomation.ts`
- Modify: `frontend/src/App/State/SettingsAppState.ts`
- Modify: `frontend/src/Store/Actions/settingsActions.js`

- [ ] **Step 1: Create the settings section module**

`frontend/src/Store/Actions/Settings/indexerAutomation.js`:

```javascript
import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

//
// Variables

const section = 'settings.indexerAutomation';

//
// Actions Types

export const FETCH_INDEXER_AUTOMATION_SETTINGS = 'settings/indexerAutomation/fetchIndexerAutomationSettings';
export const SET_INDEXER_AUTOMATION_SETTINGS_VALUE = 'settings/indexerAutomation/setIndexerAutomationSettingsValue';
export const SAVE_INDEXER_AUTOMATION_SETTINGS = 'settings/indexerAutomation/saveIndexerAutomationSettings';

//
// Action Creators

export const fetchIndexerAutomationSettings = createThunk(FETCH_INDEXER_AUTOMATION_SETTINGS);
export const saveIndexerAutomationSettings = createThunk(SAVE_INDEXER_AUTOMATION_SETTINGS);
export const setIndexerAutomationSettingsValue = createAction(SET_INDEXER_AUTOMATION_SETTINGS_VALUE, (payload) => {
  return {
    section,
    ...payload
  };
});

//
// Details

export default {

  //
  // State

  defaultState: {
    isFetching: false,
    isPopulated: false,
    error: null,
    pendingChanges: {},
    isSaving: false,
    saveError: null,
    item: {}
  },

  //
  // Action Handlers

  actionHandlers: {
    [FETCH_INDEXER_AUTOMATION_SETTINGS]: createFetchHandler(section, '/indexer/automation'),
    [SAVE_INDEXER_AUTOMATION_SETTINGS]: createSaveHandler(section, '/indexer/automation')
  },

  //
  // Reducers

  reducers: {
    [SET_INDEXER_AUTOMATION_SETTINGS_VALUE]: createSetSettingValueReducer(section)
  }

};
```

- [ ] **Step 2: Add the TypeScript section typings**

Create `frontend/src/typings/Settings/IndexerAutomation.ts`:

```typescript
export default interface IndexerAutomation {
  enabled: boolean;
  protocols: string[];
  languages: string[];
  privacy: string[];
  categories: number[];
  testTimeoutSeconds: number;
  retryDays: number;
  removalDays: number;
  maxAddsPerPass: number;
}
```

In `frontend/src/App/State/SettingsAppState.ts`, add the import:

```typescript
import IndexerAutomation from 'typings/Settings/IndexerAutomation';
```

the interface:

```typescript
export interface IndexerAutomationAppState
  extends AppSectionItemState<IndexerAutomation>,
    AppSectionSaveState {}
```

and the entry inside `interface SettingsAppState`:

```typescript
  indexerAutomation: IndexerAutomationAppState;
```

- [ ] **Step 3: Register the section in settingsActions.js**

In `frontend/src/Store/Actions/settingsActions.js`:

- Add the import next to the other `Settings/*` imports:

```javascript
import indexerAutomation from './Settings/indexerAutomation';
```

- Add the re-export next to the other `export * from` lines:

```javascript
export * from './Settings/indexerAutomation';
```

- Add the default state entry:

```javascript
  indexerAutomation: indexerAutomation.defaultState,
```

- Spread the handlers and reducers:

```javascript
  ...indexerAutomation.actionHandlers
```

inside `handleThunks({ ... })`, and:

```javascript
  ...indexerAutomation.reducers
```

inside `createHandleActions({ ... }, defaultState, section)`.

- [ ] **Step 3: Verify lint passes for the changed files**

Run: `yarn eslint --config frontend/.eslintrc.js frontend/src/Store/Actions/Settings/indexerAutomation.js frontend/src/Store/Actions/settingsActions.js`
Expected: no errors (if `node_modules` is missing, run `yarn install` first or record the gate as BLOCKED).

- [ ] **Step 4: Commit**

```bash
git add frontend/src/Store/Actions/Settings/indexerAutomation.js \
        frontend/src/typings/Settings/IndexerAutomation.ts \
        frontend/src/App/State/SettingsAppState.ts \
        frontend/src/Store/Actions/settingsActions.js
git commit -m "New: Add indexer automation settings store section"
```

---

### Task 13: Frontend state-rows store section

**Files:**

- Create: `frontend/src/Store/Actions/indexerAutomationActions.js`
- Modify: `frontend/src/Store/Actions/index.js`

- [ ] **Step 1: Create the state-rows module**

`frontend/src/Store/Actions/indexerAutomationActions.js`:

```javascript
import createAjaxRequest from 'Utilities/createAjaxRequest';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import { set } from 'Store/Actions/baseActions';
import { createThunk } from 'Store/thunks';

//
// Variables

const section = 'indexerAutomation';

//
// Actions Types

export const FETCH_INDEXER_AUTOMATION_STATE = 'indexerAutomation/fetchIndexerAutomationState';
export const CLEAR_INDEXER_AUTOMATION_SUPPRESSION = 'indexerAutomation/clearIndexerAutomationSuppression';
export const RUN_INDEXER_AUTOMATION = 'indexerAutomation/runIndexerAutomation';

//
// Action Creators

export const fetchIndexerAutomationState = createThunk(FETCH_INDEXER_AUTOMATION_STATE);
export const clearIndexerAutomationSuppression = createThunk(CLEAR_INDEXER_AUTOMATION_SUPPRESSION);
export const runIndexerAutomation = createThunk(RUN_INDEXER_AUTOMATION);

//
// Details

export default {

  //
  // State

  defaultState: {
    isFetching: false,
    isPopulated: false,
    error: null,
    items: []
  },

  //
  // Action Handlers

  actionHandlers: {
    [FETCH_INDEXER_AUTOMATION_STATE]: createFetchHandler(section, '/indexer/automation/state'),
    [CLEAR_INDEXER_AUTOMATION_SUPPRESSION]: function(getState, payload, dispatch) {
      const promise = createAjaxRequest({
        url: `/indexer/automation/state/${encodeURIComponent(payload.key)}`,
        method: 'DELETE'
      }).request;

      promise.done(() => {
        dispatch(fetchIndexerAutomationState());
      });

      promise.fail((xhr) => {
        dispatch(set({
          section,
          error: xhr
        }));
      });
    },
    [RUN_INDEXER_AUTOMATION]: function(getState, payload, dispatch) {
      const promise = createAjaxRequest({
        url: '/indexer/automation/run',
        method: 'POST'
      }).request;

      promise.done(() => {
        dispatch(fetchIndexerAutomationState());
      });

      promise.fail((xhr) => {
        dispatch(set({
          section,
          error: xhr
        }));
      });
    }
  },

  //
  // Reducers

  reducers: {

  }

};
```

Note: `createFetchHandler` sets `items` when the response is an array, which is what `/indexer/automation/state` returns.

- [ ] **Step 2: Register the section**

In `frontend/src/Store/Actions/index.js`, add:

```javascript
import * as indexerAutomation from './indexerAutomationActions';
```

and add `indexerAutomation,` to the exported array (next to `indexerStats`).

- [ ] **Step 3: Verify lint passes**

Run: `yarn eslint --config frontend/.eslintrc.js frontend/src/Store/Actions/indexerAutomationActions.js frontend/src/Store/Actions/index.js`
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add frontend/src/Store/Actions/indexerAutomationActions.js frontend/src/Store/Actions/index.js
git commit -m "New: Add indexer automation state store section"
```

---

### Task 14: Frontend page, route and settings link

**Files:**

- Create: `frontend/src/Settings/Indexers/Automation/IndexerAutomationStateTable.js`
- Create: `frontend/src/Settings/Indexers/Automation/IndexerAutomationSettings.js`
- Create: `frontend/src/Settings/Indexers/Automation/IndexerAutomationSettingsConnector.js`
- Modify: `frontend/src/App/AppRoutes.tsx`
- Modify: `frontend/src/Settings/Settings.js`

- [ ] **Step 1: Create the state table component**

`frontend/src/Settings/Indexers/Automation/IndexerAutomationStateTable.js`:

```javascript
import PropTypes from 'prop-types';
import React from 'react';
import Button from 'Components/Link/Button';
import FieldSet from 'Components/FieldSet';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function formatDate(value) {
  if (!value) {
    return '-';
  }

  return new Date(value).toLocaleString();
}

function IndexerAutomationStateTable(props) {
  const {
    items,
    onClearSuppressionPress
  } = props;

  return (
    <FieldSet legend={translate('IndexerAutomationState')}>
      {
        !items.length &&
          <div>{translate('IndexerAutomationNoState')}</div>
      }

      {
        !!items.length &&
          <Table
            columns={[
              { name: 'name', label: () => translate('Name'), isVisible: true },
              { name: 'lastAttemptResult', label: () => translate('IndexerAutomationLastResult'), isVisible: true },
              { name: 'failureSince', label: () => translate('IndexerAutomationFailureSince'), isVisible: true },
              { name: 'nextAttemptAt', label: () => translate('IndexerAutomationNextAttempt'), isVisible: true },
              { name: 'suppressed', label: () => translate('IndexerAutomationSuppressed'), isVisible: true }
            ]}
          >
            <TableBody>
              {
                items.map((item) => {
                  return (
                    <TableRow key={item.key}>
                      <TableRowCell>{item.name}</TableRowCell>
                      <TableRowCell>{item.lastAttemptResult || '-'}</TableRowCell>
                      <TableRowCell>{formatDate(item.failureSince)}</TableRowCell>
                      <TableRowCell>{formatDate(item.nextAttemptAt)}</TableRowCell>
                      <TableRowCell>
                        {
                          item.suppressed &&
                            <Button
                              kind={kinds.DEFAULT}
                              onPress={() => onClearSuppressionPress(item.key)}
                            >
                              {translate('IndexerAutomationClearSuppression')}
                            </Button>
                        }
                        {
                          !item.suppressed && '-'
                        }
                      </TableRowCell>
                    </TableRow>
                  );
                })
              }
            </TableBody>
          </Table>
      }
    </FieldSet>
  );
}

IndexerAutomationStateTable.propTypes = {
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  onClearSuppressionPress: PropTypes.func.isRequired
};

export default IndexerAutomationStateTable;
```

- [ ] **Step 2: Create the page component**

`frontend/src/Settings/Indexers/Automation/IndexerAutomationSettings.js`:

```javascript
import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import EnhancedSelectInput from 'Components/Form/EnhancedSelectInput';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import NewznabCategorySelectInputConnector from 'Components/Form/NewznabCategorySelectInputConnector';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { inputTypes, kinds } from 'Helpers/Props';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import translate from 'Utilities/String/translate';
import IndexerAutomationStateTable from './IndexerAutomationStateTable';

const PROTOCOLS = [
  { key: 'torrent', value: 'torrent' },
  { key: 'usenet', value: 'usenet' }
];

const PRIVACY_LEVELS = [
  { key: 'private', value: translate('Private') },
  { key: 'semiPrivate', value: translate('SemiPrivate') },
  { key: 'public', value: translate('Public') }
];

function IndexerAutomationSettings(props) {
  const {
    isFetching,
    isPopulated,
    error,
    settings,
    languages,
    stateItems,
    isSaving,
    hasPendingChanges,
    onInputChange,
    onSavePress,
    onRunPress,
    onClearSuppressionPress
  } = props;

  if (isFetching && !isPopulated) {
    return (
      <PageContent title={translate('IndexerAutomation')}>
        <PageContentBody>
          <LoadingIndicator />
        </PageContentBody>
      </PageContent>
    );
  }

  return (
    <PageContent title={translate('IndexerAutomation')}>
      <SettingsToolbarConnector
        isSaving={isSaving}
        hasPendingChanges={hasPendingChanges}
        onSavePress={onSavePress}
      />

      <PageContentBody>
        {
          error &&
            <Alert kind={kinds.DANGER}>
              {translate('UnableToLoadIndexerAutomation')}
            </Alert>
        }

        <Form
          id="indexerAutomationSettings"
          onSubmit={onSavePress}
        >
          <FormGroup>
            <FormLabel>{translate('IndexerAutomationEnabled')}</FormLabel>

            <FormInputGroup
              type={inputTypes.CHECK}
              name="enabled"
              helpText={translate('IndexerAutomationEnabledHelpText')}
              onChange={onInputChange}
              {...settings.enabled}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>{translate('IndexerAutomationProtocols')}</FormLabel>

            <EnhancedSelectInput
              name="protocols"
              value={settings.protocols.value}
              values={PROTOCOLS}
              onChange={onInputChange}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>{translate('IndexerAutomationPrivacy')}</FormLabel>

            <EnhancedSelectInput
              name="privacy"
              value={settings.privacy.value}
              values={PRIVACY_LEVELS}
              onChange={onInputChange}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>{translate('IndexerAutomationLanguages')}</FormLabel>

            <EnhancedSelectInput
              name="languages"
              value={settings.languages.value}
              values={languages}
              onChange={onInputChange}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>{translate('IndexerAutomationCategories')}</FormLabel>

            <NewznabCategorySelectInputConnector
              name="categories"
              value={settings.categories.value}
              onChange={onInputChange}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>{translate('IndexerAutomationTestTimeoutSeconds')}</FormLabel>

            <FormInputGroup
              type={inputTypes.NUMBER}
              name="testTimeoutSeconds"
              unit="s"
              helpText={translate('IndexerAutomationTestTimeoutSecondsHelpText')}
              onChange={onInputChange}
              {...settings.testTimeoutSeconds}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>{translate('IndexerAutomationRetryDays')}</FormLabel>

            <FormInputGroup
              type={inputTypes.NUMBER}
              name="retryDays"
              unit="days"
              helpText={translate('IndexerAutomationRetryDaysHelpText')}
              onChange={onInputChange}
              {...settings.retryDays}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>{translate('IndexerAutomationRemovalDays')}</FormLabel>

            <FormInputGroup
              type={inputTypes.NUMBER}
              name="removalDays"
              unit="days"
              helpText={translate('IndexerAutomationRemovalDaysHelpText')}
              onChange={onInputChange}
              {...settings.removalDays}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>{translate('IndexerAutomationMaxAddsPerPass')}</FormLabel>

            <FormInputGroup
              type={inputTypes.NUMBER}
              name="maxAddsPerPass"
              helpText={translate('IndexerAutomationMaxAddsPerPassHelpText')}
              onChange={onInputChange}
              {...settings.maxAddsPerPass}
            />
          </FormGroup>
        </Form>

        <IndexerAutomationStateTable
          items={stateItems}
          onClearSuppressionPress={onClearSuppressionPress}
        />

        <Button
          kind={kinds.DEFAULT}
          onPress={onRunPress}
        >
          {translate('IndexerAutomationRunNow')}
        </Button>
      </PageContentBody>
    </PageContent>
  );
}

IndexerAutomationSettings.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  languages: PropTypes.arrayOf(PropTypes.object).isRequired,
  stateItems: PropTypes.arrayOf(PropTypes.object).isRequired,
  isSaving: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onRunPress: PropTypes.func.isRequired,
  onClearSuppressionPress: PropTypes.func.isRequired
};

export default IndexerAutomationSettings;
```

- [ ] **Step 3: Create the connector**

`frontend/src/Settings/Indexers/Automation/IndexerAutomationSettingsConnector.js`:

```javascript
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { fetchIndexerSchema, selectIndexerSchema } from 'Store/Actions/indexerActions';
import { clearIndexerAutomationSuppression, fetchIndexerAutomationState, runIndexerAutomation } from 'Store/Actions/indexerAutomationActions';
import {
  fetchIndexerAutomationSettings,
  saveIndexerAutomationSettings,
  setIndexerAutomationSettingsValue
} from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import IndexerAutomationSettings from './IndexerAutomationSettings';

const SECTION = 'indexerAutomation';

function createMapStateToProps() {
  return createSelector(
    createSettingsSectionSelector(SECTION),
    (state) => state.indexerAutomation,
    selectIndexerSchema,
    (sectionSettings, automationState, schema) => {
      const languages = [];

      schema.forEach((indexer) => {
        if (indexer.language && !languages.some((item) => item.key === indexer.language)) {
          languages.push({ key: indexer.language, value: indexer.language });
        }
      });

      languages.sort((a, b) => a.value.localeCompare(b.value));

      return {
        ...sectionSettings,
        stateItems: automationState.items,
        languages
      };
    }
  );
}

const mapDispatchToProps = {
  fetchIndexerAutomationSettings,
  saveIndexerAutomationSettings,
  setIndexerAutomationSettingsValue,
  fetchIndexerAutomationState,
  clearIndexerAutomationSuppression,
  runIndexerAutomation,
  fetchIndexerSchema
};

class IndexerAutomationSettingsConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.fetchIndexerAutomationSettings();
    this.props.fetchIndexerAutomationState();
    this.props.fetchIndexerSchema();
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.setIndexerAutomationSettingsValue({ name, value });
  };

  onSavePress = () => {
    this.props.saveIndexerAutomationSettings();
  };

  onClearSuppressionPress = (key) => {
    this.props.clearIndexerAutomationSuppression({ key });
  };

  onRunPress = () => {
    this.props.runIndexerAutomation();
  };

  //
  // Render

  render() {
    return (
      <IndexerAutomationSettings
        {...this.props}
        onInputChange={this.onInputChange}
        onSavePress={this.onSavePress}
        onRunPress={this.onRunPress}
        onClearSuppressionPress={this.onClearSuppressionPress}
      />
    );
  }
}

IndexerAutomationSettingsConnector.propTypes = {
  fetchIndexerAutomationSettings: PropTypes.func.isRequired,
  saveIndexerAutomationSettings: PropTypes.func.isRequired,
  setIndexerAutomationSettingsValue: PropTypes.func.isRequired,
  fetchIndexerAutomationState: PropTypes.func.isRequired,
  clearIndexerAutomationSuppression: PropTypes.func.isRequired,
  runIndexerAutomation: PropTypes.func.isRequired,
  fetchIndexerSchema: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(IndexerAutomationSettingsConnector);
```

- [ ] **Step 4: Add the route**

In `frontend/src/App/AppRoutes.tsx`, add the import next to the other `Settings/Indexers` imports:

```typescript
import IndexerAutomationSettingsConnector from 'Settings/Indexers/Automation/IndexerAutomationSettingsConnector';
```

and the route next to `/settings/indexers`:

```tsx
      <Route path="/settings/indexers/automation" component={IndexerAutomationSettingsConnector} />
```

- [ ] **Step 5: Add the settings link**

In `frontend/src/Settings/Settings.js`, after the existing `/settings/indexers` link and its summary `div`, add:

```javascript
        <Link
          className={styles.link}
          to="/settings/indexers/automation"
        >
          {translate('IndexerAutomation')}
        </Link>

        <div className={styles.summary}>
          {translate('IndexerAutomationSettingsSummary')}
        </div>
```

- [ ] **Step 6: Verify the frontend builds**

Run: `yarn build`
Expected: webpack build succeeds (if `node_modules` is missing, run `yarn install` first, or record the gate as BLOCKED).

- [ ] **Step 7: Commit**

```bash
git add frontend/src/Settings/Indexers/Automation/IndexerAutomationStateTable.js \
        frontend/src/Settings/Indexers/Automation/IndexerAutomationSettings.js \
        frontend/src/Settings/Indexers/Automation/IndexerAutomationSettingsConnector.js \
        frontend/src/App/AppRoutes.tsx \
        frontend/src/Settings/Settings.js
git commit -m "New: Add indexer automation settings page"
```

---

### Task 15: Full verification

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build src/Prowlarr.sln -c Debug`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 2: Run the new backend tests**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~Automation|FullyQualifiedName~IndexerAutomation"`
Expected: all new fixtures pass (repository 2, settings 4, credential 9, matcher 9, candidate provider 3, probe 4,
service 15).

- [ ] **Step 3: Run the API tests**

Run: `dotnet test src/Prowlarr.Api.V1.Test/Prowlarr.Api.V1.Test.csproj`
Expected: all tests pass, including `IndexerAutomationControllerFixture`.

- [ ] **Step 4: Run the existing indexer regression tests**

Run: `dotnet test src/NzbDrone.Core.Test/Prowlarr.Core.Test.csproj --filter "FullyQualifiedName~IndexerTests"`
Expected: no regressions.

- [ ] **Step 5: Frontend lint and build**

Run: `yarn lint && yarn build`
Expected: both succeed.

- [ ] **Step 6: Manual smoke test (requires a running instance)**

Run the app (`dotnet run --project src/NzbDrone.Console` or the built binary), then:

```bash
API_KEY=<your api key>
curl -s -H "X-Api-Key: $API_KEY" http://localhost:9696/api/v1/indexer/automation
curl -s -X PUT -H "X-Api-Key: $API_KEY" -H "Content-Type: application/json" \
  -d '{"enabled":true,"protocols":["torrent"],"languages":[],"privacy":["public"],"categories":[2000],"testTimeoutSeconds":60,"retryDays":7,"removalDays":14,"maxAddsPerPass":3}' \
  http://localhost:9696/api/v1/indexer/automation
curl -s -X POST -H "X-Api-Key: $API_KEY" http://localhost:9696/api/v1/indexer/automation/run
curl -s -H "X-Api-Key: $API_KEY" http://localhost:9696/api/v1/indexer/automation/state
```

Expected: settings round-trip, the run returns 202, and the state endpoint lists evaluated keys. Open
Settings → Indexer Automation and confirm the form loads, saves, and the state table renders.

- [ ] **Step 7: Final commit (if any verification fixups were needed)**

```bash
git add -A
git commit -m "Fixed: Indexer automation verification fixups"
```

---

## Notes for the implementer

- **Do not** register services manually. DryIoc's `AutoAddServices`
  (`src/NzbDrone.Common/Composition/Extensions.cs`) registers every implementation against its interfaces, including
  `IExecute<T>` and `IHandle<T>`, by convention.
- **Do not** add a `Cardigann` or `Newznab` generic candidate. `IndexerFactory.GetDefaultDefinitions()` already
  excludes those names.
- The probe runs against an **unsaved** definition (`Id = 0`). `IndexerStatusService.RecordFailure`/`UpdateCookies`
  both guard on `id > 0`, so a timed-out probe cannot write orphan status rows. Never call `Create` before the probe
  passes.
- Removal must go through `IIndexerFactory.Delete(id)` so `ProviderDeletedEvent` fires and the status row is cleaned up.
- `IndexerAutomationService` is a singleton: the in-flight `_deletingIndexerIds` set is what separates automation
  deletes from manual ones. Do not replace it with a static.
- Prowlarr commit style: prefix messages with `New:` for features and `Fixed:` for corrections.
