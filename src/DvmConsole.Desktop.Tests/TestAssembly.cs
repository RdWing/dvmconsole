// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

// Several lifecycle tests deliberately block worker threads to model stalled
// devices. Run classes one at a time so independent stalls cannot consume each
// other's worker capacity. Concurrency within each test remains unchanged.
[assembly: CollectionBehavior(MaxParallelThreads = 1)]
