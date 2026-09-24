# Changelog

Every push to `main` publishes a new patch version automatically (see `version.json` — height-based,
not manually tagged), so not every version number gets its own entry here. This file tracks what
actually changed.

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
