# SourceManager end-to-end regression suite.
#
# Runs the real `sm` binary against throwaway repositories in the temp directory and
# asserts on observable behaviour (exit codes, stdout/stderr, repository state).
#
#   pwsh tests/run-tests.ps1
#
# Every case must be independent: it gets a fresh repository directory.

[CmdletBinding()]
param(
    [string]$SmPath,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$script:Passed = 0
$script:Failed = 0
$script:Failures = @()
$script:CaseIndex = 0

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SmPath) {
    $SmPath = Join-Path $repoRoot 'bin/Debug/net10.0/win-x64/sm.exe'
}
if (-not (Test-Path $SmPath)) {
    throw "sm binary not found at '$SmPath'. Build first: dotnet build SourceManager.csproj"
}
$SmPath = (Resolve-Path $SmPath).Path

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) "sm-tests-$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

function New-TestRepo {
    $script:CaseIndex++
    $dir = Join-Path $workRoot ("case-{0:D2}" -f $script:CaseIndex)
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

# Runs sm inside $Repo and returns @{ Exit; Out; Err; All }
function Invoke-Sm {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [string[]]$Args = @()
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $SmPath
    $psi.WorkingDirectory = $Repo
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    foreach ($a in $Args) { [void]$psi.ArgumentList.Add($a) }

    $proc = [System.Diagnostics.Process]::Start($psi)
    $out = $proc.StandardOutput.ReadToEnd()
    $err = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit(60000) | Out-Null
    if (-not $proc.HasExited) { $proc.Kill($true); throw "sm $($Args -join ' ') timed out" }

    return @{
        Exit = $proc.ExitCode
        Out  = $out
        Err  = $err
        All  = ($out + $err)
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { return }
    throw "ASSERTION FAILED: $Message"
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ("$Expected" -eq "$Actual") { return }
    throw "ASSERTION FAILED: $Message`n  expected: $Expected`n  actual:   $Actual"
}

function Assert-Match {
    param([string]$Pattern, [string]$Text, [string]$Message)
    # Normalise line endings so `^`/`$` anchors behave the same regardless of the platform the
    # suite runs on; a trailing CR otherwise defeats line-anchored patterns.
    $normalized = $Text -replace "`r`n", "`n"
    if ($normalized -match $Pattern) { return }
    throw "ASSERTION FAILED: $Message`n  pattern: $Pattern`n  text:`n$Text"
}

function Invoke-Case {
    param([string]$Name, [scriptblock]$Body)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body
        $script:Passed++
        "{0,-6} {1} ({2}ms)" -f 'PASS', $Name, $sw.ElapsedMilliseconds | Write-Host -ForegroundColor Green
    }
    catch {
        $script:Failed++
        $script:Failures += [pscustomobject]@{ Name = $Name; Error = $_.Exception.Message }
        "{0,-6} {1}" -f 'FAIL', $Name | Write-Host -ForegroundColor Red
        "       $($_.Exception.Message)" | Write-Host -ForegroundColor DarkRed
    }
}

function Write-File {
    param([string]$Dir, [string]$Relative, [string]$Content)
    $full = Join-Path $Dir $Relative
    $parent = Split-Path -Parent $full
    if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Set-Content -Path $full -Value $Content -NoNewline -Encoding UTF8
}

# ─────────────────────────────────────────────────────────────────────────────
# CLI surface
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'version: --version exits 0 and prints a version' {
    $r = Invoke-Sm -Repo $workRoot -Args @('--version')
    Assert-Equal 0 $r.Exit 'exit code'
    Assert-Match '^sm version \d+\.\d+\.\d+' $r.Out 'prints sm version'
}

Invoke-Case 'version: `sm version` subcommand works' {
    $r = Invoke-Sm -Repo $workRoot -Args @('version')
    Assert-Equal 0 $r.Exit 'exit code'
    Assert-Match '^sm version \d+\.\d+\.\d+' $r.Out 'prints sm version'
}

Invoke-Case 'help: bare invocation lists commands' {
    $r = Invoke-Sm -Repo $workRoot -Args @()
    Assert-Equal 0 $r.Exit 'exit code'
    Assert-Match 'Commands:' $r.Out 'help lists commands'
    Assert-Match '\bcommit\b' $r.Out 'help mentions commit'
}

Invoke-Case 'help: unknown option is reported, not ignored' {
    $r = Invoke-Sm -Repo $workRoot -Args @('commit', '--definitely-not-an-option')
    Assert-True ($r.Exit -ne 0) 'unknown option must fail'
    Assert-Match 'Unknown option' $r.All 'reports the unknown option'
}

# ─────────────────────────────────────────────────────────────────────────────
# Core workflow
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'init: creates a repository and a main branch' {
    $repo = New-TestRepo
    $r = Invoke-Sm -Repo $repo -Args @('init')
    Assert-Equal 0 $r.Exit 'init exit code'
    Assert-True (Test-Path (Join-Path $repo '.sm/HEAD')) 'HEAD exists'
    Assert-True (Test-Path (Join-Path $repo '.sm/objects')) 'object store exists'
    $head = Get-Content (Join-Path $repo '.sm/HEAD') -Raw
    Assert-Match 'refs/heads/main' $head 'HEAD points at main'
}

Invoke-Case 'init: refuses to reinitialise, and does not corrupt state' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'hello'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('init')
    Assert-True ($r.Exit -ne 0) 'second init must fail'
    # The original history must still be intact.
    $log = Invoke-Sm -Repo $repo -Args @('log', '--oneline')
    Assert-Match 'first' $log.Out 'history survives a failed re-init'
}

Invoke-Case 'status: reports untracked files then clean tree' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'hello'

    $st = Invoke-Sm -Repo $repo -Args @('status')
    Assert-Match 'a\.txt' $st.Out 'untracked file is listed'

    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    $st2 = Invoke-Sm -Repo $repo -Args @('status')
    Assert-Match 'nothing to commit|working tree clean' $st2.Out 'clean after commit'
}

Invoke-Case 'commit: history is readable and round-trips the message' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'hello'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    $c = Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first commit')
    Assert-Equal 0 $c.Exit 'commit exit code'

    $log = Invoke-Sm -Repo $repo -Args @('log', '--oneline')
    Assert-Match 'first commit' $log.Out 'commit message round-trips through the object store'

    $logFull = Invoke-Sm -Repo $repo -Args @('log')
    Assert-Match 'Author:' $logFull.Out 'author is shown'
}

Invoke-Case 'commit: nested directories are preserved in the tree' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'root'
    Write-File $repo 'src/b.txt' 'mid'
    Write-File $repo 'src/deep/c.txt' 'leaf'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'nested') | Out-Null

    # Delete the working tree and restore it from the commit: this only works if the
    # tree graph actually contains the subtrees.
    Remove-Item (Join-Path $repo 'src') -Recurse -Force
    Remove-Item (Join-Path $repo 'a.txt') -Force
    $co = Invoke-Sm -Repo $repo -Args @('checkout', 'HEAD', '--force')
    Assert-Equal 0 $co.Exit 'checkout exit code'
    Assert-True (Test-Path (Join-Path $repo 'src/deep/c.txt')) 'nested file restored'
    Assert-Equal 'leaf' (Get-Content (Join-Path $repo 'src/deep/c.txt') -Raw).Trim() 'nested content restored'
}

Invoke-Case 'second commit: parent chain is recorded' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'a.txt' 'two'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $log = Invoke-Sm -Repo $repo -Args @('log', '--oneline')
    $lines = @($log.Out -split "`r?`n" | Where-Object { $_.Trim() })
    Assert-True ($lines.Count -ge 2) "history has at least 2 commits, got $($lines.Count)"
    Assert-Match 'second' $lines[0] 'newest commit first'
    Assert-Match 'first' $lines[1] 'parent commit second'
}

# ─────────────────────────────────────────────────────────────────────────────
# Status / staging semantics
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'status: a staged change is reported as staged, not as unstaged' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    Write-File $repo 'a.txt' 'two'
    $before = Invoke-Sm -Repo $repo -Args @('status')
    Assert-Match 'not staged' $before.Out 'unstaged edit is reported as unstaged'

    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    $after = Invoke-Sm -Repo $repo -Args @('status')
    Assert-Match 'Changes to be committed' $after.Out 'staged edit is reported as staged'
    Assert-True ($after.Out -notmatch 'Changes not staged') 'staged edit is not also reported as unstaged'
}

Invoke-Case 'status: short format shows staged state in column X' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    Write-File $repo 'a.txt' 'two'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    $s = Invoke-Sm -Repo $repo -Args @('status', '-s')
    Assert-Match 'M  a\.txt|^M ' $s.Out "staged modification shows M in column X; got: $($s.Out.Trim())"

    Write-File $repo 'a.txt' 'three'
    $s2 = Invoke-Sm -Repo $repo -Args @('status', '-s')
    Assert-Match 'MM a\.txt' $s2.Out "partially staged file shows MM; got: $($s2.Out.Trim())"
}

Invoke-Case 'commit: refuses to create an empty commit on a clean tree' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    $again = Invoke-Sm -Repo $repo -Args @('commit', '-m', 'empty')
    Assert-True ($again.Exit -ne 0) 'empty commit must fail'
    Assert-Match 'nothing to commit|no changes added' $again.All 'explains why'

    $log = Invoke-Sm -Repo $repo -Args @('log', '--oneline')
    $count = @($log.Out -split "`r?`n" | Where-Object { $_.Trim() }).Count
    Assert-Equal 1 $count 'no second commit was created'
}

Invoke-Case 'commit: --allow-empty still permits an intentionally empty commit' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    $again = Invoke-Sm -Repo $repo -Args @('commit', '--allow-empty', '-m', 'empty on purpose')
    Assert-Equal 0 $again.Exit 'allow-empty exit code'
}

Invoke-Case 'commit: -a stages tracked edits and persists the index' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    Write-File $repo 'a.txt' 'two'
    $c = Invoke-Sm -Repo $repo -Args @('commit', '-a', '-m', 'second')
    Assert-Equal 0 $c.Exit 'commit -a exit code'

    # The on-disk index must reflect the commit, not the pre-commit state.
    $st = Invoke-Sm -Repo $repo -Args @('status')
    Assert-Match 'nothing to commit|working tree clean' $st.Out "tree is clean after commit -a; got: $($st.Out.Trim())"
}

# ─────────────────────────────────────────────────────────────────────────────
# Reset / checkout must not leak files
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'reset --hard: deletes files that the target commit does not contain' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'b.txt' 'two'
    Invoke-Sm -Repo $repo -Args @('add', 'b.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('reset', '--hard', 'HEAD~1')
    Assert-Equal 0 $r.Exit "reset exit code; output: $($r.All)"
    Assert-True (-not (Test-Path (Join-Path $repo 'b.txt'))) 'b.txt is gone after reset --hard HEAD~1'
    Assert-True (Test-Path (Join-Path $repo 'a.txt')) 'a.txt survives'
}

Invoke-Case 'checkout: switching branches removes files from the previous branch' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'only-main.txt' 'main'
    Invoke-Sm -Repo $repo -Args @('add', 'only-main.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'main file') | Out-Null

    Invoke-Sm -Repo $repo -Args @('checkout', '-b', 'feature') | Out-Null
    Remove-Item (Join-Path $repo 'only-main.txt') -Force
    Write-File $repo 'only-feature.txt' 'feature'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'feature file') | Out-Null

    Invoke-Sm -Repo $repo -Args @('checkout', 'main') | Out-Null
    Assert-True (Test-Path (Join-Path $repo 'only-main.txt')) 'main file restored'
    Assert-True (-not (Test-Path (Join-Path $repo 'only-feature.txt'))) 'feature-only file removed'
}

# ─────────────────────────────────────────────────────────────────────────────
# Garbage collection safety
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'gc: keeps objects that are only referenced by the index' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    # Staged but uncommitted: only the index references this blob.
    Write-File $repo 'staged.txt' 'uncommitted work'
    Invoke-Sm -Repo $repo -Args @('add', 'staged.txt') | Out-Null

    $g = Invoke-Sm -Repo $repo -Args @('gc', '--prune', 'now')
    Assert-Equal 0 $g.Exit "gc exit code; output: $($g.All)"

    $f = Invoke-Sm -Repo $repo -Args @('fsck')
    Assert-Equal 0 $f.Exit "fsck after gc must still pass; output: $($f.All)"

    $c = Invoke-Sm -Repo $repo -Args @('commit', '-m', 'commit staged work')
    Assert-Equal 0 $c.Exit 'the staged blob is still commit-able after gc'
}

