# Changelog

Every push to `main` publishes a new patch version automatically (see `version.json` — height-based,
not manually tagged), so not every version number gets its own entry here. This file tracks what
actually changed.

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
