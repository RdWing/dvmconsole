# Codeplug creation

A codeplug is a YAML file that defines the console's systems, tabs, resources,
and group tabs.

At minimum, a codeplug defines:

- `systems`
- `zones`

Common optional sections include:

- `groups`
- `keyFile`
- `patchSourceIdPassthrough`
- `web_streams` under a zone

---

# Compatibility

The Avalonia application preserves the current YAML contract and accepts the legacy `patchGroups` key. Review older codeplugs before use because unsupported or misspelled values can still produce a valid YAML file with incorrect operator behavior.

---

# Basic structure

```yaml
keyFile: "Full/Path/To/Keyfile.clear"

systems:
  - ...

groups:
  - ...

patchSourceIdPassthrough: false

zones:
  - ...
```

---

# Systems

Each system defines an FNE connection.

Example:

```yaml
systems:
  - name: "System 1"
    identity: "Console 1"
    address: "fne.example.local"
    port: 62031
    peerId: 1000001
    rid: "1001"
    password: "RPT_PASSWORD"
    encrypted: false
    presharedKey: "123ABC1234"
    aliasPath: "Full/Path/To/alias.yml"
```

Fields:

- `name`: internal system name. Channels reference this value.
- `identity`: peer identity shown to the FNE.
- `address`: FNE hostname or IP address.
- `port`: FNE port.
- `peerId`: peer ID used for the console connection.
- `rid`: radio ID used when the console transmits and when it requests configured P25 keys through FNE/KMM.
- `password`: FNE password.
- `encrypted`: whether the FNE connection uses transport encryption.
- `presharedKey`: key used when `encrypted` is enabled.
- `aliasPath`: optional RID alias YAML file.

The current plaintext and legacy encrypted FNE transports are compatibility
protocols, not mutually authenticated sessions. DVM Console rejects malformed
frames and exact encrypted packet replays within a bounded receive window. The
transport still cannot prove the master's identity or detect every forged
packet. Use FNE only over a trusted network or authenticated VPN.

---

# Zones

Each zone becomes a tab in the main console.

Example:

```yaml
zones:
  - name: "Primary"
    tabColor: "#E57373"
    tabTextColor: "#000000"
    channels:
      - ...
```

Fields:

- `name`: tab label.
- `tabColor`: tab background color in hex.
- `tabTextColor`: tab text color in hex.
- `channels`: resources shown on that tab.
- `web_streams`: optional web URL stream chips shown on that tab.

Long tab names are allowed. DVM Console trims their labels so activity icons
remain visible.

---

# Channels

Each channel defines a resource card.

Example:

```yaml
channels:
  - name: "Channel 1"
    system: "System 1"
    tgid: "2001"
    mode: "p25"
    keyId: 0x50
    algo: "aes"
    selectable_encryption: true
    resourceColor: "#150282"
    rx_only: false
    card_size: normal
```

Fields:

- `name`: resource/card name.
- `system`: system name from the `systems` section.
- `tgid`: target talkgroup ID.
- `mode`: `p25`, `dmr`, `nxdn`, or `analog`. If omitted, P25 is used. NXDN is the 4800-baud voice mode; NXDN 9600/EFR is not implemented in dvmhost.
- `keyId`: optional encryption key ID. P25 values are hexadecimal; use an
  `0x` prefix for clarity. Unprefixed P25 values remain hexadecimal for
  compatibility with WPF-era codeplugs.
- `algo`: optional encryption algorithm, such as `aes`, `des`, `arc4`, or `none`.
- `selectable_encryption`: optional flag for P25, DMR, or NXDN secure-capable resources. When `true`, the card shows a **SELECT** toggle so operators can choose encrypted or clear transmit. This requires a valid `keyId` and `algo`.
- `resourceColor`: optional resource card color in hex.
- `rx_only`: optional receive-only flag. When `true`, the resource card hides PTT, alert tone select, and channel marker/hold controls, and the resource is skipped by global, patch/group, and alert-tone transmit target paths.
- `card_size`: optional fixed resource card size. Supported values are `small`, `normal`, and `large`. If omitted or invalid, `normal` is used.
- `slot`: optional DMR slot field if used by the deployment.

Before transmitting or otherwise using a target TG, DVM Console checks it
against the active talkgroup rules received from the connected FNE.

Card size behavior:

- `small`: compact status/PTT card. The volume slider, alert tone select, channel marker, and call history buttons are hidden.
- `normal`: default resource card size and layout.
- `large`: larger resource card with larger text and controls.

---

# Web streams

Web streams appear as compact audio chips on a zone tab.

Example:

```yaml
web_streams:
  - name: "Stream 1"
    url: "https://streams.example.local/stream-1"
    authUsername: "stream-user"
    authPassword: "stream-password"
    idleColor: "#150282"
```

