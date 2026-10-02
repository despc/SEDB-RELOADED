<#
.SYNOPSIS
    Builds SEDiscordBridge on a machine without Visual Studio and packs the plugin zip the way CI does.

.DESCRIPTION
    The project is an old-style .csproj with WPF pages and packages.config: `dotnet build` cannot compile its XAML
    and `dotnet restore` does not read packages.config. So the build runs on the MSBuild that ships with
    .NET Framework (it compiles XAML) with a current Roslyn compiler from NuGet, against the .NET Framework 4.8
    reference assemblies from NuGet. Everything it downloads goes to .buildtools next to this script (once).

    Steps:
      - GameBinaries and TorchBinaries junctions to the server folders, if missing;
      - nuget.exe, Microsoft.Net.Compilers.Toolset and the net48 reference assemblies into .buildtools;
      - nuget restore of the solution;
      - Release build;
      - SEDiscordBridge.zip in artifact\: the assemblies and manifest.xml from bin\Release, as CI packs it.

.PARAMETER TorchRoot
    The folder with Torch.Server.exe. Default C:\SE-Vanilla.

.PARAMETER GameRoot
    The folder with the game assemblies (DedicatedServer64). Default <TorchRoot>\DedicatedServer64.

.EXAMPLE
    .\build-local.ps1
    .\build-local.ps1 -TorchRoot D:\Torch
#>
param(
    [string]$TorchRoot = 'C:\SE-Vanilla',
    [string]$GameRoot = ''
)

$ErrorActionPreference = 'Stop'
if (-not $GameRoot) { $GameRoot = Join-Path $TorchRoot 'DedicatedServer64' }
$root = $PSScriptRoot
$tools = Join-Path $root '.buildtools'
$pkgs = Join-Path $tools 'pkgs'
$compilersVersion = '5.9.0'
$refVersion = '1.0.3'

foreach ($check in @(@($TorchRoot, 'Torch.Server.exe'), @($GameRoot, 'Sandbox.Game.dll'))) {
    if (-not (Test-Path (Join-Path $check[0] $check[1]))) { throw "$($check[1]) not found in $($check[0])" }
}

# junctions to the server, as "Setup (run before opening solution).bat" makes them
foreach ($link in @(@('GameBinaries', $GameRoot), @('TorchBinaries', $TorchRoot))) {
    $path = Join-Path $root $link[0]
    if (-not (Test-Path $path)) {
        cmd /c mklink /J "$path" "$($link[1])" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "could not make the junction $path" }
        Write-Host "$($link[0]) -> $($link[1])"
    }
}

# tools
New-Item -ItemType Directory -Force -Path $pkgs | Out-Null
$nuget = Join-Path $tools 'nuget.exe'
if (-not (Test-Path $nuget)) {
    [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
    Invoke-WebRequest -UseBasicParsing 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nuget
    if ((Get-AuthenticodeSignature $nuget).Status -ne 'Valid') { Remove-Item $nuget; throw 'nuget.exe signature is not valid' }
}
& $nuget install Microsoft.Net.Compilers.Toolset -Version $compilersVersion -OutputDirectory $pkgs -NonInteractive -Verbosity quiet
& $nuget install Microsoft.NETFramework.ReferenceAssemblies.net48 -Version $refVersion -OutputDirectory $pkgs -NonInteractive -Verbosity quiet
$cscDir = Join-Path $pkgs "Microsoft.Net.Compilers.Toolset.$compilersVersion\tasks\net472"
$refDir = Join-Path $pkgs "Microsoft.NETFramework.ReferenceAssemblies.net48.$refVersion\build\.NETFramework\v4.8"

# DSharpPlus is netstandard2.0: the facade has to be referenced explicitly on this MSBuild
$facade = Join-Path $tools 'netstandard.targets'
@"
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup>
    <Reference Include="netstandard"><HintPath>$refDir\Facades\netstandard.dll</HintPath><Private>False</Private></Reference>
  </ItemGroup>
</Project>
"@ | Set-Content -Path $facade -Encoding UTF8

# straight from packages.config: restoring the .sln makes nuget evaluate it with this old MSBuild (MSB4066)
& $nuget restore (Join-Path $root 'SEDiscordBridge\packages.config') -PackagesDirectory (Join-Path $root 'packages') -NonInteractive -Verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'nuget restore failed' }

$msbuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
& $msbuild (Join-Path $root 'SEDiscordBridge\SEDiscordBridge.csproj') /p:Configuration=Release /v:minimal /nologo `
    "/p:FrameworkPathOverride=$refDir" "/p:CscToolPath=$cscDir" "/p:CustomBeforeMicrosoftCommonTargets=$facade" `
    /p:IgnoreVersionForFrameworkReferences=true /p:IgnoreDefaultInstalledAssemblyTables=true
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

# the plugin zip: assemblies plus manifest.xml, no pdbs or xmldoc (as CI packs it)
$out = Join-Path $root 'SEDiscordBridge\bin\Release'
$stage = Join-Path $root 'artifact\SEDiscordBridge'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Get-ChildItem $out -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -eq 'manifest.xml' } | Copy-Item -Destination $stage
$zip = Join-Path $root 'artifact\SEDiscordBridge.zip'
Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force
$manifest = ([xml](Get-Content (Join-Path $stage 'manifest.xml'))).PluginManifest
Write-Host "$zip  ($($manifest.Version), $($manifest.Guid))"
