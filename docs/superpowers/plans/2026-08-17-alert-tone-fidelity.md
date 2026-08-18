# Alert Tone Fidelity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep built-in Alert 1/2/3 aligned to the established vocoder timing windows, use 1 kHz for Alert 1/3, and transmit them at a calibrated -25 dBFS peak without clicks or a louder toolbar-only path.

**Architecture:** Keep one calibrated generator as the only source of built-in alert PCM. Express the documented constants directly, verify the generated signal numerically, and remove call-site amplitude overrides so every UI path transmits identical samples.

**Tech Stack:** .NET 10, C#, 8 kHz mono PCM16, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-17-channel-history-stream-stability-design.md`

## Global Constraints

- Alert 1: 1000 Hz for 3 seconds.
- Alert 2: 1500 Hz then 800 Hz, 240 ms each, seven cycles.
- Alert 3: eight 240 ms 1000 Hz bursts with seven intervening 240 ms silences.
- Each Alert 2/3 segment is twelve 20 ms vocoder frames and ends on a whole tone cycle.
- Peak target: -25 dBFS; no UI path may override it.
- Segment boundaries must not introduce discontinuity clicks.
- Do not normalize, compress, or apply microphone AGC to generated alert PCM.

---

### Task 1: Lock the vocoder-aligned waveform contract in tests

**Files:**
- Modify: `src/DvmConsole.Audio.Tests/LegacyAlertToneGeneratorTests.cs`
- Reference: `dvmconsole/Docs/Getting Started/04-Operations/04-Alert Tones.md`

**Interfaces:**
- Consumes: `LegacyAlertToneGenerator.Generate(LegacyAlertTone)`.
- Verifies: sample count, frequency, peak dBFS, silence, and transition continuity.

- [ ] **Step 1: Make the 1000 Hz / 240 ms contract explicit**

Assert the established vocoder-aligned lengths at 8 kHz:

```csharp
[Theory]
[InlineData(LegacyAlertTone.Alert1, 24_000)]
[InlineData(LegacyAlertTone.Alert2, 26_880)]
[InlineData(LegacyAlertTone.Alert3, 28_800)]
public void UsesDocumentedDuration(LegacyAlertTone tone, int expectedSamples)
    => Assert.Equal(expectedSamples, LegacyAlertToneGenerator.Generate(tone).Length);
```

Assert 1000 Hz for Alert 1/3 and retain 1500/800 Hz for Alert 2. Use 1,920-sample segments for 240 ms steps and assert each segment is divisible by the 160 samples in a 20 ms vocoder frame.

- [ ] **Step 2: Add calibrated-level and boundary tests**

Calculate peak dBFS from the generated PCM:

```csharp
double peak = samples.Max(sample => Math.Abs((double)sample)) / short.MaxValue;
double peakDbfs = 20 * Math.Log10(peak);
Assert.InRange(peakDbfs, -25.1, -24.9);
```

For every step boundary, assert the sample immediately before and at the boundary remain within a small delta consistent with a zero crossing. Assert Alert 3 silence segments contain only zero. Keep frequency estimation independent of amplitude.

- [ ] **Step 3: Run the focused tests and verify the current constants fail**

Run: `dotnet test src/DvmConsole.Audio.Tests/DvmConsole.Audio.Tests.csproj --no-restore --filter FullyQualifiedName~LegacyAlertToneGeneratorTests /m:1 /p:UseSharedCompilation=false`

Expected: existing frequency and alignment assertions PASS as characterization coverage; the new desktop transmission-path test in Task 2 provides the required RED regression for the level defect.

- [ ] **Step 4: Commit the waveform contract tests**

```bash
git add src/DvmConsole.Audio.Tests/LegacyAlertToneGeneratorTests.cs
git commit -m "test: specify alert tone waveforms"
```

### Task 2: Preserve the generator and remove the loud call-site override

