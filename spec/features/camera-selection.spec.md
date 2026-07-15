# Camera selection

Choosing which camera to monitor. Chosen once, persisted, changeable at any time without signing
out.

- **CAM-1** After sign-in with no stored camera, the user sees the account's cameras (name and
  model); devices on the account that are not cameras are not offered.
- **CAM-2** Choosing a camera stores the choice and opens its live feed.
- **CAM-3** With a camera stored, opening the app goes straight to that camera's live feed — the
  picker is skipped (see APP-1).
- **CAM-4** From the live feed the user can switch to a different camera without signing out; the
  new choice replaces the stored one and takes effect immediately.
- **CAM-5** `[device]` A failure to load the device list shows a readable error with retry; an account with
  no cameras says so instead of showing an empty screen.
- **CAM-6** When the account has exactly one camera, it is selected automatically and its live feed
  opens — the picker is never shown for a single camera. The picker appears only when there is a
  genuine choice (more than one camera); an account with no cameras still says so rather than showing
  a one-item list or an empty screen (CAM-5). This holds anywhere the picker would otherwise appear:
  the first sign-in, and after clearing the selection to switch (CAM-4). A parent with one camera
  never taps through a list of one.