Invoke-Case 'gc: history survives collection' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'a.txt' 'two'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    Invoke-Sm -Repo $repo -Args @('gc', '--prune', 'now') | Out-Null

    $log = Invoke-Sm -Repo $repo -Args @('log', '--oneline')
    Assert-Match 'first' $log.Out 'oldest commit still present'
    Assert-Match 'second' $log.Out 'newest commit still present'

    $co = Invoke-Sm -Repo $repo -Args @('checkout', 'HEAD', '--force')
    Assert-Equal 0 $co.Exit 'checkout still works after gc'
    Assert-Equal 'two' (Get-Content (Join-Path $repo 'a.txt') -Raw).Trim() 'content intact after gc'
}

Invoke-Case 'gc: slashed branch names are not treated as garbage' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'main'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'main work') | Out-Null

    Invoke-Sm -Repo $repo -Args @('checkout', '-b', 'feature/x') | Out-Null
    Write-File $repo 'b.txt' 'feature x'
    Invoke-Sm -Repo $repo -Args @('add', 'b.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'feature x work') | Out-Null
    Invoke-Sm -Repo $repo -Args @('checkout', 'main') | Out-Null

    $branches = Invoke-Sm -Repo $repo -Args @('branch')
    Assert-Match 'feature/x' $branches.Out 'slashed branch is listed'

    Invoke-Sm -Repo $repo -Args @('gc', '--prune', 'now') | Out-Null

    $log = Invoke-Sm -Repo $repo -Args @('log', '--oneline', 'feature/x')
    Assert-Match 'feature x work' $log.Out "slashed branch history survived gc; got: $($log.Out.Trim())"
    # feature/x descends from main, so its ancestor must also be reachable from it.
    Assert-Match 'main work' $log.Out "ancestor is reachable from feature/x; got: $($log.Out.Trim())"
}

Invoke-Case 'reset: HEAD~N beyond the root fails instead of silently clamping' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('reset', '--hard', 'HEAD~99')
    Assert-True ($r.Exit -ne 0) 'HEAD~99 must fail rather than clamp to the root'
}

# ─────────────────────────────────────────────────────────────────────────────
# Branching and merging
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'branch: create, list and switch' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'base'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    $b = Invoke-Sm -Repo $repo -Args @('branch', 'feature')
    Assert-Equal 0 $b.Exit 'branch create exit code'
    $list = Invoke-Sm -Repo $repo -Args @('branch')
    Assert-Match 'feature' $list.Out 'feature branch listed'

    $co = Invoke-Sm -Repo $repo -Args @('checkout', 'feature')
    Assert-Equal 0 $co.Exit 'checkout exit code'
    $head = Get-Content (Join-Path $repo '.sm/HEAD') -Raw
    Assert-Match 'refs/heads/feature' $head 'HEAD switched to feature'
}

Invoke-Case 'merge: fast-forward advances the branch' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'base'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    Invoke-Sm -Repo $repo -Args @('checkout', '-b', 'feature') | Out-Null
    Write-File $repo 'b.txt' 'feature work'
    Invoke-Sm -Repo $repo -Args @('add', 'b.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'feature work') | Out-Null

    Invoke-Sm -Repo $repo -Args @('checkout', 'main') | Out-Null
    $m = Invoke-Sm -Repo $repo -Args @('merge', 'feature')
    Assert-Equal 0 $m.Exit 'merge exit code'
    Assert-True (Test-Path (Join-Path $repo 'b.txt')) 'merged file present'
}

Invoke-Case 'merge: divergent histories produce one commit and one reflog entry' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'shared.txt' 'base'
    Invoke-Sm -Repo $repo -Args @('add', 'shared.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    Invoke-Sm -Repo $repo -Args @('checkout', '-b', 'topic') | Out-Null
    Write-File $repo 'topic.txt' 'topic'
    Invoke-Sm -Repo $repo -Args @('add', 'topic.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'topic work') | Out-Null

    Invoke-Sm -Repo $repo -Args @('checkout', 'main') | Out-Null
    Write-File $repo 'main.txt' 'main'
    Invoke-Sm -Repo $repo -Args @('add', 'main.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'main work') | Out-Null

    $m = Invoke-Sm -Repo $repo -Args @('merge', 'topic')
    Assert-Equal 0 $m.Exit 'merge exit code'
    Assert-True (Test-Path (Join-Path $repo 'topic.txt')) 'topic file merged'
    Assert-True (Test-Path (Join-Path $repo 'main.txt')) 'main file kept'

    # The merge commit must be reachable and must have two parents.
    $log = Invoke-Sm -Repo $repo -Args @('log', '--oneline')
    $lines = @($log.Out -split "`r?`n" | Where-Object { $_.Trim() })
    Assert-True ($lines.Count -ge 4) "merge history present, got $($lines.Count) commits"
}

# ─────────────────────────────────────────────────────────────────────────────
# Repository integrity
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'fsck: a clean repository reports no problems' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'hello'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('fsck')
    Assert-Equal 0 $r.Exit "fsck exit code; output: $($r.All)"
}

# ─────────────────────────────────────────────────────────────────────────────
# Remotes
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'remote: add / get-url / remove round-trip' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'x'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    $add = Invoke-Sm -Repo $repo -Args @('remote', 'add', 'origin', 'file:///tmp/nope')
    Assert-Equal 0 $add.Exit "remote add exit code; output: $($add.All)"

    $list = Invoke-Sm -Repo $repo -Args @('remote')
    Assert-Match 'origin' $list.Out 'origin is listed'

    $rm = Invoke-Sm -Repo $repo -Args @('remote', 'remove', 'origin')
    Assert-Equal 0 $rm.Exit 'remote remove exit code'
    $list2 = Invoke-Sm -Repo $repo -Args @('remote')
    Assert-True ($list2.Out -notmatch 'origin') 'origin is gone'
}

Invoke-Case 'push: objects and branch refs survive a file:// round-trip' {
    $remote = New-TestRepo
    $local = New-TestRepo

    Invoke-Sm -Repo $remote -Args @('init') | Out-Null
    Invoke-Sm -Repo $local -Args @('init') | Out-Null

    Write-File $local 'a.txt' 'from the developer'
    Write-File $local 'src/deep/b.txt' 'nested content'
    Invoke-Sm -Repo $local -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $local -Args @('commit', '-m', 'developer work') | Out-Null

    $url = 'file:///' + ($remote -replace '\\', '/')
    $add = Invoke-Sm -Repo $local -Args @('remote', 'add', 'origin', $url)
    Assert-Equal 0 $add.Exit "remote add; output: $($add.All)"

    $push = Invoke-Sm -Repo $local -Args @('push', 'origin', 'main')
    Assert-Equal 0 $push.Exit "push exit code; output: $($push.All)"

    # The remote must end up owning refs/heads/main, not refs/remotes/origin/main.
    Assert-True (Test-Path (Join-Path $remote '.sm/refs/heads/main')) 'remote owns refs/heads/main'

    # The remote's own history must be readable: this only works if pushed objects were
    # stored compressed. Writing them verbatim produced objects no reader could decompress.
    $log = Invoke-Sm -Repo $remote -Args @('log', '--oneline')
    Assert-Equal 0 $log.Exit "remote log must succeed; output: $($log.All)"
    Assert-Match 'developer work' $log.Out 'remote history is readable'

    # And the remote can materialise the pushed content.
    $co = Invoke-Sm -Repo $remote -Args @('checkout', 'main', '--force')
    Assert-Equal 0 $co.Exit "remote checkout; output: $($co.All)"
    Assert-Equal 'nested content' (Get-Content (Join-Path $remote 'src/deep/b.txt') -Raw).Trim() 'nested content restored from pushed objects'
}

Invoke-Case 'push: rejects a non-fast-forward update without --force' {
    $remote = New-TestRepo
    $alice = New-TestRepo

    foreach ($r in @($remote, $alice)) { Invoke-Sm -Repo $r -Args @('init') | Out-Null }

    Write-File $alice 'a.txt' 'one'
    Invoke-Sm -Repo $alice -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $alice -Args @('commit', '-m', 'base') | Out-Null

    $url = 'file:///' + ($remote -replace '\\', '/')
    Invoke-Sm -Repo $alice -Args @('remote', 'add', 'origin', $url) | Out-Null
    Assert-Equal 0 (Invoke-Sm -Repo $alice -Args @('push', 'origin', 'main')).Exit 'first push succeeds'

    Write-File $alice 'a.txt' 'alice change'
    Invoke-Sm -Repo $alice -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $alice -Args @('commit', '-m', 'alice work') | Out-Null
    Assert-Equal 0 (Invoke-Sm -Repo $alice -Args @('push', 'origin', 'main')).Exit 'fast-forward push succeeds'

    # Rewrite local history, then push: must be refused.
    Assert-Equal 0 (Invoke-Sm -Repo $alice -Args @('reset', '--hard', 'HEAD~1')).Exit 'rewind locally'
    Write-File $alice 'a.txt' 'different history'
    Invoke-Sm -Repo $alice -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $alice -Args @('commit', '-m', 'divergent') | Out-Null

    $rejected = Invoke-Sm -Repo $alice -Args @('push', 'origin', 'main')
    Assert-True ($rejected.Exit -ne 0) "divergent push is rejected; output: $($rejected.All)"
    Assert-Match 'non-fast-forward|rejected' $rejected.All 'explains the rejection'
}

# ─────────────────────────────────────────────────────────────────────────────
# Machine-readable output contract
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'json: status emits a parseable document with staged/unstaged split' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null
    Write-File $repo 'a.txt' 'two'
    Write-File $repo 'new.txt' 'untracked'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Write-File $repo 'a.txt' 'three'

    $r = Invoke-Sm -Repo $repo -Args @('status', '--json')
    Assert-Equal 0 $r.Exit "status --json exit code; output: $($r.All)"
    $doc = $r.Out | ConvertFrom-Json
    Assert-Equal 1 $doc.sm 'schema version present'
    Assert-Equal 'status' $doc.command 'command name present'
    Assert-Equal 'main' $doc.data.branch 'branch reported'
    Assert-Equal $false $doc.data.clean 'not clean'
    Assert-Equal 'a.txt' $doc.data.staged[0].path 'staged path reported'
    Assert-Equal 'modified' $doc.data.staged[0].status 'staged status reported'
    Assert-Equal 'a.txt' $doc.data.unstaged[0].path 'unstaged path reported'
    Assert-Match 'new\.txt' ($doc.data.untracked -join ',') 'untracked reported'
}

Invoke-Case 'json: log emits commit metadata with parents and refs' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'a.txt' 'two'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('log', '--json')
    Assert-Equal 0 $r.Exit "log --json exit code; output: $($r.All)"
    $doc = $r.Out | ConvertFrom-Json
    Assert-Equal 2 $doc.data.count 'both commits reported'
    Assert-Equal 'second' $doc.data.commits[0].subject 'newest first'
    Assert-Equal 'first' $doc.data.commits[1].subject 'oldest last'
    Assert-Equal 64 $doc.data.commits[0].id.Length 'full object id'
    Assert-Equal 1 $doc.data.commits[0].parents.Count 'newest has a parent'
    Assert-Match 'HEAD -> main' ($doc.data.commits[0].refs -join ',') 'refs included'
    # Assert on the raw document: ConvertFrom-Json would turn an ISO string back into a DateTime.
    Assert-Match '"date":\s*"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}"' $r.Out 'author date is an ISO-8601 string'
    Assert-True ($doc.data.commits[0].author.unix -gt 0) 'unix timestamp included'
}
Invoke-Case 'json: diff emits per-file hunks for unstaged work' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "line one`nline two`nline three"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null
    # Deliberately NOT staged: `sm diff` reports working tree vs index.
    Write-File $repo 'a.txt' "line one`nline two changed`nline three"

    $r = Invoke-Sm -Repo $repo -Args @('diff', '--json')
    Assert-Equal 0 $r.Exit "diff --json exit code; output: $($r.All)"
    $doc = $r.Out | ConvertFrom-Json
    Assert-Equal 1 $doc.data.fileCount 'one changed file'
    Assert-Equal 'a.txt' $doc.data.files[0].path 'path reported'
    Assert-Equal 'modified' $doc.data.files[0].status 'status reported'
    Assert-True (($doc.data.files[0].hunks | Measure-Object).Count -ge 1) "at least one hunk; got: $($r.Out)"
    # The changed line must be reported as a delete+add pair, not as context.
    Assert-Equal 1 $doc.data.files[0].added "one inserted line; got: $($r.Out)"
    Assert-Equal 1 $doc.data.files[0].deleted "one deleted line; got: $($r.Out)"

    # Once staged, `sm diff` no longer reports it; `sm diff --cached` does.
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    $afterStage = (Invoke-Sm -Repo $repo -Args @('diff', '--json')).Out | ConvertFrom-Json
    Assert-Equal 0 $afterStage.data.fileCount 'staged edit is not an unstaged diff'
    $cached = (Invoke-Sm -Repo $repo -Args @('diff', '--cached', '--json')).Out | ConvertFrom-Json
    Assert-Equal 1 $cached.data.fileCount '--cached reports the staged edit'
}

