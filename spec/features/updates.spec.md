# Updates

Every push to `main` is a release, and both devices are expected to end up running it without
anyone doing anything. That is the whole point: a baby monitor that needs to be manually updated
is one that quietly runs last month's bugs.

An updater is also the most dangerous thing in the app. It is the one component allowed to replace
the running code, and the one component that could take the monitor down at the exact moment it is
needed. So the rules below are about restraint as much as delivery.

## Delivery

- **UPD-1** A release is published per platform, from the same commit and under the same version.
  A change that only touches one app's shell releases only that app; a change to the shared monitor
  releases both, because it changed both.
- **UPD-2** `[android]` `[device]` The phone updates itself from the published release (Obtainium
  watches the repository). Exactly one `.apk` asset per Android release — Obtainium must be able to
  resolve a single APK or silent background updates stop working. A release that carries the Mac's
  assets too changes nothing (they are not APKs), and a release carrying **no** APK — a macOS-only
  change — must leave the phone on its current version rather than stalling it.
- **UPD-3** `[macos]` `[device]` The Mac updates itself: it checks for a newer release on launch and
  periodically while running, downloads it in the background, and verifies it before it is ever run.
  A download that does not match the checksum published with the release is discarded, not installed.
- **UPD-6** `[macos]` `[device]` The running version is visible in the app (LIVE-15), so "did the
  update land?" is answerable at a glance.

## Restraint

- **UPD-5** `[macos]` `[device]` **An update is never applied while monitoring is running.** It is
  downloaded, verified, and then it waits — until monitoring is stopped, or until the next launch.
  The app never restarts itself, and never asks to. A monitor that relaunches itself at 3am is
  precisely the failure this project exists to prevent, and no update is worth it.
- **UPD-7** `[macos]` `[device]` Once an update is staged, the app says so quietly (it is ready, it
  will apply when monitoring stops) — the parent is never nagged, and never surprised.

## Saying so when it stops working

- **UPD-4** `[macos]` `[device]` The updater needs a credential to read the private repository, and
  credentials expire. If update checks fail repeatedly — an expired or revoked token, a repository
  that cannot be reached — **the app says so**. It does not go quiet. An app that has silently
  stopped updating looks exactly like an app that is up to date, and would keep looking that way
  for months.
- **UPD-8** `[macos]` A failed update check never affects monitoring. Checking, downloading and
  verifying happen out of the monitor's way; if any of it fails, the monitor carries on and the
  failure is reported (UPD-4) rather than surfaced as a monitoring problem.
