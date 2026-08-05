// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2024 Caleb, K4PHP
*   Copyright (C) 2025 Steven Jennison, KD8RHO
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/
#nullable enable
using System.Collections.Generic;

namespace dvmconsole
{
    /// <summary>
    /// Portable, UI-framework-agnostic selection-state manager ported from the
    /// WPF <c>dvmconsole.SelectedChannelsManager</c>. Tracks a set of selected
    /// channels plus an optional primary channel and raises change events and
    /// constructor-injected visual/log effect delegates in the exact order of
    /// the WPF original.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Membership is reference-identity based: the internal set uses
    /// <see cref="EqualityComparer{T}.Default"/>, so types that do not
    /// override <see cref="object.Equals(object)"/> (such as the WPF
    /// <c>ChannelBox</c>) are compared by reference, exactly like the WPF
    /// source.
    /// </para>
    /// <para>
    /// Instances are intended for UI-thread ownership only; this class is not
    /// thread-safe and performs no synchronization.
    /// </para>
    /// <para>
    /// Compatibility quirk preserved from the WPF source:
    /// <see cref="ClearSelections"/> leaves the primary channel untouched and
    /// raises no <see cref="PrimaryChannelChanged"/> event.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The channel type being tracked.</typeparam>
    public sealed class SelectedChannelsManager<T> where T : class
    {
        private T? primaryChannel;

        private readonly HashSet<T> selectedChannels;

        /// <summary>
        /// Gets the current primary channel, or null when none is set.
        /// </summary>
        public T? PrimaryChannel => primaryChannel;

        /// <summary>
        /// Initializes a new instance of the <see cref="SelectedChannelsManager{T}"/> class.
        /// </summary>
        /// <param name="selectionVisualChanged">
        /// Optional delegate invoked when a channel's selection visual state
        /// changes (WPF <c>ChannelBox.IsSelected</c> replacement). May be null.
        /// </param>
        /// <param name="primaryVisualChanged">
        /// Optional delegate invoked when a channel's primary visual state
        /// changes (WPF <c>ChannelBox.IsPrimary</c> replacement). May be null.
        /// </param>
        /// <param name="primaryChannelSet">
        /// Optional delegate invoked when a primary channel is set (WPF
        /// <c>Log.WriteLine</c> replacement). May be null.
        /// </param>
        public SelectedChannelsManager(
            Action<T, bool>? selectionVisualChanged = null,
            Action<T, bool>? primaryVisualChanged = null,
            Action<T>? primaryChannelSet = null)
        {
            SelectionVisualChanged = selectionVisualChanged;
            PrimaryVisualChanged = primaryVisualChanged;
            PrimaryChannelSet = primaryChannelSet;
            selectedChannels = new HashSet<T>(EqualityComparer<T>.Default);
        }

        /// <summary>
        /// Returns a fresh snapshot copy of the currently selected channels.
        /// The returned collection is detached from the manager: mutating it
        /// never affects internal state, and each call returns a distinct
        /// mutable <see cref="List{T}"/> instance.
        /// </summary>
        public IReadOnlyCollection<T> GetSelectedChannels() => new List<T>(selectedChannels);

        /*
        ** Events
        */

        /// <summary>
        /// Triggered when the selected-channel collection changes.
        /// </summary>
        public event Action? SelectedChannelsChanged;

        /// <summary>
        /// Triggered when primary channel is changed.
        /// </summary>
        public event Action? PrimaryChannelChanged;

        /// <summary>
        /// Triggered when a channel selection changes.
        /// </summary>
        public event Action<T, bool>? ChannelSelectionChanged;

        /*
        ** Methods
        */

        /// <summary>
        /// Adds the channel to the selection. A new member invokes the
        /// selection visual hook, then <see cref="ChannelSelectionChanged"/>,
        /// then <see cref="SelectedChannelsChanged"/>. Adding an
        /// already-selected member is a full no-op.
        /// </summary>
        /// <param name="channel">The channel to add.</param>
        /// <exception cref="System.ArgumentNullException">
        /// <paramref name="channel"/> is null.
        /// </exception>
        public void AddSelectedChannel(T channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            if (selectedChannels.Add(channel))
            {
                SelectionVisualChanged?.Invoke(channel, true);
                ChannelSelectionChanged?.Invoke(channel, true);
                SelectedChannelsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Removes the channel from the selection. When the removed member is
        /// also the primary channel, the primary is cleared first (raising
        /// <see cref="PrimaryChannelChanged"/> before any visual or selection
        /// events), then the primary visual hook, selection visual hook,
        /// <see cref="ChannelSelectionChanged"/> and
        /// <see cref="SelectedChannelsChanged"/> fire in that order. The
        /// primary visual hook fires unconditionally, even for members that
        /// were never primary. Removing a non-member is a full no-op.
        /// </summary>
        /// <param name="channel">The channel to remove.</param>
        /// <exception cref="System.ArgumentNullException">
        /// <paramref name="channel"/> is null.
        /// </exception>
        public void RemoveSelectedChannel(T channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            if (selectedChannels.Remove(channel))
            {
                if (EqualityComparer<T>.Default.Equals(primaryChannel, channel))
                {
                    ClearPrimaryChannel();
                }
                PrimaryVisualChanged?.Invoke(channel, false);
                SelectionVisualChanged?.Invoke(channel, false);
                ChannelSelectionChanged?.Invoke(channel, false);
                SelectedChannelsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Clears every selected channel. Each member gets the selection
        /// visual false hook and <see cref="ChannelSelectionChanged"/> false,
        /// followed by a single aggregate <see cref="SelectedChannelsChanged"/>.
        /// The primary channel is left untouched and no primary event is
        /// raised (compatibility quirk preserved from the WPF source).
        /// </summary>
        public void ClearSelections()
        {
            foreach (var channel in selectedChannels)
            {
                SelectionVisualChanged?.Invoke(channel, false);
                ChannelSelectionChanged?.Invoke(channel, false);
            }

            selectedChannels.Clear();
            SelectedChannelsChanged?.Invoke();
        }

        /// <summary>
        /// Sets the primary channel to the passed channel. Invokes the
        /// primary-channel-set hook (the WPF <c>Log.WriteLine</c> replacement)
        /// first, then assigns the channel and raises
        /// <see cref="PrimaryChannelChanged"/> unconditionally. The channel
        /// need not be selected.
        /// </summary>
        /// <param name="channel">The channel to set as primary.</param>
        /// <exception cref="System.ArgumentNullException">
        /// <paramref name="channel"/> is null.
        /// </exception>
        public void SetPrimaryChannel(T channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            PrimaryChannelSet?.Invoke(channel);
            primaryChannel = channel;
            PrimaryChannelChanged?.Invoke();
        }

        /// <summary>
        /// Clears the primary channel selection, setting it to null, and
        /// raises <see cref="PrimaryChannelChanged"/> unconditionally.
        /// </summary>
        public void ClearPrimaryChannel()
        {
            primaryChannel = null;
            PrimaryChannelChanged?.Invoke();
        }

        /*
        ** Effect delegates
        */

        private readonly Action<T, bool>? SelectionVisualChanged;
        private readonly Action<T, bool>? PrimaryVisualChanged;
        private readonly Action<T>? PrimaryChannelSet;
    } // public sealed class SelectedChannelsManager<T>
} // namespace dvmconsole
