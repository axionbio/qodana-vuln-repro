# QD ticket — ready to paste

Submit at https://youtrack.jetbrains.com/newIssue?project=QD
Type: Bug · Subsystem: .NET (or All)

---

## Summary (title)

```
Vulnerability checker detects packages then throws ThreadAccessException on the dispatcher thread; zero results reach SARIF and the job exits 0
```

---

## Description (body)

**Qodana for .NET, `qodana-dotnet`, Ultimate Plus.**
Reproduced on `QDNET-262.9598` (CLI 2026.2.0) and `QDNET-262.10050` (CLI 2026.2.1).

`RiderSecurityErrorsInspection` finds vulnerable NuGet packages and then throws before emitting any of them. The SARIF report contains zero security results, the Qodana Cloud report shows nothing, the PR comment says "no new problems", and the job exits `0`. Nothing downstream can distinguish this from a clean scan.

### Minimal reproduction

Public repository, no private feed, no credentials, no server:
**<REPO URL — fill in once public>**

Two projects, two source files, 25 seconds of analysis:

- `WithPackages` — five vulnerable public packages (`Newtonsoft.Json 9.0.1`, `System.Text.RegularExpressions 4.3.0`, `System.Net.Http 4.3.0`, `SSH.NET 2024.0.0`, `System.Security.Cryptography.Xml 8.0.1`) plus **one package absent from the vulnerability database** — a locally packed `netstandard2.0` class library served from a `./localfeed` folder source. Its id returns 404 on nuget.org, so it can never resolve on any machine.
- `NoPackages` — no `PackageReference`s, to show the contrasting `0 to fetch` path.

Run the workflow (`workflow_dispatch`, **not** `pr-mode`). Crash is deterministic.

### Stack

```
INFO   RiderSecurityErrorsInspection - Reporting security problems
SEVERE ExternalToolsProvider - JetBrains.Threading.ThreadAccessException:
       This action cannot be executed on the :2 thread.
  at JetBrains.Threading.JetDispatcherEx.AssertNonMainThread(JetDispatcher)
  at JetBrains.ProjectModel.NuGet.Searching.Fetching.NuGetSecurityMetadataFetcher.FetchAsync(PackageIdentity, Lifetime, Boolean)
  at JetBrains.Rider.Backend.Features.NuGet.Security.RiderNuGetInstalledPackageSecurityMetadataCache.FetchAndStoreAsync(PackageIdentity)
  at JetBrains.Rider.Backend.Features.NuGet.Security.RiderNuGetInstalledPackageSecurityMetadataCache.UpdateProjectAsync(IProject)
  at JetBrains.Rider.Backend.Features.NuGet.Security.RiderNuGetInstalledPackageSecurityMetadataCache.UpdateProjectsAsync(IReadOnlyCollection`1)
  at JetBrains.Rider.Backend.Features.NuGet.Security.Vulnerabilities.RiderPackageVulnerabilitiesProblemsViewReporter.<>c__DisplayClass12_0.<<-ctor>b__1>d.MoveNext()
```

Thrown 9–20ms after the `Reporting security problems` marker, depending on run. Also surfaces a second time wrapped as `com.jetbrains.rd.util.reactive.RdFault`.

### Root cause

The same fetch runs from two call sites, and only one has the correct thread affinity:

| Call site | Thread | Outcome |
|---|---|---|
| Solution load | `JetPool(S) #1:12`, `#2:13`, … | succeeds |
| Inspection report | **`:2`** (dispatcher) | throws on first fetch |

`RiderPackageVulnerabilitiesProblemsViewReporter` runs its "Fetching vulnerabilities on request" pass on the dispatcher thread and transitively calls `NuGetSecurityMetadataFetcher.FetchAsync`, which asserts it is **not** on that thread.

Two lookup paths are involved:

1. `VulnerablePackagesTracker` — one bulk query to the JetBrains service. In a real solution of ours it reports `vulnerability status is available for 596 packages`, covering 356 of 408 distinct packages.
2. `NuGetSecurityMetadataFetcher` — the per-package fallback, reached **only** for packages the bulk service does not carry.

For packages in group 2 the fetcher logs `No security metadata found for package 'X' on 1 security feeds`. Nothing is returned, so nothing is stored, so they are **permanent cache misses** — in our solution, 1,169 fetch calls for 52 distinct packages, roughly 22 re-fetches each. At report time the first project whose miss list is non-empty enters `FetchAsync` on `:2` and the assert fires, aborting the entire pass.

The walk is visible in `backend.log`. In the minimal reproduction the load-time
pass caches everything the service knows, leaving the report-time pass exactly
one package — the unresolvable one:

```
11:18:03.602  JetPool(S) #6:20   'WithPackages':          6 package(s) to fetch   load, succeeds
11:18:07.129  JetPool(S) #11:43  'WithPackages':         66 package(s) to fetch   load, succeeds
...
11:18:46.485  :2                 'Miscellaneous Files':   0 package(s) to fetch
11:18:46.485  :2                 '&':                     0 package(s) to fetch
11:18:46.485  :2                 'src':                   0 package(s) to fetch
11:18:46.486  :2                 'WithPackages':          1 package(s) to fetch   -> ThreadAccessException
```

**A miss list of one is sufficient.** The five vulnerable public packages cached
normally during load; only the package absent from the database remained a miss,
and that single entry throws. A larger real solution reaches a 25-package list on
the first affected project and fails identically — the size of the list is not a
factor, only whether it is empty.

### The trigger is broader than it looks

The condition is **a package absent from the vulnerability database** — not a package with no advisory. Most public packages have no advisory and are still present with a clean record; those cache normally. In one real solution the 52 affected ids were:

