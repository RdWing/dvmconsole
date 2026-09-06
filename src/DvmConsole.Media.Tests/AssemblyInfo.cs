// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

// fnecore's legacy DMR codecs use process-wide mutable state and are not safe
// to exercise concurrently from separate xUnit test classes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