Fields:

- `name`: stream chip label. Keep names unique because saved position, volume, active startup state, and output routing are keyed by name.
- `url`: direct HTTP or HTTPS audio stream URL. WAV and MP3 are supported by the built-in decoder. Other formats require a compatible FFmpeg executable selected with `DVM_FFMPEG`.
- `authUsername`: optional HTTP Basic Auth username for protected streams.
- `authPassword`: optional HTTP Basic Auth password for protected streams.
- `idleColor`: optional active-idle chip color in hex. If omitted, the standard selected-resource blue is used.

Web stream chips start disabled after console load. Click the chip to start or stop local playback.

With **Restore Selected Channels On Startup** enabled, DVM Console restarts a
web stream only when its codeplug path, canonical URL, and credentials match the
stream the operator previously started. After any of those values change, start
the stream once manually to authorize the new definition. Legacy selections
saved by name alone remain off until selected again.

Basic Auth credentials are stored in the codeplug. Protect the file when it
contains stream credentials.

After you click it, the chip turns amber while connecting. DVM Console makes up
to three attempts, with a short delay between them, before marking the stream
down.

An active chip turns green when DVM Console detects audio. If the URL is
unreachable or the audio cannot be decoded, the chip turns red and shows
`Down`. Click a down stream once to turn it off.

Web streams are local monitor widgets only. They are not patch or multi-select members.

---

# Groups

Each group defines a tab in the Groups window.

Example:

```yaml
groups:
  - name: "Patch 1"
    type: "patch"
  - name: "Multi Select 1"
    type: "multiselect"
```

Fields:

- `name`: group tab label.
- `type`: `patch` or `multiselect`.

Group memberships are assigned in the Groups window, not in the codeplug.

Patch group members persist across restart. Patch active state is separate and only persists when **Settings > Retain Patch State on Startup** is enabled.

Legacy `patchGroups` entries are treated as patch groups for compatibility.

---

# Patch source ID passthrough

`patchSourceIdPassthrough` selects the source ID used for forwarded patch
traffic.

```yaml
patchSourceIdPassthrough: false
```

When `false`, forwarded patch traffic uses the configured console RID for the destination system.

When `true`, the console attempts to pass through the inbound source ID while forwarding patch audio.

---

# Key file

Use `keyFile` to reference a YAML key file:

```yaml
keyFile: "Full/Path/To/Keyfile.clear"
```

The file provides the fallback for configured P25 keys. After a system
connects, DVM Console requests its keys through FNE/KMM. A valid KMM key for
that system takes precedence until the FNE disconnects. See **Encryption Keys**
for the file format and runtime behavior.

---

# Example codeplug

```yaml
keyFile: "C:/Example/keys.clear"

systems:
  - name: "System 1"
    identity: "Console 1"
    address: "fne.example.local"
    port: 62031
    peerId: 1000001
    rid: "1001"
    password: "RPT_PASSWORD"
    encrypted: false
    presharedKey: "123ABC1234"
    aliasPath: "C:/Example/alias.yml"

groups:
  - name: "Patch 1"
    type: "patch"
  - name: "Multi Select 1"
    type: "multiselect"

patchSourceIdPassthrough: false

zones:
  - name: "Primary"
    tabColor: "#E57373"
    tabTextColor: "#000000"
    channels:
      - name: "Channel 1"
        system: "System 1"
        tgid: "2001"
        mode: "p25"
        keyId: 0x50
        algo: "aes"
        resourceColor: "#150282"
        card_size: normal

      - name: "Channel 2"
        system: "System 1"
        tgid: "2002"
        mode: "p25"
        resourceColor: "#150282"
        card_size: small
    web_streams:
      - name: "Stream 1"
        url: "https://streams.example.local/stream-1"
        # Optional HTTP Basic Auth credentials for protected streams.
        #authUsername: "stream-user"
        #authPassword: "stream-password"
        idleColor: "#150282"

  - name: "DMR"
    tabColor: "#81C784"
    tabTextColor: "#000000"
    channels:
      - name: "Channel 3"
        system: "System 1"
        tgid: "3001"
        mode: "dmr"
```

---

# Recommended practices

- Keep system names stable after deployment because channels reference them.
- Use clear channel names because saved positions and volume are keyed by channel name.
- Use clear group names, such as `Patch 1` or `Multi Select 1`.
- Keep zone tabs short enough for operators to scan quickly.
- Verify each channel references a valid system.
- Verify each group has a supported `type`.
- Keep web stream names stable so saved chip position and volume remain associated with the correct stream.
- Confirm TGs exist in FNE talkgroup rules before relying on them operationally.
