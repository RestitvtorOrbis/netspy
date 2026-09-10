[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
	param(
		[Parameter(Mandatory = $true)] [bool]$Condition,
		[Parameter(Mandatory = $true)] [string]$Message
	)

	if (-not $Condition) {
		throw $Message
	}
}

function Assert-Equal {
	param(
		[Parameter(Mandatory = $true)] $Expected,
		[Parameter(Mandatory = $true)] $Actual,
		[Parameter(Mandatory = $true)] [string]$Message
	)

	if ($Expected -cne $Actual) {
		throw "$Message Expected '$Expected', actual '$Actual'."
	}
}

function Assert-ExpectedFailure {
	param(
		[Parameter(Mandatory = $true)] [scriptblock]$Action,
		[Parameter(Mandatory = $true)] [string]$Message
	)

	$failed = $false
	try {
		& $Action
	} catch {
		$failed = $true
	}
	Assert-True -Condition $failed -Message $Message
}

function Get-FileHashMap {
	param([Parameter(Mandatory = $true)] [string]$Root)

	$map = @{}
	foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) {
		$relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
		$map[$relativePath] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
	}
	return $map
}

function Get-ZipEntryText {
	param(
		[Parameter(Mandatory = $true)] [System.IO.Compression.ZipArchiveEntry]$Entry
	)

	$entryStream = $Entry.Open()
	$reader = [System.IO.StreamReader]::new($entryStream)
	try {
		return $reader.ReadToEnd()
	} finally {
		$reader.Dispose()
		$entryStream.Dispose()
	}
}

$tempParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testDirectoryName = "dnspy-blc004-packaging-$([guid]::NewGuid().ToString('N'))"
$testRoot = Join-Path $tempParent $testDirectoryName
$testRootLeaf = [System.IO.Path]::GetFileName($testRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar))
$testRootCreated = $false

