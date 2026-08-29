[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path $PSScriptRoot 'Net10/artifacts/net10.0'),
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
$requiredSdk = '10.0.111'
$sourceRoot = Join-Path $PSScriptRoot 'Net10'
$appProject = Join-Path $sourceRoot 'App.csproj'
$metadataProject = Join-Path $PSScriptRoot 'FixtureMetadata/FixtureMetadata.csproj'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

function Invoke-Dotnet {
    param([string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

Push-Location $sourceRoot
try {
    $actualSdk = (& dotnet --version).Trim()
    if ($actualSdk -ne $requiredSdk) {
        throw "The net10 fixture requires SDK $requiredSdk, but dotnet --version returned $actualSdk"
    }

    # Validate the canonical generated-artifact root before recursive cleanup.
    # The shared helper rejects repository/source roots and existing symlink or
    # junction escapes, keeping this script in parity with the Bash generator.
    $validationArguments = @(
        'run', '--project', $metadataProject, '--', '--validate', $sourceRoot, $OutputRoot
    )
    Invoke-Dotnet $validationArguments
    $OutputRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot 'artifacts/net10.0'))

    # Every run starts from an empty SDK-specific root. This prevents stale
    # variants from being discovered by the test locator after a matrix change.
    if (Test-Path -LiteralPath $OutputRoot) {
        Remove-Item -LiteralPath $OutputRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

    # Keep variants explicit and stable. The two PDB variants exercise the
    # portable-symbol-in-the-bundle path independently of compression.
    $variants = @(
        [pscustomobject] @{ Name = 'fdd-uncompressed'; SelfContained = $false; Compressed = $false; IncludeSymbols = $false },
        [pscustomobject] @{ Name = 'scd-uncompressed'; SelfContained = $true; Compressed = $false; IncludeSymbols = $false },
        [pscustomobject] @{ Name = 'scd-compressed'; SelfContained = $true; Compressed = $true; IncludeSymbols = $false },
        [pscustomobject] @{ Name = 'scd-uncompressed-pdb'; SelfContained = $true; Compressed = $false; IncludeSymbols = $true },
        [pscustomobject] @{ Name = 'scd-compressed-pdb'; SelfContained = $true; Compressed = $true; IncludeSymbols = $true }
    )

    foreach ($variant in $variants) {
        $variantRoot = Join-Path $OutputRoot $variant.Name
        $publishRoot = Join-Path $variantRoot 'publish'
        if (Test-Path -LiteralPath $variantRoot) {
            Remove-Item -LiteralPath $variantRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

        $properties = @(
            '-p:SingleFileFixtureRoot=' + $variantRoot,
            '-p:PublishSingleFile=true',
            '-p:DebugType=portable',
            '-p:DebugSymbols=true',
            '-p:Deterministic=true',
            '-p:ContinuousIntegrationBuild=true',
            '-p:PathMap=' + $sourceRoot + '=/_/SingleFile',
            '-p:SingleFileFixtureIncludeSymbols=' + $variant.IncludeSymbols.ToString().ToLowerInvariant(),
            '-p:SingleFileFixtureCompression=' + $variant.Compressed.ToString().ToLowerInvariant(),
            '-p:EnableCompressionInSingleFile=' + $variant.Compressed.ToString().ToLowerInvariant(),
            '-p:IncludeSymbolsInSingleFile=' + $variant.IncludeSymbols.ToString().ToLowerInvariant()
        )
        $buildArguments = @(
            'build', $appProject, '--nologo', '--configuration', 'Release',
            '--framework', 'net10.0', '--runtime', 'win-x64',
            '--self-contained', $variant.SelfContained.ToString().ToLowerInvariant()
        ) + $properties
        Invoke-Dotnet $buildArguments

        $arguments = @(
            'publish', $appProject, '--nologo', '--configuration', 'Release',
            '--framework', 'net10.0', '--runtime', 'win-x64',
            '--self-contained', $variant.SelfContained.ToString().ToLowerInvariant(),
            '--output', $publishRoot, '--no-build'
        ) + $properties
        Invoke-Dotnet $arguments

        $metadataArguments = @(
            'run', '--project', $metadataProject, '--',
            '--variant-root', $variantRoot,
            '--publish-root', $publishRoot,
            '--variant', $variant.Name,
            '--sdk-version', $actualSdk,
            '--target-framework', 'net10.0',
            '--runtime-identifier', 'win-x64',
            '--self-contained', $variant.SelfContained.ToString().ToLowerInvariant(),
            '--compressed', $variant.Compressed.ToString().ToLowerInvariant(),
            '--includes-symbols', $variant.IncludeSymbols.ToString().ToLowerInvariant()
        )
        Invoke-Dotnet $metadataArguments
    }
}
finally {
    Pop-Location
}
