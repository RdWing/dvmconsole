# Configuration Studio and codeplugs

Configuration Studio edits a copy kept in the Configuration Library. Import
and export use YAML, but edits do not change the original file. DVM Console
copies the YAML and approved companion files into the library, gives the
configuration a stable ID, and saves each revision without changing earlier
ones. Your edits stay in a draft until you review and save them.

Open the current codeplug from:

```
File > Configuration Studio
```

To create a configuration from scratch, choose **File > New Configuration**.
To import an existing YAML codeplug, choose **File > Import Codeplug**.
Studio opens in a separate window, so you can use the main console
while editing. Opening a Studio section again brings the existing window
forward.

**File > Open Recent** lists recently opened managed revisions, not external
YAML paths. Use it to return to a configuration that is already in the library.

Use **File > Configuration Library** to activate, duplicate, remove, or restore
managed configurations. Only saved revisions can be loaded. Removing an
inactive configuration moves it to recoverable library trash; it never deletes
an imported source file.

![Configuration Studio shell](../../Assets/configuration-studio-shell.png)

## Creating your first configuration

No preexisting YAML, alias file, or key file is required.

1. Choose **File > New Configuration**, then **FNE Systems > Add FNE**.
2. Enter the FNE name, address, port, peer ID, console RID, and credentials
   supplied for your system. A starter zone is created with the FNE.
3. Select **Add channel**. Name the zone and channel, enter the destination ID,
   and choose P25, DMR, or NXDN. For DMR, set the appropriate slot.
4. For encrypted operation, open **Encryption Keys**, select **Add**, and set
   the owning FNE, protocol, algorithm, key ID, and material. Use the matching
   algorithm and key ID on the channel. Studio creates the managed key file.
5. Under **Files & Interoperability**, select the FNE that owns your RID aliases
   and choose **Add**. Enter a radio ID and its display name. Studio creates
   the alias companion when needed; repeat for other radios or FNEs.
6. Under **Web Streams**, choose **Add**, enter a direct audio URL, and select
   its owning zone. Streams provide local listening, not radio transmission.
7. Add any patch or multi-select definitions under **Groups**. Operational
   membership becomes available after this configuration is saved and loaded.
8. Select **Review & Save**, resolve any errors, and confirm the review. Accept
   **Disconnect and load** to activate the new configuration. Saving alone
   leaves it in the library without replacing the running configuration.
9. Choose the input/output devices in **Audio settings**. Check the talk-permit
   tone, then connect the FNE when ready. Enable TAR for the channels you want
   recorded and check its destination on the Recorder page.

To add more zones, select the intended FNE or one of its zones, open the zone
editor's edit menu, and choose **Add zone**. The new zone uses the selected
FNE; check **Assigned FNE system** before adding its channels.

## Importing an existing codeplug

Version 0.7.0 uses a separate NEO application-data directory and never opens the
ambiguous WPF-era directory as live storage. When the new store is empty, the
first-launch assistant can copy selected settings, profiles, managed
configurations, companions, and referenced assets into NEO. Nothing is selected
by default, and recordings, logs, unknown files, and the old directory are left
unchanged. You can also use **File > Import Codeplug** at any time. A command-line
YAML path is imported and activated.

DVM Console compares the source location, YAML content, companion references,
and companion file hashes to recognize an import. Reopening an unchanged
source reuses its library entry. If only the source changed, importing it adds
a revision to that entry. If both the source and library copy changed, DVM
Console asks whether to import as new, replace with a recoverable revision,
or cancel. It does not merge YAML.

Safe same-folder key and alias references are copied automatically. Absolute or
out-of-tree companions require explicit approval or selection. Missing-file
warnings remain visible, and a reimport never silently substitutes a stale
managed companion.

Legacy card positions and other safely attributable operator state move to the
managed configuration ID during import. Ambiguous state is assigned only to the
previously active codeplug, and security-sensitive web-stream authorization is
never guessed.

## Finding and editing configuration

The left side groups each zone and channel directly beneath its FNE system;
there is no duplicate top-level Zones & Channels item. Open a system to see its
zones, then open a zone to see its channels. The disclosure arrows show which
branches can be opened or closed, including the currently selected branch.
The complete navigation rail scrolls when the pointer is over either the tree
or the surrounding section links. Search checks the complete hierarchy as well
as the other Studio sections.

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
ID, console RID, call-priority policy, password, transport encryption,
transport mode, transport preshared key, and KMF preshared key. Adding an FNE
also creates and selects an empty named zone for it. The new system name appears
in the hierarchy as it is edited. Select **Add channel** to create the first
channel without saving or reopening Studio. RID alias ownership and import are
managed under **Files & Interoperability**.

Passwords and preshared keys stay masked. Validation messages name the field but
never include its value. Port and Peer ID are plain numeric text fields without
increment/decrement spinner buttons.

![FNE system inspector](../../Assets/configuration-studio-system.png)

The FNE transport is a compatibility protocol. Use it on a trusted network or
an authenticated VPN. Enabling transport encryption requires a transport
preshared key. The KMF preshared key has a separate purpose and is not reused as
the transport key.

## Zones and channels

Open an FNE system in the left hierarchy and select one of its zones. Each zone
is assigned to one FNE system. The zone inspector shows that assignment and
lets you edit the zone name or change its system. Studio then writes the
selected system into every channel's existing YAML `system` field. There is no
separate system selector for each channel.

