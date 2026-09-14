## Problem and outcome

Explain the problem and what changes for the operator or contributor. Include a
before/after example when helpful, and link the related issue if there is one.

Closes #

## Change type

- [ ] Bug fix
- [ ] Feature or workflow improvement
- [ ] Performance or reliability improvement
- [ ] Behavior-preserving refactor
- [ ] Documentation, community, or release engineering
- [ ] Other

## Compatibility and scope

Describe any compatibility changes: settings paths or schema, codeplugs, FNE
framing, native ABI, package contents or names, executable or bundle identity,
application-data paths, public constructors, XAML bindings, or operator workflows.

- [ ] Existing functionality and information density are preserved, or the
      intentional change and migration are documented.
- [ ] `fnecore` remains at the intended pinned revision, or its update is
      explicitly included and reviewed.
- [ ] User documentation and examples are updated when required.
- [ ] The change does not present DVM Console NEO as suitable for public- or
      life-safety operation.

## Evidence

List the commands you ran, test counts, package targets, environments, and
results. Mark checks you did not perform **not run**. Report each kind of testing
separately: a passing build or test suite does not establish live radio or device
behavior.

| Check | Result and evidence |
| --- | --- |
| Source review | |
| Automated validation | |
| Package smoke: macOS arm64 | Not run |
| Package smoke: macOS x64 | Not run |
| Package smoke: Windows x64 | Not run |
| Package smoke: Windows ARM64 | Not run |
| Package smoke: Linux x64 | Not run |
| Package smoke: Linux ARM64 | Not run |
| iPhone simulator | Not run |
| iPad simulator | Not run |
| Physical iPhone | Not run |
| Physical iPad | Not run |
| Live FNE integration | Not run |
| Hardware exercise | Not run |
| Community validation | Not run |

## Visual changes

For visible changes, attach before/after images at the affected screen sizes and
themes. Use fictional or fully anonymized data. Describe your checks for touch or
keyboard interaction, scaling, contrast, and screen-reader access.

## Privacy and publication review

- [ ] I reviewed the diff and attachments for credentials, keys, operational
      codeplugs, aliases, recordings, crash logs, packet captures, diagnostics,
      local measurements, and other private artifacts.
- [ ] I listed only evidence actually collected and stated material limitations.
- [ ] I have the right to submit this contribution under AGPL-3.0-only.
