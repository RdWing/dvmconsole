# Configuration Studio and codeplugs

Configuration Studio edits the same YAML codeplug that DVM Console already
loads. You can still use the file with compatible versions or maintain it by
hand. Studio keeps your changes in a draft until you review and save them.

Open the current codeplug from:

```
File > Configuration Studio
```

To start a new file, choose **File > New Configuration**. The Studio is a
separate, modeless window, so you can refer to the main console while editing.
Opening a Studio section again brings the existing window forward.

![Configuration Studio shell](../../Assets/configuration-studio-shell.png)

## Finding and editing configuration

The left side groups configuration as FNE systems, zones, and channels. Open a
system to see its zones, then open a zone to see its channels. The disclosure
arrows show which branches can be opened or closed. Search checks the complete
hierarchy as well as the other Studio sections.

Most editors use the same layout:

- The center table shows many records at once.
- The inspector on the right edits the selected record.
- Add, duplicate, delete, and reorder controls sit beside the table they affect.
- The status bar reports errors and warnings for the whole draft. Select it to
  open the validation drawer and see the section, field path, and explanation
  for each issue.

An error prevents saving. A warning calls attention to a usable but potentially
unsafe value, such as an unencrypted HTTP stream.

Undo and redo cover draft edits. Closing a changed draft asks before discarding
it.

## Systems

The FNE Systems page covers the connection name, identity, address, port, peer
ID, console RID, password, transport encryption, transport mode, transport
preshared key, KMF preshared key, and RID alias path.

Passwords and preshared keys stay masked. Validation messages name the field but
never include its value.

![FNE system inspector](../../Assets/configuration-studio-system.png)

The FNE transport is a compatibility protocol. Use it on a trusted network or
an authenticated VPN. Enabling transport encryption requires a transport
preshared key. The KMF preshared key has a separate purpose and is not reused as
the transport key.

## Zones and channels

Open an FNE system in the left hierarchy and select one of its zones. Each zone
is assigned to one FNE system. The zone inspector shows that assignment and
lets you change it. Studio then writes the selected system into every channel's
existing YAML `system` field. There is no separate system selector for each
channel.

Channel fields include destination ID, mode, algorithm, key ID, selectable
encryption, receive-only state, resource color, and card size. DMR channels
also include a slot. The valid card sizes are `small`, `normal`, and `large`.
The channel list has its own scrollbar, so the layout drawer never hides rows
that still need editing.

Select several channel rows to apply the current card size or change their
receive-only state together.

Select **Live zone layout** at the bottom of the channel table to open the
layout drawer. It uses the same card width, height, spacing, controls, colors,
and two-dimensional canvas as the main console. The card controls are disabled
in the drawer. Select a table row to find its card, then drag the card to its
new position. Studio stores those positions in operator settings when you
save. The running console keeps its current layout until you reload the saved
codeplug.

A compatible older codeplug may contain a zone whose channels name different
systems. Studio places that zone under **Unassigned or mixed**. Choose the
correct FNE in the zone inspector to make the assignment consistent before
saving.

The encryption algorithm list changes with the channel mode. It shows the
supported names instead of asking for a protocol number. Key IDs are shown in
hexadecimal. The `0x` prefix stays in the field, so you enter only the digits
that follow it.

![Zone editor and card preview](../../Assets/configuration-studio-zone.png)

The main operator workspace keeps its existing card layout and controls.
Configuration Studio is the place to edit definitions and prepare a layout.

## Web streams

The Web Streams page shows streams from every zone in one table. The owning zone
is still explicit. Moving a stream to another zone changes where its
`web_streams` entry is written.

Each stream has a name, URL, optional Basic Auth username and password, and idle
color. Use a direct HTTP or HTTPS audio URL. HTTPS is preferred. Stream
credentials are stored in the codeplug, so protect the file accordingly.

Web streams are local monitor widgets. They cannot be patch or multi-select
members.

