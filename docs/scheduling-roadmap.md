# Scheduling Roadmap

## Current baseline

Replicera supports two complementary execution styles:

- `replicera sync --job <name>` performs a single manual or externally scheduled synchronization.
- `replicera worker --job <name>` remains open and synchronizes one job using its configured interval.

The portable foreground worker is the permanent baseline. Future service integrations must wrap the same worker and synchronization runner rather than replacing the portable command.

The first worker intentionally supports one job per process, an interval measured after completion, optional execution on startup, graceful cancellation, and retry on the next interval after failure. It does not accumulate missed runs, automatically perform full resynchronization, reload configuration while running, or install itself as an operating-system service.

## Next: harden the portable worker

- Extract the synchronization orchestration and structured results from the CLI formatting layer so `sync` and `worker` share a dedicated application service.
- Add a health model that distinguishes the running process from the most recent synchronization result.
- Add optional bounded failure backoff and jitter without duplicating Dataverse request retries.
- Decide whether permanent configuration, authentication, schema, and `ResyncRequired` failures should pause a job until operator intervention.
- Add an optional configuration reload mechanism with an explicit validation-and-swap boundary.
- Add end-to-end cancellation coverage against each destination provider.

## Multiple scheduled jobs

Add `replicera worker --all` only after the single-job lifecycle is proven. Resolve these behaviors first:

- Whether jobs run serially or with bounded concurrency.
- Fairness when one job has a long initial load.
- Dataverse service-protection limits shared by jobs using one source.
- Destination contention and resource limits.
- Failure isolation so one job cannot terminate other job loops.
- Per-job health and shutdown reporting.

One-process-per-job must remain supported because it provides simple isolation and allows process managers to restart jobs independently.

## Calendar schedules

Evaluate calendar schedules separately from interval polling. Candidate behavior includes cron expressions or explicit daily/weekly schedules. A design must define:

- Time-zone identifiers and cross-platform mapping.
- Daylight-saving gaps and repeated local times.
- Misfire behavior after downtime.
- Whether a missed occurrence is skipped or run once on restart.
- Validation and human-readable schedule previews.

Interval scheduling remains the recommended choice for change-feed polling.

## Service and daemon integration

Add native background operation around the existing foreground worker:

1. Host lifecycle support for SIGTERM and service-manager shutdown deadlines.
2. A documented systemd unit using `replicera worker`.
3. Windows Service integration using the same worker host.
4. Service installation, removal, start, stop, and status commands only if automatic installation provides enough value to justify elevated privileges and platform-specific maintenance.
5. Recovery policies and restart-loop protection owned by the service manager where possible.

Configuration and secrets must remain outside versioned application directories so portable and service installations can use the same files and environment-variable references.

## Container operations

- Publish a worker-oriented container example with the foreground process as PID 1.
- Define liveness as process responsiveness and readiness/health as the latest job result.
- Document graceful-stop timeouts for long destination transactions.
- Avoid embedding a second process supervisor in the container.

## Observability and administration

- Provide structured worker lifecycle events and stable event identifiers.
- Expose next planned run, last attempt, consecutive failures, and paused reason through `status`.
- Define retention for synchronization and schema history.
- Consider notification hooks only after sanitized event payloads and retry behavior are specified.
- Preserve the rule that raw credentials, connection strings, row values, and Dataverse change tokens never appear in logs or health output.

## Compatibility requirements

All future scheduling and service work must preserve:

- Manual `sync`, including `--table` and `--full`.
- Portable foreground worker operation.
- Existing per-table destination locks and atomic checkpoint behavior.
- Explicit operator control over full resynchronization and destructive operations.
- Linux development and deployment without Windows-only tooling.
- Direct source-to-destination data flow with no required hosted Replicera service.
