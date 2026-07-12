# Xiaomi protocol (interop contract)

The wire behavior the app must reproduce to talk to Mi Cloud and to the camera. This is externally
imposed (Mi Home / go2rtc compatible) — byte-level detail here is deliberate. Reference vectors
generated from the proven implementation live in `app/src/test/resources/protocol-vectors.json`.

## Mi account login

- **PROTO-1** The password is sent as its MD5 hash, uppercase hex.
- **PROTO-2** Login endpoints respond with JSON prefixed by `&&&START&&&`; the prefix is stripped
  before parsing, and a missing prefix is an error.
- **PROTO-3** Login is: `GET serviceLogin` (yields `qs`, `_sign`, `sid`, `callback`) →
  `POST serviceLoginAuth2` with those plus `user` and the hashed password. The response either
  carries `ssecurity`+`passToken`+`location`, or demands a captcha (`captchaURL`) or two-factor
  verification (`notificationUrl`).
- **PROTO-4** Completing login follows the `location` redirect chain manually (up to 10 hops),
  collecting `userId`, `cUserId`, `serviceToken`, `passToken` cookies along the way; `ssecurity`
  may also arrive as JSON inside an `Extension-Pragma` response header on any hop.
- **PROTO-5** A stored `userId` + `passToken` pair re-authenticates via `serviceLogin` with only
  those two cookies; a fresh `ssecurity` always pairs with the fresh `serviceToken` minted by its
  own redirect chain (old/new must never be mixed).
- **PROTO-6** Two-factor flow: the notification URL's `identity/list` yields a `flag` (4 = phone,
  8 = email) and an `identity_session` cookie; `verify<Phone|Email>` reveals the masked target;
  `send<Phone|Email>Ticket` requests the code; posting the ticket to `verify<Name>` yields the
  final `location` chain.

## Signed cloud API requests

- **PROTO-7** Every API call is a form POST where: `_nonce` = 8 random bytes + big-endian u32
  minutes-since-epoch (base64); `signedNonce` = SHA-256(`ssecurity` ‖ nonce); `rc4_hash__` =
  base64 SHA-1 of `METHOD&path&data=<data>&<base64 signedNonce>`; then `data` and `rc4_hash__`
  are RC4-encrypted; `signature` is recomputed the same way over the encrypted values.
- **PROTO-8** RC4 uses the signedNonce as key and discards the first 1024 keystream bytes.
  Responses are base64 + RC4-decrypted with the same key; JSON `code != 0` is an error.
- **PROTO-9** Signed requests carry exactly the cookies `userId` and `serviceToken` (plus
  `cUserId` when known) — no others.
- **PROTO-10** An auth-shaped failure (401/403, "auth"/"token"/"invalid"/"expired") triggers one
  re-login via the stored `passToken` and a single retry of the request.
- **PROTO-11** The device list comes from `/v2/home/device_list_page` (`did`, `name`, `model`,
  `mac`, `localip`); a camera is a device whose model contains `.camera.`.
- **PROTO-12** Camera stream access comes from `/v2/device/miss_get_vendor` with the client's
  NaCl public key (hex) and `support_vendors: "TUTK_CS2_MTP"`; it returns the vendor id
  (4 = cs2 — the only one supported), the device public key (hex), and a `sign` token.
- **PROTO-24** Camera settings are MiOT properties, read via `/miotspec/prop/get` and written via
  `/miotspec/prop/set`, each with body `{"params":[{"did","siid","piid"[,"value"]}]}`. The result
  is a per-item array; an item's `code` of 0 (or absent) is success, anything else is an error.
  On the C100, night vision is Camera Control **siid 2 / piid 3** (uint8: `0` = on, `1` = off,
  `2` = auto).

## Media key + encryption

- **PROTO-13** The media key is the NaCl box precomputed shared key (X25519 scalar mult +
  HSalsa20) of the device public key and the client private key — 32 bytes.
- **PROTO-14** Command and media payloads are ChaCha20-encrypted: wire format is 8 nonce bytes
  followed by the ciphertext, where the cipher nonce is those 8 bytes left-padded with 4 zero
  bytes (counter 0). Encryption uses fresh random nonce bytes per message.

## CS2 transport (camera P2P)

- **PROTO-15** Handshake over UDP to camera port 32108: send LAN-search `[0xF1 0x30 0x00 0x00]`
  until a punch packet (`0x41`) arrives from the camera; echo the punch packet back until the
  camera answers P2P-ready — `0x42` selects UDP, `0x43` selects TCP on the port the reply came
  from.
- **PROTO-16** TCP frames are the payload prefixed by an 8-byte header: big-endian u16 payload
  size at offset 0, magic `0x68` at offset 2.
- **PROTO-17** Data rides in DRW messages (`0xF1 0xD0`): big-endian u16 body size at offset 2,
  magic `0xD1` at offset 4, channel byte at offset 5, big-endian u16 sequence at offset 6.
  Channel 0 carries commands, channel 2 incoming media. On UDP each DRW is acked
  (`0xF1 0xD1`) with its channel and sequence.
- **PROTO-18** Both channels carry 4-byte big-endian length-prefixed records, reassembled from
  the DRW byte stream regardless of DRW packet boundaries (media records routinely span many
  DRW frames — a keyframe is tens of KB). Command records on channel 0 are: little-endian u32
  command id, then the payload. A length prefix no real record could have (far beyond a
  keyframe) is corrupt input: it is treated as a dead connection — it never crashes the app and
  never stalls the stream waiting for bytes that will not come.
- **PROTO-19** `[device]` On TCP the client sends ping `[0xF1 0xE0 0x00 0x00]` every ~1 s on an
  independent timer, and never replies to the camera's PING with a PONG (doing so makes the
  camera tear the session down).

## MISS session (over CS2)

- **PROTO-20** After transport connect, the client authenticates with command 0x100 carrying JSON
  `{public_key: <client public hex>, sign, uuid: "", support_encrypt: 0}` (unencrypted); success
  is a response containing `"result":"success"`.
- **PROTO-21** Control commands after auth are sent as command 0x1001 whose payload is
  ChaCha20-encoded `[big-endian u32 inner command][JSON body]`. Video start (0x102) body is
  `{"videoquality":<q>,"enableaudio":<0|1>}` where q maps hd→2 (3 on C200/C300 models), sd→1,
  auto→0; video stop is 0x103 with empty body.
- **PROTO-22** Each reassembled channel-2 record is one media packet: 32-byte little-endian
  header — u32 payload size, u32 codec id, u32 sequence, u32 flags, u64 timestamp (ms) —
  followed by the ChaCha20-encrypted payload.
- **PROTO-23** Codec ids: 4 = H.264, 5 = H.265, 1024 = PCM, 1026 = PCMU, 1027 = PCMA,
  1032 = Opus. Opus audio is 48 kHz; PCM-family sample rate is 16 kHz when bits 3–6 of flags are
  non-zero, else 8 kHz. Unknown codec ids are skipped, and a packet that fails to decrypt is
  skipped — neither kills the stream.
