<#
.SYNOPSIS
  Gera a migration SQL a partir dos blocos ```sql de docs/05-ddl.md (ADR-0042).

.DESCRIPTION
  O documento 05 é a fonte de verdade do schema. Este script concatena, na ordem, todos os blocos
  ```sql do documento, exceto o bloco de papéis (§5.1), que é infraestrutura executada por um
  administrador e não pela migration. O teste DdlMigrationSyncTests falha se o arquivo gerado
  divergir do documento.

.EXAMPLE
  pwsh backend/tools/Sync-DdlMigration.ps1 -Target 0001_InitialSchema.sql
#>
param(
  [string]$Target = '0001_InitialSchema.sql'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$doc = Join-Path $repo 'docs\05-ddl.md'
$out = Join-Path $repo "backend\src\AlmoSeis.Infrastructure\Persistence\Migrations\Sql\$Target"

$md = [IO.File]::ReadAllText($doc) -replace "`r`n", "`n"
$blocks = [regex]::Matches($md, '(?s)```sql\n(.*?)```') |
  ForEach-Object { $_.Groups[1].Value } |
  Where-Object { $_ -notmatch 'CREATE ROLE' }

$header = "-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.`n"
[IO.File]::WriteAllText($out, $header + ($blocks -join "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Gerado $out ($($blocks.Count) blocos)"
