# Replicera Community Principles

## A Useful Open-Source Product

Replicera Community is intended to remain a genuine, useful open-source product rather than a limited demonstration of a commercial offering. Core replication functionality belongs in the MPL-2.0-licensed Community project. This includes the capabilities represented by `Replicera.Core`, `Replicera.Dataverse`, `Replicera.Cli`, and community database providers such as SQL Server, PostgreSQL, Oracle, and future MySQL/MariaDB and SQLite providers.

## Community Contribution Guarantee

Features and contributions accepted into Replicera Community are intended to remain available as part of the open-source Community project. A feature will not subsequently be removed from Community solely to make that same feature exclusive to a commercial edition.

This guarantee is a project governance commitment. It does not modify, supplement, or replace the terms of the Mozilla Public License 2.0.

## Potential Commercial Products

Replicera may eventually offer a separately licensed Enterprise edition or commercial extensions. Possible areas include centralized or fleet management, web administration, enterprise SSO and RBAC, high availability and clustering, advanced monitoring and alerting, organizational governance, audit and compliance capabilities, and commercial support. These examples describe a possible product boundary and are not commitments to build any feature.

Existing Community functionality will not be moved behind an Enterprise boundary as part of establishing this model.

## Architecture Boundary

Community replication components should retain clean, provider-neutral contracts and explicit extension points. Optional commercial modules should be able to integrate through those boundaries without placing Enterprise-specific code in this repository or requiring Community components to depend on commercial assemblies.

Architecture decisions should continue to be based on current Community requirements. The possibility of future commercial modules does not justify speculative redesign or reduced Community functionality.