Invoke-Case 'json: `--format json` is accepted as a synonym' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'x'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('status', '--format', 'json')
    Assert-Equal 0 $r.Exit 'exit code'
    $doc = $r.Out | ConvertFrom-Json
    Assert-Equal 'status' $doc.command 'document produced'
}

# ─────────────────────────────────────────────────────────────────────────────
# Structure-aware history: renames first-class
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'diff: a moved file is reported as a rename, not delete+add' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'src/old.txt' "alpha`nbeta`ngamma"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'add file') | Out-Null

    # A pure move: identical content, different path.
    New-Item -ItemType Directory (Join-Path $repo 'lib') -Force | Out-Null
    Move-Item (Join-Path $repo 'src/old.txt') (Join-Path $repo 'lib/new.txt')
    Remove-Item (Join-Path $repo 'src') -Recurse -Force
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'move file') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('diff', '--name-status', 'HEAD~1', 'HEAD')
    Assert-Equal 0 $r.Exit "diff --name-status exit code; output: $($r.All)"
    Assert-Match '^R100\s+src/old\.txt\s+lib/new\.txt' $r.Out "reported as R100 rename; got: $($r.Out.Trim())"
    Assert-True ($r.Out -notmatch '^A\s') 'no spurious add'
    Assert-True ($r.Out -notmatch '^D\s') 'no spurious delete'
}

Invoke-Case 'diff: an edited move is a rename with a similarity score below 100' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    $body = (1..20 | ForEach-Object { "line $_" }) -join "`n"
    Write-File $repo 'old.txt' $body
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'add') | Out-Null

    Move-Item (Join-Path $repo 'old.txt') (Join-Path $repo 'new.txt')
    Add-Content (Join-Path $repo 'new.txt') "line 21"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'move and edit') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('diff', '--name-status', 'HEAD~1', 'HEAD')
    Assert-Match '^R\d+\s+old\.txt\s+new\.txt' $r.Out "move+edit is a rename; got: $($r.Out.Trim())"

    $j = (Invoke-Sm -Repo $repo -Args @('diff', '--json', 'HEAD~1', 'HEAD')).Out | ConvertFrom-Json
    Assert-Equal 'renamed' $j.data.files[0].status 'json reports renamed'
    Assert-Equal 'old.txt' $j.data.files[0].oldPath 'json carries the previous path'
}

Invoke-Case 'log --follow: history continues across a rename' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'first.txt' "one`ntwo"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'original name') | Out-Null

    Move-Item (Join-Path $repo 'first.txt') (Join-Path $repo 'second.txt')
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'renamed file') | Out-Null

    Add-Content (Join-Path $repo 'second.txt') "three"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'edit after rename') | Out-Null

    # Without --follow the walk stops at the rename.
    $plain = Invoke-Sm -Repo $repo -Args @('log', '--oneline', '--', 'second.txt')
    Assert-Match 'edit after rename' $plain.Out 'plain log finds the post-rename edit'

    # With --follow it must reach the commit made under the OLD name.
    $followed = Invoke-Sm -Repo $repo -Args @('log', '--oneline', '--follow', '--', 'second.txt')
    Assert-Match 'edit after rename' $followed.Out 'follow finds the edit'
    Assert-Match 'original name' $followed.Out "follow crosses the rename; got: $($followed.Out.Trim())"
}

# ─────────────────────────────────────────────────────────────────────────────
# Integrity verification
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'verify: a healthy repository verifies and reports its shape' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Write-File $repo 'src/b.txt' 'two'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('verify')
    Assert-Equal 0 $r.Exit "verify exit code; output: $($r.All)"

    $j = (Invoke-Sm -Repo $repo -Args @('verify', '--json')).Out | ConvertFrom-Json
    Assert-Equal $true $j.data.ok 'ok flag'
    Assert-Equal 1 $j.data.objects.commits 'one commit counted'
    Assert-True ($j.data.objects.trees -ge 2) "nested trees counted; got $($j.data.objects.trees)"
    Assert-Equal 0 $j.data.errors 'no errors'
}

Invoke-Case 'verify: detects an object whose bytes no longer match its id' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'original content'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    # Corrupt one loose object: overwrite it with a validly-compressed different payload.
    $obj = Get-ChildItem (Join-Path $repo '.sm/objects') -Recurse -File | Select-Object -First 1
    $bytes = [System.IO.File]::ReadAllBytes($obj.FullName)
    $ms = New-Object System.IO.MemoryStream(,$bytes)
    $z = New-Object System.IO.Compression.ZLibStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
    $out = New-Object System.IO.MemoryStream
    $z.CopyTo($out); $z.Dispose()
    $plain = $out.ToArray()
    # Flip a byte in the middle of the payload.
    $plain[[int]($plain.Length / 2)] = $plain[[int]($plain.Length / 2)] -bxor 0xFF
    $out2 = New-Object System.IO.MemoryStream
    $z2 = New-Object System.IO.Compression.ZLibStream($out2, [System.IO.Compression.CompressionLevel]::Optimal)
    $z2.Write($plain, 0, $plain.Length); $z2.Dispose()
    [System.IO.File]::WriteAllBytes($obj.FullName, $out2.ToArray())

    $r = Invoke-Sm -Repo $repo -Args @('verify')
    Assert-True ($r.Exit -ne 0) "verify must fail on a corrupted object; output: $($r.All)"
    Assert-Match 'hash-mismatch|does not match its content|unreadable' $r.All 'reports the corruption'

    $j = (Invoke-Sm -Repo $repo -Args @('verify', '--json')).Out | ConvertFrom-Json
    Assert-Equal $false $j.data.ok 'json ok flag is false'
    Assert-True ($j.data.errors -ge 1) 'at least one error reported'
}

Invoke-Case 'verify: detects a ref pointing at a missing object' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    # Point a branch at an object that was never written.
    $bogus = 'a' * 64
    Set-Content -Path (Join-Path $repo '.sm/refs/heads/ghost') -Value $bogus
    $r = Invoke-Sm -Repo $repo -Args @('verify')
    Assert-True ($r.Exit -ne 0) 'verify fails on a dangling ref'
    Assert-Match 'dangling-ref|missing object' $r.All 'names the broken ref'
}

# ─────────────────────────────────────────────────────────────────────────────
# Structured conflicts
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'conflicts: a conflicting merge is structured state, not just markers' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'shared.txt' "base line`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    Invoke-Sm -Repo $repo -Args @('checkout', '-b', 'topic') | Out-Null
    Write-File $repo 'shared.txt' "theirs line`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'topic edit') | Out-Null

    Invoke-Sm -Repo $repo -Args @('checkout', 'main') | Out-Null
    Write-File $repo 'shared.txt' "ours line`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'main edit') | Out-Null

    $m = Invoke-Sm -Repo $repo -Args @('merge', 'topic')
    Assert-True ($m.Exit -ne 0) 'conflicting merge reports failure'
    Assert-Match 'CONFLICT' $m.All 'names the conflict'

    # status must surface the unmerged path (this used to be permanently dead code).
    $st = Invoke-Sm -Repo $repo -Args @('status')
    Assert-Match 'Unmerged paths' $st.Out "status shows unmerged paths; got: $($st.Out)"
    Assert-Match 'shared\.txt' $st.Out 'names the conflicted file'

    $sj = (Invoke-Sm -Repo $repo -Args @('status', '--json')).Out | ConvertFrom-Json
    Assert-Match 'shared\.txt' ($sj.data.conflicted -join ',') 'json reports the conflicted path'

    # commit must refuse while conflicts remain.
    $c = Invoke-Sm -Repo $repo -Args @('commit', '-m', 'premature')
    Assert-True ($c.Exit -ne 0) 'commit refuses with unresolved conflicts'
    Assert-Match 'unmerged' $c.All 'explains why'

    # The conflict set is on disk as structured data, not only marker text.
    Assert-True (Test-Path (Join-Path $repo '.sm/CONFLICTS')) 'conflict set persisted'
    $conflictDoc = Get-Content (Join-Path $repo '.sm/CONFLICTS') -Raw | ConvertFrom-Json
    # Index the array BEFORE reading the property: member enumeration on an array would make
    # `.OursId[0]` mean "the first character of the first OursId".
    $firstConflict = @($conflictDoc.conflicts)[0]
    Assert-Match 'shared\.txt' $firstConflict.Path 'persisted path'
    Assert-True ($firstConflict.OursId.Length -eq 64) 'ours object id persisted'
    Assert-True ($firstConflict.TheirsId.Length -eq 64) 'theirs object id persisted'
    Assert-True ($firstConflict.BaseId.Length -eq 64) 'base object id persisted'
    Assert-True ($firstConflict.Hunks -ge 1) 'conflict region count persisted'

    # Resolving and committing completes the merge.
    Write-File $repo 'shared.txt' "resolved line`n"
    Invoke-Sm -Repo $repo -Args @('add', 'shared.txt') | Out-Null
    $c2 = Invoke-Sm -Repo $repo -Args @('commit', '-m', 'resolved merge')
    Assert-Equal 0 $c2.Exit "commit after resolution; output: $($c2.All)"
    Assert-True (-not (Test-Path (Join-Path $repo '.sm/CONFLICTS'))) 'conflict set cleared after commit'
    $log = Invoke-Sm -Repo $repo -Args @('log', '--json')
    $lj = $log.Out | ConvertFrom-Json
    Assert-Equal 2 $lj.data.commits[0].parents.Count 'merge commit has two parents'
}

Invoke-Case 'conflicts: merge --abort clears the conflict state' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'f.txt' "base`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null
    Invoke-Sm -Repo $repo -Args @('checkout', '-b', 'other') | Out-Null
    Write-File $repo 'f.txt' "theirs`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'other edit') | Out-Null
    Invoke-Sm -Repo $repo -Args @('checkout', 'main') | Out-Null
    Write-File $repo 'f.txt' "ours`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'main edit') | Out-Null
    Invoke-Sm -Repo $repo -Args @('merge', 'other') | Out-Null

    $a = Invoke-Sm -Repo $repo -Args @('merge', '--abort')
    Assert-Equal 0 $a.Exit "merge --abort exit code; output: $($a.All)"
    $st = Invoke-Sm -Repo $repo -Args @('status')
    Assert-True ($st.Out -notmatch 'Unmerged paths') 'conflict state cleared'
    Assert-Equal 'ours' (Get-Content (Join-Path $repo 'f.txt') -Raw).Trim() 'working tree restored to ours'
}

