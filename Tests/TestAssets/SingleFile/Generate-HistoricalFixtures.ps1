[CmdletBinding()]
param(
    # Running without -Generation is the CI/local all-generations command. A
    # matrix job can select one generation after installing that SDK only.
    [string] $Generation = '',
    [string] $OutputRoot = (Join-Path $PSScriptRoot 'artifacts/historical')
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'FixtureGeneration.Common.ps1')

$generations = [ordered]@{
    NetCoreApp31 = [pscustomobject]@{
        SdkVersion = '3.1.426'; TargetFramework = 'netcoreapp3.1'; ManifestMajorVersion = 1
        Variants = @(
            [pscustomobject]@{ Name = 'scd-uncompressed'; SelfContained = $true; Compressed = $false; IncludesSymbols = $false; CompatibilityMode = $false }
        )
    }
    Net5 = [pscustomobject]@{
        SdkVersion = '5.0.408'; TargetFramework = 'net5.0'; ManifestMajorVersion = 2
        Variants = @(
            [pscustomobject]@{ Name = 'fdd-uncompressed'; SelfContained = $false; Compressed = $false; IncludesSymbols = $false; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-uncompressed'; SelfContained = $true; Compressed = $false; IncludesSymbols = $false; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-compatibility'; SelfContained = $true; Compressed = $false; IncludesSymbols = $false; CompatibilityMode = $true }
            [pscustomobject]@{ Name = 'scd-uncompressed-pdb'; SelfContained = $true; Compressed = $false; IncludesSymbols = $true; CompatibilityMode = $false }
        )
    }
    Net6 = [pscustomobject]@{
        SdkVersion = '6.0.428'; TargetFramework = 'net6.0'; ManifestMajorVersion = 6
        Variants = @(
            [pscustomobject]@{ Name = 'fdd-uncompressed'; SelfContained = $false; Compressed = $false; IncludesSymbols = $false; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-uncompressed'; SelfContained = $true; Compressed = $false; IncludesSymbols = $false; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-compressed'; SelfContained = $true; Compressed = $true; IncludesSymbols = $false; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-uncompressed-pdb'; SelfContained = $true; Compressed = $false; IncludesSymbols = $true; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-compressed-pdb'; SelfContained = $true; Compressed = $true; IncludesSymbols = $true; CompatibilityMode = $false }
        )
    }
    Net8 = [pscustomobject]@{
        SdkVersion = '8.0.419'; TargetFramework = 'net8.0'; ManifestMajorVersion = 6
        Variants = @(
            [pscustomobject]@{ Name = 'scd-uncompressed'; SelfContained = $true; Compressed = $false; IncludesSymbols = $false; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-compressed'; SelfContained = $true; Compressed = $true; IncludesSymbols = $false; CompatibilityMode = $false }
        )
    }
    Net10 = [pscustomobject]@{
        SdkVersion = '10.0.111'; TargetFramework = 'net10.0'; ManifestMajorVersion = 6
        Variants = @(
            [pscustomobject]@{ Name = 'fdd-uncompressed'; SelfContained = $false; Compressed = $false; IncludesSymbols = $false; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-uncompressed'; SelfContained = $true; Compressed = $false; IncludesSymbols = $false; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-compressed'; SelfContained = $true; Compressed = $true; IncludesSymbols = $false; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-uncompressed-pdb'; SelfContained = $true; Compressed = $false; IncludesSymbols = $true; CompatibilityMode = $false }
            [pscustomobject]@{ Name = 'scd-compressed-pdb'; SelfContained = $true; Compressed = $true; IncludesSymbols = $true; CompatibilityMode = $false }
        )
    }
}

if ($Generation -ne '' -and -not $generations.Contains($Generation)) {
    throw "Unknown historical fixture generation '$Generation'. Expected one of: $($generations.Keys -join ', ')."
}

$scriptRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot 'artifacts/historical'))

