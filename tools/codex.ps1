#requires -Version 5.1
<#
.SYNOPSIS
  Codex documentation tooling for Cursory (CUR). Subcommands: doctor, digest.
.DESCRIPTION
  doctor  validates the docs/ canon (front-matter, unique anchors, resolvable cross-refs,
          JSON data + schemas, story test tokens, cited code paths, generatedFrom freshness)
          and warns if the digest is stale. Exits non-zero on any hard error.
  digest  regenerates docs/BIBLE.digest.md from BIBLE.md (sections 1, 3, 5, 9) + a status
          index + the latest amendment head.
  No build step. Pure ASCII source for Windows PowerShell 5.1 (Win-1252) safety.
#>
[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [ValidateSet('doctor', 'digest')]
  [string]$Command = 'doctor'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$DocsDir  = Join-Path $RepoRoot 'docs'
$BiblePath = Join-Path $DocsDir 'BIBLE.md'
$DigestPath = Join-Path $DocsDir 'BIBLE.digest.md'
$AmendmentsPath = Join-Path $DocsDir 'AMENDMENTS.md'
$StoriesPath = Join-Path $DocsDir 'USER_STORIES.md'
$DataDir = Join-Path $DocsDir 'data'

# Non-ASCII glyphs, built from code points so the source stays pure ASCII.
# NOTE: PowerShell variables are case-insensitive, so these names must NOT collide with the
# count variables ($done/$partial/$planned/$cut) used later in the digest.
$GlyphSection = [char]0x00A7                            # section sign
$GlyphDone    = [char]0x2705                            # check-mark button
$GlyphPartial = [System.Char]::ConvertFromUtf32(0x1F7E1)   # yellow circle
$GlyphPlanned = [char]0x2B1C                            # white large square
$GlyphCut     = [System.Char]::ConvertFromUtf32(0x1F5D1)   # wastebasket
# Regex char class fragment matching ASCII word chars, the section sign, and a dash.
$IDCLASS = '[A-Za-z0-9' + $GlyphSection + '\-]+'

# --- helpers -----------------------------------------------------------------
function Read-Text([string]$Path) { [System.IO.File]::ReadAllText($Path) }

function Get-FrontMatter([string]$Text) {
  if ($Text -notmatch "^---\r?\n") { return $null }
  $lines = $Text -split "\r?\n"
  if ($lines[0].Trim() -ne '---') { return $null }
  $map = @{}
  for ($i = 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i].Trim() -eq '---') { return $map }
    if ($lines[$i] -match '^\s*([A-Za-z0-9_]+)\s*:\s*(.*)$') {
      $map[$Matches[1]] = $Matches[2].Trim()
    }
  }
  return $null
}

function Get-CanonFiles {
  $files = @()
  foreach ($p in @($BiblePath, $StoriesPath, $AmendmentsPath)) {
    if (Test-Path $p) { $files += [pscustomobject]@{ Path = $p; Kind = 'core' } }
  }
  $rfcDir = Join-Path $DocsDir 'rfc'
  if (Test-Path $rfcDir) {
    Get-ChildItem -Path $rfcDir -Filter '*.md' -File | ForEach-Object {
      $files += [pscustomobject]@{ Path = $_.FullName; Kind = 'rfc' }
    }
  }
  if (Test-Path $DataDir) {
    Get-ChildItem -Path $DataDir -Filter '*.json' -File -Recurse |
      Where-Object { $_.FullName -notmatch '[\\/]_schema[\\/]' } |
      ForEach-Object { $files += [pscustomobject]@{ Path = $_.FullName; Kind = 'data' } }
  }
  return $files
}

