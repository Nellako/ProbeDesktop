param(
    [ValidateSet('build', 'test', 'publish')]
    [string]$Task = 'build',
    [string]$Dotnet = '',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$SingleFile
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Dotnet) { $Dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$sdks = & $Dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^10\.')) {
    throw '.NET 10 SDK is required. Install from https://dotnet.microsoft.com/download/dotnet/10.0 or pass -Dotnet with its full path.'
}
function Invoke-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed (exit $LASTEXITCODE)." }
}
$solution = Join-Path $root 'ProbeDesktop.slnx'
$app = Join-Path $root 'src\ProbeDesktop.App\ProbeDesktop.App.csproj'
$tests = Join-Path $root 'tests\ProbeDesktop.Tests\ProbeDesktop.Tests.csproj'
switch ($Task) {
    'build' { Invoke-Dotnet @('build', $solution, '-c', 'Release') }
    'test' { Invoke-Dotnet @('run', '--project', $tests, '-c', 'Release') }
    'publish' {
        Invoke-Dotnet @('build', $solution, '-c', 'Release')
        Invoke-Dotnet @('run', '--project', $tests, '-c', 'Release', '--no-build')
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        [xml]$buildProperties = Get-Content (Join-Path $root 'Directory.Build.props')
        $version = [string]$buildProperties.Project.PropertyGroup.Version
        $label = 'ProbeDesktop-' + $version + '-' + $(if ($SingleFile) { 'single-' } else { '' }) + $Runtime + '-' + $stamp
        $out = Join-Path $root ('artifacts\' + $label)
        if (Test-Path $out) { throw 'Publish destination already exists; existing artifacts will not be overwritten.' }
        $publishArgs = @('publish', $app, '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $out)
        if ($SingleFile) { $publishArgs += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
        else { $publishArgs += '-p:PublishSingleFile=false' }
        Invoke-Dotnet $publishArgs
        Copy-Item (Join-Path $root 'README.md') (Join-Path $out 'README.md')
        foreach ($document in @('RELEASE-NOTES.md', 'SAFETY.md')) {
            Copy-Item (Join-Path $root $document) (Join-Path $out $document)
        }
        $archiveName = $label + '.zip'
        $archive = Join-Path $root ('artifacts\' + $archiveName)
        if ($SingleFile) {
            $exePath = Join-Path $out 'ProbeDesktop.exe'
            $exeHash = Get-FileHash -Algorithm SHA256 $exePath
            [System.IO.File]::WriteAllText($exePath + '.sha256', $exeHash.Hash.ToLowerInvariant() + '  ProbeDesktop.exe' + [Environment]::NewLine, [System.Text.Encoding]::ASCII)
        }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $archive
        $hash = Get-FileHash -Algorithm SHA256 $archive
        [System.IO.File]::WriteAllText($archive + '.sha256', $hash.Hash.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName($archive) + [Environment]::NewLine, [System.Text.Encoding]::ASCII)
        $hash | Format-List
        Write-Output ("Published: " + $archive)
    }
}
