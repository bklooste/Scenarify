# Changelog

Every push to `main` publishes a new patch version automatically (see `version.json` — height-based,
not manually tagged), so not every version number gets its own entry here. This file tracks what
actually changed.

## 2026-10-04 (later)

### Changed

- The caller's `CancellationToken` moved from an optional trailing parameter on `Eventually.Assert`/`.True` to a
  **required first** parameter on separate overloads: `Eventually.Assert(ct, assertion, timeout, because)`. Calls that
  pass no token keep a signature with no `CancellationToken` at all, so nothing existing changes.

  Why: an optional trailing `CancellationToken` makes xUnit's analyzer (xUnit1051, "should use
  `TestContext.Current.CancellationToken`") fire at every call site that omits it. In a consumer treating warnings as
  errors that is a build break, and 0.1.11 caused one on the first direct `Eventually.Assert` in a test body it met.
  An overload the caller either picks or does not cannot produce the diagnostic.

## 2026-10-04

### Fixed

- `ServiceTestFixture.InitializeAsync` gave MockServer setup, `Features` and the health/readiness waits one shared
  `StartupTimeout` budget. A feature that overran left the health wait holding an already-cancelled token, so it failed
  instantly on every attempt for its whole timeout and then blamed the service — `'service at https://… healthy' still
  failing after 244 attempts over 119.6s: A task was canceled` — for a stack it had never actually asked. Each phase now
  gets its own budget.

  Why: observed on an hourly end-to-end run where seeding overran 2 minutes. All 46 tests failed with that message,
  including ones that only load a Swagger page, while the service under test was healthy and answering in 116ms. The
  startup budget was always documented as time for *the service* to come up, never as a ceiling on feature setup.

- `Eventually.Assert` and `.True` take the caller's `CancellationToken`. Retrying cannot outlive it: a token that is
  already cancelled fails immediately without evaluating the assertion, and one cancelled mid-wait reports the last
  real failure rather than polling on to the timeout and reporting `A task was canceled`. Passing no token keeps the
  previous behaviour exactly.

  Why: the loop only ever checked `TestContext.Current.CancellationToken`, so an `OperationCanceledException` raised by
  any *other* token was treated as an ordinary failure and retried. That is what turned the bug above from a zero-second
  problem into two minutes of misattributed polling, and it would do the same to any caller that passes a budget down.

## 2026-09-28

### Added

- `Eventually` gives up early once the environment is clearly broken: after `ConsecutiveTimeoutLimit` waits (default
  **3**) have each timed out back to back, later waits fail immediately instead of polling, and say plainly that they
  were not evaluated and which earlier failure is the real one. Any success resets the count, so a slow suite never
  trips it. Only a wait whose budget was at least `MinimumCountedTimeout` (default 5s) counts, so a deliberately fast
  negative test cannot trip it for whatever runs next. `EVENTUALLY_TIMEOUT_LIMIT` sets the limit from the environment;
  0 restores the old behaviour of always polling.

  Why: a timeout costs the *whole* timeout, every time. On a suite with dozens of waits, one dependency being down
  turned a red build into tens of minutes of CI that told you nothing the first failure had not already said.
  Observed on a 14-class end-to-end suite that spent 40 minutes hitting its job ceiling rather than failing.

## 2026-09-24

### Added

- Pluggable readiness for `ServiceTestFixture`: `ServiceTestOptions.Readiness` (`IReadinessProbe`) replaces the
  `/health` poll; `ReadinessProbe.From`, `.None` and `.PublishAndObserve` built-ins; `ServiceTestOptions.ServesHttp = false`
  for workers with no listener. `StandardApiTests` is a no-op for non-HTTP services. Default behaviour is unchanged.

## 2026-09-18

### Added

- Initial public release, forked from a betting platform's internal `Orange.Lib.XUnit`/`Orange.Lib.XUnit.Redis`
  (renamed `Scenarify`/`Scenarify.Redis`): scenario builder, JSON tokens/matching/snapshots, MockServer steps,
  `ServiceTestFixture`, and Redis Streams steps over [RedisEvents](https://github.com/bklooste/RedisEvents).
