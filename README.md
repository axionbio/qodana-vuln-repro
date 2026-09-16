# qodana-dotnet: vulnerability checker throws before reporting

`RiderSecurityErrorsInspection` detects vulnerable NuGet packages, then throws
`ThreadAccessException` before any of them reach SARIF. The job exits `0` with
`executionSuccessful: true` and `toolExecutionNotifications: null`, so nothing
downstream can distinguish this from a clean scan.

Reproduced on `QDNET-262.10050` (CLI 2026.2.1) and `QDNET-262.9598`
(CLI 2026.2.0), Ultimate Plus.

Full write-up: [`QD-TICKET.md`](QD-TICKET.md). Logs and SARIF: [`evidence/`](evidence).

## Trigger

One package absent from the vulnerability database — not a package without an
advisory. The service returns nothing, `FetchAndStoreAsync` stores nothing, and
the package remains a cache miss permanently. Every later pass re-fetches it,
including the `RiderPackageVulnerabilitiesProblemsViewReporter` pass, which runs
on `:2` and reaches a fetcher that asserts it is not on that thread.

Here that package is `Qvuln.Unlisted.Probe 1.0.0`: an empty `netstandard2.0`
class library packed into `./localfeed`, with an id that 404s on nuget.org. No
server, no credentials, no private feed.

## Reproduce

Needs a self-hosted Windows runner and an Ultimate Plus token. Add the token as
a repository secret named `QODANA_TOKEN` and restore the `env:` block noted at
the foot of the workflow, then:

```
gh workflow run qodana.yml
```

Two projects, 25s of analysis, 2m40s end to end. Deterministic.

Full scan only — under `pr-mode` no analysable file is in the diff, the
inspection never runs, and nothing crashes.

## What the run shows

Load-time fetches succeed on pool threads. The report-time pass runs on `:2`,
finds the probe still uncached, and throws 11ms after the inspection announces
it is reporting:

```
13:45:37.408  JetPool(S) #2:13   Updating security metadata for project 'WithPackages': 66 package(s) to fetch
13:45:52.419  :2                 Updating security metadata for project 'WithPackages': 1 package(s) to fetch

13:45:52,411 INFO   RiderSecurityErrorsInspection - Reporting security problems
13:45:52,422 SEVERE JetBrains.Threading.ThreadAccessException: This action cannot be executed on the :2 thread.
   at JetBrains.Threading.JetDispatcherEx.AssertNonMainThread(JetDispatcher dispatcher)
   at JetBrains.ProjectModel.NuGet.Searching.Fetching.NuGetSecurityMetadataFetcher.FetchAsync(PackageIdentity identity, Lifetime lifetime, Boolean noCache)
   at JetBrains.Rider.Backend.Features.NuGet.Security.RiderNuGetInstalledPackageSecurityMetadataCache.FetchAndStoreAsync(PackageIdentity package)
   at JetBrains.Rider.Backend.Features.NuGet.Security.RiderNuGetInstalledPackageSecurityMetadataCache.UpdateProjectAsync(IProject project)
   at JetBrains.Rider.Backend.Features.NuGet.Security.RiderNuGetInstalledPackageSecurityMetadataCache.UpdateProjectsAsync(IReadOnlyCollection`1 projects)
   at JetBrains.Rider.Backend.Features.NuGet.Security.Vulnerabilities.RiderPackageVulnerabilitiesProblemsViewReporter.<>c__DisplayClass12_0.<<-ctor>b__1>d.MoveNext()
```

The findings exist — `backend.log` carries 77 `contains vulnerabilities`
lines. None reach the report:

```
results                                   : 6      (the control findings)
  RiderSecurityErrorsInspection           : 0
  VulnerableApi                           : 0
invocations[0].exitCode                   : 0
invocations[0].executionSuccessful        : true
invocations[0].toolExecutionNotifications : null
```

`Detailed summary` reports High 1 / Moderate 4 / Low 1 — no Critical, against
6 Critical `Rider project security errors` on a run without the probe.

## Not the cause

Each ruled out by running it:

| | |
|---|---|
| Elapsed time between load and report | Clean at 5m42s, 7m49s, 9m31s, 11m52s; report-time walk all-zero every time |
| Solution or dependency size | 315 distinct transitive packages at 2016-2018 pins vs 317 at current; 2-project and 59-project runs behave identically |
| A private or authenticated feed | A local folder source reproduces it |

Those larger configurations were generated variants of this repository; only
the minimal case is kept here.

## Layout

```
src/WithPackages   5 vulnerable nuget.org packages, plus the probe
src/NoPackages     no packages — the contrasting "0 to fetch" path
tools/probe        the unresolvable package, packed by the workflow
evidence/          log and SARIF excerpts from run 35129948060
```

Note that GitHub does not permit public repositories to use self-hosted runners
unless the runner group opts in, so on a public fork this workflow is a record
of how the runs were produced rather than something that will execute as-is.
