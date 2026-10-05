# RustDesk: Architecture Notes

Study of the RustDesk source as of 2026-10-05: client `rustdesk/rustdesk` v1.5.0 and server `rustdesk/rustdesk-server`.
Licence: **AGPL-3.0** for both. If ConnectMe copies or links RustDesk code and is offered over a network, ConnectMe's source must be released under AGPL too.

---

## 1. What it is

RustDesk is an open-source, self-hostable remote desktop tool, similar to TeamViewer or AnyDesk.

- **Client** (one binary per OS): it can control other machines and can also be controlled. Targets are Windows, macOS, Linux, Android and iOS, plus a web client.
- **Server** (`rustdesk-server`): two small daemons.
  - **hbbs**: the ID/rendezvous server. Peers register their ID there, and it brokers NAT hole-punching.
  - **hbbr**: the relay server. It forwards traffic between two peers when a direct connection fails.
- Features: screen view/control, multi-monitor, audio, clipboard (text, images, files), file transfer, chat, TCP port forwarding/tunnelling, a remote terminal, camera viewing, voice call, session recording, privacy mode (blank the remote screen), virtual displays, remote printer, 2FA, trusted devices, address book, LAN discovery, Wake-on-LAN, and switching sides.
- The paid **Server Pro** adds a web console, users/groups, address-book sync, audit logs, OIDC login and a custom client generator. The OSS client already contains the HTTP calls for these (`src/hbbs_http/`, `/api/...`).

## 2. Network topology and ports

| Port | Proto | Who | Purpose |
|---|---|---|---|
| 21115 | TCP | hbbs | NAT type test |
| 21116 | UDP | hbbs | Peer registration/heartbeat (`RegisterPeer` every ~15s), UDP punch |
| 21116 | TCP | hbbs | Punch-hole requests, signalling, key exchange |
| 21117 | TCP | hbbr | Relay |
| 21118 | TCP | hbbs | WebSocket rendezvous (web client) |
| 21119 | TCP | hbbr | WebSocket relay |
| 21118 (client) | TCP | client | Optional "direct IP access" listener (`RENDEZVOUS_PORT + 2`) |

Key constants are in `libs/hbb_common/src/config.rs`: `CONNECT_TIMEOUT` 18s, `REG_INTERVAL` 15s, `RENDEZVOUS_TIMEOUT` 12s. The defaults are the public server `rs-ny.rustdesk.com` and the hard-coded server public key `RS_PUB_KEY`.

## 3. Repository layout (client)

```
src/                     Rust core (~75k lines)
  main.rs / core_main.rs CLI arg dispatch (--server, --tray, --cm, --connect, --install, --service, ...)
  rendezvous_mediator.rs Controlled side: talks to hbbs, answers punch/relay requests
  client.rs              Controller side: connects to a peer (punch → direct/relay/WebRTC), login, codecs, audio
  client/io_loop.rs      Controller session loop (video/audio/files/clipboard)
  server.rs              Controlled side "Server": registry of pub/sub Services + per-connection setup
  server/connection.rs   (8k lines) One controlled session: auth, permissions, message dispatch
  server/video_service.rs, video_qos.rs, display_service.rs, audio_service.rs,
         clipboard_service.rs, input_service.rs, terminal_service.rs, portable_service.rs, ...
  ipc.rs                 Local IPC (named pipe / unix socket) between the processes
  platform/              OS-specific code (Windows service, macOS perms, Linux X11/Wayland)
  privacy_mode*, virtual_display_manager.rs, port_forward*.rs, whiteboard/, lang/ (i18n)
  flutter_ffi.rs/flutter.rs  Bridge to the Flutter UI (flutter_rust_bridge 1.80)
  ui/                    Legacy Sciter UI (deprecated)
libs/
  hbb_common/  (git submodule, shared with server) rendezvous.proto, sockets, TCP framing+crypto, Config
  base/        message.proto (peer-to-peer protocol), option keys, file transfer (fs.rs)
  scrap/       Screen capture (DXGI/GDI on Windows, Quartz/ScreenCaptureKit, X11, Wayland/PipeWire, DRM, Android) + codecs (VPX, AV1, HW H.264/H.265, VRAM)
  enigo/       Input injection (mouse/keyboard)
  clipboard/   File clipboard (cliprdr-style)
  virtual_display/, remote_printer/, portable/
flutter/lib/   Current UI: desktop/, mobile/, web/, common/, models/ (ab, chat, file, input, peer, server, terminal...)
build.py       Build driver (`./build.py --flutter --hwcodec`)
```

