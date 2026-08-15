# Live FNE testing

Live testing is intentionally separate from the normal desktop and unit-test
paths. The local testing codeplug may contain private endpoints and credentials,
so it is ignored by Git and should never be copied into a commit or log.

Build and run the explicit probe with a bounded duration:

```sh
dotnet build src/DvmConsole.FneProbe/DvmConsole.FneProbe.csproj \
  --no-restore /p:UseSharedCompilation=false

dotnet run --project src/DvmConsole.FneProbe/DvmConsole.FneProbe.csproj -- \
  /Users/jchang/Documents/codex_projects/dvmconsole/configs/codeplug_testing.yml 10
```

The probe prints system names and connection states only. It does not print
passwords, preshared keys, aliases, or raw packets. A zero exit code means at
least one configured connection reached `Connected`; a nonzero exit code means
the probe completed without a connected FNE or the configuration was invalid.