function Normalize-PathText([string] $Path) {
    return $Path.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Is-UnderRoot([string] $Root, [string] $Path) {
    $rootText = (Normalize-PathText $Root) + [IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith($rootText, [StringComparison]::OrdinalIgnoreCase) -or
        [StringComparer]::OrdinalIgnoreCase.Equals((Normalize-PathText $Root), (Normalize-PathText $Path))
}

if (-not (Is-UnderRoot $allowedRoot $OutputRoot)) {
    throw "Refusing to write historical fixtures outside '$allowedRoot': $OutputRoot"
}

function Test-NoReparsePath([string] $Path) {
    $current = [IO.DirectoryInfo]::new($Path)
    while ($null -ne $current) {
        if ($current.Exists -and ($current.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Fixture output paths cannot contain symbolic links or junctions: $($current.FullName)"
        }
        $current = $current.Parent
    }
}

Test-NoReparsePath $scriptRoot
Test-NoReparsePath $OutputRoot

function Get-RelativePath([string] $Root, [string] $Path) {
    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Get-RequiredSingleFile([string] $Root, [string] $Pattern, [string] $Description) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Required $Description directory is missing: $Root"
    }
    $matches = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $Pattern |
        Sort-Object -Property FullName)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $Description ('$Pattern') below '$Root'; found $($matches.Count)."
    }
    return $matches[0].FullName
}

function Get-PublishedFileRecords([string] $VariantRoot, [string] $PublishRoot) {
    $files = @(Get-ChildItem -LiteralPath $PublishRoot -Recurse -File |
        Sort-Object -Property FullName)
    if ($files.Count -eq 0) {
        throw "The publish output is empty: $PublishRoot"
    }
    $records = @()
    foreach ($file in $files) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $records += [ordered]@{
            path = Get-RelativePath $VariantRoot $file.FullName
            length = [int64]$file.Length
            sha256 = $hash
        }
    }
    return $records
}

function Write-Json([string] $Path, $Value) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Read-BundleByte($State) {
    if ($State.Position -ge $State.Bytes.Length) {
        throw "The generated bundle manifest is truncated."
    }
    $value = $State.Bytes[$State.Position]
    $State.Position = $State.Position + 1
    return [byte]$value
}

function Read-BundleInt32($State) {
    if ($State.Position -gt $State.Bytes.Length - 4) {
        throw "The generated bundle manifest is truncated."
    }
    $value = [BitConverter]::ToInt32($State.Bytes, $State.Position)
    $State.Position += 4
    return $value
}

function Read-BundleUInt32($State) {
    if ($State.Position -gt $State.Bytes.Length - 4) {
        throw "The generated bundle manifest is truncated."
    }
    $value = [BitConverter]::ToUInt32($State.Bytes, $State.Position)
    $State.Position += 4
    return $value
}

function Read-BundleInt64($State) {
    if ($State.Position -gt $State.Bytes.Length - 8) {
        throw "The generated bundle manifest is truncated."
    }
    $value = [BitConverter]::ToInt64($State.Bytes, $State.Position)
    $State.Position += 8
    return $value
}

function Read-BundleUInt64($State) {
    if ($State.Position -gt $State.Bytes.Length - 8) {
        throw "The generated bundle manifest is truncated."
    }
    $value = [BitConverter]::ToUInt64($State.Bytes, $State.Position)
    $State.Position += 8
    return $value
}

function Read-BundleString($State) {
    # Bundle strings use BinaryWriter's 7-bit byte-count prefix. This is a
    # generation-time manifest reader, intentionally independent of the
    # production BundleReader used by the assertions below.
    [uint32]$length = 0
    $shift = 0
    do {
        if ($shift -ge 35) {
            throw "The generated bundle manifest has an invalid string length."
        }
        [byte]$current = Read-BundleByte $State
        $length = $length -bor ([uint32]($current -band 0x7f) -shl $shift)
        $shift += 7
    } while (($current -band 0x80) -ne 0)
    if ($length -gt [int]::MaxValue -or $length -gt ($State.Bytes.Length - $State.Position)) {
        throw "The generated bundle manifest string is truncated."
    }
    if ($length -eq 0) {
        return ''
    }
    $value = [Text.Encoding]::UTF8.GetString($State.Bytes, $State.Position, [int]$length)
    $State.Position += [int]$length
    return $value
}

