# Changelog

Every push to `main` publishes a new patch version automatically (see `version.json` — height-based,
not manually tagged), so not every version number gets its own entry here. This file tracks what
actually changed.

## 2026-09-18

### Added

- Initial public release, forked from a betting platform's internal `Orange.Lib.XUnit`/`Orange.Lib.XUnit.Redis`
  (renamed `Scenarify`/`Scenarify.Redis`): scenario builder, JSON tokens/matching/snapshots, MockServer steps,
  `ServiceTestFixture`, and Redis Streams steps over [RedisEvents](https://github.com/bklooste/RedisEvents).