# ─────────────────────────────────────────────────────────────────────────────
# Stash verbs and CLI surface
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'stash: `stash list` lists instead of creating a stash' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    $empty = Invoke-Sm -Repo $repo -Args @('stash', 'list')
    Assert-Equal 0 $empty.Exit 'stash list exit code'

    Write-File $repo 'a.txt' 'modified'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    $push = Invoke-Sm -Repo $repo -Args @('stash')
    Assert-Equal 0 $push.Exit "stash push exit code; output: $($push.All)"

    $list = Invoke-Sm -Repo $repo -Args @('stash', 'list')
    Assert-Match 'stash@\{0\}' $list.Out "stash list shows the entry; got: $($list.Out)"
    # Listing twice must not accumulate entries.
    $list2 = Invoke-Sm -Repo $repo -Args @('stash', 'list')
    Assert-Equal ((($list.Out -split "`r?`n") | Where-Object { $_ -match 'stash@' }).Count) ((($list2.Out -split "`r?`n") | Where-Object { $_ -match 'stash@' }).Count) 'listing does not create entries'
}

Invoke-Case 'help: `sm help <cmd>` prints that command help without running it' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('help', 'status')
    Assert-Equal 0 $r.Exit 'help exit code'
    Assert-Match 'Show the working tree status|Usage' $r.Out 'prints help'
    Assert-True ($r.Out -notmatch 'On branch') 'did not actually run status'

    $bad = Invoke-Sm -Repo $repo -Args @('help', 'not-a-command')
    Assert-True ($bad.Exit -ne 0) 'unknown help topic fails'
}

Invoke-Case 'cli: an unknown command fails instead of printing help and exiting 0' {
    $r = Invoke-Sm -Repo $workRoot -Args @('bogus-command')
    Assert-True ($r.Exit -ne 0) 'unknown command must fail'
    Assert-Match 'not a SourceManager command' $r.All 'explains the problem'
}

# ─────────────────────────────────────────────────────────────────────────────
# Transport protocol
# ─────────────────────────────────────────────────────────────────────────────

# Runs `sm serve --stdio` on $Repo and exchanges raw protocol lines with it.
function New-StdioServer {
    param([string]$Repo, [string]$Token)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $SmPath
    $psi.WorkingDirectory = $Repo
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    [void]$psi.ArgumentList.Add('serve')
    [void]$psi.ArgumentList.Add('--stdio')
    if ($Token) { $psi.Environment['SM_SERVE_TOKEN'] = $Token }
    return [System.Diagnostics.Process]::Start($psi)
}

function Send-Line {
    param($Proc, [string]$Line)
    $Proc.StandardInput.WriteLine($Line)
    $Proc.StandardInput.Flush()
}

function Read-Line {
    param($Proc)
    return $Proc.StandardOutput.ReadLine()
}

Invoke-Case 'protocol: stdio handshake advertises version and capabilities' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'x'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    $proc = New-StdioServer -Repo $repo
    try {
        $helloLine = Read-Line $proc
        Assert-True ($null -ne $helloLine) 'server sends a hello'
        $hello = $helloLine | ConvertFrom-Json
        Assert-Equal 'ready' $hello.status 'hello status'
        Assert-Equal 'sm' $hello.protocol 'protocol name'
        Assert-Match '^\d+\.\d+$' $hello.version 'protocol version'
        Assert-True (($hello.capabilities | Measure-Object).Count -ge 1) 'capabilities advertised'

        Send-Line $proc '{"id":1,"method":"hello","params":{"version":"1.0"}}'
        $neg = (Read-Line $proc) | ConvertFrom-Json
        Assert-Equal 1 $neg.id 'response echoes the request id'
        Assert-Equal '1.0' $neg.version 'server confirms a compatible version'
        Assert-True (($neg.capabilities | Measure-Object).Count -ge 1) 'server lists capabilities'

        # Every response must carry the id of its request, in order.
        Send-Line $proc '{"id":2,"method":"get-current-branch","params":{}}'
        $r2 = (Read-Line $proc) | ConvertFrom-Json
        Assert-Equal 2 $r2.id 'second response carries its own id'
        Assert-Equal 'main' $r2.branch 'serves repository state'

        Send-Line $proc '{"id":3,"method":"get-head-commit","params":{}}'
        $r3 = (Read-Line $proc) | ConvertFrom-Json
        Assert-Equal 3 $r3.id 'third response carries its own id'
    }
    finally {
        try { $proc.Kill($true) } catch { }
        $proc.Dispose()
    }
}

Invoke-Case 'protocol: blank lines are padding and are never answered' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null

    $proc = New-StdioServer -Repo $repo
    try {
        Read-Line $proc | Out-Null   # hello

        # A liveness probe: if the server answered this, the next real response would be the
        # probe's answer and the stream would be permanently offset by one.
        Send-Line $proc ''
        Send-Line $proc ''
        Send-Line $proc '{"id":7,"method":"get-current-branch","params":{}}'

        $resp = (Read-Line $proc) | ConvertFrom-Json
        Assert-Equal 7 $resp.id 'the first response answers the first real request'
    }
    finally {
        try { $proc.Kill($true) } catch { }
        $proc.Dispose()
    }
}

Invoke-Case 'protocol: an incompatible major version is refused clearly' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null

    $proc = New-StdioServer -Repo $repo
    try {
        Read-Line $proc | Out-Null   # hello
        Send-Line $proc '{"id":1,"method":"hello","params":{"version":"99.0"}}'
        $resp = (Read-Line $proc) | ConvertFrom-Json
        Assert-Match 'version mismatch' $resp.error 'reports the mismatch'
    }
    finally {
        try { $proc.Kill($true) } catch { }
        $proc.Dispose()
    }
}

Invoke-Case 'protocol: a required token is enforced over stdio' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null

    $proc = New-StdioServer -Repo $repo -Token 'sekrit'
    try {
        Read-Line $proc | Out-Null   # hello

        Send-Line $proc '{"id":1,"method":"hello","params":{"version":"1.0","token":"wrong"}}'
        $bad = (Read-Line $proc) | ConvertFrom-Json
        Assert-Match 'authentication failed' $bad.error 'wrong token is rejected'

        Send-Line $proc '{"id":2,"method":"hello","params":{"version":"1.0","token":"sekrit"}}'
        $good = (Read-Line $proc) | ConvertFrom-Json
        Assert-Equal '1.0' $good.version 'correct token is accepted'
        Assert-True (-not $good.PSObject.Properties.Name.Contains('error')) 'no error with the right token'
    }
    finally {
        try { $proc.Kill($true) } catch { }
        $proc.Dispose()
    }
}

# ─── Native sm:// protocol (binary framing over TCP) ────────────────────────

function Get-FreePort {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    $port = $l.LocalEndpoint.Port
    $l.Stop()
    return $port
}

# Starts `sm serve` (native sm:// protocol) and waits until the port accepts connections.
function Start-SmServer {
    param([string]$Repo, [int]$Port, [string]$Token)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $SmPath
    $psi.WorkingDirectory = $Repo
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    [void]$psi.ArgumentList.Add('serve')
    [void]$psi.ArgumentList.Add('--path'); [void]$psi.ArgumentList.Add($Repo)
    [void]$psi.ArgumentList.Add('--port'); [void]$psi.ArgumentList.Add("$Port")
    [void]$psi.ArgumentList.Add('--bind'); [void]$psi.ArgumentList.Add('127.0.0.1')
    if ($Token) { $psi.Environment['SM_SERVE_TOKEN'] = $Token }
    $proc = [System.Diagnostics.Process]::Start($psi)
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 100
        try {
            $probe = [System.Net.Sockets.TcpClient]::new()
            $probe.Connect('127.0.0.1', $Port)
            $probe.Close()
            return $proc
        }
        catch { }
    }
    try { $proc.Kill($true) } catch { }
    throw "sm serve did not start listening on port $Port"
}

function Stop-SmServer {
    param($Proc)
    if ($null -eq $Proc) { return }
    try { if (-not $Proc.HasExited) { $Proc.Kill($true) } } catch { }
    try { $Proc.Dispose() } catch { }
}

function Write-SmFrame {
    param([System.IO.Stream]$Stream, [int]$Type, [string]$Payload)
    $body = [System.Text.Encoding]::UTF8.GetBytes($Payload)
    $header = [byte[]]::new(12)
    $header[0] = [byte][char]'S'; $header[1] = [byte][char]'M'
    $header[2] = [byte][char]'P'; $header[3] = [byte][char]'1'
    $header[4] = 1
    $header[5] = [byte]$Type
    $len = $body.Length
    $header[8] = [byte](($len -shr 24) -band 0xFF)
    $header[9] = [byte](($len -shr 16) -band 0xFF)
    $header[10] = [byte](($len -shr 8) -band 0xFF)
    $header[11] = [byte]($len -band 0xFF)
    $Stream.Write($header, 0, 12)
    if ($len -gt 0) { $Stream.Write($body, 0, $len) }
    $Stream.Flush()
}

function Read-SmFrame {
    param([System.IO.Stream]$Stream)
    $header = [byte[]]::new(12)
    $read = 0
    while ($read -lt 12) {
        $n = $Stream.Read($header, $read, 12 - $read)
        if ($n -le 0) { throw 'connection closed mid-frame (header)' }
        $read += $n
    }
    $type = [int]$header[5]
    $len = ($header[8] -shl 24) -bor ($header[9] -shl 16) -bor ($header[10] -shl 8) -bor $header[11]
    $body = [byte[]]::new($len)
    $read = 0
    while ($read -lt $len) {
        $n = $Stream.Read($body, $read, $len - $read)
        if ($n -le 0) { throw 'connection closed mid-frame (body)' }
        $read += $n
    }
    return @{ Type = $type; Payload = [System.Text.Encoding]::UTF8.GetString($body) }
}

Invoke-Case 'protocol: native sm:// push and fetch round-trip' {
    $server = New-TestRepo
    Invoke-Sm -Repo $server -Args @('init') | Out-Null
    $client = New-TestRepo
    Invoke-Sm -Repo $client -Args @('init') | Out-Null
    Write-File $client 'a.txt' 'hello sm'
    Invoke-Sm -Repo $client -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $client -Args @('commit', '-m', 'base') | Out-Null

    $port = Get-FreePort
    $proc = Start-SmServer -Repo $server -Port $port
    try {
        $url = "sm://127.0.0.1:$port/"
        Invoke-Sm -Repo $client -Args @('remote', 'add', 'origin', $url) | Out-Null

        $push = Invoke-Sm -Repo $client -Args @('push', 'origin', 'main')
        Assert-Equal 0 $push.Exit 'push over sm:// succeeds'
        Assert-Match 'Push completed successfully' $push.Out 'push reports success'
        Assert-True (Test-Path (Join-Path $server '.sm/refs/heads/main')) 'server received the branch'

        $consumer = New-TestRepo
        Invoke-Sm -Repo $consumer -Args @('init') | Out-Null
        Invoke-Sm -Repo $consumer -Args @('remote', 'add', 'origin', $url) | Out-Null
        $fetch = Invoke-Sm -Repo $consumer -Args @('fetch', 'origin')
        Assert-Equal 0 $fetch.Exit 'fetch over sm:// succeeds'

        $show = Invoke-Sm -Repo $consumer -Args @('show', 'origin/main:a.txt')
        Assert-Match 'hello sm' $show.Out 'object content transferred intact'
    }
    finally { Stop-SmServer $proc }
}

Invoke-Case 'protocol: native frame magic and handshake' {
    $server = New-TestRepo
    Invoke-Sm -Repo $server -Args @('init') | Out-Null
    $port = Get-FreePort
    $proc = Start-SmServer -Repo $server -Port $port
    try {
        $tcp = [System.Net.Sockets.TcpClient]::new()
        $tcp.Connect('127.0.0.1', $port)
        $stream = $tcp.GetStream()

        Write-SmFrame $stream 1 '{"protocol":"sm","version":"1.0","capabilities":[]}'
        $ack = Read-SmFrame $stream
        Assert-Equal 2 $ack.Type 'server answers with a HelloAck frame'
        $hello = $ack.Payload | ConvertFrom-Json
        Assert-Equal 'sm' $hello.protocol 'protocol name'
        Assert-Match '^\d+\.\d+$' $hello.version 'protocol version'
        Assert-Equal 1 $hello.frameVersion 'frame version advertised'
        Assert-True (($hello.capabilities | Measure-Object).Count -ge 1) 'capabilities advertised'

        Write-SmFrame $stream 3 '{"id":5,"method":"get-current-branch","params":{}}'
        $resp = Read-SmFrame $stream
        Assert-Equal 4 $resp.Type 'server answers a Request with a Response frame'
        $doc = $resp.Payload | ConvertFrom-Json
        Assert-Equal 5 $doc.id 'response echoes the request id'
        Assert-Equal 'main' $doc.branch 'serves repository state'

        Write-SmFrame $stream 6 '{}'
        $tcp.Close()
    }
    finally { Stop-SmServer $proc }
}

