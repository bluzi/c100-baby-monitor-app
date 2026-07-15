# Login

Signing in to the Xiaomi account that owns the camera. Signing in happens once; afterwards the
session persists and refreshes itself.

- **AUTH-1** A signed-out user is asked for Xiaomi account username/email, password, and server
  region — nothing else is reachable before signing in.
- **AUTH-2** Submitting valid credentials signs the user in and moves on (to camera selection, or
  straight to the live feed when a camera is already stored — see APP-1).
- **AUTH-3** When Xiaomi demands a captcha, the captcha image is shown and login continues with
  the entered code; a rejected code yields a fresh captcha to retry, not a failure.
- **AUTH-4** When Xiaomi demands two-factor verification, the app says where the code was sent
  (masked phone number or email) and completes login with the submitted code.
- **AUTH-11** `[device]` Sign-in never asks for a tap before typing: the credentials screen opens
  with the username field already focused, and the two-factor step with its code field already
  focused — keyboard up, ready to type straight away.
- **AUTH-5** A successful sign-in is persisted: relaunching the app (including after force-stop)
  does not ask for credentials.
- **AUTH-6** Persisted session tokens are stored encrypted, never in plain text.
- **AUTH-12** `[desktop]` `[device]` The token is kept in the machine's own encrypted store (the
  Keychain on a Mac, the user's DPAPI store on a PC — AUTH-6) and the app reads it back **without
  ever asking the user for anything**, including after an update, when every byte of the binary has
  changed. A monitor that stopped at a password box after an overnight update, with nobody awake to
  answer it, would be a monitor that failed exactly when it mattered. If the store refuses or the
  item is gone, the session is dropped and the app asks for a sign-in (AUTH-8). It never crashes, and
  it never falls back to storing the token unencrypted.
- **AUTH-7** When the short-lived service session expires, the app transparently refreshes it
  using the stored long-lived token — no user interaction — and persists the refreshed session.
- **AUTH-8** If the server **refuses** the stored long-lived token, the user lands back on sign-in
  with a message saying the session expired. A refresh that fails for any other reason — no
  network, an answer that did not come from the account server (captive portal, outage page), a
  redirect loop — is a temporary error: it is retried and **never** signs the user out. Declaring
  a session expired discards it, so it is only ever declared when the server actually said no.
- **AUTH-9** A failed sign-in (wrong credentials, network error, unexpected response) shows a
  readable error and lets the user retry with the fields still editable.
- **AUTH-13** When sign-in authenticates the user but the session cannot be stored — the encrypted
  store of AUTH-6 refuses to seal it — the app reports that sign-in did not complete and leaves the
  user on the sign-in screen to retry, fields still editable (as AUTH-9). It never reports success
  and then silently returns to sign-in as though the attempt never happened. Authenticating a user
  and quietly logging them back out, with nothing said, is precisely the silent failure this app
  exists to prevent.
- **AUTH-10** The user can sign out from inside the app; signing out forgets the session, the
  selected camera, and returns to sign-in.
