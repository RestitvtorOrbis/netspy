# Focused, Pester-free contract test for FixtureGeneration.Common.ps1.
# This test uses a command shim so no SDK or fixture project is required.

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

    # Use a PowerShell script shim on both platforms. cmd.exe treats '=' as an
    # argument separator, so a .cmd shim cannot observe dotnet.exe arguments faithfully.
    $shimPath = Join-Path $testRoot 'dotnet.ps1'
    $shimCommand = @'
$logPath = $env:SINGLE_FILE_FIXTURE_CONTRACT_LOG
Add-Content -LiteralPath $logPath -Value 'BEGIN'
foreach ($argument in $args) {
    if ($argument -ceq '--nologo') { exit 2 }
    Add-Content -LiteralPath $logPath -Value "ARG:$argument"
}
Add-Content -LiteralPath $logPath -Value 'END'
exit 0
'@
    [IO.File]::WriteAllText($shimPath, $shimCommand)

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
        'restore', $projectPath, '--runtime', $runtimeIdentifier,
        '-p:SelfContained=true'
    ) + $properties
    $expectedBuild = @(
        'build', $projectPath, '--configuration', 'Release',
        '--runtime', $runtimeIdentifier,
        '--no-restore', '-p:SelfContained=true'
    ) + $properties
    $expectedPublish = @(
        'publish', $projectPath, '--configuration', 'Release',
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

    $syntheticRoot = Join-Path $testRoot 'synthetic-output'
    $exactMainPath = Join-Path $syntheticRoot 'build/App/Release/net10.0/win-x64/SingleFile.App.dll'
    $exactDependencyPath = Join-Path $syntheticRoot 'build/SingleFile.Dependency/Release/netstandard2.0/SingleFile.Dependency.dll'
    $exactBundlePath = Join-Path $syntheticRoot 'publish/SingleFile.App.exe'
    $decoyPaths = @(
        (Join-Path $syntheticRoot 'build/App/Release/net10.0/ref/SingleFile.App.dll'),
        (Join-Path $syntheticRoot 'build/App/Release/net10.0/refint/SingleFile.App.dll'),
        (Join-Path $syntheticRoot 'build/copied/SingleFile.App.dll')
    )
    foreach ($fixturePath in @($exactMainPath, $exactDependencyPath, $exactBundlePath) + $decoyPaths) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $fixturePath)) | Out-Null
        [IO.File]::WriteAllText($fixturePath, 'synthetic fixture')
    }

    $resolvedMainPath = Get-RequiredFixtureFile $exactMainPath 'built main assembly'
    Assert-Contract ([StringComparer]::OrdinalIgnoreCase.Equals($resolvedMainPath, [IO.Path]::GetFullPath($exactMainPath))) "The exact main assembly path resolved incorrectly: $resolvedMainPath"
    $resolvedDependencyPath = Get-RequiredFixtureFile $exactDependencyPath 'built dependency assembly'
    Assert-Contract ([StringComparer]::OrdinalIgnoreCase.Equals($resolvedDependencyPath, [IO.Path]::GetFullPath($exactDependencyPath))) "The exact dependency assembly path resolved incorrectly: $resolvedDependencyPath"
    $resolvedBundlePath = Get-RequiredFixtureFile $exactBundlePath 'published bundle'
    Assert-Contract ([StringComparer]::OrdinalIgnoreCase.Equals($resolvedBundlePath, [IO.Path]::GetFullPath($exactBundlePath))) "The exact bundle path resolved incorrectly: $resolvedBundlePath"

    Remove-Item -LiteralPath $exactMainPath
    $missingExactLeafError = $null
    try {
        Get-RequiredFixtureFile $exactMainPath 'built main assembly' | Out-Null
    }
    catch {
        $missingExactLeafError = $_.Exception.Message
    }
    Assert-Contract ($null -ne $missingExactLeafError) 'A missing exact fixture leaf did not fail.'
    Assert-Contract ($missingExactLeafError.Contains('built main assembly')) "The missing exact fixture error omitted the description: $missingExactLeafError"
    Assert-Contract ($missingExactLeafError.Contains($exactMainPath)) "The missing exact fixture error omitted the path: $missingExactLeafError"

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