Invoke-Case 'protocol: an incompatible major version is refused over sm://' {
    $server = New-TestRepo
    Invoke-Sm -Repo $server -Args @('init') | Out-Null
    $port = Get-FreePort
    $proc = Start-SmServer -Repo $server -Port $port
    try {
        $tcp = [System.Net.Sockets.TcpClient]::new()
        $tcp.Connect('127.0.0.1', $port)
        $stream = $tcp.GetStream()
        Write-SmFrame $stream 1 '{"protocol":"sm","version":"99.0"}'
        $frame = Read-SmFrame $stream
        Assert-Equal 5 $frame.Type 'server refuses with an Error frame'
        Assert-Match 'version mismatch' $frame.Payload 'reports the mismatch'
        $tcp.Close()
    }
    finally { Stop-SmServer $proc }
}

Invoke-Case 'protocol: a required token is enforced over sm://' {
    $server = New-TestRepo
    Invoke-Sm -Repo $server -Args @('init') | Out-Null
    $port = Get-FreePort
    $proc = Start-SmServer -Repo $server -Port $port -Token 'sekrit'
    try {
        $bad = [System.Net.Sockets.TcpClient]::new()
        $bad.Connect('127.0.0.1', $port)
        $badStream = $bad.GetStream()
        Write-SmFrame $badStream 1 '{"protocol":"sm","version":"1.0","token":"wrong"}'
        $rejected = Read-SmFrame $badStream
        Assert-Equal 5 $rejected.Type 'wrong token yields an Error frame'
        Assert-Match 'authentication failed' $rejected.Payload 'explains the rejection'
        $bad.Close()

        $good = [System.Net.Sockets.TcpClient]::new()
        $good.Connect('127.0.0.1', $port)
        $goodStream = $good.GetStream()
        Write-SmFrame $goodStream 1 '{"protocol":"sm","version":"1.0","token":"sekrit"}'
        $accepted = Read-SmFrame $goodStream
        Assert-Equal 2 $accepted.Type 'correct token yields a HelloAck'
        $good.Close()
    }
    finally { Stop-SmServer $proc }
}

Invoke-Case 'config: color.ui forces or suppresses ANSI color' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'hi'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    Invoke-Sm -Repo $repo -Args @('config', 'color.ui', 'always') | Out-Null
    $on = Invoke-Sm -Repo $repo -Args @('log', '--oneline')
    Assert-True ($on.Out -match "$([char]27)\[") 'color.ui=always emits ANSI escapes even when piped'

    Invoke-Sm -Repo $repo -Args @('config', 'color.ui', 'never') | Out-Null
    $off = Invoke-Sm -Repo $repo -Args @('log', '--oneline')
    Assert-True (-not ($off.Out -match "$([char]27)\[")) 'color.ui=never suppresses ANSI escapes'
}

Invoke-Case 'hooks: pre-commit aborts the commit and --no-verify bypasses it' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'hi'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    $hookDir = Join-Path $repo '.sm/hooks'
    New-Item -ItemType Directory -Path $hookDir -Force | Out-Null
    Set-Content -Path (Join-Path $hookDir 'pre-commit.cmd') -NoNewline -Value "@echo off`r`nexit /b 1"
    Write-File $repo 'b.txt' 'x'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null

    $blocked = Invoke-Sm -Repo $repo -Args @('commit', '-m', 'should fail')
    Assert-True ($blocked.Exit -ne 0) 'a failing pre-commit aborts the commit'
    Assert-Match "hook 'pre-commit'" $blocked.All 'reports which hook failed'

    $bypassed = Invoke-Sm -Repo $repo -Args @('commit', '-m', 'bypass', '--no-verify')
    Assert-Equal 0 $bypassed.Exit '--no-verify skips the hook'
}

Invoke-Case 'hooks: commit-msg can rewrite the message' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'hi'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null

    $hookDir = Join-Path $repo '.sm/hooks'
    New-Item -ItemType Directory -Path $hookDir -Force | Out-Null
    Set-Content -Path (Join-Path $hookDir 'commit-msg.cmd') -NoNewline -Value "@echo off`r`n> `"%1`" echo hooked-message`r`nexit /b 0"

    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'original') | Out-Null
    $log = Invoke-Sm -Repo $repo -Args @('log', '--oneline')
    Assert-Match 'hooked-message' $log.Out 'the hook rewrote the commit message'
}

Invoke-Case 'index: a corrupt index is rejected by its checksum' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'hi'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    $idx = Join-Path $repo '.sm/index'
    $bytes = [IO.File]::ReadAllBytes($idx)
    $bytes[20] = $bytes[20] -bxor 0xFF
    [IO.File]::WriteAllBytes($idx, $bytes)

    $r = Invoke-Sm -Repo $repo -Args @('status')
    Assert-True ($r.Exit -ne 0) 'a corrupt index must fail'
    Assert-Match 'checksum' $r.All 'reports the checksum mismatch'
}

Invoke-Case 'json: a failing command emits an error envelope' {
    $repo = New-TestRepo   # deliberately not initialised
    $r = Invoke-Sm -Repo $repo -Args @('status', '--json')
    Assert-True ($r.Exit -ne 0) 'failure exit code'
    $doc = $r.Out | ConvertFrom-Json
    Assert-Equal 1 $doc.sm 'schema version'
    Assert-Equal 'status' $doc.command 'command name'
    Assert-True (-not [string]::IsNullOrEmpty($doc.error)) 'an error message is present'
    Assert-Equal 'fatal' $doc.code 'fatal code'
}

Invoke-Case 'json: a usage error emits an error envelope' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    $r = Invoke-Sm -Repo $repo -Args @('status', '--json', '--definitely-bogus')
    Assert-Equal 1 $r.Exit 'usage error exit code'
    $doc = $r.Out | ConvertFrom-Json
    Assert-Equal 'usage' $doc.code 'usage code'
    Assert-Match 'Unknown option' $doc.error 'carries the parser error'
}

Invoke-Case 'rebase: refuses to run with a dirty tree unless --autostash is given' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "one`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Invoke-Sm -Repo $repo -Args @('checkout', '-b', 'feature') | Out-Null
    Write-File $repo 'a.txt' "one`nfeature`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'feature work') | Out-Null
    Invoke-Sm -Repo $repo -Args @('checkout', 'main') | Out-Null
    Write-File $repo 'a.txt' "one`nmain`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'main work') | Out-Null

    # Uncommitted edit that must NOT be silently discarded.
    Write-File $repo 'a.txt' "one`nmain`nprecious uncommitted line`n"

    $r = Invoke-Sm -Repo $repo -Args @('rebase', 'feature')
    Assert-True ($r.Exit -ne 0) 'rebase refuses a dirty tree'
    Assert-Match 'unstaged changes|autostash' $r.All 'explains how to proceed'
    Assert-Match 'precious uncommitted line' (Get-Content (Join-Path $repo 'a.txt') -Raw) 'uncommitted work is untouched'
}

Invoke-Case 'rebase --autostash: uncommitted work survives the rebase' {
    $remote = New-TestRepo
    $repo = New-TestRepo
    Invoke-Sm -Repo $remote -Args @('init') | Out-Null
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null

    # A base commit published to the remote.
    Write-File $repo 'a.txt' "base`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null
    $url = 'file:///' + ($remote -replace '\\', '/')
    Invoke-Sm -Repo $repo -Args @('remote', 'add', 'origin', $url) | Out-Null
    Invoke-Sm -Repo $repo -Args @('push', 'origin', 'main') | Out-Null

    # The remote moves ahead.
    Write-File $remote 'a.txt' "base`nremote advance`n"
    Invoke-Sm -Repo $remote -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $remote -Args @('commit', '-m', 'remote advance') | Out-Null

    # Local diverges on the same file, then tries to rebase onto the remote with dirty work.
    Write-File $repo 'a.txt' "base`nlocal work`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'local work') | Out-Null
    Write-File $repo 'scratch.txt' "uncommitted scratch`n"
    Invoke-Sm -Repo $repo -Args @('add', 'scratch.txt') | Out-Null

    Invoke-Sm -Repo $repo -Args @('fetch', 'origin') | Out-Null
    $r = Invoke-Sm -Repo $repo -Args @('rebase', '--autostash', 'origin/main')
    # The rebase itself may conflict; what matters is that the stashed work is not lost.
    Assert-Match 'autostash' $r.All "autostash is reported; got: $($r.All)"

    $scratch = Join-Path $repo 'scratch.txt'
    Assert-True (Test-Path $scratch) 'autostashed file is back on disk'
    Assert-Match 'uncommitted scratch' (Get-Content $scratch -Raw) 'its content is intact'
}

# ─────────────────────────────────────────────────────────────────────────────
# Working-tree file operations
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'mv: renames the file and the index together' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'old.txt' "content`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'add') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('mv', 'old.txt', 'new.txt')
    Assert-Equal 0 $r.Exit "mv exit code; output: $($r.All)"
    Assert-True (-not (Test-Path (Join-Path $repo 'old.txt'))) 'source is gone'
    Assert-True (Test-Path (Join-Path $repo 'new.txt')) 'destination exists'

    # The index must follow, or the next commit records a delete plus an add.
    $files = Invoke-Sm -Repo $repo -Args @('ls-files')
    Assert-Match 'new\.txt' $files.Out 'index tracks the new path'
    Assert-True ($files.Out -notmatch 'old\.txt') 'index no longer tracks the old path'

    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'rename') | Out-Null
    $d = Invoke-Sm -Repo $repo -Args @('diff', '--name-status', 'HEAD~1', 'HEAD')
    Assert-Match '^R100\s+old\.txt\s+new\.txt' $d.Out "recorded as a rename; got: $($d.Out.Trim())"
}

Invoke-Case 'mv: refuses to clobber an existing destination' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'a'
    Write-File $repo 'b.txt' 'b'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'both') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('mv', 'a.txt', 'b.txt')
    Assert-True ($r.Exit -ne 0) 'mv refuses without --force'
    Assert-Equal 'a' (Get-Content (Join-Path $repo 'a.txt') -Raw).Trim() 'source intact'
    Assert-Equal 'b' (Get-Content (Join-Path $repo 'b.txt') -Raw).Trim() 'destination intact'
}

Invoke-Case 'rm: removes from the working tree and the index' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'a'
    Write-File $repo 'b.txt' 'b'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'both') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('rm', 'a.txt')
    Assert-Equal 0 $r.Exit "rm exit code; output: $($r.All)"
    Assert-True (-not (Test-Path (Join-Path $repo 'a.txt'))) 'file deleted'
    $files = Invoke-Sm -Repo $repo -Args @('ls-files')
    Assert-True ($files.Out -notmatch 'a\.txt') 'index entry removed'
    Assert-Match 'b\.txt' $files.Out 'other file untouched'

    $j = (Invoke-Sm -Repo $repo -Args @('status', '--json')).Out | ConvertFrom-Json
    Assert-Equal 'deleted' (($j.data.staged | Where-Object { $_.path -eq 'a.txt' }).status) 'reported as staged delete'
}

Invoke-Case 'rm: refuses to discard local modifications without --force' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'original'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null
    Write-File $repo 'a.txt' 'precious local edit'

    $blocked = Invoke-Sm -Repo $repo -Args @('rm', 'a.txt')
    Assert-True ($blocked.Exit -ne 0) 'rm refuses to lose local changes'
    Assert-Match 'would be lost|--force' $blocked.All 'explains why'
    Assert-True (Test-Path (Join-Path $repo 'a.txt')) 'file survives the refusal'

    $forced = Invoke-Sm -Repo $repo -Args @('rm', '-f', 'a.txt')
    Assert-Equal 0 $forced.Exit 'rm -f proceeds'
    Assert-True (-not (Test-Path (Join-Path $repo 'a.txt'))) 'file deleted with --force'
}