The server repo is `src/rendezvous_server.rs` (hbbs), `src/relay_server.rs` (hbbr), `src/peer.rs` (in-memory peer map) and `src/database.rs` (SQLite `peer` table: guid, id, uuid, pk, user, status, note, info).

## 4. Process model on the controlled machine

The same executable runs in several roles, picked by CLI flag in `core_main.rs`:

- **`--service`** (Windows service / launchd / systemd) runs as SYSTEM/root. It keeps the server alive and survives logout, UAC and the lock screen.
- **`--server`** is the actual Server. It holds the rendezvous mediator, capture services and connections.
- **`--tray`** is the tray icon.
- **`--cm`** is the Connection Manager window. It shows "X wants to connect, Accept/Dismiss", chat and file permissions.
- Main UI process: the Flutter app.

They talk through **IPC** (`src/ipc.rs`): a named pipe on Windows, a Unix socket elsewhere, carrying a big `ipc::Data` enum. Config is owned by the server process and synced to the UI over IPC.

## 5. Identity and keys

- **ID**: 9-10 digit numeric. On desktop it is derived from the machine (MAC-based) or optionally the hostname. On mobile it is random 1,000,000,000–2,000,000,000. It is stored in config and registered with hbbs.
- **Keypair**: each device has an Ed25519 signing keypair (sodiumoxide `sign`). The public key is registered with hbbs through `RegisterPk {id, uuid, pk}`.
- **hbbs keypair**: hbbs has its own Ed25519 key (`id_ed25519` / `-k KEY`). Clients are configured with its public key, the "Key" field in network settings. hbbs signs `IdPk{id, pk}` of the target peer, so the controller can trust the peer's public key.

## 6. Rendezvous protocol (`hbb_common/protos/rendezvous.proto`)

Everything is one `RendezvousMessage` oneof sent over UDP/TCP/WS. The main ones:

- `RegisterPeer` / `RegisterPeerResponse{request_pk}`: heartbeat over UDP. hbbs records the peer's public `socket_addr` and `last_reg_time`. A peer is offline after `REG_TIMEOUT`.
- `RegisterPk` / `RegisterPkResponse`: registers the ID and public key, handles ID changes, `UUID_MISMATCH`, `ID_EXISTS`.
- `PunchHoleRequest{id, nat_type, licence_key, conn_type, token, udp_port, force_relay, socket_addr_v6, webrtc_sdp_offer}`: controller → hbbs.
- `PunchHole{socket_addr, relay_server, nat_type, ...}`: hbbs → target.
- `PunchHoleSent` / `PunchHoleResponse{socket_addr, pk(signed IdPk), relay_server, nat_type|is_local, failure}`: the reply path to the controller.
- `FetchLocalAddr` / `LocalAddr`: used when both peers sit behind the same public IP (LAN), so the LAN address is used instead of punching.
- `RequestRelay{id, uuid, relay_server, ...}` / `RelayResponse`: sets up a relay rendezvous keyed by a random UUID.
- `TestNatRequest/Response`: NAT type detection (asymmetric vs symmetric).
- `OnlineRequest/Response`: online status of peers in the address book (bitmap).
- `KeyExchange`/`KxParams`: encrypts the client↔hbbs TCP channel (signed by the server key).
- `PeerDiscovery`: LAN broadcast discovery. `IceCandidate`: trickle ICE for WebRTC.
- `ConnType`: DEFAULT_CONN, FILE_TRANSFER, PORT_FORWARD, RDP, VIEW_CAMERA, TERMINAL.
- `licence_key`: if hbbs/hbbr run with `-k`, requests whose key does not match are rejected (`LICENSE_MISMATCH`).

## 7. Connection establishment (end to end)

```
Controller A                      hbbs                         Controlled B
    |-- TCP connect, KeyExchange --->|                               |
    |-- PunchHoleRequest(id=B) ----->|  lookup B (online? LAN?)      |
    |                                |-- PunchHole(A addr) (UDP) --->|
    |                                |                               |-- B decides: punch UDP / TCP / IPv6 / WebRTC answer / relay
    |                                |<-- PunchHoleSent (TCP) -------|   (B also fires packets at A's addr to open its NAT)
    |<-- PunchHoleResponse(B addr, signed IdPk, relay) --|           |
    |== direct connect to B (race: UDP/KCP, TCP, IPv6, WebRTC) =====>|
    |   if all fail within the timeout:                              |
    |-- RequestRelay(uuid) -> hbbs -> B -> both connect to hbbr with same uuid; hbbr pairs & pipes bytes
```

