[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceCommit = '0123456789abcdef0123456789abcdef01234567'
$otherCommit = 'fedcba9876543210fedcba9876543210fedcba98'
$repository = 'RestitvtorOrbis/netspy'
$runUrl = "https://github.com/$repository/actions/runs/1001"
$automaticTag = "build-$sourceCommit"
$archiveNames = @(
	'netSpy-netframework.zip',
	'netSpy-net.zip',
	'netSpy-net-win32.zip',
	'netSpy-net-win64.zip'
)
$assetNames = @($archiveNames + 'SHA256SUMS.txt')

$publicationScript = Join-Path $PSScriptRoot 'Publish-Release.ps1'
. $publicationScript -EventName test -EventAction test -Ref test -SourceCommit $sourceCommit `
	-Repository $repository -RunUrl $runUrl -ArtifactDirectory $PSScriptRoot

function Assert-True {
	param([bool]$Condition, [string]$Message)
	if (-not $Condition) { throw $Message }
}

function Assert-Equal {
	param($Expected, $Actual, [string]$Message)
	if ($Expected -cne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." }
}

function Assert-BytesEqual {
	param([byte[]]$Expected, [byte[]]$Actual, [string]$Message)
	Assert-True -Condition ([System.Linq.Enumerable]::SequenceEqual($Expected, $Actual)) -Message $Message
}

function Invoke-Case {
	param([string]$Name, [scriptblock]$Action)
	try {
		& $Action
		$script:PassedCases++
		Write-Host "PASS: $Name"
	}
	catch {
		throw "FAIL: $Name`n$($_.Exception.Message)`nLast fake command: $script:LastFakeCommand"
	}
}

function Assert-Fails {
	param([scriptblock]$Action, [string]$Message)
	$failed = $false
	try { & $Action | Out-Null } catch { $failed = $true }
	Assert-True $failed $Message
}

function Get-Mutations {
	param($State)
	return @($State.Calls | Where-Object { $_[0] -ceq 'release' -and $_[1] -in @('create', 'edit', 'upload', 'delete') })
}

function Assert-NoDangerousArguments {
	param($State)
	foreach ($call in $State.Calls) {
		foreach ($argument in $call) {
			Assert-True ($argument -notin @('--force', '--clobber', 'delete')) "Forbidden mutation argument was used: $argument"
		}
	}
}

function New-ArtifactSet {
	param([string]$Root, [string]$Commit = $sourceCommit)
	[System.IO.Directory]::CreateDirectory($Root) | Out-Null
	for ($index = 0; $index -lt $archiveNames.Count; $index++) {
		$name = $archiveNames[$index]
		$directory = Join-Path $Root "variant-$index"
		[System.IO.Directory]::CreateDirectory($directory) | Out-Null
		$archivePath = Join-Path $directory $name
		[System.IO.File]::WriteAllBytes($archivePath, [Text.Encoding]::UTF8.GetBytes("archive:${name}:$index"))
		$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
		[System.IO.File]::WriteAllText("$archivePath.sha256", "$hash  $name`n", [Text.UTF8Encoding]::new($false))
		[System.IO.File]::WriteAllText("$archivePath.source.txt", "$Commit`n", [Text.UTF8Encoding]::new($false))
	}
}

function Get-LocalAssetBytes {
	param([string]$ArtifactRoot)
	$temporary = Join-Path $script:TestRoot ([guid]::NewGuid().ToString('N'))
	$local = Test-LocalReleaseArtifacts -ArtifactDirectory $ArtifactRoot -SourceCommit $sourceCommit -TemporaryDirectory $temporary
	$result = @{}
	foreach ($name in $assetNames) { $result[$name] = [IO.File]::ReadAllBytes($local.Assets[$name].Path) }
	return $result
}

function New-ReleaseObject {
	param($State)
	$assets = @()
	foreach ($name in $State.Assets.Keys) {
		$assets += [pscustomobject]@{ id = "asset-$name"; name = $name }
	}
	foreach ($item in $State.AssetEntries) { $assets += [pscustomobject]@{ id = $item.Id; name = $item.Name } }
	return [pscustomobject]@{
		id = [long]$State.ReleaseId
		tag_name = $State.Tag
		name = $State.Title
		prerelease = [bool]$State.Prerelease
		assets = $assets
	}
}