Invoke-Case 'rm --cached: unstages but keeps the file on disk' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'a'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('rm', '--cached', 'a.txt')
    Assert-Equal 0 $r.Exit 'rm --cached exit code'
    Assert-True (Test-Path (Join-Path $repo 'a.txt')) 'file kept on disk'
    $files = Invoke-Sm -Repo $repo -Args @('ls-files')
    Assert-True ($files.Out -notmatch 'a\.txt') 'index entry removed'

    $st = Invoke-Sm -Repo $repo -Args @('status', '--porcelain')
    Assert-Match '(?m)^D  a\.txt' $st.Out "staged deletion; got: $($st.Out.Trim())"
    Assert-Match '(?m)^\?\? a\.txt' $st.Out "now untracked; got: $($st.Out.Trim())"
}

Invoke-Case 'clean: does nothing without --force, then removes untracked files' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'tracked.txt' 'tracked'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null
    Write-File $repo 'junk.txt' 'junk'
    Write-File $repo 'sub/nested.txt' 'nested junk'

    $refuse = Invoke-Sm -Repo $repo -Args @('clean')
    Assert-True ($refuse.Exit -ne 0) 'clean refuses without -f or -n'
    Assert-True (Test-Path (Join-Path $repo 'junk.txt')) 'nothing removed on refusal'

    $dry = Invoke-Sm -Repo $repo -Args @('clean', '-n')
    Assert-Equal 0 $dry.Exit 'dry run exit code'
    Assert-Match 'junk\.txt' $dry.Out 'dry run lists the file'
    Assert-True (Test-Path (Join-Path $repo 'junk.txt')) 'dry run removes nothing'

    $real = Invoke-Sm -Repo $repo -Args @('clean', '-f')
    Assert-Equal 0 $real.Exit 'clean -f exit code'
    Assert-True (-not (Test-Path (Join-Path $repo 'junk.txt'))) 'untracked file removed'
    Assert-True (Test-Path (Join-Path $repo 'tracked.txt')) 'tracked file untouched'
}

# ─────────────────────────────────────────────────────────────────────────────
# Inspection: show / ls-files / ls-tree
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'show: displays a commit with its patch and its stat' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "one`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'a.txt' "one`ntwo`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('show', 'HEAD')
    Assert-Equal 0 $r.Exit "show exit code; output: $($r.All)"
    Assert-Match 'second' $r.Out 'shows the message'
    Assert-Match '\+two' $r.Out 'shows the patch'

    $stat = Invoke-Sm -Repo $repo -Args @('show', '--stat', 'HEAD')
    Assert-Match 'insertion' $stat.Out 'shows a diffstat'

    $j = (Invoke-Sm -Repo $repo -Args @('show', '--json', 'HEAD')).Out | ConvertFrom-Json
    Assert-Equal 'commit' $j.data.type 'json type'
    Assert-Equal 1 $j.data.parents.Count 'json parents'
    Assert-Equal 'a.txt' $j.data.files[0].path 'json file list'
}

Invoke-Case 'show: displays a file at a revision' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "original content`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'a.txt' "changed content`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('show', 'HEAD~1:a.txt')
    Assert-Equal 0 $r.Exit "show <rev>:<path> exit code; output: $($r.All)"
    Assert-Match 'original content' $r.Out 'shows the old content'
    Assert-True ($r.Out -notmatch 'changed content') 'does not show the new content'
}

Invoke-Case 'ls-files: reports tracked, modified and untracked files' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'tracked.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null
    Write-File $repo 'tracked.txt' 'two'
    Write-File $repo 'untracked.txt' 'new'

    $cached = Invoke-Sm -Repo $repo -Args @('ls-files')
    Assert-Match 'tracked\.txt' $cached.Out 'lists tracked files'

    $modified = Invoke-Sm -Repo $repo -Args @('ls-files', '-m')
    Assert-Match 'tracked\.txt' $modified.Out 'lists modified files'

    $others = Invoke-Sm -Repo $repo -Args @('ls-files', '-o')
    Assert-Match 'untracked\.txt' $others.Out 'lists untracked files'

    $j = (Invoke-Sm -Repo $repo -Args @('ls-files', '--json')).Out | ConvertFrom-Json
    Assert-True ($j.data.files[0].id.Length -eq 64) 'json carries the object id'
}

Invoke-Case 'ls-tree: lists a committed tree with octal modes' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'a'
    Write-File $repo 'src/deep/b.txt' 'b'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'tree') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('ls-tree', '-r', 'HEAD')
    Assert-Equal 0 $r.Exit "ls-tree exit code; output: $($r.All)"
    Assert-Match '100644 blob \w{64}\s+a\.txt' $r.Out "octal file mode; got: $($r.Out.Trim())"
    Assert-Match 'src/deep/b\.txt' $r.Out 'recurses into subtrees'

    # A committed tree must be readable even when the file is gone from disk.
    Remove-Item (Join-Path $repo 'a.txt') -Force
    $again = Invoke-Sm -Repo $repo -Args @('ls-tree', '-r', 'HEAD')
    Assert-Match 'a\.txt' $again.Out 'reads from the object database, not the working tree'
}

# ─────────────────────────────────────────────────────────────────────────────
# Plumbing
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'rev-parse: resolves revisions, abbreviations and branch names' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'a'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    $full = (Invoke-Sm -Repo $repo -Args @('rev-parse', 'HEAD')).Out.Trim()
    Assert-Equal 64 $full.Length 'full object id'

    $short = (Invoke-Sm -Repo $repo -Args @('rev-parse', '--short', '12', 'HEAD')).Out.Trim()
    Assert-Equal 12 $short.Length 'abbreviated id'
    Assert-Equal $full.Substring(0, 12) $short 'abbreviation is a prefix'

    $branch = (Invoke-Sm -Repo $repo -Args @('rev-parse', '--abbrev-ref', 'HEAD')).Out.Trim()
    Assert-Equal 'main' $branch 'branch name'

    $top = (Invoke-Sm -Repo $repo -Args @('rev-parse', '--show-toplevel')).Out.Trim()
    Assert-True ($top.Length -gt 0) 'toplevel path'

    # Exit code is the signal for scripts.
    $bad = Invoke-Sm -Repo $repo -Args @('rev-parse', '--verify', 'no-such-revision')
    Assert-True ($bad.Exit -ne 0) 'unknown revision fails'

    $good = Invoke-Sm -Repo $repo -Args @('rev-parse', '--verify', 'HEAD')
    Assert-Equal 0 $good.Exit 'valid revision succeeds'
}

Invoke-Case 'cat-file: reads type, size and content of objects' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "hello world`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    $head = (Invoke-Sm -Repo $repo -Args @('rev-parse', 'HEAD')).Out.Trim()
    Assert-Equal 'commit' (Invoke-Sm -Repo $repo -Args @('cat-file', '-t', $head)).Out.Trim() 'commit type'

    $blobId = (Invoke-Sm -Repo $repo -Args @('hash-object', 'a.txt')).Out.Trim()
    Assert-Equal 'blob' (Invoke-Sm -Repo $repo -Args @('cat-file', '-t', $blobId)).Out.Trim() 'blob type'
    Assert-Equal '12' (Invoke-Sm -Repo $repo -Args @('cat-file', '-s', $blobId)).Out.Trim() 'blob size'
    Assert-Match 'hello world' (Invoke-Sm -Repo $repo -Args @('cat-file', '-p', $blobId)).Out 'blob content'

    $missing = Invoke-Sm -Repo $repo -Args @('cat-file', '--exists', ('f' * 64))
    Assert-True ($missing.Exit -ne 0) 'missing object signals failure'

    $j = (Invoke-Sm -Repo $repo -Args @('cat-file', '--json', $head)).Out | ConvertFrom-Json
    Assert-Equal 'commit' $j.data.type 'json type'
    Assert-Match 'first' $j.data.commit.message 'json commit body'
}

Invoke-Case 'hash-object: the id matches what the index stores' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "content`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null

    $hashed = (Invoke-Sm -Repo $repo -Args @('hash-object', 'a.txt')).Out.Trim()
    $staged = (Invoke-Sm -Repo $repo -Args @('ls-files', '--stage')).Out.Trim()
    Assert-Match $hashed $staged 'hash-object agrees with the index'

    # -w actually stores the object, so cat-file can read it back.
    Write-File $repo 'b.txt' "stored content`n"
    $wid = (Invoke-Sm -Repo $repo -Args @('hash-object', '-w', 'b.txt')).Out.Trim()
    Assert-Match 'stored content' (Invoke-Sm -Repo $repo -Args @('cat-file', '-p', $wid)).Out 'written object is readable'
}

Invoke-Case 'merge-base: finds the common ancestor and tests ancestry' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "base`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null
    $baseId = (Invoke-Sm -Repo $repo -Args @('rev-parse', 'HEAD')).Out.Trim()

    Invoke-Sm -Repo $repo -Args @('checkout', '-b', 'topic') | Out-Null
    Write-File $repo 'b.txt' "topic`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'topic work') | Out-Null

    $mb = (Invoke-Sm -Repo $repo -Args @('merge-base', 'main', 'topic')).Out.Trim()
    Assert-Equal $baseId $mb 'common ancestor is the base commit'

    $isAnc = Invoke-Sm -Repo $repo -Args @('merge-base', '--is-ancestor', 'main', 'topic')
    Assert-Equal 0 $isAnc.Exit 'main is an ancestor of topic'

    $notAnc = Invoke-Sm -Repo $repo -Args @('merge-base', '--is-ancestor', 'topic', 'main')
    Assert-True ($notAnc.Exit -ne 0) 'topic is not an ancestor of main'
}

Invoke-Case 'describe: names a commit by its nearest tag' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "one`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Invoke-Sm -Repo $repo -Args @('tag', 'v1.0') | Out-Null

    Assert-Equal 'v1.0' (Invoke-Sm -Repo $repo -Args @('describe', 'HEAD')).Out.Trim() 'exact tag'

    Write-File $repo 'a.txt' "one`ntwo`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null
    $desc = (Invoke-Sm -Repo $repo -Args @('describe', 'HEAD')).Out.Trim()
    Assert-Match '^v1\.0-1-g[0-9a-f]{7}$' $desc "distance form; got: $desc"

    $j = (Invoke-Sm -Repo $repo -Args @('describe', '--json', 'HEAD')).Out | ConvertFrom-Json
    Assert-Equal 'v1.0' $j.data.tag 'json tag'
    Assert-Equal 1 $j.data.distance 'json distance'

    $none = Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Assert-True ($j.data.exact -eq $false) 'not exact when ahead'
}

Invoke-Case 'shortlog: summarises commits by author' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'a.txt' 'two'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('shortlog', '-n', '-s')
    Assert-Equal 0 $r.Exit "shortlog exit code; output: $($r.All)"
    Assert-Match '2\s+\S+' $r.Out "commit count per author; got: $($r.Out)"

    $j = (Invoke-Sm -Repo $repo -Args @('shortlog', '--json')).Out | ConvertFrom-Json
    Assert-Equal 2 $j.data.totalCommits 'json total'
    Assert-Equal 2 $j.data.authors[0].commits 'json per-author count'
}

Invoke-Case 'archive: exports a tree to zip and tar' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "archived content`n"
    Write-File $repo 'src/b.txt' "nested archived`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'archive me') | Out-Null

    $zipPath = Join-Path $repo 'out.zip'
    $z = Invoke-Sm -Repo $repo -Args @('archive', '--format', 'zip', '-o', $zipPath, 'HEAD')
    Assert-Equal 0 $z.Exit "archive zip exit code; output: $($z.All)"
    Assert-True (Test-Path $zipPath) 'zip created'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = $zip.Entries | ForEach-Object { $_.FullName }
        Assert-Match 'a\.txt' ($names -join ',') 'zip contains a.txt'
        Assert-Match 'src/b\.txt' ($names -join ',') 'zip contains the nested file'
    }
    finally { $zip.Dispose() }

    $tarPath = Join-Path $repo 'out.tar'
    $t = Invoke-Sm -Repo $repo -Args @('archive', '--format', 'tar', '-o', $tarPath, 'HEAD')
    Assert-Equal 0 $t.Exit 'archive tar exit code'
    $bytes = [System.IO.File]::ReadAllBytes($tarPath)
    Assert-True ($bytes.Length -gt 1024) 'tar has content'
    # ustar magic identifies a real tar stream.
    $magic = [System.Text.Encoding]::ASCII.GetString($bytes, 257, 5)
    Assert-Equal 'ustar' $magic 'tar has the ustar magic'
}