function Get-BundleFileType([byte] $RawType) {
    switch ([int]$RawType) {
        1 { return 'Assembly' }
        2 { return 'NativeBinary' }
        3 { return 'DepsJson' }
        4 { return 'RuntimeConfigJson' }
        5 { return 'Symbols' }
        default { return 'Unknown' }
    }
}

function Get-GeneratedBundleInventory([string] $BundlePath) {
    # ReadAllBytes is limited to generated CI fixtures and keeps this expected
    # value independent from dnSpy.Bundles. In particular, it observes the
    # physical manifest's compressedSize rather than assuming every entry in a
    # compression-enabled publish was compressed (HostModel may keep a file
    # uncompressed when its ratio is insufficient).
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
        throw "The generated output does not contain a valid official bundle marker."
    }

    $state = [pscustomobject]@{ Bytes = $bytes; Position = [int]$header }
    [uint32]$major = Read-BundleUInt32 $state
    [uint32]$minor = Read-BundleUInt32 $state
    [int]$fileCount = Read-BundleInt32 $state
    if ($fileCount -lt 0) {
        throw "The generated bundle manifest has a negative file count."
    }
    [string]$bundleId = Read-BundleString $state
    [uint64]$flags = if ($major -eq 1) {
        1
    }
    else {
        [void](Read-BundleInt64 $state)
        [void](Read-BundleInt64 $state)
        [void](Read-BundleInt64 $state)
        [void](Read-BundleInt64 $state)
        Read-BundleUInt64 $state
    }
    $entries = @()
    for ($entryIndex = 0; $entryIndex -lt $fileCount; $entryIndex++) {
        [int64]$offset = Read-BundleInt64 $state
        [int64]$size = Read-BundleInt64 $state
        [int64]$compressedSize = if ($major -ge 6) { Read-BundleInt64 $state } else { 0 }
        [byte]$rawType = Read-BundleByte $state
        [string]$relativePath = (Read-BundleString $state).Replace('\', '/')
        $entries += [ordered]@{
            index = $entryIndex
            relativePath = $relativePath
            fileType = Get-BundleFileType $rawType
            rawFileType = [int]$rawType
            offset = $offset
            size = $size
            compressedSize = $compressedSize
            isCompressed = ($compressedSize -ne 0)
        }
    }
    return [ordered]@{
        markerOffset = [int64]$marker
        headerOffset = [int64]$header
        majorVersion = $major
        minorVersion = $minor
        bundleId = $bundleId
        manifestFlags = $flags
        entries = $entries
    }
}

