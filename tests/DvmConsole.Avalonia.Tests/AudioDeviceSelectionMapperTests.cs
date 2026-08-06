// SPDX-License-Identifier: AGPL-3.0-only
/**
* Contract gate for the pure audio-device selection mapper slice:
*
*   DvmConsole.Avalonia.ViewModels.AudioDeviceSelectionMapper
*
* AudioDeviceSelectionMapper is a pure static mapper with exactly one
* public method:
*
*   public static AudioDeviceOptionViewModel? FindById(
*       IReadOnlyList<AudioDeviceOptionViewModel> options,
*       AudioDeviceId? id)
*
* Locked contract:
*   - Null id returns null.
*   - AudioDeviceId.Default returns the first option whose Id.IsDefault;
*     for normal view-model lists this is the system-default row; when no
*     row carries the default marker the result is null.
*   - A non-default id matches options by Id.Value with
*     StringComparison.OrdinalIgnoreCase and returns the first matching
*     row — including an unavailable saved row; case-insensitive
*     duplicates keep the first match.
*   - No match returns null.
*   - Null options throws ArgumentNullException (programmer error).
*   - Pure and side-effect free: no catalog, UI, native, network, file or
*     persistence access, and the options list is never altered.
*
* This file is the executable contract for the mapper slice; it is fully
* headless — plain managed option rows only, no window, display, native
* call, or file.
*/
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Contract gate for <c>AudioDeviceSelectionMapper</c>.
    /// </summary>
    public sealed class AudioDeviceSelectionMapperTests
    {
        // ---- Fixtures ---------------------------------------------------------

        /// <summary>
        /// Builds a non-default option row.
        /// </summary>
        private static AudioDeviceOptionViewModel Row(string key, string name, bool isAvailable)
            => new(AudioDeviceId.FromKey(key), name, isAvailable);

        /// <summary>
        /// Builds the system-default option row.
        /// </summary>
        private static AudioDeviceOptionViewModel DefaultRow(string name, bool isAvailable = true)
            => new(AudioDeviceId.Default, name, isAvailable);

        // ---- A. Shape gate ------------------------------------------------------

        /// <summary>
        /// Shape gate for the mapper: public static class with exactly one
        /// public static method, <c>FindById</c>, taking
        /// (<see cref="IReadOnlyList{T}"/> of <see cref="AudioDeviceOptionViewModel"/>,
        /// <see cref="AudioDeviceId"/>?) and returning
        /// <see cref="AudioDeviceOptionViewModel"/>? — and nothing else
        /// public: no catalog parameter, no state, no extras. The exact
        /// signature is what makes the slice pure: the mapper cannot reach
        /// a catalog, UI, native, or persistence surface.
        /// </summary>
        [Fact]
        public void Mapper_Shape_StaticClass_SinglePublicStaticMethod_ExactSignature()
        {
            var mapper = typeof(AudioDeviceSelectionMapper);

            Assert.True(mapper.IsAbstract);
            Assert.True(mapper.IsSealed);

            var members = mapper.GetMembers(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.Single(members);

            var find = Assert.IsAssignableFrom<MethodInfo>(members[0]);
            Assert.Equal("FindById", find.Name);
            Assert.False(find.IsGenericMethod);
            Assert.True(find.IsStatic);
            Assert.Equal(typeof(AudioDeviceOptionViewModel), find.ReturnType);

            var parameters = find.GetParameters();
            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(IReadOnlyList<AudioDeviceOptionViewModel>), parameters[0].ParameterType);
            Assert.Equal(typeof(AudioDeviceId?), parameters[1].ParameterType);
        }

        // ---- B. Default id -------------------------------------------------------

        /// <summary>
        /// Null id returns null, whatever the options hold.
        /// </summary>
        [Fact]
        public void FindById_NullId_ReturnsNull()
        {
            var options = new[]
            {
                DefaultRow("System Default Input"),
                Row("usb-mic", "USB Mic", true),
            };

            Assert.Null(AudioDeviceSelectionMapper.FindById(options, null));
        }

        /// <summary>
        /// AudioDeviceId.Default resolves to the system-default row of a
        /// normal view-model list (default row first).
        /// </summary>
        [Fact]
        public void FindById_DefaultId_NormalList_ReturnsSystemDefaultRow()
        {
            var options = new[]
            {
                DefaultRow("System Default Input"),
                Row("usb-mic", "USB Mic", true),
            };

            var result = AudioDeviceSelectionMapper.FindById(options, AudioDeviceId.Default);

            Assert.Same(options[0], result);
            Assert.Equal(AudioDeviceId.Default, result!.Id);
            Assert.Equal("System Default Input", result.Name);
            Assert.True(result.IsAvailable);
        }

        /// <summary>
        /// The default match is by the <see cref="AudioDeviceId.IsDefault"/>
        /// marker, not by position: a default-marked row anywhere in the
        /// list is returned, and the first such row wins.
        /// </summary>
        [Fact]
        public void FindById_DefaultId_DefaultRowNotFirst_MatchesByIsDefaultMarker()
        {
            var options = new[]
            {
                Row("usb-mic", "USB Mic", true),
                DefaultRow("System Default Input"),
                DefaultRow("Second Default Row", false),
            };

            var result = AudioDeviceSelectionMapper.FindById(options, AudioDeviceId.Default);

            Assert.Same(options[1], result);
        }

        /// <summary>
        /// No row carrying the default marker yields null, including an
        /// empty list.
        /// </summary>
        [Fact]
        public void FindById_DefaultId_NoDefaultRow_ReturnsNull()
        {
            var options = new[]
            {
                Row("usb-mic", "USB Mic", true),
                Row("bluetooth", "BT Headset", true),
            };

            Assert.Null(AudioDeviceSelectionMapper.FindById(options, AudioDeviceId.Default));
            Assert.Null(AudioDeviceSelectionMapper.FindById(Array.Empty<AudioDeviceOptionViewModel>(), AudioDeviceId.Default));
        }

        // ---- C. Non-default id ----------------------------------------------------

        /// <summary>
        /// A non-default id matches an existing row by
        /// <see cref="AudioDeviceId.Value"/> case-insensitively.
        /// </summary>
        [Fact]
        public void FindById_ExistingId_MatchCaseInsensitive_ReturnsRow()
        {
            var options = new[]
            {
                DefaultRow("System Default Input"),
                Row("usb-mic", "USB Mic", true),
            };

            var result = AudioDeviceSelectionMapper.FindById(options, AudioDeviceId.FromKey("USB-MIC"));

            Assert.Same(options[1], result);
            Assert.Equal(AudioDeviceId.FromKey("usb-mic"), result!.Id);
            Assert.Equal("USB Mic", result.Name);
        }

        /// <summary>
        /// An unavailable saved row matches like any other row.
        /// </summary>
        [Fact]
        public void FindById_UnavailableSavedRow_MatchReturnsUnavailableRow()
        {
            var options = new[]
            {
                DefaultRow("System Default Input"),
                Row("usb-mic", "USB Mic", true),
                Row("gone-input", "Saved input device unavailable; using system default until it returns", false),
            };

            var result = AudioDeviceSelectionMapper.FindById(options, AudioDeviceId.FromKey("gone-input"));

            Assert.Same(options[2], result);
            Assert.False(result!.IsAvailable);
            Assert.Equal(AudioDeviceId.FromKey("gone-input"), result.Id);
        }

        /// <summary>
        /// Case-insensitive duplicates keep the first match.
        /// </summary>
        [Fact]
        public void FindById_DuplicateCaseInsensitiveIds_ReturnsFirstMatch()
        {
            var options = new[]
            {
                Row("USB-MIC", "USB Mic Upper", true),
                Row("usb-mic", "USB Mic Lower", true),
            };

            var result = AudioDeviceSelectionMapper.FindById(options, AudioDeviceId.FromKey("usb-mic"));

            Assert.Same(options[0], result);
        }

        /// <summary>
        /// An id present in no row yields null.
        /// </summary>
        [Fact]
        public void FindById_NotFound_ReturnsNull()
        {
            var options = new[]
            {
                DefaultRow("System Default Input"),
                Row("usb-mic", "USB Mic", true),
            };

            Assert.Null(AudioDeviceSelectionMapper.FindById(options, AudioDeviceId.FromKey("absent")));
        }

        // ---- D. Programmer error and purity -----------------------------------------

        /// <summary>
        /// Null options is a programmer error and is rejected, for both
        /// the default and a non-default id.
        /// </summary>
        [Fact]
        public void FindById_NullOptions_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(
                () => AudioDeviceSelectionMapper.FindById(null!, AudioDeviceId.Default));
            Assert.Throws<ArgumentNullException>(
                () => AudioDeviceSelectionMapper.FindById(null!, AudioDeviceId.FromKey("usb-mic")));
        }

        /// <summary>
        /// The mapper never alters the options list: same instances, same
        /// count, same projected values, after default, case-insensitive
        /// and not-found lookups.
        /// </summary>
        [Fact]
        public void FindById_DoesNotMutateOptions()
        {
            var options = new List<AudioDeviceOptionViewModel>
            {
                DefaultRow("System Default Input"),
                Row("usb-mic", "USB Mic", true),
                Row("gone-input", "Saved gone input", false),
            };
            var beforeRows = options.ToArray();
            var before = beforeRows.Select(r => (r.Id, r.Name, r.IsAvailable)).ToArray();

            _ = AudioDeviceSelectionMapper.FindById(options, AudioDeviceId.Default);
            _ = AudioDeviceSelectionMapper.FindById(options, AudioDeviceId.FromKey("USB-MIC"));
            _ = AudioDeviceSelectionMapper.FindById(options, AudioDeviceId.FromKey("absent"));

            Assert.Equal(beforeRows.Length, options.Count);
            for (var i = 0; i < beforeRows.Length; i++)
            {
                Assert.Same(beforeRows[i], options[i]);
                Assert.Equal(before[i], (options[i].Id, options[i].Name, options[i].IsAvailable));
            }
        }
    }
}