- hbbs `handle_punch_hole_request` (server `rendezvous_server.rs:703`) checks the licence key, whether the peer is online and LAN vs WAN. `ALWAYS_USE_RELAY=Y`, or one peer on LAN with the other on WAN, forces `SYMMETRIC`, which means relay.
- The controlled side (`rendezvous_mediator.rs::handle_punch_hole`) relays if either NAT is symmetric, a proxy/WebSocket is in use, or `force_relay` is set. Otherwise it punches: UDP (KCP over UDP) if the controller reported a UDP NAT port, else TCP simultaneous-open, plus IPv6, plus a WebRTC answer if an SDP offer came in.
- The controller (`client.rs::_start_inner`) makes up to 3 punch attempts (3s, 6s, 9s). It then **races transports**, preferring WebRTC, and falls back to relay after a delay.
- **hbbr** (`relay_server.rs::make_pair_`): the first peer with a UUID waits up to 30s. When the second arrives with the same UUID they are paired, and bytes are copied raw with per-connection and total bandwidth limits. The relay never sees plaintext.

## 8. Peer-to-peer security handshake

Once a stream (direct or relayed) exists:

1. **B → A: `SignedId`**. B signs `IdPk{id, ephemeral X25519 pk, dtls_fingerprint, kx_version}` with its Ed25519 key (`server.rs::identity_handshake`).
2. A verifies the signature with B's Ed25519 public key. A got that key from hbbs, signed by hbbs's key, which A verifies with the configured server key. This chain is the anti-MITM guarantee.
3. **A → B: `PublicKey{asymmetric_value, symmetric_value}`**. A generates a random secretbox key and seals it with NaCl `box_` (X25519 + XSalsa20-Poly1305) to B's ephemeral key (`common.rs::create_symmetric_key_msg`).
4. Both switch the stream to **`secretbox`** (XSalsa20-Poly1305) with a per-direction sequence-number nonce. Newer `kx_version` derives separate per-direction subkeys bound to the handshake transcript (`hbb_common/src/tcp.rs`).
5. If the key cannot be verified (no server key configured), it **falls back to unencrypted** and the UI shows it as not secured. WebRTC fails closed instead.

## 9. Login and authorization

- B sends `Hash{salt, challenge}`. A sends `LoginRequest{username=B's id, password, my_id, my_name, option, conn-type union, version, os_login, hwid}`.
- Password proof: `h1 = SHA256(password + salt)`, `password_field = SHA256(h1 + challenge)`. It is compared in constant time (`connection.rs::verify_h1`). Stored permanent passwords hold only `h1`.
- Password kinds: a **temporary/one-time password** that rotates and auto-rotates after 10 consecutive failures, and a **permanent password**.
- Approve modes: password, click (CM dialog), or both. `LOGIN_MSG_NO_PASSWORD_ACCESS` means waiting for a click.
- Other gates: IP whitelist, ID whitelist, per-IP failure throttling (by minute, plus IPv6 prefix), a cap on unauthenticated connections per IP, optional **2FA** (TOTP, `auth_2fa.rs`), trusted devices (hwid), and per-feature permissions (`keyboard, clipboard, file, audio, camera, terminal, tunnel, restart, recording, block_input, privacy_mode...`). Each login type (file transfer, terminal, port forward, camera) is checked against its permission.
- On success B replies `LoginResponse{peer_info}` with displays, platform, codec abilities, resolutions and features, and the session starts.

## 10. Session protocol (`libs/base/protos/message.proto`)

This is a single `Message` oneof. Main families:

- **Video**: `VideoFrame` (VP8/VP9/AV1/H264/H265 `EncodedVideoFrames`, or RGB/YUV), `CursorData`, `CursorPosition`, `SwitchDisplay`, `CaptureDisplays`, resolution changes.
- **Input**: `MouseEvent`, `KeyEvent` (keyboard modes: legacy, map, translate), `PointerDeviceEvent` (touch).
- **Audio**: `AudioFormat` + `AudioFrame` (Opus via magnum-opus).
- **Clipboard**: `Clipboard`/`MultiClipboards` (text, RTF, HTML, images) and `Cliprdr` (file clipboard, RDP-style).
- **Files**: `FileAction`/`FileResponse` (read dir, send/receive with block, digest and resume), plus rename, delete and mkdir.
- **Misc**: chat, option changes, permission info, elevation request (UAC), restart, close reason, privacy mode, virtual display, and more.
- Also: `TerminalAction/Response` (PTY), `PortForwardChannel` (multiplexed tunnels), `ScreenshotRequest`, `VoiceCall*`, `SwitchSides*`, `TestDelay` (latency probe that drives QoS).

