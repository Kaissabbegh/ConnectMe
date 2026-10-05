# ConnectMe wire protocol (v1)

The laptop (sender) connects to the iMac (receiver) over **TCP port 47800**.

## Discovery

The receiver advertises a Bonjour/mDNS service:

- type: `_connectme._tcp`, port 47800
- TXT: `v=1`, `name=<computer name>`, `w=<screen pixel width>`, `h=<screen pixel height>`

The sender sends a PTR query for `_connectme._tcp.local` to `224.0.0.251:5353` from an ephemeral port (an RFC 6762 "legacy unicast" query), so responders reply directly to it. It uses the reply's source IP as the iMac's address. Manual IP entry always works as a fallback, for example on networks that block multicast.

## Framing

Every message is:

```
u32  length   (big-endian; counts type + payload)
u8   type
...  payload  (length - 1 bytes)
```

The maximum length is 16 MiB.

| type | name | direction | payload |
|---|---|---|---|
| 0x01 | HELLO | laptop → iMac | UTF-8 JSON `{"version":1,"name":"LAPTOP-01","code":"1234"}` |
| 0x02 | WELCOME | iMac → laptop | JSON `{"version":1,"name":"Office iMac","width":2560,"height":1440}` |
| 0x03 | REJECT | iMac → laptop | JSON `{"reason":"Wrong pairing code"}`, then the iMac closes the connection |
| 0x10 | VIDEO | laptop → iMac | `u64 pts_us` (BE), `u8 flags` (bit0 = keyframe/IDR), then one H.264 access unit in Annex-B format |
| 0x7F | BYE | either | empty; graceful close |

## Session

1. The laptop connects and sends HELLO within 5 s.
2. The iMac checks the 4-digit pairing code shown on its screen and replies WELCOME or REJECT. Only one session is allowed at a time; a second laptop gets `REJECT "busy"`.
3. The laptop streams VIDEO messages. Each one is a complete access unit beginning with an AUD NAL. SPS/PPS precede every IDR. Keyframes are sent at least once per second.
4. If the iMac receives no VIDEO for 5 s it closes the session. Capture runs at a constant frame rate, so silence means the link is dead.

## Video

- H.264 High/Main profile, 4:2:0, no B-frames, constant frame rate (30 or 60).
- The iMac (Late 2013, macOS ≤ Catalina) decodes H.264 in hardware through VideoToolbox. HEVC is **not** hardware-decodable on that machine.

## Not in v1 (planned)

- Encryption (v1 is plaintext on the LAN; v2 will add an X25519 + XChaCha20-Poly1305 handshake keyed by the pairing code)
- Sending the iMac's keyboard and mouse back to the laptop
- Audio
- A UDP transport and keyframe-on-demand requests