# --- doctor ------------------------------------------------------------------
function Invoke-Doctor {
  $errors = New-Object System.Collections.Generic.List[string]
  $warnings = New-Object System.Collections.Generic.List[string]
  $checks = New-Object System.Collections.Generic.List[string]

  if (-not (Test-Path $BiblePath)) { $errors.Add("missing docs/BIBLE.md") }

  # 1. Front-matter on every canon file; data files must be valid JSON.
  $canon = Get-CanonFiles
  foreach ($f in $canon) {
    $text = Read-Text $f.Path
    $rel = $f.Path.Substring($RepoRoot.Length + 1)
    if ($f.Kind -eq 'data') {
      try { $null = $text | ConvertFrom-Json } catch { $errors.Add("invalid JSON: $rel") }
      continue
    }
    $fm = Get-FrontMatter $text
    if ($null -eq $fm) { $errors.Add("missing/malformed front-matter: $rel"); continue }
    foreach ($key in @('codex', 'project', 'code', 'layer', 'status', 'updated')) {
      if (-not $fm.ContainsKey($key)) { $errors.Add("front-matter missing '$key': $rel") }
    }
    if ($fm['code'] -and $fm['code'] -ne 'CUR') { $errors.Add("front-matter code != CUR: $rel ($($fm['code']))") }
  }
  $checks.Add("front-matter: $($canon.Count) canon file(s) checked")

  # 2. Anchors unique + cross-refs resolve across docs/*.md (excluding the digest).
  $mdFiles = Get-ChildItem -Path $DocsDir -Filter '*.md' -File -Recurse | Where-Object { $_.Name -ne 'BIBLE.digest.md' }
  $anchors = @{}
  $refs = New-Object System.Collections.Generic.List[object]
  $anchorPattern = '\{#(' + $IDCLASS + ')\}'
  $refPattern = '\]\([^)]*#(' + $IDCLASS + ')\)'
  foreach ($md in $mdFiles) {
    $text = Read-Text $md.FullName
    $rel = $md.FullName.Substring($RepoRoot.Length + 1)
    foreach ($m in [regex]::Matches($text, $anchorPattern)) {
      $id = $m.Groups[1].Value
      if ($anchors.ContainsKey($id)) { $errors.Add("duplicate anchor {#$id}: $rel and $($anchors[$id])") }
      else { $anchors[$id] = $rel }
    }
    foreach ($m in [regex]::Matches($text, $refPattern)) {
      $refs.Add([pscustomobject]@{ Id = $m.Groups[1].Value; In = $rel })
    }
  }
  # House-rule anchors live in the shared file outside the repo: HOUSE-* are external/known.
  foreach ($r in $refs) {
    if ($r.Id -like 'HOUSE-*') { continue }
    if (-not $anchors.ContainsKey($r.Id)) { $errors.Add("dangling cross-ref to {#$($r.Id)} in $($r.In)") }
  }
  $checks.Add("anchors: $($anchors.Count) unique, $($refs.Count) cross-ref(s) resolved")

  # 3. JSON data validates; entity ids unique. (None today -> skip.)
  if (Test-Path $DataDir) {
    $dataFiles = Get-ChildItem -Path $DataDir -Filter '*.json' -File -Recurse |
      Where-Object { $_.FullName -notmatch '[\\/]_schema[\\/]' }
    $ids = @{}
    foreach ($d in $dataFiles) {
      try { $json = (Read-Text $d.FullName) | ConvertFrom-Json } catch { continue }
      $entities = if ($json -is [array]) { $json } else { @($json) }
      foreach ($e in $entities) {
        if ($e.PSObject.Properties.Name -contains 'id') {
          if ($ids.ContainsKey($e.id)) { $errors.Add("duplicate data id '$($e.id)'") } else { $ids[$e.id] = $true }
        }
      }
    }
    $checks.Add("data: $($dataFiles.Count) file(s), $($ids.Count) entity id(s)")
  } else {
    $checks.Add("data: none (no docs/data - domain has no tabular canon)")
  }

  # 4. Every done-story names a test token that exists in the test tree.
  if (Test-Path $StoriesPath) {
    $storyText = Read-Text $StoriesPath
    $testFiles = @()
    $testRoot = Join-Path $RepoRoot 'Cursory.Tests'
    if (Test-Path $testRoot) { $testFiles = Get-ChildItem -Path $testRoot -Filter '*.cs' -File -Recurse | ForEach-Object { Read-Text $_.FullName } }
    $allTestSrc = ($testFiles -join "`n")
    $storyLines = $storyText -split "\r?\n"
    # A story is a bullet "- **CUR-US-Xn <status>** ..." possibly continued on following
    # indented/continuation lines until the next bullet or blank line. Collect each story's
    # full block so a citation on the next line still counts.
    $checkedStories = 0; $missingTests = 0
    $blocks = New-Object System.Collections.Generic.List[string]
    $cur = $null
    foreach ($line in $storyLines) {
      if ($line -match '^\s*-\s+\*\*CUR-US-') {
        if ($null -ne $cur) { $blocks.Add($cur) }
        $cur = $line
      } elseif ($null -ne $cur) {
        if ($line.Trim() -eq '' -or $line -match '^#{1,6}\s' -or $line -match '^\s*-\s') {
          $blocks.Add($cur); $cur = $null
          if ($line -match '^\s*-\s') { } # a non-story bullet ends the block
        } else {
          $cur = $cur + " " + $line.Trim()
        }
      }
    }
    if ($null -ne $cur) { $blocks.Add($cur) }

    foreach ($block in $blocks) {
      $isDone = $block -match ('\*\*CUR-US-[A-Za-z0-9]+\s+' + $GlyphDone + '\*\*')
      if (-not $isDone) { continue }
      $checkedStories++
      $tokens = [regex]::Matches($block, 'verified by `([^`]+)`')
      if ($tokens.Count -eq 0) {
        $snippet = $block.Trim()
        $errors.Add("done-story without a test token: " + $snippet.Substring(0, [Math]::Min(60, $snippet.Length)))
        continue
      }
      foreach ($t in $tokens) {
        foreach ($name in ($t.Groups[1].Value -split '[`,]\s*' | Where-Object { $_ -match '\w' })) {
          $clean = ($name -replace '[^A-Za-z0-9_]', '')
          if ($clean -and $allTestSrc -notmatch [regex]::Escape($clean)) {
            $warnings.Add("story test token not found in test tree: $clean")
            $missingTests++
          }
        }
      }
    }
    $checks.Add("stories: $checkedStories done-story block(s); $missingTests token(s) unmatched")
  }

  # 5. Every code path/file cited in the bible exists on disk.
  if (Test-Path $BiblePath) {
    $bible = Read-Text $BiblePath
    $citedMissing = 0; $citedTotal = 0
    foreach ($m in [regex]::Matches($bible, '`([A-Za-z0-9_./\\]+\.(?:cs|csproj|slnx|razor|js|json|props|yml|md))`')) {
      $path = $m.Groups[1].Value
      if ($path -like '*..*') { continue }
      $citedTotal++
      $full = Join-Path $RepoRoot ($path -replace '/', '\')
      if (Test-Path $full) { continue }
      # Fallback: a bare filename cited in prose (e.g. `Program.cs`) resolves if it exists
      # anywhere in the tree (outside bin/obj). Full paths must resolve exactly.
      $hasDir = ($path -match '[/\\]')
      $leaf = Split-Path $path -Leaf
      $found = $false
      if (-not $hasDir) {
        $hit = Get-ChildItem -Path $RepoRoot -Filter $leaf -File -Recurse -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Select-Object -First 1
        if ($hit) { $found = $true }
      }
      if (-not $found) { $errors.Add("bible cites a missing path: $path"); $citedMissing++ }
    }
    $checks.Add("cited paths: $citedTotal checked, $citedMissing missing")
  }

  # 6. Digest freshness (source mtime <= artifact mtime).
  if (Test-Path $DigestPath) {
    $bibleMtime = (Get-Item $BiblePath).LastWriteTimeUtc
    $digestMtime = (Get-Item $DigestPath).LastWriteTimeUtc
    if ($bibleMtime -gt $digestMtime) { $warnings.Add("BIBLE.digest.md is older than BIBLE.md - run: codex.ps1 digest") }
    $checks.Add("digest: present")
  } else {
    $warnings.Add("BIBLE.digest.md missing - run: codex.ps1 digest")
  }

  Write-Host "Codex doctor - Cursory (CUR)" -ForegroundColor Cyan
  foreach ($c in $checks)   { Write-Host "  [check] $c" }
  foreach ($w in $warnings) { Write-Host "  [warn]  $w" -ForegroundColor Yellow }
  foreach ($e in $errors)   { Write-Host "  [ERROR] $e" -ForegroundColor Red }
  if ($errors.Count -gt 0) {
    Write-Host ("doctor: FAIL (" + $errors.Count + " error(s), " + $warnings.Count + " warning(s))") -ForegroundColor Red
    exit 1
  }
  Write-Host ("doctor: PASS (" + $warnings.Count + " warning(s))") -ForegroundColor Green
}

# --- digest ------------------------------------------------------------------
function Get-Section([string]$Text, [string]$HeaderRegex) {
  $lines = $Text -split "\r?\n"
  $start = -1
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $HeaderRegex) { $start = $i; break }
  }
  if ($start -lt 0) { return $null }
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine($lines[$start])
  for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') { break }
    [void]$sb.AppendLine($lines[$i])
  }
  return $sb.ToString().TrimEnd()
}

