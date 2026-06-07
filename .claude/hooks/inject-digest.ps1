#requires -Version 5.1
<#
  SessionStart hook — injects docs/BIBLE.digest.md as authoritative context.
  Emits Claude Code hook JSON on stdout. If the digest is missing/empty, emits {}.
  Non-ASCII is escaped to \uXXXX so the JSON is safe under Windows PowerShell 5.1 / Win-1252.
#>
$ErrorActionPreference = 'Stop'

try {
  $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
  $digestPath = Join-Path $repoRoot 'docs\BIBLE.digest.md'

  if (-not (Test-Path $digestPath)) { Write-Output '{}'; exit 0 }
  $digest = [System.IO.File]::ReadAllText($digestPath)
  if ([string]::IsNullOrWhiteSpace($digest)) { Write-Output '{}'; exit 0 }

  $preamble = @"
The following is the AUTHORITATIVE Cursory (CUR) project digest, generated from docs/BIBLE.md.
It is the source of truth for what Cursory is, its laws, and its verified state. When it conflicts
with stale comments or older docs, the digest (and the full docs/BIBLE.md) wins. Full detail and
stable anchors live in docs/BIBLE.md, docs/USER_STORIES.md, and docs/AMENDMENTS.md.

"@
  $context = $preamble + $digest

  # Build JSON manually and escape every non-ASCII char to \uXXXX (5.1-safe; no -EscapeHandling).
  $sb = New-Object System.Text.StringBuilder
  foreach ($ch in $context.ToCharArray()) {
    $code = [int]$ch
    switch ($ch) {
      '"'  { [void]$sb.Append('\"') }
      '\'  { [void]$sb.Append('\\') }
      "`b" { [void]$sb.Append('\b') }
      "`f" { [void]$sb.Append('\f') }
      "`n" { [void]$sb.Append('\n') }
      "`r" { [void]$sb.Append('\r') }
      "`t" { [void]$sb.Append('\t') }
      default {
        if ($code -lt 0x20 -or $code -gt 0x7E) {
          [void]$sb.Append('\u' + $code.ToString('x4'))
        } else {
          [void]$sb.Append($ch)
        }
      }
    }
  }
  $escaped = $sb.ToString()

  $json = '{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"' + $escaped + '"}}'
  Write-Output $json
}
catch {
  # Never block session start on a hook error.
  Write-Output '{}'
}
