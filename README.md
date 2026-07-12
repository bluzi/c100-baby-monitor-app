# C100 Baby Monitor

Native Android baby monitor for the Xiaomi C100 camera (and other `chuangmi.camera.*` models that
speak the CS2/MISS protocol). Log in with your Mi account, pick the camera once, and every launch
drops you straight into the live feed. Audio keeps playing with the app in the background and the
screen locked or off, and an optional crying alarm rings when the camera hears the baby cry — with
a single sensitivity dial, and tuned per camera by your own yes/no answers after each alarm.

- **Specs:** `spec/` — the behavioral source of truth (spec-driven repo, see `CLAUDE.md`).
- **Build:** `./gradlew assembleDebug`
- **Run on emulator:** `./gradlew runEmulator` · **Run on phone:** `./gradlew runPhone`
- **Tests + lint:** `./gradlew check`

The Xiaomi cloud + camera protocol is a Kotlin port of the working TypeScript implementation in
`bluzi/c100` (itself derived from go2rtc). Interop test vectors generated from that implementation
live in `app/src/test/resources/protocol-vectors.json`.

Battery note: for reliable all-night monitoring, exempt the app from battery optimization
(Settings → Apps → Baby Monitor → Battery → Unrestricted). The app holds a wake + Wi-Fi lock while
monitoring, but some OEMs still kill unexempted apps.