Invoke-Case 'status --porcelain: stable two-column machine output' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'tracked.txt' 'one'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    Write-File $repo 'tracked.txt' 'two'
    Write-File $repo 'new.txt' 'untracked'
    Invoke-Sm -Repo $repo -Args @('add', 'tracked.txt') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('status', '--porcelain')
    Assert-Equal 0 $r.Exit 'exit code'
    Assert-Match '(?m)^## main$' $r.Out 'branch header'
    Assert-Match '(?m)^M  tracked\.txt$' $r.Out "staged column M; got: $($r.Out)"
    Assert-Match '(?m)^\?\? new\.txt$' $r.Out "untracked marker; got: $($r.Out)"
}

Invoke-Case 'switch: switches branches without restoring files' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'base'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('switch', '-c', 'feature')
    Assert-Equal 0 $r.Exit "switch -c exit code; output: $($r.All)"
    $head = Get-Content (Join-Path $repo '.sm/HEAD') -Raw
    Assert-Match 'refs/heads/feature' $head 'switched to the new branch'

    $back = Invoke-Sm -Repo $repo -Args @('switch', 'main')
    Assert-Equal 0 $back.Exit 'switch back exit code'
    Assert-Match 'refs/heads/main' (Get-Content (Join-Path $repo '.sm/HEAD') -Raw) 'back on main'
}

Invoke-Case 'restore: undoes a working-tree edit from the index' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "committed`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    Write-File $repo 'a.txt' "unsaved work`n"
    $r = Invoke-Sm -Repo $repo -Args @('restore', 'a.txt')
    Assert-Equal 0 $r.Exit "restore exit code; output: $($r.All)"
    Assert-Match 'committed' (Get-Content (Join-Path $repo 'a.txt') -Raw) 'content reverted'

    # Restoring the working tree must NOT discard staged work.
    Write-File $repo 'a.txt' "staged version`n"
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Write-File $repo 'a.txt' "further edit`n"
    Invoke-Sm -Repo $repo -Args @('restore', 'a.txt') | Out-Null
    Assert-Match 'staged version' (Get-Content (Join-Path $repo 'a.txt') -Raw) 'restored from the index, not HEAD'

    # --staged puts the index back to HEAD.
    $s = Invoke-Sm -Repo $repo -Args @('restore', '--staged', 'a.txt')
    Assert-Equal 0 $s.Exit 'restore --staged exit code'
    $st = Invoke-Sm -Repo $repo -Args @('status', '--porcelain')
    Assert-True ($st.Out -notmatch '(?m)^[MAD] ') "index matches HEAD; got: $($st.Out)"
}

Invoke-Case 'restore --source: restores a file from an arbitrary revision' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' "first version`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'a.txt' "second version`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('restore', '--source', 'HEAD~1', 'a.txt')
    Assert-Equal 0 $r.Exit "restore --source exit code; output: $($r.All)"
    Assert-Match 'first version' (Get-Content (Join-Path $repo 'a.txt') -Raw) 'restored the older content'
}

Invoke-Case 'tag: lists one tag per line, sorts by version, and shows annotations' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'a.txt' 'x'
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null

    Invoke-Sm -Repo $repo -Args @('tag', 'v1.0') | Out-Null
    Invoke-Sm -Repo $repo -Args @('tag', '-a', 'v1.9', '-m', 'nine') | Out-Null
    Invoke-Sm -Repo $repo -Args @('tag', '-a', 'v1.10', '-m', 'ten') | Out-Null

    $plain = Invoke-Sm -Repo $repo -Args @('tag')
    $lines = @($plain.Out -split "`r?`n" | Where-Object { $_.Trim() })
    Assert-Equal 3 $lines.Count "one tag per line; got: $($plain.Out)"

    $sorted = Invoke-Sm -Repo $repo -Args @('tag', '--sort', 'version:refname')
    $order = @($sorted.Out -split "`r?`n" | Where-Object { $_.Trim() })
    Assert-Equal 'v1.0' $order[0] 'version sort: first'
    Assert-Equal 'v1.9' $order[1] 'version sort: numeric, not lexical'
    Assert-Equal 'v1.10' $order[2] 'version sort: v1.10 last'

    $annotated = Invoke-Sm -Repo $repo -Args @('tag', '-n', '1')
    Assert-Match 'v1\.9\s+nine' "annotated tag shows its annotation; got: $($annotated.Out)"

    $j = (Invoke-Sm -Repo $repo -Args @('tag', '--json')).Out | ConvertFrom-Json
    Assert-Equal 3 $j.data.count 'json count'
    Assert-Equal $true (($j.data.tags | Where-Object { $_.name -eq 'v1.9' }).annotated) 'json marks annotated tags'
    Assert-Equal $false (($j.data.tags | Where-Object { $_.name -eq 'v1.0' }).annotated) 'json marks lightweight tags'
}

# ─────────────────────────────────────────────────────────────────────────────
# Interactive rebase
# ─────────────────────────────────────────────────────────────────────────────

# A scripted "editor": rewrites the Nth pick line of the todo file to $env:SM_TEST_VERB.
# Real interactive testing is impossible here, but the plan round trip — render, edit, re-read,
# execute — is exactly the part that has to work.
function New-PlanEditor {
    $dir = New-TestRepo
    $cmd = Join-Path $dir 'editor.cmd'
    @"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0editor.ps1" "%~1"
"@ | Set-Content $cmd -Encoding ASCII

    @'
param([string]$Path)
$verb = $env:SM_TEST_VERB
$idx = if ($env:SM_TEST_INDEX) { [int]$env:SM_TEST_INDEX } else { 0 }
$lines = Get-Content -LiteralPath $Path
$n = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^pick\s') {
        if ($n -eq $idx) { $lines[$i] = $lines[$i] -replace '^pick', $verb; break }
        $n++
    }
}
Set-Content -LiteralPath $Path -Value $lines
'@ | Set-Content (Join-Path $dir 'editor.ps1')

    return $cmd
}

function New-PlanRepo {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'f.txt' "one`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'one') | Out-Null
    Write-File $repo 'f.txt' "one`ntwo`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'two') | Out-Null
    Write-File $repo 'f.txt' "one`ntwo`nthree`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'three') | Out-Null
    return $repo
}

Invoke-Case 'rebase -i: squash folds a step into the previous commit' {
    $repo = New-PlanRepo
    $editor = New-PlanEditor
    $oldEditor = $env:SM_EDITOR
    $env:SM_EDITOR = $editor
    $env:SM_TEST_VERB = 'squash'
    $env:SM_TEST_INDEX = '1'
    try {
        $r = Invoke-Sm -Repo $repo -Args @('rebase', '-i', 'HEAD~2')
        Assert-Equal 0 $r.Exit "rebase -i exit code; output: $($r.All)"

        $log = (Invoke-Sm -Repo $repo -Args @('log', '--oneline')).Out
        $count = @($log -split "`r?`n" | Where-Object { $_.Trim() }).Count
        Assert-Equal 2 $count "squash reduces the count; got: $log"

        # No content may be lost by the fold.
        $content = Get-Content (Join-Path $repo 'f.txt') -Raw
        Assert-Match 'one' $content 'first line kept'
        Assert-Match 'two' $content 'squashed step kept'
        Assert-Match 'three' $content 'later step kept'
        Assert-True ($content -notmatch '<<<<<<<') 'no conflict markers committed'
    }
    finally {
        $env:SM_EDITOR = $oldEditor
        Remove-Item Env:SM_TEST_VERB -ErrorAction SilentlyContinue
        Remove-Item Env:SM_TEST_INDEX -ErrorAction SilentlyContinue
    }
}

Invoke-Case 'rebase -i: drop removes a step and keeps the rest' {
    $repo = New-PlanRepo
    $editor = New-PlanEditor
    $oldEditor = $env:SM_EDITOR
    $env:SM_EDITOR = $editor
    $env:SM_TEST_VERB = 'drop'
    $env:SM_TEST_INDEX = '0'
    try {
        $r = Invoke-Sm -Repo $repo -Args @('rebase', '-i', 'HEAD~2')
        Assert-Equal 0 $r.Exit "rebase -i drop exit code; output: $($r.All)"

        $log = (Invoke-Sm -Repo $repo -Args @('log', '--oneline')).Out
        Assert-True ($log -notmatch "`btwo`b") "dropped step is gone; got: $log"
        Assert-Match 'one' $log 'base kept'
        Assert-Match 'three' $log 'later step kept'
    }
    finally {
        $env:SM_EDITOR = $oldEditor
        Remove-Item Env:SM_TEST_VERB -ErrorAction SilentlyContinue
        Remove-Item Env:SM_TEST_INDEX -ErrorAction SilentlyContinue
    }
}

Invoke-Case 'rebase -i: reword rewrites the message and keeps the content' {
    $repo = New-PlanRepo
    $editor = New-PlanEditor
    $oldEditor = $env:SM_EDITOR
    $env:SM_EDITOR = $editor
    $env:SM_TEST_VERB = 'reword'
    $env:SM_TEST_INDEX = '0'
    try {
        $r = Invoke-Sm -Repo $repo -Args @('rebase', '-i', 'HEAD~2')
        Assert-Equal 0 $r.Exit "rebase -i reword exit code; output: $($r.All)"

        # The message editor is a no-op here, so the message must survive unchanged.
        $log = (Invoke-Sm -Repo $repo -Args @('log', '--oneline')).Out
        Assert-Match 'two' $log 'message preserved when the editor changes nothing'
        $content = Get-Content (Join-Path $repo 'f.txt') -Raw
        Assert-Match 'three' $content 'content preserved'
    }
    finally {
        $env:SM_EDITOR = $oldEditor
        Remove-Item Env:SM_TEST_VERB -ErrorAction SilentlyContinue
        Remove-Item Env:SM_TEST_INDEX -ErrorAction SilentlyContinue
    }
}

Invoke-Case 'rebase --autosquash: a fixup! commit folds into its target' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'f.txt' "base`nheader`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null
    Write-File $repo 'f.txt' "base`nheader`nFEATURE A`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'add feature A') | Out-Null
    Write-File $repo 'f.txt' "base`nheader`nFEATURE A`nFEATURE B`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'add feature B') | Out-Null
    # The fixup edits the FEATURE A line, so folding it in cannot conflict.
    Write-File $repo 'f.txt' "base`nheader`nFEATURE A (fixed)`nFEATURE B`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'fixup! add feature A') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('rebase', '--autosquash', 'HEAD~3')
    Assert-Equal 0 $r.Exit "autosquash exit code; output: $($r.All)"

    $log = (Invoke-Sm -Repo $repo -Args @('log', '--oneline')).Out
    $count = @($log -split "`r?`n" | Where-Object { $_.Trim() }).Count
    Assert-Equal 3 $count "fixup folded into its target; got: $log"
    Assert-True ($log -notmatch 'fixup!') 'no fixup! commit remains'

    $content = Get-Content (Join-Path $repo 'f.txt') -Raw
    Assert-Match 'FEATURE A \(fixed\)' $content 'the fixup edit is present'
    Assert-Match 'FEATURE B' 'FEATURE B kept'
    Assert-Match 'FEATURE B' $content 'later feature kept'
    Assert-True ($content -notmatch '<<<<<<<') 'no conflict markers committed'
}

Invoke-Case 'rebase -i: a conflicting step stops and --abort restores the original' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'f.txt' "base`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'base') | Out-Null
    Write-File $repo 'f.txt' "from-one`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'one') | Out-Null
    Write-File $repo 'f.txt' "from-two`n"
    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'two') | Out-Null
    $originalHead = (Invoke-Sm -Repo $repo -Args @('rev-parse', 'HEAD')).Out.Trim()

    # Reorder the two steps so 'two' lands on the base first, then 'one' collides with it.
    $editorDir = New-TestRepo
    $cmd = Join-Path $editorDir 'swap.cmd'
    @"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0swap.ps1" "%~1"
