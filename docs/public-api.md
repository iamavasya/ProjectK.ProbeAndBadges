# Public API Contract (v1)

This document freezes the intended public API surface for the badges part of the library.
Any change here should be treated as an explicit contract decision.

## Abstractions

Namespace: ProjectK.ProbeAndBadges.Abstractions

- record Badge
- record BadgesMetadata
- record BadgeQuery
- record BadgesAssetsSnapshotInfo
- interface IBadgesCatalog
- interface IBadgesAssetsStore

## Core

Namespace: ProjectK.ProbeAndBadges

- enum BadgesDataSource
- enum AssetsMissingImagesPolicy
- class BadgesCatalogOptions
- class BadgesCatalog
  - AssetsDirectoryPath (read-only; resolved extracted images folder or null)
  - AssetsSnapshot (read-only; includes extracted snapshot metadata)

## Dependency Injection

Namespace: ProjectK.ProbeAndBadges.DependencyInjection

- static class ServiceCollectionExtensions
  - AddProbeAndBadgesApi(IServiceCollection, Action<BadgesCatalogOptions>?)
  - Registers: IBadgesCatalog, IBadgesAssetsStore

## Contract rules

- Read-only behavior only for badges catalog.
- No mutable operations in IBadgesCatalog.
- Constructor and options changes are considered public API changes.
- Any rename/remove in this list requires a major version bump.

Usage examples for host registration and runtime integration are documented in `docs/host-integration.md`.
