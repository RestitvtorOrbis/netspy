[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string]$BuildDirectory,

	[Parameter(Mandatory = $true)]
	[string]$OutputDirectory,

	[Parameter(Mandatory = $true)]
	[ValidateSet('netframework', 'net', 'net-win32', 'net-win64')]
	[string]$PackageName,

	[Parameter(Mandatory = $true)]
	[ValidatePattern('^[0-9a-fA-F]{40}$')]
	[string]$SourceCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require-File {
	param(
		[Parameter(Mandatory = $true)] [string]$Path,
		[Parameter(Mandatory = $true)] [string]$Description
	)

	if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		throw "Required $Description is missing: $Path"
	}
}

function Resolve-FullPath {
	param(
		[Parameter(Mandatory = $true)] [string]$Path
	)

	$fullPath = [System.IO.Path]::GetFullPath($Path)
	$missingParts = [System.Collections.Generic.Stack[string]]::new()
	$existingPath = $fullPath
	while (-not (Test-Path -LiteralPath $existingPath)) {
		$leafName = [System.IO.Path]::GetFileName($existingPath)
		if ([string]::IsNullOrEmpty($leafName)) {
			throw "Could not resolve path: $Path"
		}
		$missingParts.Push($leafName)
		$existingPath = [System.IO.Path]::GetDirectoryName($existingPath)
	}

	$resolvedPath = (Resolve-Path -LiteralPath $existingPath).Path
	while ($missingParts.Count -ne 0) {
		$resolvedPath = Join-Path $resolvedPath $missingParts.Pop()
	}
	return [System.IO.Path]::GetFullPath($resolvedPath)
}

function Test-PathEqualOrNested {
	param(
		[Parameter(Mandatory = $true)] [string]$Path,
		[Parameter(Mandatory = $true)] [string]$ParentPath
	)

	$comparison = if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
		[System.StringComparison]::OrdinalIgnoreCase
	} else {
		[System.StringComparison]::Ordinal
	}
	$directorySeparators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
	$pathRoot = [System.IO.Path]::GetPathRoot($Path)
	$parentPathRoot = [System.IO.Path]::GetPathRoot($ParentPath)
	$trimmedPath = if ($Path.Length -gt $pathRoot.Length) { $Path.TrimEnd($directorySeparators) } else { $Path }
	$trimmedParentPath = if ($ParentPath.Length -gt $parentPathRoot.Length) { $ParentPath.TrimEnd($directorySeparators) } else { $ParentPath }
	if ($trimmedPath.Equals($trimmedParentPath, $comparison)) {
		return $true
	}

	$parentBoundary = if ($trimmedParentPath.EndsWith([string][System.IO.Path]::DirectorySeparatorChar) -or
		$trimmedParentPath.EndsWith([string][System.IO.Path]::AltDirectorySeparatorChar)) {
		$trimmedParentPath
	} else {
		$trimmedParentPath + [System.IO.Path]::DirectorySeparatorChar
	}
	return $trimmedPath.StartsWith($parentBoundary, $comparison)
}

$resolvedBuildDirectory = $null
$resolvedOutputDirectory = $null
$archivePath = $null
$checksumPath = $null
$sourcePath = $null
$createdOutputDirectory = $false

try {
	if (-not (Test-Path -LiteralPath $BuildDirectory -PathType Container)) {
		throw "Build directory is missing or is not a directory: $BuildDirectory"
	}
	$resolvedBuildDirectory = Resolve-FullPath -Path $BuildDirectory
	$resolvedOutputDirectory = Resolve-FullPath -Path $OutputDirectory
	if (Test-PathEqualOrNested -Path $resolvedOutputDirectory -ParentPath $resolvedBuildDirectory) {
		throw "Output directory must not be equal to or nested under the build directory: $resolvedOutputDirectory"
	}

	$requiredFiles = @(
		@('dnSpy.exe', 'dnSpy.exe'),
		@('bin/dnSpy.Bundles.x.dll', 'dnSpy.Bundles.x.dll'),
		@('bin/dnSpy.Bundles.dll', 'dnSpy.Bundles.dll'),
		@('bin/Microsoft.NET.HostModel.Bundle.dll', 'Microsoft.NET.HostModel.Bundle.dll')
	)
	foreach ($requiredFile in $requiredFiles) {
		Require-File -Path (Join-Path $resolvedBuildDirectory $requiredFile[0]) -Description $requiredFile[1]
	}

	$archiveName = "netSpy-$PackageName.zip"
	if (Test-Path -LiteralPath $OutputDirectory -PathType Leaf) {
		throw "Output path exists as a file: $OutputDirectory"
	}
	$archivePath = Join-Path $resolvedOutputDirectory $archiveName
	$checksumPath = "$archivePath.sha256"
	$sourcePath = "$archivePath.source.txt"

	foreach ($outputPath in @($archivePath, $checksumPath, $sourcePath)) {
		if (Test-Path -LiteralPath $outputPath) {
			throw "Refusing to replace existing output: $outputPath"
		}
	}

	Write-Host 'Validated required package files structurally; dnSpy.exe was not executed.'
	if (-not (Test-Path -LiteralPath $resolvedOutputDirectory -PathType Container)) {
		[System.IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null
		$createdOutputDirectory = $true
	}

	[System.IO.Compression.ZipFile]::CreateFromDirectory(
		$resolvedBuildDirectory,
		$archivePath,
		[System.IO.Compression.CompressionLevel]::Optimal,
		$false
	)

	$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
	$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
	[System.IO.File]::WriteAllText($checksumPath, "$archiveHash  $archiveName`n", $utf8NoBom)
	[System.IO.File]::WriteAllText($sourcePath, "$SourceCommit`n", $utf8NoBom)

	Write-Host "Archive: $archivePath"
	Write-Host "SHA256: $checksumPath"
	Write-Host "Source: $sourcePath"
} catch {
	if ($archivePath -and (Test-Path -LiteralPath $archivePath) -and -not (Test-Path -LiteralPath $checksumPath) -and -not (Test-Path -LiteralPath $sourcePath)) {
		Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
	}
	if ($createdOutputDirectory -and $resolvedOutputDirectory -and (Test-Path -LiteralPath $resolvedOutputDirectory -PathType Container)) {
		$remainingOutputFiles = @(Get-ChildItem -LiteralPath $resolvedOutputDirectory -Force -ErrorAction SilentlyContinue)
		if ($remainingOutputFiles.Count -eq 0) {
			Remove-Item -LiteralPath $resolvedOutputDirectory -Force -ErrorAction SilentlyContinue
		}
	}
	throw
}