function New-FakeState {
	param(
		[string]$Tag = $automaticTag,
		[ValidateSet('absent', 'lightweight', 'annotated')] [string]$TagKind = 'lightweight',
		[bool]$ReleaseExists = $true,
		[string]$Body = '',
		[hashtable]$Assets = @{}
	)
	return [pscustomobject]@{
		Tag = $Tag
		TagKind = $TagKind
		TagCommit = $sourceCommit
		TagObjectSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
		ReleaseExists = $ReleaseExists
		ReleaseId = [long]42
		Title = if ($Tag -ceq $automaticTag) { "netSpy build $($sourceCommit.Substring(0, 12))" } else { 'Published release' }
		Prerelease = ($Tag -ceq $automaticTag)
		Body = $Body
		Assets = $Assets
		AssetEntries = [Collections.ArrayList]::new()
		Calls = [Collections.ArrayList]::new()
		MutationCount = 0
		FailReadPattern = $null
		FailMutationNumber = 0
		RefetchAction = $null
		SnapshotNumber = 0
		AutomaticReleasePages = $null
	}
}

$script:FakeState = $null
$script:LastFakeCommand = ''
$fakeGh = {
	param([object[]]$InvocationArguments)
	[string[]]$Arguments = if ($InvocationArguments.Count -eq 1 -and $InvocationArguments[0] -is [array]) {
		@($InvocationArguments[0] | ForEach-Object { [string]$_ })
	} else {
		@($InvocationArguments | ForEach-Object { [string]$_ })
	}
	$state = $script:FakeState
	[void]$state.Calls.Add([string[]]@($Arguments))
	$key = $Arguments -join ' '
	$script:LastFakeCommand = $key
	if ($null -ne $state.FailReadPattern -and $key -match $state.FailReadPattern) {
		return [pscustomobject]@{ ExitCode = 23; Stdout = 'injected read error' }
	}
	if ($Arguments[0] -ceq 'api' -and $Arguments[-1] -like 'repos/*/git/matching-refs/tags/*') {
		$state.SnapshotNumber++
		if ($state.SnapshotNumber -eq 2 -and $null -ne $state.RefetchAction) { & $state.RefetchAction $state }
		if ($state.TagKind -ceq 'absent') { return [pscustomobject]@{ ExitCode = 0; Stdout = '' } }
		$type = if ($state.TagKind -ceq 'annotated') { 'tag' } else { 'commit' }
		$sha = if ($type -ceq 'tag') { $state.TagObjectSha } else { $state.TagCommit }
		$json = @([pscustomobject]@{ ref = "refs/tags/$($state.Tag)"; object = [pscustomobject]@{ type = $type; sha = $sha } }) | ConvertTo-Json -Compress -Depth 6
		return [pscustomobject]@{ ExitCode = 0; Stdout = $json }
	}
	if ($Arguments[0] -ceq 'api' -and $Arguments[-1] -like 'repos/*/git/tags/*') {
		$json = [pscustomobject]@{ object = [pscustomobject]@{ type = 'commit'; sha = $state.TagCommit } } | ConvertTo-Json -Compress
		return [pscustomobject]@{ ExitCode = 0; Stdout = $json }
	}
	if ($Arguments[0] -ceq 'api' -and $Arguments[-1] -like 'repos/*/releases?per_page=100') {
		$json = if ($null -ne $state.AutomaticReleasePages) {
			$flattened = @($state.AutomaticReleasePages | ForEach-Object { @($_) })
			$flattened | ConvertTo-Json -Compress -Depth 8
		} elseif ($state.ReleaseExists) {
			@(New-ReleaseObject $state) | ConvertTo-Json -Compress -Depth 8
		} else { '' }
		return [pscustomobject]@{ ExitCode = 0; Stdout = $json }
	}
	if ($Arguments[0] -ceq 'api' -and $Arguments[-1] -match 'repos/.+/releases/\d+$') {
		if (-not $state.ReleaseExists) { return [pscustomobject]@{ ExitCode = 1; Stdout = 'missing' } }
		return [pscustomobject]@{ ExitCode = 0; Stdout = ((New-ReleaseObject $state) | ConvertTo-Json -Compress -Depth 8) }
	}
	if ($Arguments.Count -eq 7 -and $Arguments[0] -ceq 'release' -and $Arguments[1] -ceq 'view') {
		return [pscustomobject]@{ ExitCode = 0; Stdout = ([pscustomobject]@{ body = $state.Body } | ConvertTo-Json -Compress) }
	}
	if ($Arguments[0] -ceq 'release' -and $Arguments[1] -ceq 'download') {
		$name = $Arguments[[Array]::IndexOf($Arguments, '--pattern') + 1]
		$directory = $Arguments[[Array]::IndexOf($Arguments, '--dir') + 1]
		if (-not $state.Assets.ContainsKey($name)) { return [pscustomobject]@{ ExitCode = 4; Stdout = 'missing asset' } }
		[IO.File]::WriteAllBytes((Join-Path $directory $name), [byte[]]$state.Assets[$name])
		return [pscustomobject]@{ ExitCode = 0; Stdout = '' }
	}
	if ($Arguments[0] -ceq 'release' -and $Arguments[1] -in @('create', 'edit', 'upload')) {
		$state.MutationCount++
		if ($Arguments[1] -ceq 'create') {
			$state.Tag = $Arguments[2]
			$state.TagKind = 'lightweight'
			$targetIndex = [Array]::IndexOf($Arguments, '--target')
			if ($targetIndex -ge 0) { $state.TagCommit = $Arguments[$targetIndex + 1] }
			$state.ReleaseExists = $true
			$state.Title = $Arguments[[Array]::IndexOf($Arguments, '--title') + 1]
			$state.Prerelease = ($Arguments -ccontains '--prerelease')
			$state.Body = [IO.File]::ReadAllText($Arguments[[Array]::IndexOf($Arguments, '--notes-file') + 1])
		}
		elseif ($Arguments[1] -ceq 'edit') {
			$state.Body = [IO.File]::ReadAllText($Arguments[[Array]::IndexOf($Arguments, '--notes-file') + 1])
		}
		else {
			$path = $Arguments[3]
			$state.Assets[[IO.Path]::GetFileName($path)] = [IO.File]::ReadAllBytes($path)
		}
		$exitCode = if ($state.FailMutationNumber -eq $state.MutationCount) { 17 } else { 0 }
		return [pscustomobject]@{ ExitCode = $exitCode; Stdout = if ($exitCode) { 'injected mutation error' } else { '' } }
	}
	throw "Unexpected gh invocation: $key"
}

