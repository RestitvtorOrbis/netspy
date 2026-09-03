[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path $PSScriptRoot 'Net10/artifacts/net10.0'),
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'FixtureGeneration.Common.ps1')

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

function Read-ModernBundleByte($State) {
    if ($State.Position -ge $State.Bytes.Length) {
        throw 'The generated bundle manifest is truncated.'
    }
    $value = $State.Bytes[$State.Position]
    $State.Position = $State.Position + 1
    return [byte]$value
}

function Read-ModernBundleInt32($State) {
    if ($State.Position -gt $State.Bytes.Length - 4) {
        throw 'The generated bundle manifest is truncated.'
    }
    $value = [BitConverter]::ToInt32($State.Bytes, $State.Position)
    $State.Position += 4
    return $value
}

function Read-ModernBundleUInt32($State) {
    if ($State.Position -gt $State.Bytes.Length - 4) {
        throw 'The generated bundle manifest is truncated.'
    }
    $value = [BitConverter]::ToUInt32($State.Bytes, $State.Position)
    $State.Position += 4
    return $value
}

function Read-ModernBundleInt64($State) {
    if ($State.Position -gt $State.Bytes.Length - 8) {
        throw 'The generated bundle manifest is truncated.'
    }
    $value = [BitConverter]::ToInt64($State.Bytes, $State.Position)
    $State.Position += 8
    return $value
}

function Read-ModernBundleUInt64($State) {
    if ($State.Position -gt $State.Bytes.Length - 8) {
        throw 'The generated bundle manifest is truncated.'
    }
    $value = [BitConverter]::ToUInt64($State.Bytes, $State.Position)
    $State.Position += 8
    return $value
}

function Read-ModernBundleString($State) {
    [uint32]$length = 0
    $shift = 0
    do {
        if ($shift -ge 35) {
            throw 'The generated bundle manifest has an invalid string length.'
        }
        [byte]$current = Read-ModernBundleByte $State
        $length = $length -bor ([uint32]($current -band 0x7f) -shl $shift)
        $shift += 7
    } while (($current -band 0x80) -ne 0)
    if ($length -gt [int]::MaxValue -or $length -gt ($State.Bytes.Length - $State.Position)) {
        throw 'The generated bundle manifest string is truncated.'
    }
    if ($length -eq 0) {
        return ''
    }
    $value = [Text.Encoding]::UTF8.GetString($State.Bytes, $State.Position, [int]$length)
    $State.Position += [int]$length
    return $value
}

function Get-ModernBundleEntryNames([string] $BundlePath) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($BundlePath)
    [byte[]]$signature = @(
        0x8B, 0x12, 0x02, 0xB9, 0x6A, 0x61, 0x20, 0x38,
        0x72, 0x7B, 0x93, 0x02, 0x14, 0xD7, 0xA0, 0x32,
        0x13, 0xF5, 0xB9, 0xE6, 0xEF, 0xAE, 0x33, 0x18,
        0xEE, 0x3B, 0x2D, 0xCE, 0x24, 0xB3, 0x6A, 0xAE
    )
    $marker = [Array]::IndexOf($bytes, [byte]$signature[0], 0)
    $header = -1L
    while ($marker -ge 8 -and $marker -le $bytes.Length - $signature.Length) {
        $matches = $true
        for ($index = 1; $index -lt $signature.Length; $index++) {
            if ($bytes[$marker + $index] -ne $signature[$index]) {
                $matches = $false
                break
            }
        }
        if ($matches) {
            $candidate = [BitConverter]::ToInt64($bytes, $marker - 8)
            if ($candidate -gt $marker -and $candidate - $marker -ge $signature.Length -and
                $candidate -lt $bytes.Length) {
                $header = $candidate
                break
            }
        }
        $marker = [Array]::IndexOf($bytes, [byte]$signature[0], $marker + 1)
    }
    if ($header -lt 0) {
        throw "The generated output does not contain a valid official bundle marker: $BundlePath"
    }

    $state = [pscustomobject]@{ Bytes = $bytes; Position = [int]$header }
    [uint32]$major = Read-ModernBundleUInt32 $state
    [void](Read-ModernBundleUInt32 $state)
    [int]$fileCount = Read-ModernBundleInt32 $state
    if ($major -ne 6) {
        throw "The generated Net10 bundle has manifest version $major; expected 6."
    }
    if ($fileCount -lt 0 -or $fileCount -gt 100000) {
        throw "The generated bundle manifest has an invalid file count: $fileCount"
    }
    [void](Read-ModernBundleString $state)
    [void](Read-ModernBundleInt64 $state)
    [void](Read-ModernBundleInt64 $state)
    [void](Read-ModernBundleInt64 $state)
    [void](Read-ModernBundleInt64 $state)
    [void](Read-ModernBundleUInt64 $state)

    $entryNames = @()
    for ($entryIndex = 0; $entryIndex -lt $fileCount; $entryIndex++) {
        [void](Read-ModernBundleInt64 $state)
        [void](Read-ModernBundleInt64 $state)
        [void](Read-ModernBundleInt64 $state)
        [void](Read-ModernBundleByte $state)
        $entryNames += Read-ModernBundleString $state
    }
    return $entryNames
}

