# Updates

Every push to `main` is a release, and every device is expected to end up running it without anyone
doing anything — no account, no credential, nothing to set up. That is the whole point: a baby
monitor that needs to be manually updated is one that quietly runs last month's bugs.

An updater is also the most dangerous thing in the app. It is the one component allowed to replace
the running code, and the one component that could take the monitor down at the exact moment it is
needed. So the rules below are about restraint as much as delivery.

## Delivery

- **UPD-1** A release is published per platform, from the same commit and under the same version.
  A change that only touches one app's shell releases only that app; a change to the monitor releases
  every app it changed, because it changed them. (Android and the desktops publish to the GitHub
  release; iOS ships through the App Store — UPD-2i.)
- **UPD-2** `[android]` `[device]` The phone updates itself from the published release (Obtainium
  watches the repository). Exactly one `.apk` asset per Android release — Obtainium must be able to
  resolve a single APK or silent background updates stop working. A release that carries the desktops'
  assets too changes nothing (they are not APKs), and a release carrying **no** APK — a desktop-only
  change — must leave the phone on its current version rather than stalling it.
- **UPD-2i** `[ios]` `[device]` The iPhone updates itself through the **App Store** — the only
  sanctioned channel on iOS — so the app never checks for, downloads, installs, or restarts into an
  update on its own. That is not a gap but UPD-5's restraint kept by construction: a monitor that
  never relaunches itself at 3am is exactly what this project wants, and on iOS it comes for free.
  The running version stays visible in the app (LIVE-15), so "did the update land?" is still
  answerable at a glance.
- **UPD-3** `[desktop]` `[device]` A desktop app updates itself, and it checks **only at launch** and
  when a parent asks (UPD-9): it checks for a newer release when it starts, and **never again while it
  runs**. An update that arrives at 3am is an update nobody asked for, and a monitor has nothing to gain
  from learning about a new version before morning. When there is a newer release the app **offers it**
  (UPD-5); it downloads and installs only what the parent accepts, and **verifies it against the checksum
  published with the release before running it** — a download that does not match is discarded, not
  installed. This is the one thing standing between a truncated download and a monitor that will not
  start tonight.
- **UPD-6** `[desktop]` `[device]` The running version is visible in the app (LIVE-15), so
  "did the update land?" is answerable at a glance.
- **UPD-11** `[desktop]` `[device]` Updating automatically is the default, and it can be turned off.
  A parent can switch off the automatic launch check (UPD-3) in settings; while it is off, the app
  checks for nothing and offers nothing on its own — it opens straight to the window. It still updates
  when asked — a manual check (UPD-9) works exactly as before — so turning the automatic check off
  silences it without giving up the ability to update. (There is no such control on the phones: Android's updates are Obtainium's
  and iOS's are the App Store's, and the app drives neither — UPD-2/UPD-2i.)

## Restraint

- **UPD-5** `[desktop]` `[device]` **The app never restarts itself, and never on its own initiative.**
  A monitor that relaunches itself at 3am is precisely the failure this project exists to prevent, and
  no update is worth it.
  At launch the app checks for a newer release. If there is one it asks — once — whether to **install and
  restart now**, naming the new version, in a question placed in the middle of the screen (DESK-29). The
  question is asked at launch, the one moment the app can be sure a person is at the machine: they just
  opened it.
  - **Accept**: the app downloads and verifies the update, puts it in place, and restarts into it.
    Monitoring resumes by itself; the outage is seconds long, with the parent standing there.
  - **Decline**: **nothing is downloaded and nothing changes.** Monitoring continues on the current
    version. The app does not restart itself, does not nag, and does not ask again until the next
    launch — not later that day, and not overnight.
  A check that finds nothing newer, a check that fails (UPD-4), and automatic updates being turned off
  (UPD-11) all pass without a word.
- **UPD-10** `[windows]` `[device]` Windows will not let a running program overwrite its own files
  (a Mac will, which is the only reason this is not shared), so on a PC the update is applied **by the
  newly downloaded copy**: when the parent accepts, the app downloads and verifies it beside itself,
  then hands the swap to the new version, which waits for the old one to exit, replaces the files and
  starts up. The bargain is exactly UPD-5's — the app only ever restarts because the parent just asked
  it to.

## Saying so when it stops working

- **UPD-4** `[desktop]` `[device]` A launch check can still fail — GitHub cannot be reached, its API
  returns an error, or a download will not verify. When one does, **the app says so** (in settings,
  and in the menu bar's or the tray's menu). It does not go quiet. An app that has silently stopped
  updating looks exactly like an app that is up to date, and would keep looking that way for months.
- **UPD-9** `[desktop]` `[device]` A check can always be asked for by hand — from the menu
  bar's or the tray's menu, and from the live feed's menu — so a parent who wants to know is never
  made to relaunch the app to find out. A manual check **shows its progress**: while it looks, a
  *checking for updates* indicator is on screen and can be **cancelled**. Then it always answers: if
  there is a newer release it offers to **install and restart**, showing **both the version running now
  and the version available**; if there is not, it says the app is **up to date**. Accepting downloads,
  verifies, installs and restarts exactly as the launch offer does (UPD-5); a check that fails says so
  (UPD-4) rather than leaving the parent wondering whether the click did anything.
- **UPD-8** `[desktop]` `[device]` A failed update check never affects monitoring. Checking,
  downloading and verifying happen out of the monitor's way; if any of it fails, the monitor carries
  on and the failure is reported (UPD-4) rather than surfaced as a monitoring problem.
