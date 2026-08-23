# Groups and Patching

Patch groups and multi-select groups are managed from Console Settings.

Open it from:

```
View > Groups
```

Groups are defined in the codeplug. Members are assigned in the app by the operator.

---

# Patch Groups

A patch group forwards audio between member resources.

Use a patch group when traffic received on one member should be repeated to other members in the group.

The console applies the source system's adaptive receive jitter buffer once, decodes the source to 8 kHz PCM, and re-encodes that PCM for each destination protocol. This supports cross-protocol patches. It does not pass source vocoder codewords directly to destinations.

Patch groups have two separate pieces of state:

- membership
- active/on-off state

Membership can exist while the patch is disabled.

---

# Patch Enable/Disable

Each patch group has an **Enabled** control on the Groups page.

When a patch is disabled:

- members stay assigned
- patch forwarding is inactive
- any active destination transmission is ended
- delayed or stream-ID-rewritten FNE echoes are briefly excluded from other patch routes during teardown
- channel cards can still show that the resource belongs to a patch

When a patch is enabled:

- patch forwarding can occur between members
- member card indicators show the active patch icon

Patch members are always sticky across restart.

Patch active state only restores on startup when:

```
Settings > Retain Patch State on Startup
```

is enabled.

If that setting is off, patches start disabled after restart even though members remain assigned.

---

# Two-Way Patch

When **Enable One-Way Patch** is off:

- Patch Mode: Two-Way
- All members can transmit and receive.

In this mode, any listed member can become the active patch source. Audio received from the active source can be forwarded to the other members.

---

# One-Way Patch

When **Enable One-Way Patch** is on:

- Patch Mode: One-Way
- Choose the source explicitly from the selected members.
- All other selected members are destinations and receive audio.
- An RX-only channel can be the source, but every destination must be transmit-capable.

The source is saved first internally for compatibility with existing configurations. When an older one-way patch is loaded, its first saved member is selected as the source.

```
Member 1 = Source
Members 2+ = Destinations
```

Change the **Source** selector and save the group to route the patch in the other direction.

---

# Multi-Select Groups

A multi-select group is an operator transmit tool.

It lets the console transmit to multiple member resources at the same time using **Multi-Select PTT**.

Unlike a patch group, a multi-select group does not forward received audio between members.

Use multi-select when an operator wants to key several resources together from the console.

---

# Editing Members

To edit a group:

1. Open **View > Groups**.
2. Find the required patch or multi-select group.
3. Expand **Edit members** and check each channel that should be a member.
4. For a patch, select **Enabled** and **One-way** as required.
5. For a one-way patch, choose the source. The other selected members are destinations.
6. Select **Save group**.

The console displays a conflict warning when a channel assignment cannot be used safely. Resolve the listed conflict before relying on that group.

---

# Group PTT

Multi-select groups provide a group PTT button. Patch groups forward received traffic when enabled and do not use a separate operator patch PTT control on this page.

The console includes a short transmit tail after de-key so final audio frames are not clipped before call end signaling is sent.

---

# Card Icons

Resource cards show an operator-visible indicator for patch or multi-select membership.

If a resource belongs to both a patch and a multi-select group, the multi-select indicator takes priority in the card indicator area.

---

# Persistence Summary

| Item | Persists by default | Notes |
| --- | --- | --- |
| Patch members | Yes | Always sticky |
| Patch enabled state | No | Only sticky when Retain Patch State on Startup is enabled |
| Multi-select members | Yes | Managed from the Groups window |
| Edit mode | No | Clears when editing stops or the Groups window closes |

---

# Operator Tips

- Use patch groups for cross-resource receive forwarding.
- Use multi-select groups for console-originated group transmit.
- Verify the source and destination summary before enabling a one-way patch.
- Disable a patch instead of removing members when you want to keep the setup for later.
