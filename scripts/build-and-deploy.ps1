param(
  [ValidateSet('Debug','Release')]
  [string]$Configuration = 'Debug',

  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

  [string]$DeployExePath = 'D:\Desarrollo\Claude\Mcp.exe'
)

$ErrorActionPreference = 'Stop'

$proj = Join-Path $RepoRoot 'Mcp\Mcp.csproj'
$outExe = Join-Path $RepoRoot ("Mcp\bin\{0}\Mcp.exe" -f $Configuration)

$msbuildCandidates = @(
  'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
  'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
  'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
  'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe'
)

$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $msbuild) {
  throw "MSBuild no encontrado. Instalá Visual Studio o Build Tools."
}

# If the exe is running, MSBuild/Copy-Item can't overwrite it.
if (Test-Path $outExe) {
  try {
    $procs = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $outExe }
    foreach ($p in $procs) {
      Write-Host "Stopping process $($p.Name) (PID=$($p.ProcessId)) locking $outExe"
      Stop-Process -Id $p.ProcessId -Force
    }
  } catch {
    # Best-effort; build will fail if still locked.
  }
}

if (Test-Path $DeployExePath) {
  try {
    $procs = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $DeployExePath }
    foreach ($p in $procs) {
      Write-Host "Stopping process $($p.Name) (PID=$($p.ProcessId)) locking $DeployExePath"
      Stop-Process -Id $p.ProcessId -Force
    }
  } catch {
    # Best-effort; deploy will fail if still locked.
  }
}

& $msbuild $proj /t:Build /p:Configuration=$Configuration

if (-not (Test-Path $outExe)) {
  throw "Build OK pero no se encontró el exe esperado: $outExe"
}

Copy-Item -Force $outExe $DeployExePath
Write-Host "Deployed: $DeployExePath"
