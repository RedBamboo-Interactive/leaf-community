[CmdletBinding()]
param([string]$OutputDir = "$PSScriptRoot\dist", [string]$LeafSdkProject, [switch]$SkipFrontend)
$ErrorActionPreference = "Stop"
$manifest = Get-Content (Join-Path $PSScriptRoot "plugin.json") -Raw | ConvertFrom-Json
$staging = Join-Path ([IO.Path]::GetTempPath()) "leafpkg-$($manifest.id)-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $staging, $OutputDir | Out-Null
try {
  Copy-Item (Join-Path $PSScriptRoot "plugin.json") $staging
  $backend = Join-Path $staging "backend"
  $args = @("publish", (Join-Path $PSScriptRoot "src\Leaf.Plugins.LeafCommons\Leaf.Plugins.LeafCommons.csproj"), "-c", "Release", "-o", $backend, "--nologo", "-v", "quiet")
  if ($LeafSdkProject) { $args += "-p:LeafSdkProject=$LeafSdkProject" }
  & dotnet @args
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
  @("Leaf.Sdk", "RedBamboo.AppHost") | ForEach-Object { Remove-Item (Join-Path $backend "$_.dll"),(Join-Path $backend "$_.pdb"),(Join-Path $backend "$_.xml") -Force -ErrorAction SilentlyContinue }
  if (-not $SkipFrontend) {
    Push-Location (Join-Path $PSScriptRoot "web")
    try { & pnpm run build:pkg; if ($LASTEXITCODE -ne 0) { throw "frontend build failed" } } finally { Pop-Location }
    New-Item -ItemType Directory -Force (Join-Path $staging "web\dist") | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "web\dist\*") (Join-Path $staging "web\dist") -Recurse
  }
  Copy-Item (Join-Path $PSScriptRoot "seeds") $staging -Recurse
  $pkg = Join-Path $OutputDir "$($manifest.id)-$($manifest.version).leafpkg"
  $zip = [IO.Path]::ChangeExtension($pkg, ".zip")
  Remove-Item $pkg,$zip -Force -ErrorAction SilentlyContinue
  Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip
  Move-Item $zip $pkg
  $hash = (Get-FileHash $pkg -Algorithm SHA256).Hash.ToLowerInvariant()
  [IO.File]::WriteAllText("$pkg.sha256", "$hash`n", [Text.UTF8Encoding]::new($false))
  Write-Host "$pkg`nsha256: $hash"
} finally { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