function Invoke-Publication {
	param(
		$State,
		[string]$ArtifactRoot = $script:Artifacts,
		[string]$EventName = 'push',
		[string]$EventAction = 'none',
		[string]$Ref = 'refs/heads/master',
		[string]$ReleaseTag,
		[Nullable[long]]$ReleaseId
	)
	$script:FakeState = $State
	$arguments = @{
		EventName = $EventName; EventAction = $EventAction; Ref = $Ref
		SourceCommit = $sourceCommit; Repository = $repository; RunUrl = $runUrl
		ArtifactDirectory = $ArtifactRoot; GhInvoker = $fakeGh
	}
	if (-not [string]::IsNullOrEmpty($ReleaseTag)) { $arguments.ReleaseTag = $ReleaseTag }
	if ($null -ne $ReleaseId) { $arguments.ReleaseId = $ReleaseId }
	return Invoke-ReleasePublication @arguments
}

function New-CompleteState {
	param([string]$Body)
	$state = New-FakeState -Body $Body
	foreach ($name in $assetNames) { $state.Assets[$name] = $script:LocalBytes[$name] }
	return $state
}

$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testLeaf = "dnspy-blc004-publication-$([guid]::NewGuid().ToString('N'))"
$script:TestRoot = Join-Path $tempParent $testLeaf
$created = $false
$script:PassedCases = 0