$selectedNames = if ($Generation -eq '') { @($generations.Keys) } else { @($Generation) }
foreach ($generationName in $selectedNames) {
    $generationInfo = $generations[$generationName]
    $generationRoot = Join-Path $scriptRoot $generationName
    $projectPath = Join-Path $generationRoot 'App.csproj'
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Historical fixture project is missing: $projectPath"
    }

    # SDK selection is intentionally made from the generation directory. The
    # adjacent global.json therefore controls both this check and MSBuild.
    Push-Location $generationRoot
    try {
        $actualSdk = (& dotnet --version).Trim()
        if ($actualSdk -ne $generationInfo.SdkVersion) {
            throw "$generationName requires SDK $($generationInfo.SdkVersion), but dotnet --version returned $actualSdk"
        }

        foreach ($variant in $generationInfo.Variants) {
            $variantRoot = Join-Path (Join-Path $OutputRoot $generationName) $variant.Name
            $publishRoot = Join-Path $variantRoot 'publish'
            if (-not (Is-UnderRoot $OutputRoot $variantRoot)) {
                throw "Refusing to write outside the dedicated historical artifact root: $variantRoot"
            }
            Test-NoReparsePath (Split-Path -Parent $variantRoot)
            Test-NoReparsePath $variantRoot
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
                "-p:PathMap=$generationRoot=/_/SingleFile"
            )
            if ($generationInfo.ManifestMajorVersion -eq 6) {
                $properties += "-p:EnableCompressionInSingleFile=$($variant.Compressed.ToString().ToLowerInvariant())"
            }
            if ($variant.IncludesSymbols) {
                $properties += '-p:SingleFileFixtureIncludeSymbols=true'
                $properties += '-p:IncludeSymbolsInSingleFile=true'
            }
            if ($variant.CompatibilityMode) {
                $properties += '-p:IncludeAllContentForSelfExtract=true'
            }

            Write-Host "Generating $generationName/$($variant.Name) with SDK $($generationInfo.SdkVersion), TFM $($generationInfo.TargetFramework), RID win-x64, SelfContained $($variant.SelfContained.ToString().ToLowerInvariant())."
            Invoke-SingleFileFixturePhases `
                -ProjectPath $projectPath `
                -TargetFramework $generationInfo.TargetFramework `
                -RuntimeIdentifier 'win-x64' `
                -SelfContained ([bool]$variant.SelfContained) `
                -PublishRoot $publishRoot `
                -MSBuildProperties $properties

            $bundlePath = Get-RequiredSingleFile $publishRoot '*.exe' 'published bundle'
            $buildRoot = Join-Path $variantRoot 'build'
            $buildMain = Get-RequiredSingleFile $buildRoot 'SingleFile.App.dll' 'built main assembly'
            $buildDependency = Get-RequiredSingleFile (Join-Path $buildRoot 'SingleFile.Dependency') 'SingleFile.Dependency.dll' 'built dependency assembly'
            $publishedFiles = @(Get-PublishedFileRecords $variantRoot $publishRoot)
            $bundleInventory = Get-GeneratedBundleInventory $bundlePath
            if ($bundleInventory.majorVersion -ne $generationInfo.ManifestMajorVersion) {
                throw "$generationName/$($variant.Name) produced manifest v$($bundleInventory.majorVersion), expected v$($generationInfo.ManifestMajorVersion)."
            }
            $flags = if ($variant.CompatibilityMode -or $generationInfo.ManifestMajorVersion -eq 1) { [uint64]1 } else { [uint64]0 }
            if ([uint64]$bundleInventory.manifestFlags -ne $flags) {
                throw "$generationName/$($variant.Name) produced manifest flags $($bundleInventory.manifestFlags), expected $flags."
            }
            # These are actual ordered records from the generated manifest,
            # including any HostModel compression-ratio exceptions.
            $expectedEntries = @($bundleInventory.entries)

            $fixture = [ordered]@{
                schemaVersion = 2
                generation = $generationName
                sdkVersion = $generationInfo.SdkVersion
                targetFramework = $generationInfo.TargetFramework
                runtimeIdentifier = 'win-x64'
                manifestMajorVersion = [uint32]$bundleInventory.majorVersion
                manifestFlags = $flags
                variant = $variant.Name
                selfContained = [bool]$variant.SelfContained
                compressed = [bool]$variant.Compressed
                includesSymbols = [bool]$variant.IncludesSymbols
                compatibilityMode = [bool]$variant.CompatibilityMode
                bundle = Get-RelativePath $variantRoot $bundlePath
                buildMainAssembly = Get-RelativePath $variantRoot $buildMain
                buildDependencyAssembly = Get-RelativePath $variantRoot $buildDependency
                publishedFiles = $publishedFiles
                expectedEntries = $expectedEntries
                inventory = 'inventory.json'
                hashes = 'hashes.json'
            }
            Write-Json (Join-Path $variantRoot 'fixture.json') $fixture

            $inventory = [ordered]@{
                schemaVersion = 1
                generation = $generationName
                variant = $variant.Name
                manifestMajorVersion = $generationInfo.ManifestMajorVersion
                manifestFlags = $flags
                selfContained = [bool]$variant.SelfContained
                compressed = [bool]$variant.Compressed
                includesSymbols = [bool]$variant.IncludesSymbols
                entries = $expectedEntries
            }
            Write-Json (Join-Path $variantRoot 'inventory.json') $inventory
            Write-Json (Join-Path $variantRoot 'hashes.json') ([ordered]@{
                schemaVersion = 1
                files = $publishedFiles
            })
        }
    }
    finally {
        Pop-Location
    }
}