function Invoke-Digest {
  if (-not (Test-Path $BiblePath)) { throw "missing docs/BIBLE.md" }
  $bible = Read-Text $BiblePath

  $s1 = Get-Section $bible '^##\s+1\.'
  $s3 = Get-Section $bible '^##\s+3\.'
  $s5 = Get-Section $bible '^##\s+5\.'
  $s9 = Get-Section $bible '^##\s+9\.'

  $done = 0; $partial = 0; $planned = 0; $cut = 0
  if (Test-Path $StoriesPath) {
    $st = Read-Text $StoriesPath
    $done    = ([regex]::Matches($st, [string]$GlyphDone)).Count
    $partial = ([regex]::Matches($st, [string]$GlyphPartial)).Count
    $planned = ([regex]::Matches($st, [string]$GlyphPlanned)).Count
    $cut     = ([regex]::Matches($st, [string]$GlyphCut)).Count
  }

  $amendHead = ''
  if (Test-Path $AmendmentsPath) {
    $am = Read-Text $AmendmentsPath
    $lines = $am -split "\r?\n"
    $heads = @()
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^##\s+CUR-A\d+') { $heads += $i } }
    if ($heads.Count -gt 0) {
      $last = $heads[-1]
      $sb = New-Object System.Text.StringBuilder
      [void]$sb.AppendLine($lines[$last])
      for ($i = $last + 1; $i -lt $lines.Count -and $i -lt $last + 6; $i++) {
        if ($lines[$i] -match '^##\s') { break }
        [void]$sb.AppendLine($lines[$i])
      }
      $amendHead = $sb.ToString().TrimEnd()
    }
  }

  $today = (Get-Date).ToString('yyyy-MM-dd')
  $statusLine = "Status index (USER_STORIES.md): $GlyphDone $done | $GlyphPartial $partial | $GlyphPlanned $planned | $GlyphCut $cut"
  $genFrom = "<!-- generatedFrom: CUR-${GlyphSection}1,CUR-${GlyphSection}3,CUR-${GlyphSection}5,CUR-${GlyphSection}9 + USER_STORIES status + latest amendment. Generated by tools/codex.ps1 digest on $today. Do not hand-edit. -->"

  $out = New-Object System.Text.StringBuilder
  [void]$out.AppendLine("AUTHORITATIVE - full detail in docs/BIBLE.md")
  [void]$out.AppendLine($genFrom)
  [void]$out.AppendLine("")
  [void]$out.AppendLine("# Cursory (CUR) - Bible digest")
  [void]$out.AppendLine("")
  [void]$out.AppendLine($statusLine)
  [void]$out.AppendLine("")
  if ($s1) { [void]$out.AppendLine($s1); [void]$out.AppendLine("") }
  if ($s3) { [void]$out.AppendLine($s3); [void]$out.AppendLine("") }
  if ($s5) { [void]$out.AppendLine($s5); [void]$out.AppendLine("") }
  if ($s9) { [void]$out.AppendLine($s9); [void]$out.AppendLine("") }
  if ($amendHead) {
    [void]$out.AppendLine("## Latest amendment")
    [void]$out.AppendLine($amendHead)
    [void]$out.AppendLine("")
  }

  [System.IO.File]::WriteAllText($DigestPath, $out.ToString(), (New-Object System.Text.UTF8Encoding($false)))
  Write-Host ("digest: wrote docs/BIBLE.digest.md (" + [Math]::Round((Get-Item $DigestPath).Length / 1KB, 1) + " KB)") -ForegroundColor Green
}

switch ($Command) {
  'doctor' { Invoke-Doctor }
  'digest' { Invoke-Digest }
}