try {
	Assert-True ($testLeaf -match '^dnspy-blc004-publication-[0-9a-f]{32}$') 'Unsafe test directory name.'
	[IO.Directory]::CreateDirectory($script:TestRoot) | Out-Null
	$created = $true
	$script:Artifacts = Join-Path $script:TestRoot 'artifacts'
	New-ArtifactSet $script:Artifacts
	$script:LocalBytes = Get-LocalAssetBytes $script:Artifacts
	$expectedNotes = New-ReleaseNotes -SourceCommit $sourceCommit -RunUrl $runUrl -Repository $repository

	Invoke-Case 'local validation writes deterministic SHA256SUMS' {
		$one = Test-LocalReleaseArtifacts $script:Artifacts $sourceCommit (Join-Path $script:TestRoot 'local-one')
		$two = Test-LocalReleaseArtifacts $script:Artifacts $sourceCommit (Join-Path $script:TestRoot 'local-two')
		Assert-Equal $one.ChecksumText $two.ChecksumText 'Combined checksum output is not deterministic.'
		$lines = @($one.ChecksumText.TrimEnd("`n").Split("`n"))
		Assert-Equal 4 $lines.Count 'Combined checksum must contain four archives.'
		$expectedOrder = @($archiveNames | Sort-Object)
		for ($index = 0; $index -lt 4; $index++) { Assert-True ($lines[$index].EndsWith("  $($expectedOrder[$index])")) 'Combined checksum order is incorrect.' }
	}

	$localFailures = @(
		@('missing triplet file', { param($root) Remove-Item (Get-ChildItem $root -Recurse -Filter '*.sha256' | Select-Object -First 1).FullName }),
		@('extra file', { param($root) [IO.File]::WriteAllText((Join-Path $root 'extra.txt'), 'extra') }),
		@('case alias', { param($root) $file = Get-ChildItem $root -Recurse -Filter 'netSpy-net.zip' | Select-Object -First 1; Rename-Item $file.FullName 'NETSPY-NET.ZIP' }),
		@('bad checksum', { param($root) $file = Get-ChildItem $root -Recurse -Filter '*.sha256' | Select-Object -First 1; [IO.File]::WriteAllText($file.FullName, ('0' * 64) + '  netSpy-netframework.zip' + "`n") }),
		@('wrong checksum basename', { param($root) $file = Get-ChildItem $root -Recurse -Filter '*.sha256' | Select-Object -First 1; [IO.File]::WriteAllText($file.FullName, ('0' * 64) + '  wrong.zip' + "`n") }),
		@('wrong source', { param($root) $file = Get-ChildItem $root -Recurse -Filter '*.source.txt' | Select-Object -First 1; [IO.File]::WriteAllText($file.FullName, "$otherCommit`n") }),
		@('duplicate basename', { param($root) $file = Get-ChildItem $root -Recurse -Filter 'netSpy-net.zip' | Select-Object -First 1; $dir = Join-Path $root duplicate; [IO.Directory]::CreateDirectory($dir) | Out-Null; Copy-Item $file.FullName $dir })
	)
	foreach ($item in $localFailures) {
		Invoke-Case "rejects local $($item[0]) before mutation" {
			$root = Join-Path $script:TestRoot ([guid]::NewGuid().ToString('N'))
			Copy-Item $script:Artifacts $root -Recurse
			& $item[1] $root
			$state = New-FakeState
			Assert-Fails { Invoke-Publication $state $root } "Local $($item[0]) was accepted."
			Assert-Equal 0 @(Get-Mutations $state).Count 'Local validation failure made a mutation call.'
		}
	}

	$ineligible = @(
		@('pull_request', 'none', 'refs/pull/1/merge'), @('push', 'none', 'refs/heads/topic'),
		@('workflow_dispatch', 'none', 'refs/heads/topic'), @('release', 'released', 'refs/tags/v1'),
		@('release', 'unpublished', 'refs/tags/v1'), @('schedule', 'none', 'refs/heads/master')
	)
	foreach ($event in $ineligible) {
		Invoke-Case "ineligible $($event -join '/') performs zero gh calls" {
			$state = New-FakeState
			$result = Invoke-Publication $state (Join-Path $script:TestRoot 'does-not-exist') $event[0] $event[1] $event[2]
			Assert-True (-not $result.Eligible) 'Ineligible event was marked eligible.'
			Assert-Equal 0 $state.Calls.Count 'Ineligible event contacted gh.'
		}
	}

	foreach ($eventName in @('push', 'workflow_dispatch')) {
		Invoke-Case "eligible $eventName creates exact automatic prerelease" {
			$state = New-FakeState -TagKind absent -ReleaseExists $false
			$result = Invoke-Publication $state $script:Artifacts $eventName
			Assert-True ($result.Created -and $result.Mutated) 'Automatic release was not created.'
			$create = @(Get-Mutations $state | Where-Object { $_[1] -ceq 'create' })
			Assert-Equal 1 $create.Count 'Expected one release create call.'
			Assert-True ($create[0] -ccontains '--target' -and $create[0] -ccontains $sourceCommit) 'Create target was not the exact source SHA.'
			Assert-True ($create[0] -ccontains '--prerelease' -and $create[0] -ccontains "netSpy build $($sourceCommit.Substring(0, 12))") 'Create identity was incorrect.'
			Assert-Equal 5 $state.Assets.Count 'Create path did not upload five assets.'
			Assert-NoDangerousArguments $state
		}
	}

	Invoke-Case 'workflow dispatch with a nonempty action remains eligible' {
		$state = New-FakeState -TagKind absent -ReleaseExists $false
		$result = Invoke-Publication $state $script:Artifacts workflow_dispatch 'sentinel-action'
		Assert-True ($result.Eligible -and $result.Kind -ceq 'automatic') 'Workflow dispatch action incorrectly changed eligibility.'
		Assert-Equal 1 @(Get-Mutations $state | Where-Object { $_[1] -ceq 'create' }).Count 'Eligible workflow dispatch did not create one release.'
		Assert-Equal 5 $state.Assets.Count 'Eligible workflow dispatch did not upload five assets.'
	}

	foreach ($kind in @('lightweight', 'annotated')) {
		Invoke-Case "matching $kind tag resumes" {
			$state = New-FakeState -TagKind $kind -ReleaseExists $false
			$result = Invoke-Publication $state
			Assert-True $result.Created 'Matching tag did not permit release creation.'
			$create = @(Get-Mutations $state | Where-Object { $_[1] -ceq 'create' })[0]
			Assert-True ($create -notcontains '--target') 'Existing matching tag should not receive --target.'
		}
	}

	$identityFailures = @(
		@('wrong peeled tag', { param($s) $s.TagCommit = $otherCommit }),
		@('automatic title conflict', { param($s) $s.Title = 'wrong' }),
		@('automatic prerelease conflict', { param($s) $s.Prerelease = $false }),
		@('tag read error', { param($s) $s.FailReadPattern = 'matching-refs' }),
		@('release read error', { param($s) $s.FailReadPattern = 'releases\?per_page' })
	)
	foreach ($item in $identityFailures) {
		Invoke-Case "rejects $($item[0]) before mutation" {
			$state = New-FakeState
			& $item[1] $state
			Assert-Fails { Invoke-Publication $state } "$($item[0]) was accepted."
			Assert-Equal 0 @(Get-Mutations $state).Count 'Identity/read failure mutated remote state.'
		}
	}

	Invoke-Case 'published release event succeeds with exact tag and id' {
		$tag = 'v1.2.3'
		$state = New-FakeState -Tag $tag
		$result = Invoke-Publication $state $script:Artifacts release published 'refs/tags/v1.2.3' $tag 42
		Assert-True ($result.Eligible -and $result.Kind -ceq 'release') 'Published release did not proceed.'
		Assert-Equal 5 $state.Assets.Count 'Published release did not receive all assets.'
	}

	$releaseFailures = @(
		@('wrong event release id', 'v1.2.3', 41, { param($s) }),
		@('wrong event release tag', 'v9', 42, { param($s) }),
		@('missing event release', 'v1.2.3', 42, { param($s) $s.ReleaseExists = $false }),
		@('event repository read error', 'v1.2.3', 42, { param($s) $s.FailReadPattern = 'releases/42' })
	)
	foreach ($item in $releaseFailures) {
		Invoke-Case "rejects $($item[0])" {
			$state = New-FakeState -Tag 'v1.2.3'
			& $item[3] $state
			Assert-Fails { Invoke-Publication $state $script:Artifacts release published 'refs/tags/v1.2.3' $item[1] $item[2] } "$($item[0]) was accepted."
			Assert-Equal 0 @(Get-Mutations $state).Count 'Event identity failure mutated remote state.'
		}
	}

	Invoke-Case 'notes preserve Unicode CRLF and trailing prose exactly' {
		$body = "Préface Ω`r`nsecond line`r`ntrailing  "
		$state = New-FakeState -Body $body
		Invoke-Publication $state | Out-Null
		Assert-Equal ($body + "`n`n" + $expectedNotes) $state.Body 'Existing release prose was not preserved exactly.'
		Assert-Equal 1 ([regex]::Matches($state.Body, '<!-- netspy-build:').Count) 'Marker was not appended exactly once.'
	}

	Invoke-Case 'matching marker from an earlier run is reused byte-for-byte' {
		$oldNotes = New-ReleaseNotes $sourceCommit "https://github.com/$repository/actions/runs/999" $repository
		$body = "intro`r`n`r`n" + $oldNotes.Replace("`n", "`r`n") + "`r`ntrailing"
		$state = New-CompleteState $body
		$result = Invoke-Publication $state
		Assert-True (-not $result.Mutated) 'Complete matching state was not a no-op.'
		Assert-Equal $body $state.Body 'Matching marker body changed.'
		Assert-Equal 0 @(Get-Mutations $state).Count 'Complete matching state made a mutation.'
	}

	Invoke-Case 'matching marker with an attacker host fails before mutation' {
		$attackerNotes = $expectedNotes.Replace('https://github.com/', 'https://attacker.example/')
		$state = New-CompleteState $attackerNotes
		Assert-Fails { Invoke-Publication $state } 'A matching marker from an attacker-controlled host was accepted.'
		Assert-Equal 0 @(Get-Mutations $state).Count 'Attacker-host marker mutated remote state.'
	}

	Invoke-Case 'automatic release lookup handles paginated JSON documents' {
		$state = New-CompleteState $expectedNotes
		$decoy = [pscustomobject]@{
			id = [long]7
			tag_name = 'build-decoy'
			name = 'Decoy release'
			prerelease = $true
			assets = @()
		}
		$state.AutomaticReleasePages = @(@($decoy), @((New-ReleaseObject $state)))
		$result = Invoke-Publication $state
		Assert-True ($result.Eligible -and -not $result.Mutated) 'Paginated release lookup did not find the complete matching release.'
		Assert-Equal 0 @(Get-Mutations $state).Count 'Paginated complete state made a mutation.'
		$lookups = @($state.Calls | Where-Object { $_[0] -ceq 'api' -and $_[-1] -like 'repos/*/releases?per_page=100' })
		Assert-Equal 2 $lookups.Count 'Expected both bounded preflight release lookups.'
		foreach ($lookup in $lookups) {
			Assert-True ($lookup -ccontains '--paginate') 'Automatic release lookup omitted pagination.'
			Assert-True ($lookup -ccontains '--slurp') 'Automatic release lookup did not combine page documents.'
			$jqIndex = [Array]::IndexOf($lookup, '--jq')
			Assert-True ($jqIndex -ge 0 -and $lookup[$jqIndex + 1] -ceq 'flatten') 'Automatic release lookup did not flatten page arrays.'
		}
	}

	$badBodies = @(
		@('other SHA marker', $expectedNotes.Replace($sourceCommit, $otherCommit)),
		@('source mismatch', $expectedNotes.Replace("Source SHA: ``$sourceCommit``", "Source SHA: ``$otherCommit``")),
		@('malformed marker', $expectedNotes.Replace('<!-- /netspy-build -->', '')),
		@('duplicate marker', "$expectedNotes`n$expectedNotes"),
		@('conflicting section', $expectedNotes.Replace('includes the x64 runtime', 'different text')),
		@('wrong repository URL', $expectedNotes.Replace($repository, 'someone/else'))
	)
	foreach ($item in $badBodies) {
		Invoke-Case "rejects $($item[0]) before mutation" {
			$state = New-FakeState -Body $item[1]
			Assert-Fails { Invoke-Publication $state } "$($item[0]) was accepted."
			Assert-Equal 0 @(Get-Mutations $state).Count 'Bad marker state mutated release.'
		}
	}

	$remoteConflicts = @(
		@('bad zip bytes', { param($s) $s.Assets['netSpy-net.zip'] = [byte[]](1,2,3) }),
		@('conflicting checksum bytes', { param($s) $s.Assets['SHA256SUMS.txt'] = [byte[]](9,8,7) }),
		@('duplicate expected asset', { param($s) $s.Assets['netSpy-net.zip'] = $script:LocalBytes['netSpy-net.zip']; [void]$s.AssetEntries.Add([pscustomobject]@{ Id='duplicate'; Name='netSpy-net.zip' }) }),
		@('case alias asset', { param($s) [void]$s.AssetEntries.Add([pscustomobject]@{ Id='alias'; Name='NETSPY-NET.ZIP' }) })
	)
	foreach ($item in $remoteConflicts) {
		Invoke-Case "rejects remote $($item[0]) before mutation" {
			$state = New-FakeState
			& $item[1] $state
			Assert-Fails { Invoke-Publication $state } "Remote $($item[0]) was accepted."
			Assert-Equal 0 @(Get-Mutations $state).Count 'Remote conflict mutated release.'
		}
	}

	Invoke-Case 'unrelated remote asset is untouched' {
		$state = New-FakeState
		$state.Assets['unrelated.log'] = [byte[]](4,5,6)
		Invoke-Publication $state | Out-Null
		Assert-BytesEqual ([byte[]](4,5,6)) $state.Assets['unrelated.log'] 'Unrelated asset changed.'
		Assert-NoDangerousArguments $state
	}

	$resumeCases = @(
		@('notes only', $expectedNotes, @()),
		@('markerless assets', '', @('netSpy-net.zip')),
		@('checksum only', '', @('SHA256SUMS.txt')),
		@('partial assets', $expectedNotes, @('netSpy-netframework.zip', 'netSpy-net-win64.zip'))
	)
	foreach ($item in $resumeCases) {
		Invoke-Case "resumes $($item[0])" {
			$state = New-FakeState -Body $item[1]
			foreach ($name in $item[2]) { $state.Assets[$name] = $script:LocalBytes[$name] }
			Invoke-Publication $state | Out-Null
			Assert-Equal 5 @($assetNames | Where-Object { $state.Assets.ContainsKey($_) }).Count 'Resume did not produce all expected assets.'
			Assert-Equal 1 ([regex]::Matches($state.Body, '<!-- netspy-build:').Count) 'Resume did not produce exactly one marker.'
			Assert-NoDangerousArguments $state
		}
	}

	Invoke-Case 'refetch state change fails before mutation' {
		$state = New-FakeState
		$state.RefetchAction = { param($s) $s.Body = 'changed between preflights' }
		Assert-Fails { Invoke-Publication $state } 'Refetch change was accepted.'
		Assert-Equal 0 @(Get-Mutations $state).Count 'Refetch conflict mutated release.'
	}

	Invoke-Case 'malformed API response and asset download error fail closed' {
		$state = New-FakeState
		$state.FailReadPattern = 'release view'
		Assert-Fails { Invoke-Publication $state } 'Release body read error was accepted.'
		Assert-Equal 0 @(Get-Mutations $state).Count 'Body read error mutated release.'
		$state = New-FakeState
		$state.Assets['netSpy-net.zip'] = $script:LocalBytes['netSpy-net.zip']
		$state.FailReadPattern = 'release download'
		Assert-Fails { Invoke-Publication $state } 'Asset download error was accepted.'
		Assert-Equal 0 @(Get-Mutations $state).Count 'Asset read error mutated release.'
	}

	for ($failureNumber = 1; $failureNumber -le 6; $failureNumber++) {
		Invoke-Case "retry after persisted mutation failure $failureNumber" {
			$state = New-FakeState
			$state.FailMutationNumber = $failureNumber
			Assert-Fails { Invoke-Publication $state } "Injected mutation failure $failureNumber did not propagate."
			Assert-Equal $failureNumber $state.MutationCount 'Unexpected mutation count before injected failure.'
			$state.FailMutationNumber = 0
			$beforeRetryCalls = $state.Calls.Count
			$result = Invoke-Publication $state
			Assert-Equal 5 @($assetNames | Where-Object { $state.Assets.ContainsKey($_) }).Count 'Retry did not complete assets.'
			Assert-Equal 1 ([regex]::Matches($state.Body, '<!-- netspy-build:').Count) 'Retry duplicated or lost marker.'
			Assert-True ($state.Calls.Count -gt $beforeRetryCalls) 'Retry did not rerun remote validation.'
			Assert-NoDangerousArguments $state
		}
	}

	Write-Host "Release publication tests passed: $script:PassedCases cases."
}
finally {
	if ($created) {
		$resolvedRoot = [IO.Path]::GetFullPath($script:TestRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
		$resolvedParent = $tempParent.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
		if ($resolvedRoot -ne $resolvedParent -and [IO.Path]::GetFileName($resolvedRoot) -match '^dnspy-blc004-publication-[0-9a-f]{32}$') {
			Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
		}
	}
}