**Video pipeline**: capture (`libs/scrap`) → encode, picking a codec negotiated from both sides' `SupportedEncoding`/`SupportedDecoding` (default VP9; AV1/H264/H265 when available; HW encode via `hwcodec`/`vram`) → send. **QoS** (`video_qos.rs`) adapts FPS (init 15, default 30, 1–120) and bitrate ratio from `TestDelay` RTT. 150 ms is the "good network" threshold.

**Services** (`server.rs`) use a publish/subscribe model. Each capability (video per display, audio, clipboard, cursor, input, printer) is a `Service`, and connections subscribe to the ones their permissions allow.

## 11. Configuration and customization

- Config files: `RustDesk.toml` (id, keypair, passwords), `RustDesk2.toml` (options: rendezvous server, relay, api server, key...), and per-peer configs. On Windows they live in `%AppData%\RustDesk\config`, or the service profile when installed.
- **Custom server without rebuilding**: rename the exe, e.g. `rustdesk-host=<server>,key=<pubkey>,api=<url>,relay=<r>.exe` (`src/custom_server.rs`). Options can also be set with `--config` or `--option`.
- Built-in settings (`BUILTIN_SETTINGS`) let branded builds lock options: hide settings, force approve mode, hostname as ID, and others.
- Proxy support: SOCKS5/HTTP, and WebSocket transport for restrictive networks.

## 12. Self-hosting the server

```bash
# simplest
docker run -d --net=host -v ./data:/root rustdesk/rustdesk-server hbbs
docker run -d --net=host -v ./data:/root rustdesk/rustdesk-server hbbr
```

- On first run, hbbs generates `id_ed25519` and `id_ed25519.pub`. Give the `.pub` contents to clients as "Key".
- Useful env/flags: `-k _` (require key), `-r relay:21117`, `ALWAYS_USE_RELAY=Y`, `DB_URL`, `RUST_LOG`, bandwidth limits on hbbr (`TOTAL_BANDWIDTH`, `SINGLE_BANDWIDTH`, `LIMIT_SPEED`).
- hbbs stores peers in SQLite `db_v2.sqlite3`. The online state is in memory only.
- If the WebSocket ports (21118/21119) sit behind a reverse proxy, the proxy must overwrite `X-Real-IP`/`X-Forwarded-For`.

## 13. Building the client

- Requirements: Rust ≥1.75, vcpkg (libvpx, libyuv, opus, aom), and Flutter for the modern UI. Linux also needs X11/PipeWire/PulseAudio dev libs.
- Run `python build.py --flutter [--hwcodec]`, or `cargo run` for the legacy Sciter UI (needs the sciter dll).
- Cargo features: `flutter`, `hwcodec`, `vram`, `mediacodec`, `drm`, `unix-file-copy-paste`, `screencapturekit`.
- Outputs: `librustdesk` (cdylib loaded by Flutter), plus the `rustdesk`, `service` and `naming` binaries.

## 14. Engineering conventions in the codebase (from its AGENTS.md)

- No `unwrap()`/`expect()` outside tests or lock poisoning. Assumes a Tokio runtime with no nested runtimes, and locks are never held across `.await`.
- Keep diffs minimal and additive, put platform code in `src/platform/` behind thin hooks, and throttle hot-path logs (`throttled_log!`).
- `hbb_common` is a submodule shared with the server, so client-only code goes in `libs/base`.

## 15. Takeaways for ConnectMe

1. The core architecture is a **rendezvous server**, **P2P hole punching** with **relay fallback**, an **E2E-encrypted session** signed by device keys, and **protobuf messages**. It is a proven design for a TeamViewer-style product.
2. The simplest viable reimplementation: an ID server (register + heartbeat + punch broker), a dumb relay, an Ed25519/X25519/secretbox handshake, and a protobuf session with video frames (VP9), input and clipboard.
3. The hardest parts, which make up most of RustDesk's code: cross-platform screen capture (Wayland, the Windows secure desktop/UAC, macOS permissions), running as a system service across login/lock screens, codec negotiation and QoS, and file transfer with resume.
4. **Licensing**: forking RustDesk means ConnectMe is AGPL. A closed-source product would need a clean-room implementation, or would have to talk to the server/protocol without copying code.