- **~40 of the solution's own project outputs**, queried as though they were packages — `ProjectA.7.0.0.10`, `AppB.5.0.0.10`, including test projects. Rider is asking a vulnerability service about the user's own unpublished assemblies, which can never be in any database. *(Correlates with projects participating in a `ProjectReference` graph; a generated solution with zero `ProjectReference`s did not exhibit it. Not yet isolated.)*
- 11 packages from a private feed.
- **`Nancy.Hosting.Self 2.0.1`** — an ordinary public nuget.org package, ~800k downloads for that version.

If the project-outputs behaviour is general, every multi-project solution carries permanent cache misses by construction, and this is not a niche configuration.

### What this is not

Each ruled out with a dedicated run:

- **Not elapsed time.** Clean at 5m42s, 7m49s, 9m31s and 11m52s between load and report; the report-time walk was all-zero every time.
- **Not solution size or package volume.** 315 distinct transitive packages at 2016–2018 pins vs 317 at current versions (BCL-era `System.*` fan-out is shared, not additive). A 2-project solution and a 59-project one crash identically.
- **Not a private or authenticated feed.** A local folder source reproduces it.

### Why this is invisible to every consumer

The findings **are** produced — they reach the Problems View channel and appear in `backend.log` throughout:

```
[Error] Newtonsoft.Json 9.0.1 contains vulnerabilities: Packages with vulnerabilities have been detected
```

but `RiderSecurityErrorsInspection` is the only bridge from that channel to SARIF. In the minimal repro, 102 such lines were logged and **zero** reached the report.

Meanwhile:

- `invocations[0].exitCode` = `0`
- `executionSuccessful` = `true`
- `toolExecutionNotifications` = `null`
- Severity breakdown drops from **6 Critical** on a healthy run to **0 Critical**
- Both `RiderSecurityErrorsInspection` and `VulnerableApi` are **still declared** in `tool.extensions` (`rider.intellij.plugin.appender`) — so rule declaration does not signal the failure either

There is no signal anywhere that a security inspection died. A CI pipeline gating on Qodana passes green with known-vulnerable dependencies in the dependency graph.

### No configuration workaround

- `dependencyIgnores` gates the **licence** audit only — an ignored package is still the first one fetched.
- `NuGetAuditSuppress` takes an **advisory URL**, not a package id, and is report-side. Packages with no advisory have no URL to suppress.
- [RIDER-141570](https://youtrack.jetbrains.com/issue/RIDER-141570), which would let NuGet audit settings suppress the fetch, is still `Upvoting`.

Anything that only suppresses **reporting** would silence the checker rather than fix it.

### Related

- [QD-12585](https://youtrack.jetbrains.com/issue/QD-12585) — "Vulnerable .NET dependency not found in cloud" (2025-09-23) reports the same symptom: Ultimate Plus, raised correctly in Rider locally, absent from the CI run and Qodana Cloud. It was closed as a duplicate of [QD-10049](https://youtrack.jetbrains.com/issue/QD-10049) (`Type: UX`, licence-labelling). This report supplies the mechanism and a public reproduction that QD-12585 lacked.
- [QD-15005](https://youtrack.jetbrains.com/issue/QD-15005) — same defect class on the JVM side (`VulnerableApiService`, read-action threading assertion).
- [RIDER-141423](https://youtrack.jetbrains.com/issue/RIDER-141423) — related NuGet metadata-fetch issue, fixed; does not cover this path.

### Secondary observations

**a) The vulnerability service is queried with an unresolved `0.0.0` placeholder during the restore window.** For a package referenced as `Version="*"`:

```
10:37:20  GetResolvedInstalledPackagesAsync -> IdentityModel.0.0.0     stale
10:37:36  PackageSignatureVerificationLog:     IdentityModel.7.0.0     actually fetched
10:37:41  GetResolvedInstalledPackagesAsync -> IdentityModel.0.0.0     still stale
10:37:44  NuGetSecurityMetadataFetcher: no metadata for 'IdentityModel.0.0.0'
10:37:49  GetResolvedInstalledPackagesAsync -> IdentityModel.7.0.0     resolved
```

A ~29-second window in which the project model serves `0.0.0` and the security lookup lands inside it. Floating versions widen the window rather than causing it — a `2.*` reference in another solution resolved fast enough that no lookup fell inside. This did **not** cause the crash above and is reported separately as incorrect behaviour: querying with an unresolved version is wrong regardless.

**b) The metadata cache key appears to be case-sensitive.** Ids differing only in case (`Some.Package.1.0.0` and `some.package.1.0.0`) are both fetched, doubling work for affected packages.

### Attachments to include

- `log/idea.log` — the exception
- `log/*.backend.log` — the fetch walk, the `No security metadata found` lines, the `contains vulnerabilities` detections
- `qodana.sarif.json` — zero security results, both rules declared in `tool.extensions`

---

## Notes before submitting

1. **Fill in the repro URL** — the repo must be public first. Strip the workflow's `QODANA_TOKEN` binding and remove the secret before flipping visibility (see the repo README's pre-publication checklist).
2. **Project names are already generic** (`ProjectA`, `AppB`). Check any attached log excerpts are redacted the same way before uploading — raw `backend.log` contains real project and package names.
3. Do **not** ask for QD-12585 to be reopened; it is cited as prior art only, per David's call.
4. The `ProjectReference` correlation in "The trigger is broader than it looks" is explicitly marked *not yet isolated*. Leave it that way unless that run happens — an overstated secondary claim is what let QD-12585 be dismissed.
