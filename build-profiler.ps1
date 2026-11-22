param(
  [string]$ValheimPath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim",
  [string]$ProfileName = "Enigma_FiWi",
  [string]$GaleRoot = "$env:APPDATA\com.kesomannen.gale\valheim\profiles",
  [string]$LibsDirectory = ""
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Compute hashes via .NET, avoiding Get-FileHash (not available in every
# PowerShell host used by build-profiler.bat). The FileStream and SHA256
# instance are always disposed, even if hashing throws.
function Get-EwpSha256 {
  param([Parameter(Mandatory = $true)][string]$Path)
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $stream = $null
  try {
    $stream = [System.IO.File]::OpenRead($Path)
    return [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '')
  } finally {
    if ($null -ne $stream) { $stream.Dispose() }
    if ($null -ne $sha) { $sha.Dispose() }
  }
}

# NEVER edit the game's managed DLLs. Keep build references separate from the shared
# ..\Libs folder and from any references other mods might be using.
if ([string]::IsNullOrWhiteSpace($LibsDirectory)) {
  $LibsDirectory = Join-Path $root '.ewp-profiler-libs'
}
$LibsDirectory = [System.IO.Path]::GetFullPath($LibsDirectory)
$managed = Join-Path $ValheimPath 'valheim_Data\Managed'
$profile = Join-Path $GaleRoot $ProfileName
$cores = @((Join-Path $profile 'BepInEx\core'), (Join-Path $ValheimPath 'BepInEx\core'))
$core = $cores | Where-Object { Test-Path (Join-Path $_ 'BepInEx.dll') } | Select-Object -First 1
if (!$core) { throw "BepInEx.dll not found in profile or game installation. Check -ProfileName, -GaleRoot, -ValheimPath." }
if (!(Test-Path $managed)) { throw "Valheim managed assemblies not found: $managed" }
$pluginDirs = @((Join-Path $profile 'BepInEx\plugins'), (Join-Path $ValheimPath 'BepInEx\plugins')) | Where-Object { Test-Path $_ }
$required = @(
  'BepinEx.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll',
  'UnityEngine.UI.dll', 'UnityEngine.PhysicsModule.dll', '0Harmony.dll',
  'BepInEx.Harmony.dll', 'assembly_valheim.dll', 'assembly_utils.dll',
  'assembly_guiutils.dll', 'SPlatform.dll', 'SoftReferenceableAssets.dll', 'YamlDotNet.dll'
)
New-Item -ItemType Directory -Path $LibsDirectory -Force | Out-Null
Write-Host "Isolated reference folder: $LibsDirectory"
foreach ($name in $required) {
  $destination = Join-Path $LibsDirectory $name
  $source = $null
  foreach ($dir in @($managed, $core)) {
    $candidate = Join-Path $dir $name
    if (Test-Path -LiteralPath $candidate) { $source = $candidate; break }
  }
  if (!$source) {
    foreach ($dir in $pluginDirs) {
      $match = Get-ChildItem -LiteralPath $dir -Filter $name -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($match) { $source = $match.FullName; break }
    }
  }
  if (!$source) { throw "Required assembly not found: $name. Check the game installation and -ProfileName." }

  # Avoid stale DLLs left from another Valheim version. Preserve timestamps of
  # unchanged references to keep incremental compilation effective.
  $refresh = !(Test-Path -LiteralPath $destination)
  if (!$refresh) {
    $sourceHash = Get-EwpSha256 -Path $source
    $targetHash = Get-EwpSha256 -Path $destination
    $refresh = ($sourceHash -ne $targetHash)
  }
  if ($refresh) {
    Copy-Item -LiteralPath $source -Destination $destination -Force
    # A replaced reference must be newer than the previous build output.
    (Get-Item -LiteralPath $destination).LastWriteTimeUtc = [DateTime]::UtcNow
    Write-Host "Reference updated: $name <= $source"
  }
}

$project = Join-Path $root 'ExpandWorldPrefabs\ExpandWorldPrefabs.csproj'
Write-Host "Building net4.8 EWP with compile-time publicized game references: $project"
Write-Host "NuGet package: BepInEx.AssemblyPublicizer.MSBuild 0.4.3 (restored automatically)"
# EwpSkipCopyDll prevents the original project target from modifying a shared Libs folder.
# Normal incremental build; dotnet/MSBuild recompiles when sources or references change.
& dotnet build $project -c Release "-p:EwpLibsDirectory=$LibsDirectory" '-p:EwpSkipCopyDll=true'
if ($LASTEXITCODE -ne 0) {
  throw "Compilation failed (exit $LASTEXITCODE). See the first compiler errors above. NuGet restore requires internet the first time."
}
$dll = Join-Path $root 'ExpandWorldPrefabs\bin\Release\net4.8\ExpandWorldPrefabs.dll'
if (!(Test-Path -LiteralPath $dll)) { throw "Build reported success but output DLL is missing: $dll" }
Write-Host "Compiled: $dll"
