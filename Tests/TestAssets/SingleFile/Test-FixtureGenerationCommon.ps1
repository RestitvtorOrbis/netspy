# Focused, Pester-free contract test for FixtureGeneration.Common.ps1.
# This test uses an executable shim so no SDK or fixture project is required.

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$commonPath = Join-Path $scriptRoot 'FixtureGeneration.Common.ps1'
$historicalPath = Join-Path $scriptRoot 'Generate-HistoricalFixtures.ps1'
$modernPath = Join-Path $scriptRoot 'Generate-ModernFixtures.ps1'
$originalPath = $env:PATH
$originalLogPath = $env:SINGLE_FILE_FIXTURE_CONTRACT_LOG
$testRoot = $null

function Assert-Contract([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        throw "Fixture-generation contract failure: $Message"
    }
}

function Assert-InvocationArguments([string[]] $Invocation, [string[]] $Expected, [string] $Phase) {
    $actual = @($Invocation | ForEach-Object { [string]$_ })
    Assert-Contract ($actual.Count -eq $Expected.Count) "$Phase argument count was $($actual.Count), expected $($Expected.Count): $($actual -join ' | ')"
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        Assert-Contract ($actual[$index] -ceq $Expected[$index]) "$Phase argument $index was '$($actual[$index])', expected '$($Expected[$index])'."
    }
}

