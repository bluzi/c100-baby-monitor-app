# Updates

Every push to `main` is a release, and every device is expected to end up running it without anyone
doing anything. That is the whole point: a baby monitor that needs to be manually updated is one
that quietly runs last month's bugs.

An updater is also the most dangerous thing in the app. It is the one component allowed to replace
the running code, and the one component that could take the monitor down at the exact moment it is
needed. So the rules below are about restraint as much as delivery.

## Delivery

- **UPD-1** A release is published per platform, from the same commit and under the same version.
  A change that only touches one app's shell releases only that app; a change to the monitor releases
  every app it changed, because it changed them.
- **UPD-2** `[android]` `[device]` The phone updates itself from the published release (Obtainium
  watches the repository). Exactly one `.apk` asset per Android release — Obtainium must be able to
  resolve a single APK or silent background updates stop working. A release that carries the desktops'
  assets too changes nothing (they are not APKs), and a release carrying **no** APK — a desktop-only
  change — must leave the phone on its current version rather than stalling it.
- **UPD-3** `[desktop]` `[device]` A desktop app updates itself, and it does so **only at
  launch**: it checks for a newer release when it starts, downloads it in the background, and
  verifies it before it is ever run. A download that does not match the checksum published with the
  release is discarded, not installed. It never checks again while it is running — an update that
  arrives at 3am is an update nobody asked for, and a monitor has nothing to gain from learning about
  it before morning.
- **UPD-6** `[desktop]` `[device]` The running version is visible in the app (LIVE-15), so
  "did the update land?" is answerable at a glance.

## Restraint

- **UPD-5** `[desktop]` `[device]` **The app never restarts itself.** A monitor that
  relaunches itself at 3am is precisely the failure this project exists to prevent, and no update is
  worth it.
  A verified update is put in place *on disk* at launch — the running app is untouched and keeps
  watching — and then the app **asks, once, whether to restart into it**. The question is asked
  seconds after launch, which is the one moment the app can be sure a person is at the machine:
  they just opened it.
  - Answering yes restarts the app; monitoring resumes by itself, and the outage is seconds long,
    with the parent standing there.
  - Answering no changes nothing at all. The new version is already on disk and takes over at the
    next launch. **The app does not ask again**, does not nag, and does not restart on its own —
    not later that day, and not overnight.
- **UPD-10** `[windows]` `[device]` Windows will not let a running program overwrite its own files
  (a Mac will, which is the only reason this is not shared), so on a PC the verified update waits
  *beside* the app rather than on top of it, and the swap happens at the next launch, before
  monitoring starts. The bargain a parent is offered is exactly UPD-5's — never a restart they did
  not ask for, one question, and declining costs nothing.
- **UPD-7** `[desktop]` `[device]` The version an update put in place is visible before it
  is running (UPD-6): a parent who declined the restart can still tell what will run next time, and
  what is running now.

## Saying so when it stops working

- **UPD-4** `[desktop]` `[device]` The updater needs a credential to read the private
  repository, and credentials expire. If a launch check fails — an expired or revoked token, a
  repository that cannot be reached — **the app says so** (in settings, and in the menu bar's or the
  tray's menu). It does not go quiet. An app that has silently stopped updating looks exactly like an
  app that is up to date, and would keep looking that way for months.
- **UPD-9** `[desktop]` `[device]` A check can always be asked for by hand — from the menu
  bar's or the tray's menu, and from the live feed's menu — so a parent who wants to know is never
  made to relaunch the app to find out. A manual check behaves exactly like the launch check: verify,
  put it in place, ask once.
- **UPD-8** `[desktop]` `[device]` A failed update check never affects monitoring. Checking,
  downloading and verifying happen out of the monitor's way; if any of it fails, the monitor carries
  on and the failure is reported (UPD-4) rather than surfaced as a monitoring problem.
