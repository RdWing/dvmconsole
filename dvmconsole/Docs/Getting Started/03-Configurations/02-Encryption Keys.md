# Encryption Keys

Encryption keys allow the console to decrypt and transmit encrypted voice traffic when supported by the connected FNE system.

The console can load local key material from a YAML key file referenced by the codeplug.

---

# FNE Compatibility

Encryption and key-management behavior depends on the connected FNE. Validate key delivery, algorithm support, and encrypted voice against the exact FNE build used by the deployment.

---

# Key File Location

Reference the key file with `keyFile` in the codeplug:

```yaml
keyFile: "Full/Path/To/Keyfile.clear"
```

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
- `key`: key material as an even-length hexadecimal string. Do not include spaces, separators, or non-hexadecimal characters.

---

# Channel Encryption Fields

Encrypted channels can include:

```yaml
keyId: 0x50
algo: "aes"
```

Supported `algo` values include:

- `aes`
- `des`
- `arc4`
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

When **Restore Selected Channels On Startup** is enabled, selected encrypted channels may need to request keys after startup.

The console waits for the relevant FNE connection to complete before sending startup key requests. It then waits a short post-connect delay and spaces multiple key requests apart so the FNE is not flooded.

This startup delay applies to restored selected encrypted resources. Normal key behavior outside startup remains unchanged.

---

# Key Status

Open **Tools > Encryption Key Status** to inspect loaded or received key state for configured encrypted resources.

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