**Files:**
- Modify: `src/DvmConsole.Audio/LegacyAlertToneGenerator.cs`
- Modify: `src/DvmConsole.Desktop/AlertToneViewModel.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:5690-5710`
- Modify: `src/DvmConsole.Audio.Tests/LegacyAlertToneGeneratorTests.cs`
- Create: `src/DvmConsole.Desktop.Tests/BuiltInAlertToneViewModelTests.cs`

**Interfaces:**
- Produces: a single default alert amplitude of `10^(-25/20)` (approximately `0.056234`).
- Consumes: generated PCM through `SendGeneratedToneAsync` without gain replacement.

- [ ] **Step 1: Add a desktop regression test for the exact generator path**

Write `BuiltInAlertToneViewModelTests` against a wished-for `GenerateSamples()` method and assert Alert 1 yields 24,000 samples at -25 dBFS, while Alert 3 yields 28,800 vocoder-aligned samples at -25 dBFS. Run it and verify RED because the method does not exist. Do not make the test send FNE traffic merely to inspect sample amplitude.

- [ ] **Step 2: Keep the vocoder-aligned generator constants and expose its calibrated output**

Retain the vocoder-aligned frequency and timing constants:

```csharp
public const double ToneFrequencyHz = 1000;
public const double TargetPeakDbfs = -25;
public static readonly double Amplitude = Math.Pow(10, TargetPeakDbfs / 20);
public static readonly TimeSpan StepDuration = TimeSpan.FromMilliseconds(240);
```

Keep the seven Alert 2 cycles and eight Alert 3 bursts. All listed frequencies complete an integer number of cycles in 240 ms, and every segment spans exactly twelve 20 ms vocoder frames, so each generated tone step returns to a zero crossing without splitting the intended timing window.

- [ ] **Step 3: Remove the toolbar transmission-level override**

Implement `BuiltInAlertToneViewModel.GenerateSamples()` as the single default-calibrated generator call, and change built-in alert transmission to:

```csharp
short[] samples = tone.GenerateSamples();
```

Delete the test that legitimizes `amplitude: 0.35`. Keep the public amplitude overload only if another verified caller needs deliberate test generation; no product call site may use it for built-in alerts.

- [ ] **Step 4: Run audio and desktop alert tests**

Run: `dotnet test src/DvmConsole.Audio.Tests/DvmConsole.Audio.Tests.csproj --no-restore --filter "FullyQualifiedName~LegacyAlertToneGeneratorTests|FullyQualifiedName~PcmToneGeneratorTests" /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~BuiltInAlert /m:1 /p:UseSharedCompilation=false`

Expected: PASS with -25 dBFS peak and exact documented lengths.

- [ ] **Step 5: Commit the calibrated alert path**

```bash
git add src/DvmConsole.Audio/LegacyAlertToneGenerator.cs src/DvmConsole.Desktop/AlertToneViewModel.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Audio.Tests/LegacyAlertToneGeneratorTests.cs src/DvmConsole.Desktop.Tests/BuiltInAlertToneViewModelTests.cs
git commit -m "fix: calibrate built-in alert tones"
```

### Task 3: Validate encoded operator output

**Files:**
- No additional production files.

**Interfaces:**
- Consumes the corrected generated-tone transmit path for analog and supported vocoder modes.

- [ ] **Step 1: Run complete audio and media suites**

Run: `dotnet test src/DvmConsole.Audio.Tests/DvmConsole.Audio.Tests.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Media.Tests/DvmConsole.Media.Tests.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 2: Run desktop tests and build**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 3: Perform operator listening validation**

On a controlled test channel, compare Alert 1/2/3 with the documented pattern and the original bundled WAV references. Confirm the received/transmitted signal is no louder than the -25 dBFS generated peak before codec loss, the alternation/pulses sound clean, and toolbar and Console Settings paths produce the same result. Do not test on an operational channel.

- [ ] **Step 4: Commit any test-only corrections**

```bash
git add src/DvmConsole.Audio.Tests src/DvmConsole.Desktop.Tests
git commit -m "test: validate alert tone fidelity"
```
