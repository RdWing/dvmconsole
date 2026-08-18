# Alert Tone Fidelity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make built-in Alert 1/2/3 match the documented timing and frequency patterns at a calibrated -25 dBFS peak without clicks or a louder toolbar-only path.

**Architecture:** Keep one calibrated generator as the only source of built-in alert PCM. Express the documented constants directly, verify the generated signal numerically, and remove call-site amplitude overrides so every UI path transmits identical samples.

**Tech Stack:** .NET 10, C#, 8 kHz mono PCM16, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-17-channel-history-stream-stability-design.md`

## Global Constraints

- Alert 1: 1004 Hz for 3 seconds.
- Alert 2: 1500 Hz then 800 Hz, 250 ms each, seven cycles.
- Alert 3: eight 250 ms 1004 Hz bursts with seven intervening 250 ms silences.
- Peak target: -25 dBFS; no UI path may override it.
- Segment boundaries must not introduce discontinuity clicks.
- Do not normalize, compress, or apply microphone AGC to generated alert PCM.

---

### Task 1: Lock the documented waveform contract in tests

**Files:**
- Modify: `src/DvmConsole.Audio.Tests/LegacyAlertToneGeneratorTests.cs`
- Reference: `dvmconsole/Docs/Getting Started/04-Operations/04-Alert Tones.md`

**Interfaces:**
- Consumes: `LegacyAlertToneGenerator.Generate(LegacyAlertTone)`.
- Verifies: sample count, frequency, peak dBFS, silence, and transition continuity.

- [ ] **Step 1: Replace the current 1000 Hz / 240 ms expectations**

Assert the documented lengths at 8 kHz:

```csharp
[Theory]
[InlineData(LegacyAlertTone.Alert1, 24_000)]
[InlineData(LegacyAlertTone.Alert2, 28_000)]
[InlineData(LegacyAlertTone.Alert3, 30_000)]
public void UsesDocumentedDuration(LegacyAlertTone tone, int expectedSamples)
    => Assert.Equal(expectedSamples, LegacyAlertToneGenerator.Generate(tone).Length);
```

Update frequency assertions to 1004 Hz for Alert 1/3 and retain 1500/800 Hz for Alert 2. Use 2,000-sample segments for 250 ms steps.

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

Expected: FAIL on 1000 Hz, 240 ms, and current Alert 2/3 sample counts.

- [ ] **Step 4: Commit the waveform contract tests**

```bash
git add src/DvmConsole.Audio.Tests/LegacyAlertToneGeneratorTests.cs
git commit -m "test: specify alert tone waveforms"
```

### Task 2: Correct the generator and remove the loud call-site override

**Files:**
- Modify: `src/DvmConsole.Audio/LegacyAlertToneGenerator.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:5690-5710`
- Modify: `src/DvmConsole.Audio.Tests/LegacyAlertToneGeneratorTests.cs`
- Test: `src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs`

**Interfaces:**
- Produces: a single default alert amplitude of `10^(-25/20)` (approximately `0.056234`).
- Consumes: generated PCM through `SendGeneratedToneAsync` without gain replacement.

- [ ] **Step 1: Implement the documented generator constants**

Set:

```csharp
public const double ToneFrequencyHz = 1004;
public const double TargetPeakDbfs = -25;
public static readonly double Amplitude = Math.Pow(10, TargetPeakDbfs / 20);
public static readonly TimeSpan StepDuration = TimeSpan.FromMilliseconds(250);
```

Keep the seven Alert 2 cycles and eight Alert 3 bursts. Because all listed frequencies complete an integer number of cycles in 250 ms, each generated tone step returns to a zero crossing. Preserve step-local phase or explicitly carry phase only if the boundary tests show a discontinuity.

- [ ] **Step 2: Remove the toolbar transmission-level override**

Change built-in alert transmission to:

```csharp
short[] samples = LegacyAlertToneGenerator.Generate(tone.Tone);
```

Delete the test that legitimizes `amplitude: 0.35`. Keep the public amplitude overload only if another verified caller needs deliberate test generation; no product call site may use it for built-in alerts.

- [ ] **Step 3: Add a desktop regression test for the exact generator path**

Extract or expose an internal pure helper used by `SendBuiltInAlertToneAsync` so the desktop test can assert it returns the default calibrated waveform. Do not make the test send FNE traffic merely to inspect sample amplitude.

- [ ] **Step 4: Run audio and desktop alert tests**

Run: `dotnet test src/DvmConsole.Audio.Tests/DvmConsole.Audio.Tests.csproj --no-restore --filter "FullyQualifiedName~LegacyAlertToneGeneratorTests|FullyQualifiedName~PcmToneGeneratorTests" /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~BuiltInAlert /m:1 /p:UseSharedCompilation=false`

Expected: PASS with -25 dBFS peak and exact documented lengths.

- [ ] **Step 5: Commit the calibrated alert path**

```bash
git add src/DvmConsole.Audio/LegacyAlertToneGenerator.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Audio.Tests/LegacyAlertToneGeneratorTests.cs src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs
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
