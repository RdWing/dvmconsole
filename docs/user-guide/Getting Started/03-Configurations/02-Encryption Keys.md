# Encryption Keys

Encryption keys allow the console to decrypt and transmit protected P25, DMR,
and NXDN 4800 voice traffic when supported by the connected FNE system.

The console uses two P25 key-material pathways. After an FNE connects, it
requests every configured P25 algorithm/key ID through KMM. A valid key
delivered by that FNE takes precedence over the local YAML fallback. DMR and
NXDN privacy keys are loaded from the local YAML file.

Keys are isolated by FNE system. Two systems can safely use the same algorithm and key ID with different key material. KMM-delivered material is retained only in memory and is cleared when that system disconnects, revealing the local fallback again.

---

# FNE Compatibility

Encryption and key-management behavior depends on the connected FNE. Validate key delivery, algorithm support, and encrypted voice against the exact FNE build used by the deployment.

---

# Key File Location

Reference the key file with `keyFile` in the codeplug:

```yaml
keyFile: "Full/Path/To/Keyfile.clear"
```

The key file is optional when every required key will be delivered by FNE/KMM. When present, it provides the fallback for all configured systems.

---

# Key File Format

The key file contains a `keys` list.

Example:

```yaml
keys:
  - protocol: "p25"
    keyId: 0x1
    algId: 0x84
    key: "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"

  - protocol: "dmr"
    keyId: 0x2
    algId: 0x05
    key: "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"

  - protocol: "nxdn"
    keyId: 0x3
    algId: 0x01
    key: "1234"
```

Fields:

- `protocol`: `p25`, `dmr`, or `nxdn`. Entries without this field remain P25
  for compatibility with existing key files.
- `keyId`: key ID referenced by channels. NXDN key IDs are 1 through 63.
- `algId`: algorithm ID.
- `key`: key material as hexadecimal without spaces or separators. P25 keeps
  its established compatibility rules. DMR ARC4 uses 5 bytes, DES-OFB uses 8,
  and AES-256 uses 32. NXDN EHR uses a non-zero 15-bit seed stored in 2 bytes,
  DES uses 8 bytes, and AES-256 uses 32.

Algorithm IDs are protocol-specific and are not interchangeable. P25 AES uses
`algId: 0x84`, while DMR Association AES-256 uses `algId: 0x05`. A DMR key must
also declare `protocol: "dmr"`; an entry without `protocol` is treated as P25
for compatibility with older key files.

---

# Channel Encryption Fields

Encrypted channels can include:

```yaml
keyId: 0x50
algo: "aes"
```

Supported `algo` values include:

- `aes`
- `des` or `des-ofb`
- `arc4` or `adp`
- `none`

For NXDN, use `ehr`, `des`, or `aes`. NXDN 9600/EFR is not implemented in dvmhost.

P25 channel key IDs are hexadecimal. The `0x` prefix is recommended for
clarity; unprefixed P25 values retain the legacy WPF hexadecimal
interpretation.

If `keyId` is blank or zero, the channel is treated as clear for normal operation.

---

# Selectable Encryption

P25, DMR, and NXDN secure-capable channels can expose an in-card encryption toggle:

```yaml
keyId: 0x50
algo: "aes"
selectable_encryption: true
```

When enabled, the resource card shows **SELECT** next to the TAR indicator area. Clicking **SELECT** toggles console transmit between encrypted and clear for that system/talkgroup.

The selected encrypted/clear state is saved and restored across restarts. The
key and algorithm still come from the codeplug. **CLEAR** sends clear audio and
admits clear receive traffic; **SECURE** sends encrypted audio and rejects clear
receive traffic. Incoming secure calls are decoded only when their on-air
metadata identifies an available configured key. A selectable channel in
**CLEAR** can therefore receive clear calls even when that secure key is not
currently available. DMR
secure calls also encode late-entry MI fragments and burst-F algorithm/key
identifiers. The reviewed `dvmhost r05a06_dev` clears burst F while regenerating
RF, so that identity is not preserved end to end through that host revision.
NXDN DES/AES calls likewise alternate `VCALL` and successor-IV metadata in
SACCH while voice continues, allowing recovery at the next eight-frame
encryption-session boundary when the host preserves the required startup MI.

---

# FNE Key Requests

After each FNE connection completes, the console requests the distinct algorithm/key IDs configured by that system's encrypted P25 channels. The system `rid` is used as the requesting console identity and must be a valid nonzero 24-bit ID.

The console does not request or consume the FNE key inventory. Each automatic
KMM request therefore requires a nonzero `keyId` and supported `algo` on at
least one P25 channel. KMM supplies the requested key material; it does not
assign keys to channels or send a channel-to-key list.

If the FNE delivers a valid KMM key, it becomes the active key for that system. A response from one FNE is never applied to another FNE, even when both use the same algorithm and key ID. When the connection is lost, its KMM keys are removed and local keys become active again where available.

Clear and MI-instruction KMM responses are accepted. Peer-encrypted KMM responses require the system's separate `kmfPresharedKey`; the FNE transport `presharedKey` is never reused for this purpose.

---

# Key Status

Open **Tools > Encryption Key Status** to inspect loaded or received key state for configured encrypted resources. Available entries identify their active source as **local file** or **FNE/KMM**.

When a supported local DMR key is unavailable, the row also shows the required
`protocol`, `algId`, and key length. This is configuration guidance only; the
actual key value is never shown.

The status page shows identifiers and availability only. Key material is never displayed or written to the debug log.

If an encrypted channel does not decrypt correctly:

- verify the channel `keyId`
- verify the channel `algo`
- verify the key file path
- verify that the FNE is connected
- verify that the FNE has delivered required key material

---

# Safety Notes

- Protect clear key files.
- Do not commit operational key material to source control.
- Use test keys for development environments.
- KMM-delivered key material is never written back to the local key file.
