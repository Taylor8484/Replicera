# ADR 0005: JSON configuration, environment secrets, and exit codes

- Status: Accepted for initial implementation
- Date: 2026-09-19

## Decision

Use a portable JSON configuration file containing named sources, destinations, and jobs. Configuration stores secret references, not secret values. v0.1 resolves secrets from environment variables; platform secret stores may be added behind an abstraction.

CLI exit categories are: `0` success, `2` invalid command or configuration, `3` authentication/authorization, `4` source connectivity, `5` destination connectivity, `6` schema conflict or unsupported metadata, `7` synchronization failure, `8` resynchronization required, and `70` unexpected internal failure.

## Consequences

Commands accept an explicit configuration path and remain non-interactive in automation. Validation reports object paths but never resolved secret values. Exit codes are public compatibility surface from v0.1 onward.
