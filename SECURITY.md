# Security Policy

Replicera is pre-release software and has no supported production version yet. Report suspected vulnerabilities privately to the repository maintainers rather than in a public issue.

Do not include credentials, connection strings, certificates, tokens, or replicated business data in a report. Provide a minimal sanitized reproduction, affected commit, impact, and any known mitigation.

Replicera must resolve secrets at runtime, redact sensitive values from diagnostics, use encrypted transport supported by each service, and avoid destructive schema changes by default. Test only against systems and data you are authorized to access.
