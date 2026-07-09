# Auto-Update: Remaining Known Limitations

> **Status: item 1 still open, item 2 implemented (see below).** Of the original 7-item
> "Known Limitations" list (README, under Auto-Update), five were previously fixed outright
> (startup version logging, `setup-service.ps1`'s `appsettings.json` pre-check, the
> dev-build/`0.0.0` guard, opt-in passwordless-sudo, and automatic rollback via
> `update.ps1`'s `Start-BotService`/`Test-BotIsRunning` health check). Item 2 (checksum
> verification) is now also fixed — see below. Item 1 remains open because it needs live
> infrastructure this session doesn't have.

## 1. Windows-Service self-stop-during-update race (needs live verification, not code)

**The open question**: when `UpdateCheckService` calls `_appLifetime.StopApplication()` while
running under `.UseWindowsService()`, does the Windows Service Control Manager (SCM) receive a
proper `SERVICE_STOPPED` status *before* the process exits? Microsoft's documented behavior says
yes — a clean, reported stop should not trigger `sc.exe failure`'s configured recovery actions
(`setup-service.ps1` sets `restart/60000/restart/60000/restart/60000`), since those only apply
when a service terminates *unexpectedly*. This is the intended, supported pattern for a
self-restarting Generic Host Windows Service. But it has never been exercised against a real
Windows Service in this project, so it remains an assumption, not a verified fact.

**Why it matters if the assumption is wrong**: Windows' failure-recovery could try to restart the
*old* (soon-to-be-replaced) exe concurrently with `update.ps1`'s file copy — a race that could
either block the copy (file lock conflict) or, worse, briefly run two instances of the bot at
once, double-posting alerts.

**How to actually verify this** (needs a real Windows machine/VM with Administrator access —
none of this is achievable through code review or a sandboxed test):

1. Publish a real build (`dotnet publish -p:Version=X.Y.Z`) and `setup-service.ps1
   -ServiceName nwsalertbot-test` to register it as a genuine Windows Service.
2. Set `Update.AutoApply: true` in `appsettings.Local.json` with a real newer tag available on
   GitHub Releases to update to.
3. Trigger the update (either wait for the real `CheckIntervalHours`, or temporarily point
   `GitHubRepo`/the check interval at something that fires sooner for testing purposes).
4. Watch **Event Viewer → Windows Logs → System** (Service Control Manager source) during the
   update to see whether the stop is logged as a clean/expected stop or an error/crash event.
5. Confirm no double-process or file-lock conflict occurs — e.g. watch `Get-Process NwsAlertBot`
   across the whole update window, and check `posted_alerts.txt`/logs for any sign of duplicate
   posting activity during the transition.
6. Repeat a few times — a race condition may not reproduce on every attempt.

**If the assumption turns out to be wrong**, options in rough order of preference:

- **Simplest**: increase the delay in `sc.exe failure`'s restart actions (currently 60000ms)
  comfortably past how long a typical download+swap takes, so Windows' own recovery never gets a
  chance to fire before `update.ps1` has already finished. A band-aid, not a real fix — a slow
  network could still exceed any fixed delay — but cheap and likely sufficient in practice.
- **More correct**: have `UpdateCheckService` shell out to `sc.exe failure $ServiceName reset= 0
  actions= ""` immediately before calling `StopApplication()`, clearing recovery actions for the
  duration of the update, and have `update.ps1` restore them (or just leave them cleared —
  `setup-service.ps1` could re-apply them on next run, or `update.ps1` could re-run the same
  `sc.exe failure` command after the swap completes).
- **Already partially mitigated**: the rollback health check added for item 3 (see README) will
  still catch a genuinely broken end state even if this race occurs — it's not a fix for the race
  itself, but it limits the blast radius of one going wrong.

If live testing confirms the assumption holds (most likely outcome, since this is a
Microsoft-documented supported pattern), the fix here is just to update the README/CLAUDE.md
"Known Limitations" language to reflect that it's now verified — no code change needed.

## 2. Checksum/signature verification on downloaded releases

> **Status: implemented.** `release.yml`'s `release` job now runs `sha256sum * > checksums.txt`
> over the downloaded artifacts before `gh release create`, so `checksums.txt` rides along with
> every release automatically. `update.ps1` downloads it right after downloading the release
> archive (unconditionally — including under `-DryRun`) and aborts before extracting anything if
> the entry for the current asset is missing or the hash doesn't match. README "Known
> Limitations" and Auto-Update sections, and `CLAUDE.md`'s Common Pitfalls entry, updated to
> reflect this and its honest scope (see below — this doesn't protect against a compromised
> `release.yml`/repo/token).

**What's missing**: `update.ps1` downloads a release archive over HTTPS with no verification that
the bytes it receives match what `release.yml` actually built — HTTPS protects against
network-level tampering in transit, but not against a corrupted upload, a compromised release
asset, or (in the worst case) a compromised repo/token pushing a malicious release.

**Implementation sketch**:

`release.yml`, in the `release` job (after downloading all artifacts, before `gh release
create`):
```yaml
- name: Compute checksums
  working-directory: ./artifacts
  run: sha256sum * > checksums.txt
```
`gh release create ./artifacts/*` already uploads everything in that directory, so
`checksums.txt` rides along automatically — no separate upload step needed.

`update.ps1`, after downloading `$archivePath` and before extracting it:
```powershell
$checksumUrl = "https://github.com/$Repo/releases/download/$Tag/checksums.txt"
$checksumFile = Join-Path $tempDir "checksums.txt"
Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumFile -UseBasicParsing

$expectedLine = Get-Content $checksumFile | Where-Object { $_ -match [regex]::Escape($assetName) }
if (-not $expectedLine) {
    Write-Error "No checksum entry found for $assetName in checksums.txt -- aborting for safety."
    exit 1
}
$expectedHash = ($expectedLine -split '\s+')[0]
$actualHash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) {
    Write-Error "Checksum mismatch for $assetName! Expected $expectedHash, got $actualHash. Aborting -- do not trust this download."
    exit 1
}
Write-Step "Checksum verified for $assetName."
```
`-DryRun` should perform this same check (it already validates the download/extraction pipeline
end-to-end; checksum verification is a natural extension of "confirm this release is good"
before it's ever trusted for a real install).

**Honest scope of what this does and doesn't protect against** — worth stating explicitly so a
future reader doesn't assume more security than this actually provides:
- **Protects against**: corrupted uploads, download/transfer corruption, simple tampering with
  the archive after it was built.
- **Does NOT protect against**: a genuinely compromised `release.yml`/repo/`GITHUB_TOKEN` — an
  attacker who can push a malicious release can just as easily update `checksums.txt` to match
  their malicious archive. Real supply-chain protection against *that* threat would need
  cryptographic signing (e.g., sign `checksums.txt` with a private key held outside the CI
  pipeline, verify against a public key baked into `update.ps1`) — a meaningfully bigger lift
  (key generation, secure storage, rotation story) that isn't obviously worth it for this
  project's actual threat model (a personal weather bot, not a high-value target). Plain
  checksum verification is proportionate; don't over-build this into full code signing without a
  concrete reason to.

**How to apply**: this is a self-contained, independently buildable change (no dependency on
item 1 above) — pick it up whenever the added download-time latency (one extra small HTTP
request) and moderate implementation effort are worth it.