## Review and save

Select **Review & Save** when the draft is ready. The review lists each file
that will change, including the codeplug, referenced key or alias files, and
operator settings.

If the draft has an error, **Review & Save** opens the validation drawer. Select
an issue to open the record that needs attention. Warnings remain visible but
do not prevent saving.

![Review and save](../../Assets/configuration-studio-review.png)

Studio performs these checks before replacing a file:

1. It captures the latest values from the open editors and validates the
   complete draft and its cross-references.
2. It rejects a plan if two outputs resolve to the same destination path.
3. It compares each current file with the source hash in the save plan
   immediately before the commit.
4. It stages and validates every changed file.
5. It creates restricted backups under the DVM Console application data folder.
6. It replaces the originals. If a later replacement fails, it restores files
   that were already replaced.

If another program changed a source file, Studio does not overwrite it. Save the
draft as a copy, or close and reopen Studio to load the external edit.

Saving an active codeplug does not change a running FNE session. After the save,
choose **Disconnect and reload** to use the new topology, or leave the session
alone and reload later.

If the save includes operator settings, Studio adopts the committed settings
snapshot immediately. A settings change made elsewhere while the review is open
is included in the final save instead of being overwritten by an older preview.

## Full and sanitized exports

Files & Interoperability has two export choices:

- A full interoperable copy includes credentials, operational addresses, stream
  URLs, identifiers, and references to local key material. Treat it as a
  secret.
- A sanitized support copy removes those values and is suitable for attaching
  to a troubleshooting report.

Review the sanitized copy before sharing it. Site-specific names may still be
meaningful even after credentials and identifiers are removed.

## YAML interoperability

Studio writes the same codeplug fields used by DVM Console's runtime loader. It
does not put group membership, direction, source order, or enabled state into
the codeplug. Older compatible DVM Console versions can therefore load files
saved by Studio.

Unmatched mapping fields, including legacy fields ignored by the current typed
model, are retained while their containing record remains in the draft. Studio
uses canonical formatting for edited sections. Comments and hand formatting in
those sections may change.

YAML anchors, aliases, custom tags, duplicate keys, and multi-document files
cannot be rewritten safely. Studio opens such files read-only. Maintain those
constructs by hand or save a compatible copy without them.

Legacy `patchGroups` entries still load. Studio writes group definitions under
`groups` when it rewrites that section.

## Manual YAML reference

A codeplug has `systems` and `zones` lists. Common optional fields are `groups`,
`keyFile`, and `patchSourceIdPassthrough`. Streams remain under their owning
zone as `web_streams`.

```yaml
keyFile: "./keys.clear"

systems:
  - name: "System 1"
    identity: "Console 1"
    address: "fne.example.local"
    port: 62031
    peerId: 1000001
    rid: "1001"
    password: "RPT_PASSWORD"
    encrypted: false
    transportEncryptionMode: "auto"
    aliasPath: "./alias.yml"

groups:
  - name: "Dispatch Patch"
    type: "patch"

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
        selectable_encryption: true
        resourceColor: "#150282"
        rx_only: false
        card_size: normal
    web_streams:
      - name: "Stream 1"
        url: "https://streams.example.local/stream-1"
        idleColor: "#150282"
```

Channel `mode` accepts `p25`, `dmr`, `nxdn`, or `analog`. Studio displays the
YAML value `p25` as **P25 Phase 1**. That value will continue to mean Phase 1
when Phase 2 support is added. P25 Phase 1 has no timeslots, so Studio hides the
slot field for those channels. DMR channels use whole-number `slot` values 1 or
2. NXDN destination IDs are limited to 16 bits. A receive-only channel cannot
be a patch destination or another transmit target.

`patchSourceIdPassthrough` controls the source ID used for forwarded patch
traffic. When it is false, forwarding uses the configured console RID for the
destination system. When true, DVM Console attempts to retain the inbound
source ID.
