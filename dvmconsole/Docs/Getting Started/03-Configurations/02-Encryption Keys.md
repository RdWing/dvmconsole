# Encryption Keys

Encryption keys allow the console to decrypt and transmit encrypted P25 voice traffic when supported by the connected FNE system.

The console uses two automatic key-material pathways. After an FNE connects, it requests every configured P25 algorithm/key ID through KMM. A valid key delivered by that FNE takes precedence. If KMM has not supplied the key, the console falls back to the local YAML key file referenced by the codeplug.

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
  - keyId: 0x1
    algId: 0x84
    key: "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"

  - keyId: 0x2
    algId: 0xAA
    key: "0011223344"
```

Fields:

- `keyId`: key ID referenced by channels.
- `algId`: algorithm ID.
- `key`: key material as hexadecimal without spaces or separators. AES-256 requires exactly 32 bytes (64 hexadecimal characters), DES-OFB requires 8 bytes (16 hexadecimal characters), and ARC4/ADP requires 5 bytes (10 hexadecimal characters).

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

If `keyId` is blank or zero, the channel is treated as clear for normal operation.

---

# Selectable Encryption

P25 secure-capable channels can expose an in-card encryption toggle:

```yaml
keyId: 0x50
algo: "aes"
selectable_encryption: true
```

When enabled, the resource card shows **SELECT** next to the TAR indicator area. Clicking **SELECT** toggles console transmit between encrypted and clear for that system/talkgroup.

The selected encrypted/clear state is saved and restored across restarts. The key and algorithm still come from the codeplug; the toggle only controls whether the console uses them for transmit.

---

# FNE Key Requests

After each FNE connection completes, the console requests the distinct algorithm/key IDs configured by that system's encrypted P25 channels. The system `rid` is used as the requesting console identity and must be a valid nonzero 24-bit ID.

If the FNE delivers a valid KMM key, it becomes the active key for that system. A response from one FNE is never applied to another FNE, even when both use the same algorithm and key ID. When the connection is lost, its KMM keys are removed and local keys become active again where available.

Clear and MI-instruction KMM responses are accepted. Peer-encrypted KMM responses require the system's separate `kmfPresharedKey`; the FNE transport `presharedKey` is never reused for this purpose.

---

# Key Status

Open **Tools > Encryption Key Status** to inspect loaded or received key state for configured encrypted resources. Available entries identify their active source as **local file** or **FNE/KMM**.

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
