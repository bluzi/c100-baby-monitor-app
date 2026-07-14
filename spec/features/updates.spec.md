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
- **UPD-3** `[macos]` `[device]` The Mac updates itself, and it does so **only at launch**: it checks
  for a newer release when it starts, downloads it in the background, and verifies it before it is
  ever run. A download that does not match the checksum published with the release is discarded, not
  installed. It never checks again while it is running — an update that arrives at 3am is an update
  nobody asked for, and a monitor has nothing to gain from learning about it before morning.
- **UPD-6** `[macos]` `[device]` The running version is visible in the app (LIVE-15), so "did the
  update land?" is answerable at a glance.

## Restraint

- **UPD-5** `[macos]` `[device]` **The app never restarts itself.** A monitor that relaunches itself
  at 3am is precisely the failure this project exists to prevent, and no update is worth it.
  A verified update is installed *on disk* at launch — the running app is untouched and keeps
  watching — and then the app **asks, once, whether to restart into it**. The question is asked
  seconds after launch, which is the one moment the app can be sure a person is at the Mac:
  they just opened it.
  - Answering yes restarts the app; monitoring resumes by itself, and the outage is seconds long,
    with the parent standing there.
  - Answering no changes nothing at all. The new version is already on disk and takes over at the
    next launch. **The app does not ask again**, does not nag, and does not restart on its own —
    not later that day, and not overnight.
- **UPD-7** `[macos]` `[device]` The version an update installed is visible before it is running
  (UPD-6): a parent who declined the restart can still tell what will run next time, and what is
  running now.

## Saying so when it stops working

- **UPD-4** `[macos]` `[device]` The updater needs a credential to read the private repository, and
  credentials expire. If a launch check fails — an expired or revoked token, a repository that
  cannot be reached — **the app says so** (in settings, and in the menu bar's menu). It does not go
  quiet. An app that has silently stopped updating looks exactly like an app that is up to date, and
  would keep looking that way for months.
- **UPD-9** `[macos]` `[device]` A check can always be asked for by hand — from the menu bar's menu
  and from the live feed's menu — so a parent who wants to know is never made to relaunch the app to
  find out. A manual check behaves exactly like the launch check: verify, install, ask once.
- **UPD-8** `[macos]` A failed update check never affects monitoring. Checking, downloading and
  verifying happen out of the monitor's way; if any of it fails, the monitor carries on and the
  failure is reported (UPD-4) rather than surfaced as a monitoring problem.
