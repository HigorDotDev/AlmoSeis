<#
.SYNOPSIS
  Gera uma migration SQL a partir dos blocos ```sql de docs/05-ddl.md (ADR-0042).

.DESCRIPTION
  O documento 05 é a fonte de verdade do schema.
  - 0001_InitialSchema.sql: todos os blocos ```sql ANTES da seção "## 5.17 Alterações", exceto o bloco de papéis
    (§5.1), que é infraestrutura executada por um administrador e não pela migration.
  - NNNN_Nome.sql (alterações): os blocos ```sql da subseção "### NNNN_Nome ..." da seção de alterações.
  O teste DdlMigrationSyncTests falha se algum arquivo gerado divergir do documento.

.EXAMPLE
  pwsh backend/tools/Sync-DdlMigration.ps1 -Target 0001_InitialSchema.sql
  pwsh backend/tools/Sync-DdlMigration.ps1 -Target 0002_SharedDeviceWebPlatform.sql
#>
param(
  [string]$Target = '0001_InitialSchema.sql'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$doc = Join-Path $repo 'docs\05-ddl.md'
$out = Join-Path $repo "backend\src\AlmoSeis.Infrastructure\Persistence\Migrations\Sql\$Target"

$md = [IO.File]::ReadAllText($doc) -replace "`r`n", "`n"
$alterationsAt = $md.IndexOf("`n## 5.17 ")  # "## 5.17 Alterações" (só ASCII: o Windows PowerShell lê o script como ANSI)
$initial = if ($alterationsAt -ge 0) { $md.Substring(0, $alterationsAt) } else { $md }

if ($Target -like '0001_*') {
  $source = $initial
} else {
  $name = [IO.Path]::GetFileNameWithoutExtension($Target)
  $match = [regex]::Match($md.Substring($alterationsAt), "(?s)\n### $([regex]::Escape($name))\b(.*?)(?=\n### |\n## |\z)")
  if (-not $match.Success) { throw "Subsecao '### $name' nao encontrada em 05, secao 5.17." }
  $source = $match.Groups[1].Value
}

$blocks = [regex]::Matches($source, '(?s)```sql\n(.*?)```') |
  ForEach-Object { $_.Groups[1].Value } |
  Where-Object { $_ -notmatch 'CREATE ROLE' }

# Strings fora do ASCII so por escape: o Windows PowerShell 5.1 le este .ps1 (sem BOM) como ANSI e gravava o cabecalho com mojibake.
$a = [char]0x00C3; $aGrave = [char]0x00C0
$header = "-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. N${a}O EDITE ${aGrave} M${a}O.`n"
[IO.File]::WriteAllText($out, $header + ($blocks -join "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Gerado $out ($($blocks.Count) blocos)"