try {
	$workflowPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.github\workflows\build.yml'))
	$workflowText = [System.IO.File]::ReadAllText($workflowPath)
	$archiveStepMatch = [System.Text.RegularExpressions.Regex]::Match(
		$workflowText,
		'(?ms)^      - name: Create release archive\r?\n(?<body>.*?)(?=^      - uses: actions/upload-artifact@)'
	)
	Assert-True -Condition $archiveStepMatch.Success -Message 'Could not isolate the release archive workflow step.'
	$archiveStepBody = $archiveStepMatch.Groups['body'].Value
	Assert-True -Condition ($archiveStepBody -match '(?m)^\s*& pwsh -NoProfile -File \.\\Build\\New-ReleaseArchive\.ps1 `\r?$') -Message 'Workflow does not invoke release archiving through pwsh -NoProfile -File.'
	Assert-True -Condition ($archiveStepBody -match '(?m)^\s*if \(\$LASTEXITCODE -ne 0\) \{\r?$') -Message 'Workflow release archive step lost its LASTEXITCODE check.'

	Assert-True -Condition ($testRootLeaf -match '^dnspy-blc004-packaging-[0-9a-f]{32}$') -Message 'Test cleanup path is not narrowly named.'
	[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
	$testRootCreated = $true

	$buildRoot = Join-Path $testRoot 'build'
	$buildBin = Join-Path $buildRoot 'bin'
	$outputRoot = Join-Path $testRoot 'output'
	$unsetOutputRoot = Join-Path $testRoot 'child-unset-output'
	$staleOutputRoot = Join-Path $testRoot 'child-stale-output'
	$missingBuildRoot = Join-Path $testRoot 'missing-build'
	$missingOutputRoot = Join-Path $testRoot 'child-missing-output'
	$existingOutputRoot = Join-Path $testRoot 'existing-output'
	$wrapperScript = Join-Path $testRoot 'Invoke-ReleaseArchiveChild.ps1'
	[System.IO.Directory]::CreateDirectory($buildBin) | Out-Null

	$files = [ordered]@{
		'dnSpy.exe' = 'synthetic executable; structural validation only'
		'bin/dnSpy.Bundles.x.dll' = 'bundle extension'
		'bin/dnSpy.Bundles.dll' = 'bundle library'
		'bin/Microsoft.NET.HostModel.Bundle.dll' = 'host model library'
		'bin/extra.txt' = 'extra package content'
		'root.txt' = 'root package content'
	}
	foreach ($relativePath in $files.Keys) {
		$filePath = Join-Path $buildRoot ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
		$fileParent = Split-Path -Parent $filePath
		if (-not (Test-Path -LiteralPath $fileParent -PathType Container)) {
			[System.IO.Directory]::CreateDirectory($fileParent) | Out-Null
		}
		[System.IO.File]::WriteAllText($filePath, $files[$relativePath], [System.Text.UTF8Encoding]::new($false))
	}

	$sourceCommit = '0123456789abcdef0123456789abcdef01234567'
	$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
	$wrapperContent = @'
[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)] [string]$RepositoryRoot,
	[Parameter(Mandatory = $true)] [string]$BuildDirectory,
	[Parameter(Mandatory = $true)] [string]$OutputDirectory,
	[Parameter(Mandatory = $true)] [ValidateSet('netframework', 'net', 'net-win32', 'net-win64')] [string]$PackageName,
	[Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string]$SourceCommit,
	[Parameter(Mandatory = $true)] [ValidateSet('Unset', 'Stale')] [string]$InitialExitState
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Set-Location -LiteralPath $RepositoryRoot
if ($InitialExitState -eq 'Unset') {
	Remove-Variable LASTEXITCODE -Scope Local -Force -ErrorAction SilentlyContinue
} else {
	& pwsh -NoProfile -Command 'exit 37'
	if ($LASTEXITCODE -ne 37) {
		throw "Could not establish the stale caller exit state; got $LASTEXITCODE."
	}
}

& pwsh -NoProfile -File .\Build\New-ReleaseArchive.ps1 `
	-BuildDirectory $BuildDirectory `
	-OutputDirectory $OutputDirectory `
	-PackageName $PackageName `
	-SourceCommit $SourceCommit
if ($LASTEXITCODE -ne 0) {
		throw "Release archive creation failed with exit code $LASTEXITCODE."
}
'@
	[System.IO.File]::WriteAllText($wrapperScript, $wrapperContent, [System.Text.UTF8Encoding]::new($false))

	function Invoke-ArchiveWrapper {
		param(
			[Parameter(Mandatory = $true)] [string]$BuildDirectory,
			[Parameter(Mandatory = $true)] [string]$OutputDirectory,
			[Parameter(Mandatory = $true)] [ValidateSet('Unset', 'Stale')] [string]$InitialExitState
		)

		$null = & pwsh -NoProfile -File $wrapperScript `
			-RepositoryRoot $repositoryRoot `
			-BuildDirectory $BuildDirectory `
			-OutputDirectory $OutputDirectory `
			-PackageName net-win64 `
			-SourceCommit $sourceCommit `
			-InitialExitState $InitialExitState 2>&1
		return [int]$LASTEXITCODE
	}

	$beforeHashes = Get-FileHashMap -Root $buildRoot
	$archiveScript = Join-Path $PSScriptRoot 'New-ReleaseArchive.ps1'
	& $archiveScript -BuildDirectory $buildRoot -OutputDirectory $outputRoot -PackageName net-win64 -SourceCommit $sourceCommit

	$archivePath = Join-Path $outputRoot 'netSpy-net-win64.zip'
	$checksumPath = "$archivePath.sha256"
	$sourcePath = "$archivePath.source.txt"
	Assert-True -Condition (Test-Path -LiteralPath $archivePath -PathType Leaf) -Message 'Archive was not created.'
	Assert-True -Condition (Test-Path -LiteralPath $checksumPath -PathType Leaf) -Message 'Checksum sidecar was not created.'
	Assert-True -Condition (Test-Path -LiteralPath $sourcePath -PathType Leaf) -Message 'Source sidecar was not created.'

	$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
	try {
		$entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
		Assert-Equal -Expected 6 -Actual $entryNames.Count -Message 'Unexpected archive entry count.'
		foreach ($relativePath in $files.Keys) {
			$zipPath = $relativePath.Replace('/', '/')
			Assert-True -Condition ($entryNames -ccontains $zipPath) -Message "Archive is missing root-relative path '$zipPath'."
			$entry = $archive.GetEntry($zipPath)
			Assert-Equal -Expected $files[$relativePath] -Actual (Get-ZipEntryText -Entry $entry) -Message "Archive content mismatch for '$zipPath'."
		}
		Assert-True -Condition (@($entryNames | Where-Object { $_ -match '^build/' }).Count -eq 0) -Message 'Archive contains a build-directory root.'
	} finally {
		$archive.Dispose()
	}

	$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
	$expectedChecksum = "$archiveHash  netSpy-net-win64.zip`n"
	Assert-Equal -Expected $expectedChecksum -Actual ([System.IO.File]::ReadAllText($checksumPath)) -Message 'Checksum sidecar content is incorrect.'
	Assert-Equal -Expected "$sourceCommit`n" -Actual ([System.IO.File]::ReadAllText($sourcePath)) -Message 'Source sidecar content is incorrect.'
	Assert-Equal -Expected $beforeHashes.Count -Actual (Get-FileHashMap -Root $buildRoot).Count -Message 'Build input file count changed.'
	$afterHashes = Get-FileHashMap -Root $buildRoot
	foreach ($relativePath in $beforeHashes.Keys) {
		Assert-Equal -Expected $beforeHashes[$relativePath] -Actual $afterHashes[$relativePath] -Message "Build input changed: $relativePath"
	}

	$unsetChildExitCode = Invoke-ArchiveWrapper -BuildDirectory $buildRoot -OutputDirectory $unsetOutputRoot -InitialExitState Unset
	Assert-Equal -Expected 0 -Actual $unsetChildExitCode -Message 'Successful child packaging failed with an initially unset LASTEXITCODE.'
	Assert-True -Condition (Test-Path -LiteralPath (Join-Path $unsetOutputRoot 'netSpy-net-win64.zip') -PathType Leaf) -Message 'Unset-state child packaging did not create an archive.'
	Assert-True -Condition (Test-Path -LiteralPath (Join-Path $unsetOutputRoot 'netSpy-net-win64.zip.sha256') -PathType Leaf) -Message 'Unset-state child packaging did not create a checksum sidecar.'
	Assert-True -Condition (Test-Path -LiteralPath (Join-Path $unsetOutputRoot 'netSpy-net-win64.zip.source.txt') -PathType Leaf) -Message 'Unset-state child packaging did not create a source sidecar.'

	$staleChildExitCode = Invoke-ArchiveWrapper -BuildDirectory $buildRoot -OutputDirectory $staleOutputRoot -InitialExitState Stale
	Assert-Equal -Expected 0 -Actual $staleChildExitCode -Message 'Successful child packaging failed with a stale nonzero LASTEXITCODE.'
	Assert-True -Condition (Test-Path -LiteralPath (Join-Path $staleOutputRoot 'netSpy-net-win64.zip') -PathType Leaf) -Message 'Stale-state child packaging did not create an archive.'
	Assert-True -Condition (Test-Path -LiteralPath (Join-Path $staleOutputRoot 'netSpy-net-win64.zip.sha256') -PathType Leaf) -Message 'Stale-state child packaging did not create a checksum sidecar.'
	Assert-True -Condition (Test-Path -LiteralPath (Join-Path $staleOutputRoot 'netSpy-net-win64.zip.source.txt') -PathType Leaf) -Message 'Stale-state child packaging did not create a source sidecar.'

	$overlappingOutputDirectories = @(
		$buildRoot,
		(Join-Path $buildRoot 'nested-output')
	)
	foreach ($overlappingOutputDirectory in $overlappingOutputDirectories) {
		$overlappingArchivePath = Join-Path $overlappingOutputDirectory 'netSpy-net.zip'
		Assert-ExpectedFailure -Action {
			& $archiveScript -BuildDirectory $buildRoot -OutputDirectory $overlappingOutputDirectory -PackageName net -SourceCommit $sourceCommit
		} -Message "Packaging did not reject overlapping output directory '$overlappingOutputDirectory'."
		Assert-True -Condition (-not (Test-Path -LiteralPath $overlappingArchivePath)) -Message 'Overlapping-directory validation created an archive.'
		Assert-True -Condition (-not (Test-Path -LiteralPath "$overlappingArchivePath.sha256")) -Message 'Overlapping-directory validation created a checksum sidecar.'
		Assert-True -Condition (-not (Test-Path -LiteralPath "$overlappingArchivePath.source.txt")) -Message 'Overlapping-directory validation created a source sidecar.'
		$afterOverlappingOutputHashes = Get-FileHashMap -Root $buildRoot
		Assert-Equal -Expected $beforeHashes.Count -Actual $afterOverlappingOutputHashes.Count -Message 'Overlapping-directory validation changed the build input file count.'
		foreach ($relativePath in $beforeHashes.Keys) {
			Assert-Equal -Expected $beforeHashes[$relativePath] -Actual $afterOverlappingOutputHashes[$relativePath] -Message "Overlapping-directory validation changed build input: $relativePath"
		}
	}
	Assert-True -Condition (-not (Test-Path -LiteralPath (Join-Path $buildRoot 'nested-output'))) -Message 'Overlapping-directory validation created an output directory inside the build input.'

	Copy-Item -LiteralPath $buildRoot -Destination $missingBuildRoot -Recurse
	Remove-Item -LiteralPath (Join-Path $missingBuildRoot 'bin/dnSpy.Bundles.dll') -Force
	Assert-ExpectedFailure -Action {
		& $archiveScript -BuildDirectory $missingBuildRoot -OutputDirectory $missingOutputRoot -PackageName net -SourceCommit $sourceCommit
	} -Message 'Packaging did not reject a missing required file.'
	Assert-True -Condition (-not (Test-Path -LiteralPath $missingOutputRoot)) -Message 'Missing-file validation created output.'

	$missingChildExitCode = Invoke-ArchiveWrapper -BuildDirectory $missingBuildRoot -OutputDirectory $missingOutputRoot -InitialExitState Unset
	Assert-True -Condition ($missingChildExitCode -ne 0) -Message 'Child packaging accepted a missing required file.'
	Assert-True -Condition (-not (Test-Path -LiteralPath $missingOutputRoot)) -Message 'Missing-file child validation created output.'

	[System.IO.Directory]::CreateDirectory($existingOutputRoot) | Out-Null
	$existingArchivePath = Join-Path $existingOutputRoot 'netSpy-netframework.zip'
	$existingChecksumPath = "$existingArchivePath.sha256"
	$existingSourcePath = "$existingArchivePath.source.txt"
	[System.IO.File]::WriteAllBytes($existingArchivePath, [byte[]](1, 2, 3, 4))
	[System.IO.File]::WriteAllText($existingChecksumPath, 'sentinel checksum', [System.Text.UTF8Encoding]::new($false))
	[System.IO.File]::WriteAllText($existingSourcePath, 'sentinel source', [System.Text.UTF8Encoding]::new($false))
	$existingArchiveBytes = [System.IO.File]::ReadAllBytes($existingArchivePath)
	$existingChecksumText = [System.IO.File]::ReadAllText($existingChecksumPath)
	$existingSourceText = [System.IO.File]::ReadAllText($existingSourcePath)
	Assert-ExpectedFailure -Action {
		& $archiveScript -BuildDirectory $buildRoot -OutputDirectory $existingOutputRoot -PackageName netframework -SourceCommit $sourceCommit
	} -Message 'Packaging did not reject a pre-existing archive or sidecar.'
	Assert-True -Condition ([System.Linq.Enumerable]::SequenceEqual($existingArchiveBytes, [System.IO.File]::ReadAllBytes($existingArchivePath))) -Message 'Pre-existing archive was replaced.'
	Assert-Equal -Expected $existingChecksumText -Actual ([System.IO.File]::ReadAllText($existingChecksumPath)) -Message 'Pre-existing checksum sidecar was replaced.'
	Assert-Equal -Expected $existingSourceText -Actual ([System.IO.File]::ReadAllText($existingSourcePath)) -Message 'Pre-existing source sidecar was replaced.'

	Write-Host 'Release packaging test passed.'
} finally {
	if ($testRootCreated) {
		$resolvedTempParent = (Resolve-Path -LiteralPath $tempParent).Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
		$resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
		if ($resolvedTestRoot -ne $resolvedTempParent -and $testRootLeaf -match '^dnspy-blc004-packaging-[0-9a-f]{32}$' -and (Test-Path -LiteralPath $testRoot)) {
			Remove-Item -LiteralPath $testRoot -Recurse -Force
		}
	}
}