Channel fields include destination ID, mode, algorithm, key ID, selectable
encryption, receive-only state, resource color, and card size. DMR channels
also include a slot. The valid card sizes are `small`, `normal`, and `large`.
The channel list has its own scrollbar, so the layout drawer never hides rows
that still need editing.

On desktop-sized Studio windows, edit the channel name, destination ID, mode,
DMR slot, encryption algorithm, receive-only state, and card size directly in
the table. Selecting or focusing an inline editor also selects that channel;
the table and right-side inspector stay synchronized and share the same
validation and Undo/Redo history. Use the inspector for key ID, selectable
encryption, and resource color. Desktop-sized windows keep the full grid and
use horizontal scrolling when its columns need more room. Only phone-width
viewports switch to a compact two-line channel summary; the selected channel's
fields remain available in the inspector below it.

The resource-color picker leaves each swatch visible. The selected swatch uses
an accent outline instead of replacing the chosen color with the accent color.

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

## Encryption keys and RID aliases

The first time you select **Add** under Encryption Keys, Studio creates a
managed `keys.clear` companion if the configuration does not already reference
one. Enter the key protocol, algorithm, hexadecimal key ID, and key material,
choose its owning FNE system, then select the channel and use the same
algorithm and key ID there.

Under **Files & Interoperability**, use **Browse…** to choose an existing key
file. For RID aliases, select the owning FNE and use **Choose file…** to open
the operating system's file picker. Studio parses the selection, copies its
contents into that FNE's current managed draft, and replaces only that FNE's
portable reference. The original file and other FNE alias lists are never
edited, and internal managed-runtime paths are not shown in the alias table.

If the selected FNE has no alias file, one **Add** creates its managed
`aliases.yml` companion and selects the first editable row. Enter the RID and
alias in the fields below the list; the selected row updates immediately.

If an imported or compatible configuration has an FNE system but no zone,
adding its first channel creates the required zone automatically. This keeps
the setup path continuous from FNE system to channel and encryption.

## Web streams

The Web Streams page shows streams from every zone in one table. **Add** creates
a starter zone if none exists, so you can begin entering a stream immediately.
A saved codeplug still requires at least one FNE system. The owning zone is
explicit. Moving a stream to another zone changes where its
`web_streams` entry is written.

Each stream has a name, URL, optional Basic Auth username and password, and idle
border color. Use a direct HTTP or HTTPS audio URL. HTTPS is preferred. Stream
credentials are stored in the codeplug, so protect the file accordingly.

Web streams are local monitor widgets. They cannot be patch or multi-select
members.

## Review and save

Select **Review & Save** when the draft is ready. The review lists the managed
YAML, referenced key or alias companions, and configuration-scoped operator
state that will be committed.

If the draft has an error, **Review & Save** opens the validation drawer. Select
an issue to open the record that needs attention. Warnings remain visible but
do not prevent saving.

![Review and save](../../Assets/configuration-studio-review.png)

Studio performs these checks before committing a revision:

1. It validates the complete draft and its cross-references.
2. It verifies the active Studio draft and managed companion set.
3. It writes a new immutable revision and atomically updates the catalog.
4. It leaves every imported source file unchanged.

For a new configuration, accept **Disconnect and load** after saving to use it
immediately, or open it later from the Configuration Library. Reopening the
console restores its last active managed configuration.

Saving the active configuration does not change the running FNE session. The
library marks the entry **Pending Reload**. Choose **Disconnect and reload** to
activate that committed revision, or leave the current session on its earlier
revision and reload later.

**Save a Copy** creates a new configuration ID. It copies non-trust
configuration state but does not copy import provenance or automatic web-stream
authorization.

## Full and sanitized exports

**Export YAML** and Files & Interoperability provide two export choices:

- A full interoperable copy includes credentials, operational addresses, stream
  URLs, identifiers, and references to local key material. Treat it as a
  secret.
- A sanitized support copy removes those values and is suitable for attaching
  to a troubleshooting report. Removed required identifiers are replaced with
  non-secret placeholders so the support copy remains valid YAML that DVM
  Console can import for diagnosis.

Exports go to the location chosen in the file picker. Companion files are
written beside the YAML with safe relative references, and DVM Console reads
the exported bundle back before reporting success. Export never changes the
current configuration ID, active revision, or which Studio edits count as unsaved.

Review the sanitized copy before sharing it. Site-specific names may still be
meaningful even after credentials and identifiers are removed.

## Moving a configuration to another computer

Export a full YAML copy and keep its companion files beside it. Import that
YAML on the destination computer, then load the managed configuration. The
YAML carries FNEs, zones, channels, stream definitions, and group definitions;
card positions and receive on/off selections belong to operator settings.

To transfer those selections and positions too, follow
[Import and export settings](../04-Operations/02-Settings%20Reference.md#import-and-export-settings).
Start web streams deliberately on the destination and check group membership,
PTT selections, audio devices, and the recording location before operating.

## YAML interoperability

Studio exports the same codeplug fields used by DVM Console's runtime loader.
It does not put group membership, direction, source order, or enabled state
into YAML. Older compatible DVM Console versions can therefore load a full
export produced by Studio.

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