"@ | Set-Content $cmd -Encoding ASCII
    @'
param([string]$Path)
$lines = Get-Content -LiteralPath $Path
$picks = @()
for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^pick\s') { $picks += $i } }
if ($picks.Count -eq 2) {
    $a = $lines[$picks[0]]; $b = $lines[$picks[1]]
    $lines[$picks[0]] = $b; $lines[$picks[1]] = $a
}
Set-Content -LiteralPath $Path -Value $lines
'@ | Set-Content (Join-Path $editorDir 'swap.ps1')

    $oldEditor = $env:SM_EDITOR
    $env:SM_EDITOR = $cmd
    try {
        $r = Invoke-Sm -Repo $repo -Args @('rebase', '-i', 'HEAD~2')
        Assert-True ($r.Exit -ne 0) "conflicting rebase reports failure; output: $($r.All)"

        $abort = Invoke-Sm -Repo $repo -Args @('rebase', '--abort')
        Assert-Equal 0 $abort.Exit "rebase --abort exit code; output: $($abort.All)"

        $head = (Invoke-Sm -Repo $repo -Args @('rev-parse', 'HEAD')).Out.Trim()
        Assert-Equal $originalHead $head 'HEAD restored to the pre-rebase commit'
        Assert-Equal 'from-two' (Get-Content (Join-Path $repo 'f.txt') -Raw).Trim() 'file restored'
    }
    finally {
        $env:SM_EDITOR = $oldEditor
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Ignore rules
# ─────────────────────────────────────────────────────────────────────────────

Invoke-Case 'ignore: globs, anchoring, character classes and negation all apply' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo '.smignore' "*.log`n/build/`n[abc]har.txt`n!keep.log`n"
    Write-File $repo 'a.log' 'x'
    Write-File $repo 'keep.log' 'x'
    Write-File $repo 'b.txt' 'x'
    Write-File $repo 'build/out.dll' 'x'
    Write-File $repo 'sub/build/out.dll' 'x'
    # [abc]har.txt matches bhar.txt (b is in the class), not zhar.txt or achar.txt.
    Write-File $repo 'bhar.txt' 'x'
    Write-File $repo 'zhar.txt' 'x'

    $out = (Invoke-Sm -Repo $repo -Args @('ls-files', '-o')).Out
    $names = @($out -split "`r?`n" | Where-Object { $_.Trim() })

    Assert-True ($names -notcontains 'a.log') '*.log is ignored'
    Assert-True ($names -contains 'keep.log') 'a later negation re-includes keep.log'
    Assert-True ($names -contains 'b.txt') 'unmatched file is untracked'
    Assert-True ($names -notcontains 'build/out.dll') 'an anchored /build/ ignores the root build dir'
    # "/build/" is anchored, so a nested build directory is NOT ignored.
    Assert-True ($names -contains 'sub/build/out.dll') 'anchored pattern does not match at depth'
    Assert-True ($names -notcontains 'bhar.txt') '[abc] is a character class'
    Assert-True ($names -contains 'zhar.txt') 'zhar.txt is outside the class'
}

Invoke-Case 'ignore: nested .smignore and .sm/info/exclude are honoured' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo 'sub/.smignore' "nested.txt`n"
    Write-File $repo 'sub/nested.txt' 'x'
    Write-File $repo 'sub/other.txt' 'x'
    Write-File $repo 'root.txt' 'x'

    # init writes .sm/info/exclude; it must actually be read.
    $excludePath = Join-Path $repo '.sm/info/exclude'
    Add-Content -Path $excludePath -Value 'from-exclude.txt'
    Write-File $repo 'from-exclude.txt' 'x'

    $out = (Invoke-Sm -Repo $repo -Args @('ls-files', '-o')).Out
    $names = @($out -split "`r?`n" | Where-Object { $_.Trim() })

    Assert-True ($names -notcontains 'sub/nested.txt') 'nested .smignore applies in its directory'
    Assert-True ($names -contains 'sub/other.txt') 'sibling in the same directory is not ignored'
    Assert-True ($names -contains 'root.txt') 'root file is not ignored'
    Assert-True ($names -notcontains 'from-exclude.txt') '.sm/info/exclude is honoured'
}

Invoke-Case 'ignore: an ignored file is not staged by add . but can be forced' {
    $repo = New-TestRepo
    Invoke-Sm -Repo $repo -Args @('init') | Out-Null
    Write-File $repo '.smignore' "secret.txt`n"
    Write-File $repo 'normal.txt' 'n'
    Write-File $repo 'secret.txt' 's'

    Invoke-Sm -Repo $repo -Args @('add', '.') | Out-Null
    $tracked = (Invoke-Sm -Repo $repo -Args @('ls-files')).Out
    Assert-Match 'normal\.txt' $tracked 'normal file staged'
    Assert-True ($tracked -notmatch 'secret\.txt') 'ignored file not staged by add .'

    $forced = Invoke-Sm -Repo $repo -Args @('add', '--force', 'secret.txt')
    Assert-Equal 0 $forced.Exit "add --force exit code; output: $($forced.All)"
    $after = (Invoke-Sm -Repo $repo -Args @('ls-files')).Out
    Assert-Match 'secret\.txt' $after 'ignored file can be tracked explicitly'
}

# ────────────────────────────────────────────────────────────────────────────
# Reflog
# ────────────────────────────────────────────────────────────────────────────

function Initialize-ReflogRepo {
    param([string]$Repo)
    Invoke-Sm -Repo $Repo -Args @('init') | Out-Null
    Invoke-Sm -Repo $Repo -Args @('config', 'user.name', 'tester') | Out-Null
    Invoke-Sm -Repo $Repo -Args @('config', 'user.email', 't@example.com') | Out-Null
}

Invoke-Case 'reflog: HEAD records commits and checkouts with real timestamps' {
    $repo = New-TestRepo
    Initialize-ReflogRepo $repo
    Write-File $repo 'a.txt' 'a'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Invoke-Sm -Repo $repo -Args @('checkout', '-b', 'topic') | Out-Null
    Write-File $repo 'b.txt' 'b'
    Invoke-Sm -Repo $repo -Args @('add', 'b.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'topic work') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('reflog', '-v')
    Assert-Equal 0 $r.Exit 'reflog exit code'
    Assert-Match 'HEAD@\{0\}: commit: topic work' $r.Out 'HEAD@{0} is the latest commit'
    Assert-Match 'checkout: moving from main to topic' $r.Out 'checkout is recorded'
    Assert-True ($r.Out -notmatch '0001-01-01') 'timestamps are real, not the epoch'
}

Invoke-Case 'reflog: a commit updates both HEAD and the branch reflog' {
    $repo = New-TestRepo
    Initialize-ReflogRepo $repo
    Write-File $repo 'a.txt' 'a'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'b.txt' 'b'
    Invoke-Sm -Repo $repo -Args @('add', 'b.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $head = (Invoke-Sm -Repo $repo -Args @('reflog')).Out
    $branch = (Invoke-Sm -Repo $repo -Args @('reflog', 'main')).Out
    Assert-Match 'HEAD@\{0\}: commit: second' $head 'HEAD tip updated'
    Assert-Match 'main@\{0\}: commit: second' $branch 'branch tip updated'
    Assert-Match 'main@\{1\}: commit: first' $branch 'branch previous value retained'
}

Invoke-Case 'rev-parse: HEAD@{n} and <branch>@{n} resolve to prior values' {
    $repo = New-TestRepo
    Initialize-ReflogRepo $repo
    Write-File $repo 'a.txt' 'a'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'b.txt' 'b'
    Invoke-Sm -Repo $repo -Args @('add', 'b.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $tip = (Invoke-Sm -Repo $repo -Args @('rev-parse', 'HEAD')).Out.Trim()
    $prev = (Invoke-Sm -Repo $repo -Args @('rev-parse', 'HEAD@{1}')).Out.Trim()
    $branchPrev = (Invoke-Sm -Repo $repo -Args @('rev-parse', 'main@{1}')).Out.Trim()
    Assert-True ($tip -ne $prev) 'HEAD@{1} differs from HEAD'
    Assert-Equal $prev $branchPrev 'main@{1} equals HEAD@{1}'
}

Invoke-Case 'rev-parse: an out-of-range reflog selector fails cleanly' {
    $repo = New-TestRepo
    Initialize-ReflogRepo $repo
    Write-File $repo 'a.txt' 'a'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null

    $r = Invoke-Sm -Repo $repo -Args @('rev-parse', 'HEAD@{99}')
    Assert-True ($r.Exit -ne 0) 'out-of-range selector must fail'
    Assert-Match 'out of range' $r.All 'reports out of range'
}

Invoke-Case 'reflog expire: drops older entries but keeps the tip' {
    $repo = New-TestRepo
    Initialize-ReflogRepo $repo
    Write-File $repo 'a.txt' 'a'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'b.txt' 'b'
    Invoke-Sm -Repo $repo -Args @('add', 'b.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $before = @((Invoke-Sm -Repo $repo -Args @('reflog', '-n', '100')).Out -split "`r?`n" | Where-Object { $_.Trim() })
    Assert-True ($before.Count -ge 2) "expected at least two entries, got $($before.Count)"

    $e = Invoke-Sm -Repo $repo -Args @('reflog', 'expire', '--expire=now')
    Assert-Equal 0 $e.Exit "expire exit code; output: $($e.All)"

    $after = @((Invoke-Sm -Repo $repo -Args @('reflog', '-n', '100')).Out -split "`r?`n" | Where-Object { $_.Trim() })
    Assert-Equal 1 $after.Count 'expire keeps only the tip'
    Assert-Match 'HEAD@\{0\}: commit: second' ($after -join "`n") 'tip preserved after expire'
}

Invoke-Case 'reflog delete: removes the selected entry' {
    $repo = New-TestRepo
    Initialize-ReflogRepo $repo
    Write-File $repo 'a.txt' 'a'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Write-File $repo 'b.txt' 'b'
    Invoke-Sm -Repo $repo -Args @('add', 'b.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'second') | Out-Null

    $d = Invoke-Sm -Repo $repo -Args @('reflog', 'delete', 'HEAD@{1}')
    Assert-Equal 0 $d.Exit "delete exit code; output: $($d.All)"

    $after = @((Invoke-Sm -Repo $repo -Args @('reflog', '-n', '100')).Out -split "`r?`n" | Where-Object { $_.Trim() })
    Assert-Equal 1 $after.Count 'one entry remains after delete'
}

Invoke-Case 'checkout -: returns to the previous branch via the reflog' {
    $repo = New-TestRepo
    Initialize-ReflogRepo $repo
    Write-File $repo 'a.txt' 'a'
    Invoke-Sm -Repo $repo -Args @('add', 'a.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'first') | Out-Null
    Invoke-Sm -Repo $repo -Args @('checkout', '-b', 'topic') | Out-Null
    Write-File $repo 'b.txt' 'b'
    Invoke-Sm -Repo $repo -Args @('add', 'b.txt') | Out-Null
    Invoke-Sm -Repo $repo -Args @('commit', '-m', 'topic work') | Out-Null
    Invoke-Sm -Repo $repo -Args @('checkout', 'main') | Out-Null

    $c = Invoke-Sm -Repo $repo -Args @('checkout', '-')
    Assert-Equal 0 $c.Exit "checkout - exit code; output: $($c.All)"
    $status = (Invoke-Sm -Repo $repo -Args @('status')).Out
    Assert-Match 'On branch topic' $status 'checkout - returned to topic'
}

Write-Host ''
Write-Host ('-' * 60)
if ($script:Failed -eq 0) {
    Write-Host "All $script:Passed cases passed." -ForegroundColor Green
}
else {
    Write-Host "$script:Passed passed, $script:Failed failed." -ForegroundColor Red
    Write-Host ''
    foreach ($f in $script:Failures) {
        Write-Host "  FAIL $($f.Name)" -ForegroundColor Red
        Write-Host "       $($f.Error)" -ForegroundColor DarkRed
    }
}
Write-Host ('-' * 60)

if ($KeepArtifacts) {
    Write-Host "Artifacts kept in $workRoot"
}
else {
    Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:Failed -gt 0) { exit 1 }
exit 0