function Test-SafeTemporaryDirectory([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path)) {
        return $false
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $false
    }
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $candidate = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $relative = [IO.Path]::GetRelativePath($temporaryRoot, $candidate)
    if ([IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) {
        return $false
    }
    $directory = [IO.DirectoryInfo]::new($candidate)
    return -not [bool]($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

try {
    Assert-Contract (Test-Path -LiteralPath $commonPath -PathType Leaf) "Missing helper '$commonPath'."
    . $commonPath

    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $testRoot = Join-Path $temporaryRoot ('netspy-fixture-generation-contract-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Assert-Contract (Test-SafeTemporaryDirectory $testRoot) "Unsafe test directory '$testRoot'."

    $logPath = Join-Path $testRoot 'dotnet-invocations.jsonl'

    if ($IsWindows) {
        $shimPath = Join-Path $testRoot 'dotnet.cmd'
        $shimCommand = @'
@echo off
setlocal EnableExtensions EnableDelayedExpansion
>>"%SINGLE_FILE_FIXTURE_CONTRACT_LOG%" echo BEGIN
:nextArgument
if "%~1"=="" goto done
>>"%SINGLE_FILE_FIXTURE_CONTRACT_LOG%" echo ARG:%~1
shift
goto nextArgument
:done
>>"%SINGLE_FILE_FIXTURE_CONTRACT_LOG%" echo END
exit /b 0
'@
        [IO.File]::WriteAllText($shimPath, $shimCommand)
    }
    else {
        $shimPath = Join-Path $testRoot 'dotnet'
        $shimCommand = @'
#!/bin/sh
log_path="$SINGLE_FILE_FIXTURE_CONTRACT_LOG"
printf '%s\n' BEGIN >> "$log_path"
for argument in "$@"; do
    printf 'ARG:%s\n' "$argument" >> "$log_path"
done
printf '%s\n' END >> "$log_path"
exit 0
'@
        [IO.File]::WriteAllText($shimPath, $shimCommand)
        & chmod +x $shimPath
        Assert-Contract ($LASTEXITCODE -eq 0) "Could not make the dotnet shim executable."
    }

    $env:SINGLE_FILE_FIXTURE_CONTRACT_LOG = $logPath
    $env:PATH = $testRoot + [IO.Path]::PathSeparator + [string]$originalPath

    $projectPath = Join-Path $testRoot 'input/App.csproj'
    $publishRoot = Join-Path $testRoot 'output/publish'
    $targetFramework = 'net10.0'
    $runtimeIdentifier = 'win-x64'
    $properties = @(
        "-p:SingleFileFixtureRoot=$publishRoot",
        "-p:PathMap=$projectPath=/_/SingleFile",
        '-p:PublishSingleFile=true',
        '-p:DebugType=portable',
        '-p:Deterministic=true'
    )
    Invoke-SingleFileFixturePhases `
        -ProjectPath $projectPath `
        -TargetFramework $targetFramework `
        -RuntimeIdentifier $runtimeIdentifier `
        -SelfContained $true `
        -PublishRoot $publishRoot `
        -MSBuildProperties $properties

    foreach ($reservedFrameworkProperty in @('TargetFramework', 'TargetFrameworks')) {
        $frameworkPropertyRejected = $false
        try {
            Invoke-SingleFileFixturePhases `
                -ProjectPath $projectPath `
                -TargetFramework $targetFramework `
                -RuntimeIdentifier $runtimeIdentifier `
                -SelfContained $true `
                -PublishRoot $publishRoot `
                -MSBuildProperties @("-p:$reservedFrameworkProperty=net10.0")
        }
        catch {
            $frameworkPropertyRejected = $true
        }
        Assert-Contract $frameworkPropertyRejected "$reservedFrameworkProperty was not rejected as a reserved MSBuild property."
    }

    Assert-Contract (Test-Path -LiteralPath $logPath -PathType Leaf) 'The dotnet shim did not record any invocations.'
    $invocations = @()
    $currentInvocation = $null
    foreach ($line in @(Get-Content -LiteralPath $logPath)) {
        if ($line -ceq 'BEGIN') {
            Assert-Contract ($null -eq $currentInvocation) 'The dotnet shim began an invocation before ending the previous one.'
            $currentInvocation = [Collections.Generic.List[string]]::new()
        }
        elseif ($line -ceq 'END') {
            Assert-Contract ($null -ne $currentInvocation) 'The dotnet shim ended an invocation before it began one.'
            $invocations += ,([string[]]$currentInvocation)
            $currentInvocation = $null
        }
        elseif ($line.StartsWith('ARG:', [StringComparison]::Ordinal)) {
            Assert-Contract ($null -ne $currentInvocation) 'The dotnet shim recorded an argument outside an invocation.'
            $currentInvocation.Add($line.Substring(4))
        }
        elseif (-not [string]::IsNullOrWhiteSpace($line)) {
            throw "Fixture-generation contract failure: unexpected dotnet shim log line '$line'."
        }
    }
    Assert-Contract ($null -eq $currentInvocation) 'The dotnet shim log ended with an incomplete invocation.'
    Assert-Contract ($invocations.Count -eq 3) "Expected three phase invocations, found $($invocations.Count)."

    $expectedRestore = @(
        'restore', $projectPath, '--nologo', '--runtime', $runtimeIdentifier,
        '-p:SelfContained=true'
    ) + $properties
    $expectedBuild = @(
        'build', $projectPath, '--nologo', '--configuration', 'Release',
        '--runtime', $runtimeIdentifier,
        '--no-restore', '-p:SelfContained=true'
    ) + $properties
    $expectedPublish = @(
        'publish', $projectPath, '--nologo', '--configuration', 'Release',
        '--runtime', $runtimeIdentifier,
        '--output', $publishRoot, '--no-build', '--no-restore',
        '-p:SelfContained=true'
    ) + $properties
    Assert-InvocationArguments $invocations[0] $expectedRestore 'restore'
    Assert-InvocationArguments $invocations[1] $expectedBuild 'build'
    Assert-InvocationArguments $invocations[2] $expectedPublish 'publish'
    foreach ($invocation in $invocations) {
        foreach ($argument in @($invocation)) {
            Assert-Contract ($argument -notmatch '^(?:-p:TargetFramework(?:s)?(?:=|$)|--framework$|-f$)') "Framework selector '$argument' was forwarded to dotnet."
        }
    }

    $dotSourcePattern = '(?m)^\s*\.\s+\(Join-Path\s+\$PSScriptRoot\s+[\x27\"]FixtureGeneration\.Common\.ps1[\x27\"]\)'
    foreach ($generatorPath in @($historicalPath, $modernPath)) {
        $generatorText = [IO.File]::ReadAllText($generatorPath)
        Assert-Contract ($generatorText -match $dotSourcePattern) "$generatorPath does not dot-source FixtureGeneration.Common.ps1 relative to PSScriptRoot."
        Assert-Contract ($generatorText -match '\bInvoke-SingleFileFixturePhases\b') "$generatorPath does not call Invoke-SingleFileFixturePhases."
        Assert-Contract ($generatorText -notmatch '(?im)^\s*(?:&\s*)?dotnet\s+(?:restore|build|publish)\b') "$generatorPath contains a direct restore, build, or publish invocation."
    }

    Write-Host 'Fixture-generation common helper contract passed.'
}
finally {
    if ($null -eq $originalPath) {
        Remove-Item -LiteralPath Env:PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:PATH = $originalPath
    }
    if ($null -eq $originalLogPath) {
        Remove-Item -LiteralPath Env:SINGLE_FILE_FIXTURE_CONTRACT_LOG -ErrorAction SilentlyContinue
    }
    else {
        $env:SINGLE_FILE_FIXTURE_CONTRACT_LOG = $originalLogPath
    }
    if ($null -ne $testRoot -and (Test-Path -LiteralPath $testRoot -PathType Container)) {
        if (-not (Test-SafeTemporaryDirectory $testRoot)) {
            throw "Refusing to remove an unvalidated temporary directory '$testRoot'."
        }
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