function Assert-ModernFixtureAssetGraph([string] $VariantRoot) {
    $appAssetsPath = Join-Path $VariantRoot 'obj/App/project.assets.json'
    $dependencyAssetsPath = Join-Path $VariantRoot 'obj/SingleFile.Dependency/project.assets.json'
    if (-not (Test-Path -LiteralPath $appAssetsPath -PathType Leaf)) {
        throw "The generated app assets file is missing: $appAssetsPath"
    }
    if (-not (Test-Path -LiteralPath $dependencyAssetsPath -PathType Leaf)) {
        throw "The generated dependency assets file is missing: $dependencyAssetsPath"
    }
    $appAssets = Get-Content -LiteralPath $appAssetsPath -Raw | ConvertFrom-Json
    $dependencyAssets = Get-Content -LiteralPath $dependencyAssetsPath -Raw | ConvertFrom-Json
    $appTargets = @($appAssets.targets.PSObject.Properties.Name)
    $dependencyTargets = @($dependencyAssets.targets.PSObject.Properties.Name)
    foreach ($target in @('net10.0', 'net10.0/win-x64')) {
        if ($appTargets -notcontains $target) {
            throw "The generated app assets are missing target '$target': $appAssetsPath"
        }
    }
    $expectedDependencyTargets = @(
        '.NETStandard,Version=v2.0',
        '.NETStandard,Version=v2.0/win-x64'
    )
    $contaminatedTargets = @($dependencyTargets | Where-Object {
        $_ -match '(?i)(?:net10\.0|\.NETCoreApp,Version=v10\.0)'
    })
    if ($contaminatedTargets.Count -ne 0) {
        throw "The generated dependency assets contain forbidden Net10 target keys: $($contaminatedTargets -join ', ')"
    }
    $missingTargets = @($expectedDependencyTargets | Where-Object { $dependencyTargets -notcontains $_ })
    $unexpectedTargets = @($dependencyTargets | Where-Object { $expectedDependencyTargets -notcontains $_ })
    if ($dependencyTargets.Count -ne $expectedDependencyTargets.Count -or
        $missingTargets.Count -ne 0 -or $unexpectedTargets.Count -ne 0) {
        throw "The generated dependency assets must contain exactly '$($expectedDependencyTargets -join "', '")'; actual target keys: '$($dependencyTargets -join "', '")'."
    }
}

function Assert-ModernBundleInventory([string] $BundlePath) {
    $entryNames = @(Get-ModernBundleEntryNames $BundlePath)
    foreach ($requiredEntry in @('SingleFile.App.dll', 'SingleFile.Dependency.dll')) {
        if ($entryNames -notcontains $requiredEntry) {
            throw "The generated bundle inventory is missing '$requiredEntry': $BundlePath"
        }
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
            "-p:SingleFileFixtureRoot=$variantRoot",
            '-p:PublishSingleFile=true',
            '-p:DebugType=portable',
            '-p:DebugSymbols=true',
            '-p:Deterministic=true',
            '-p:ContinuousIntegrationBuild=true',
            "-p:PathMap=$sourceRoot=/_/SingleFile",
            "-p:SingleFileFixtureIncludeSymbols=$($variant.IncludeSymbols.ToString().ToLowerInvariant())",
            "-p:SingleFileFixtureCompression=$($variant.Compressed.ToString().ToLowerInvariant())",
            "-p:EnableCompressionInSingleFile=$($variant.Compressed.ToString().ToLowerInvariant())",
            "-p:IncludeSymbolsInSingleFile=$($variant.IncludeSymbols.ToString().ToLowerInvariant())"
        )
        Write-Host "Generating net10/$($variant.Name) with SDK $actualSdk, TFM net10.0, RID win-x64, SelfContained $($variant.SelfContained.ToString().ToLowerInvariant())."
        Invoke-SingleFileFixturePhases `
            -ProjectPath $appProject `
            -TargetFramework 'net10.0' `
            -RuntimeIdentifier 'win-x64' `
            -SelfContained ([bool]$variant.SelfContained) `
            -PublishRoot $publishRoot `
            -MSBuildProperties $properties

        Assert-ModernFixtureAssetGraph $variantRoot
        Assert-ModernBundleInventory (Join-Path $publishRoot 'SingleFile.App.exe')

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
