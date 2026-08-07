// SPDX-License-Identifier: AGPL-3.0-only
// The zone/channel UI slice exposes its internal re-assignment entry
// point (ChannelSlotViewModel.Reassign) to the contract-gate test
// assembly only; the public surface of the slot view-model stays
// byte-identical (identity and assignment are get-only).
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DvmConsole.Avalonia.Tests")]
