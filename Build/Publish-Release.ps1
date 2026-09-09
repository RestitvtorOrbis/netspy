[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $EventName,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string] $EventAction,

    [Parameter(Mandatory = $true)]
    [string] $Ref,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $SourceCommit,

    [Parameter(Mandatory = $true)]
    [string] $Repository,

    [Parameter(Mandatory = $true)]
    [string] $RunUrl,

    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory,

    [Parameter(Mandatory = $false)]
    [string] $ReleaseTag,

    [Parameter(Mandatory = $false)]
    [Nullable[long]] $ReleaseId,

    [Parameter(Mandatory = $false)]
    [scriptblock] $GhInvoker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:BLC004ArchiveNames = @(
    'netSpy-netframework.zip',
    'netSpy-net.zip',
    'netSpy-net-win32.zip',
    'netSpy-net-win64.zip'
)

$script:BLC004ExpectedAssetNames = @(
    $script:BLC004ArchiveNames + 'SHA256SUMS.txt'
)

function Throw-BLC004Error {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    throw [System.InvalidOperationException]::new("BLC-004: $Message")
}

function Get-BLC004Property {
    param(
        [Parameter(Mandatory = $true)]
        [object] $InputObject,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        Throw-BLC004Error "The GitHub response is missing '$Name'."
    }

    return $property.Value
}

function Test-BLC004Sha {
    param(
        [AllowEmptyString()]
        [string] $Value
    )

    return ($null -ne $Value -and $Value -match '^[0-9a-fA-F]{40}$')
}

function ConvertTo-BLC004Sha {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Value,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if (-not (Test-BLC004Sha $Value)) {
        Throw-BLC004Error "$Description must contain exactly 40 hexadecimal characters."
    }

    return $Value.ToLowerInvariant()
}

function Test-BLC004RepositoryName {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository
    )

    return ($Repository -match '^[^/\s]+/[^/\s]+$' -and
        $Repository -notmatch '[\x00-\x1f\x7f]')
}

function ConvertTo-BLC004Repository {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository
    )

    if (-not (Test-BLC004RepositoryName $Repository)) {
        Throw-BLC004Error "Repository must be an owner/name value."
    }

    return $Repository
}

function Get-BLC004RunUrlParts {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RunUrl,

        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    try {
        $uri = [System.Uri]::new($RunUrl)
    }
    catch {
        Throw-BLC004Error "$Description must be an absolute HTTP(S) workflow URL."
    }

    if ($uri.Scheme -notin @('http', 'https') -or
        [string]::IsNullOrEmpty($uri.Host) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        Throw-BLC004Error "$Description must be an absolute HTTP(S) workflow URL."
    }

    $repositoryParts = $Repository.Split('/')
    $pathParts = $uri.AbsolutePath.Trim('/').Split('/')
    if ($pathParts.Count -ne 5 -or
        $pathParts[0] -ine $repositoryParts[0] -or
        $pathParts[1] -ine $repositoryParts[1] -or
        $pathParts[2] -cne 'actions' -or
        $pathParts[3] -cne 'runs' -or
        $pathParts[4] -notmatch '^[0-9]+$') {
        Throw-BLC004Error "$Description must point to $Repository/actions/runs/<run-id>."
    }

    $canonical = $uri.GetLeftPart([System.UriPartial]::Path).TrimEnd('/')
    return [pscustomobject]@{
        Original = $RunUrl
        Canonical = $canonical
        Scheme = $uri.Scheme.ToLowerInvariant()
        Authority = $uri.Authority.ToLowerInvariant()
        Host = $uri.Host.ToLowerInvariant()
        RepositoryPath = ('/' + $pathParts[0] + '/' + $pathParts[1])
        Prefix = ($uri.Scheme.ToLowerInvariant() + '://' + $uri.Authority.ToLowerInvariant() +
            '/' + $repositoryParts[0] + '/' + $repositoryParts[1] + '/actions/runs')
        RunId = $pathParts[4]
    }
}

function Test-BLC004Eligibility {
    param(
        [Parameter(Mandatory = $true)]
        [string] $EventName,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $EventAction,

        [Parameter(Mandatory = $true)]
        [string] $Ref
    )

    if ($EventName -ceq 'release' -and $EventAction -ceq 'published') {
        return [pscustomobject]@{
            Eligible = $true
            Kind = 'release'
        }
    }

    if (($EventName -ceq 'push' -or $EventName -ceq 'workflow_dispatch') -and
        $Ref -ceq 'refs/heads/master') {
        return [pscustomobject]@{
            Eligible = $true
            Kind = 'automatic'
        }
    }

    return [pscustomobject]@{
        Eligible = $false
        Kind = 'ineligible'
    }
}

function New-BLC004TemporaryDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Prefix
    )

    $name = "{0}-{1}" -f $Prefix, ([System.Guid]::NewGuid().ToString('N'))
    $path = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), $name)
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}

function Get-BLC004FileHash {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    try {
        $hash = [System.Security.Cryptography.SHA256]::Create()
        try {
            $stream = [System.IO.File]::Open(
                $Path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read
            )
            try {
                $bytes = $hash.ComputeHash($stream)
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            $hash.Dispose()
        }

        return ([System.BitConverter]::ToString($bytes).Replace('-', '')).ToLowerInvariant()
    }
    catch {
        Throw-BLC004Error "Could not read '$Path' for SHA-256 validation."
    }
}

function Read-BLC004Utf8Text {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        $encoding = [System.Text.UTF8Encoding]::new($false, $true)
        $text = $encoding.GetString($bytes)
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xfeff) {
            $text = $text.Substring(1)
        }

        return $text
    }
    catch {
        Throw-BLC004Error "Could not read '$Path' as UTF-8 text."
    }
}

function Write-BLC004Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Text
    )

    try {
        $encoding = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($Path, $Text, $encoding)
    }
    catch {
        Throw-BLC004Error "Could not write '$Path'."
    }
}

function Test-BLC004FileBytesEqual {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LeftPath,

        [Parameter(Mandatory = $true)]
        [string] $RightPath
    )

    try {
        $left = [System.IO.File]::Open(
            $LeftPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read
        )
        $right = [System.IO.File]::Open(
            $RightPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read
        )
        try {
            if ($left.Length -ne $right.Length) {
                return $false
            }

            $leftBuffer = New-Object byte[] 65536
            $rightBuffer = New-Object byte[] 65536
            while ($true) {
                $leftRead = $left.Read($leftBuffer, 0, $leftBuffer.Length)
                $rightRead = $right.Read($rightBuffer, 0, $rightBuffer.Length)
                if ($leftRead -ne $rightRead) {
                    return $false
                }
                if ($leftRead -eq 0) {
                    return $true
                }

                for ($index = 0; $index -lt $leftRead; $index++) {
                    if ($leftBuffer[$index] -ne $rightBuffer[$index]) {
                        return $false
                    }
                }
            }
        }
        finally {
            $left.Dispose()
            $right.Dispose()
        }
    }
    catch {
        Throw-BLC004Error "Could not compare downloaded release asset bytes."
    }
}

function Get-BLC004OrdinalSortedRecords {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Records
    )

    $sorted = [System.Collections.Generic.List[object]]::new()
    foreach ($record in $Records) {
        $insertAt = $sorted.Count
        for ($index = 0; $index -lt $sorted.Count; $index++) {
            if ([System.StringComparer]::Ordinal.Compare($record.Name, $sorted[$index].Name) -lt 0) {
                $insertAt = $index
                break
            }
        }
        $sorted.Insert($insertAt, $record)
    }

    return @($sorted.ToArray())
}

function Get-BLC004LocalArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ArtifactDirectory,

        [Parameter(Mandatory = $true)]
        [string] $TemporaryDirectory,

        [Parameter(Mandatory = $true)]
        [string] $SourceCommit
    )

    try {
        $directory = Get-Item -LiteralPath $ArtifactDirectory -Force -ErrorAction Stop
    }
    catch {
        Throw-BLC004Error "ArtifactDirectory '$ArtifactDirectory' could not be opened."
    }

    if (-not $directory.PSIsContainer) {
        Throw-BLC004Error "ArtifactDirectory must be a directory."
    }

    $expectedByLowerName = @{}
    foreach ($name in $script:BLC004ArchiveNames) {
        $expectedByLowerName[$name.ToLowerInvariant()] = $name
        $expectedByLowerName[("{0}.sha256" -f $name).ToLowerInvariant()] = "{0}.sha256" -f $name
        $expectedByLowerName[("{0}.source.txt" -f $name).ToLowerInvariant()] = "{0}.source.txt" -f $name
    }

    try {
        $files = @(Get-ChildItem -LiteralPath $directory.FullName -File -Recurse -Force -ErrorAction Stop)
    }
    catch {
        Throw-BLC004Error "ArtifactDirectory could not be enumerated."
    }

    $filesByCanonicalName = @{}
    foreach ($file in $files) {
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Throw-BLC004Error "Symbolic links and reparse-point artifact files are not accepted."
        }

        $lowerName = $file.Name.ToLowerInvariant()
        if (-not $expectedByLowerName.ContainsKey($lowerName)) {
            Throw-BLC004Error "Unexpected artifact file '$($file.Name)'."
        }

        $canonicalName = $expectedByLowerName[$lowerName]
        if ($file.Name -cne $canonicalName) {
            Throw-BLC004Error "Artifact file '$($file.Name)' is a case alias for '$canonicalName'."
        }
        if ($filesByCanonicalName.ContainsKey($canonicalName)) {
            Throw-BLC004Error "Duplicate artifact file '$canonicalName'."
        }

        $filesByCanonicalName[$canonicalName] = $file
    }

    foreach ($name in $expectedByLowerName.Values | Select-Object -Unique) {
        if (-not $filesByCanonicalName.ContainsKey($name)) {
            Throw-BLC004Error "Required artifact file '$name' is missing."
        }
    }

    $sourceSha = ConvertTo-BLC004Sha $SourceCommit 'SourceCommit'
    $archiveRecords = @()
    foreach ($archiveName in $script:BLC004ArchiveNames) {
        $archiveFile = $filesByCanonicalName[$archiveName]
        $hash = Get-BLC004FileHash $archiveFile.FullName

        $checksumName = "{0}.sha256" -f $archiveName
        $checksumText = Read-BLC004Utf8Text $filesByCanonicalName[$checksumName].FullName
        if ($checksumText -notmatch ('\A(?<hash>[0-9a-f]{64}) {2}' + [regex]::Escape($archiveName) + '\r?\n?\z')) {
            Throw-BLC004Error "Checksum sidecar '$checksumName' is malformed."
        }
        if ($Matches['hash'] -cne $hash -or $Matches['hash'] -cne $Matches['hash'].ToLowerInvariant()) {
            Throw-BLC004Error "Checksum sidecar '$checksumName' does not match the archive."
        }

        $sourceName = "{0}.source.txt" -f $archiveName
        $sourceText = Read-BLC004Utf8Text $filesByCanonicalName[$sourceName].FullName
        if ($sourceText -notmatch '\A(?<source>[0-9a-fA-F]{40})\r?\n?\z' -or
            $Matches['source'].ToLowerInvariant() -cne $sourceSha) {
            Throw-BLC004Error "Source sidecar '$sourceName' does not contain SourceCommit."
        }

        $archiveRecords += [pscustomobject]@{
            Name = $archiveName
            Path = $archiveFile.FullName
            Hash = $hash
            Length = $archiveFile.Length
        }
    }

    $orderedRecords = Get-BLC004OrdinalSortedRecords $archiveRecords
    $checksumLines = foreach ($record in $orderedRecords) {
        "{0}  {1}" -f $record.Hash, $record.Name
    }
    $checksumText = (($checksumLines -join "`n") + "`n")
    $checksumPath = [System.IO.Path]::Combine($TemporaryDirectory, 'SHA256SUMS.txt')
    Write-BLC004Utf8NoBom $checksumPath $checksumText
    $checksumHash = Get-BLC004FileHash $checksumPath

    $assetRecords = [ordered]@{}
    foreach ($record in $archiveRecords) {
        $assetRecords[$record.Name] = [pscustomobject]@{
            Name = $record.Name
            Path = $record.Path
            Hash = $record.Hash
            Length = $record.Length
        }
    }
    $assetRecords['SHA256SUMS.txt'] = [pscustomobject]@{
        Name = 'SHA256SUMS.txt'
        Path = $checksumPath
        Hash = $checksumHash
        Length = (Get-Item -LiteralPath $checksumPath).Length
    }

    return [pscustomobject]@{
        ArchiveRecords = $archiveRecords
        Assets = $assetRecords
        ChecksumPath = $checksumPath
        ChecksumText = $checksumText
    }
}

function Test-ReleasePublicationEligibility {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $EventName,

        [Parameter(Mandatory = $true)]
        [string] $EventAction,

        [Parameter(Mandatory = $true)]
        [string] $Ref
    )

    return (Test-BLC004Eligibility -EventName $EventName -EventAction $EventAction -Ref $Ref)
}

function Invoke-Gh {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $false)]
        [scriptblock] $GhInvoker
    )

    if ($null -ne $GhInvoker) {
        try {
            $rawResult = @(& $GhInvoker (,([string[]]$Arguments)))
        }
        catch {
            Throw-BLC004Error 'The injected GhInvoker failed before returning a result.'
        }

        if ($rawResult.Count -ne 1 -or $null -eq $rawResult[0]) {
            Throw-BLC004Error 'GhInvoker must return one normalized result object.'
        }

        $result = $rawResult[0]
        $exitProperty = $result.PSObject.Properties['ExitCode']
        $stdoutProperty = $result.PSObject.Properties['Stdout']
        if ($null -eq $exitProperty -or $null -eq $stdoutProperty) {
            Throw-BLC004Error 'GhInvoker results must contain ExitCode and Stdout.'
        }

        try {
            $exitCode = [int]$exitProperty.Value
        }
        catch {
            Throw-BLC004Error 'GhInvoker ExitCode must be an integer.'
        }

        $stdout = if ($null -eq $stdoutProperty.Value) { '' } else { [string]$stdoutProperty.Value }
        return [pscustomobject]@{
            ExitCode = $exitCode
            Stdout = $stdout
        }
    }

    try {
        $output = @(& gh @Arguments 2>&1)
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    }
    catch {
        Throw-BLC004Error 'The gh executable could not be invoked.'
    }

    $stdout = if ($output.Count -eq 0) {
        ''
    }
    else {
        [string]::Join([System.Environment]::NewLine, @($output | ForEach-Object { [string]$_ }))
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Stdout = $stdout
    }
}

function Test-LocalReleaseArtifacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $ArtifactDirectory,

        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9a-fA-F]{40}$')]
        [string] $SourceCommit,

        [Parameter(Mandatory = $false)]
        [string] $TemporaryDirectory
    )

    if ([string]::IsNullOrEmpty($TemporaryDirectory)) {
        $TemporaryDirectory = New-BLC004TemporaryDirectory -Prefix 'netspy-release-local'
    }
    elseif (-not [System.IO.Directory]::Exists($TemporaryDirectory)) {
        [System.IO.Directory]::CreateDirectory($TemporaryDirectory) | Out-Null
    }

    $artifacts = Get-BLC004LocalArtifacts -ArtifactDirectory $ArtifactDirectory `
        -TemporaryDirectory $TemporaryDirectory -SourceCommit $SourceCommit

    return [pscustomobject]@{
        Valid = $true
        TemporaryDirectory = $TemporaryDirectory
        ArchiveRecords = $artifacts.ArchiveRecords
        Assets = $artifacts.Assets
        ChecksumPath = $artifacts.ChecksumPath
        ChecksumText = $artifacts.ChecksumText
    }
}

function New-ReleaseNotes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9a-fA-F]{40}$')]
        [string] $SourceCommit,

        [Parameter(Mandatory = $true)]
        [string] $RunUrl,

        [Parameter(Mandatory = $false)]
        [string] $Repository
    )

    $sourceSha = ConvertTo-BLC004Sha -Value $SourceCommit -Description 'SourceCommit'
    if (-not [string]::IsNullOrEmpty($Repository)) {
        $repositoryName = ConvertTo-BLC004Repository -Repository $Repository
        $run = Get-BLC004RunUrlParts -RunUrl $RunUrl -Repository $repositoryName -Description 'RunUrl'
        $notesUrl = $run.Canonical
    }
    else {
        $notesUrl = $RunUrl
    }

    return @(
        "<!-- netspy-build:$sourceSha -->"
        "## netSpy build $($sourceSha.Substring(0, 12))"
        ''
        "Source SHA: ``$sourceSha``"
        "Workflow run: $notesUrl"
        ''
        'Variants:'
        '- `netSpy-netframework.zip` — requires .NET Framework 4.8.'
        '- `netSpy-net.zip` — requires the .NET 10 Desktop Runtime.'
        '- `netSpy-net-win32.zip` — includes the x86 runtime.'
        '- `netSpy-net-win64.zip` — includes the x64 runtime.'
        ''
        'Usage:'
        'Extract the complete archive directory, launch `dnSpy.exe`, open the bundle, expand `Assemblies`, and select its main DLL or type.'
        ''
        'These builds support official .NET single-file bundles. Support for all methods and third-party packers is not claimed.'
        '<!-- /netspy-build -->'
    ) -join "`n"
}

function Invoke-BLC004GhChecked {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $false)]
        [scriptblock] $GhInvoker,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $result = Invoke-Gh -Arguments $Arguments -GhInvoker $GhInvoker
    if ($result.ExitCode -ne 0) {
        Throw-BLC004Error "$Description failed (gh exit code $($result.ExitCode))."
    }
    return [string]$result.Stdout
}

function ConvertFrom-BLC004Json {
    param(
        [AllowEmptyString()]
        [string] $Text,

        [Parameter(Mandatory = $true)]
        [string] $Description,

        [switch] $AllowEmpty
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        if ($AllowEmpty) { return $null }
        Throw-BLC004Error "$Description returned an empty response."
    }
    try {
        return ($Text | ConvertFrom-Json -ErrorAction Stop)
    }
    catch {
        Throw-BLC004Error "$Description returned invalid JSON."
    }
}

function Get-BLC004TagCommit {
    param(
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [string] $Tag,
        [Parameter(Mandatory = $false)] [scriptblock] $GhInvoker
    )

    $encodedTag = [System.Uri]::EscapeDataString($Tag)
    $text = Invoke-BLC004GhChecked -Arguments @('api', "repos/$Repository/git/matching-refs/tags/$encodedTag") `
        -GhInvoker $GhInvoker -Description 'Tag lookup'
    $items = ConvertFrom-BLC004Json -Text $text -Description 'Tag lookup' -AllowEmpty
    if ($null -eq $items) { return $null }
    $matches = @($items | Where-Object { [string](Get-BLC004Property $_ 'ref') -ceq "refs/tags/$Tag" })
    if ($matches.Count -eq 0) { return $null }
    if ($matches.Count -ne 1) { Throw-BLC004Error "Tag '$Tag' is ambiguous." }

    $object = Get-BLC004Property $matches[0] 'object'
    $seen = @{}
    for ($depth = 0; $depth -lt 16; $depth++) {
        $type = [string](Get-BLC004Property $object 'type')
        $sha = ConvertTo-BLC004Sha -Value ([string](Get-BLC004Property $object 'sha')) -Description 'Tag object SHA'
        if ($type -ceq 'commit') { return $sha }
        if ($type -cne 'tag' -or $seen.ContainsKey($sha)) {
            Throw-BLC004Error "Tag '$Tag' does not resolve to a commit."
        }
        $seen[$sha] = $true
        $tagText = Invoke-BLC004GhChecked -Arguments @('api', "repos/$Repository/git/tags/$sha") `
            -GhInvoker $GhInvoker -Description 'Annotated tag lookup'
        $tagObject = ConvertFrom-BLC004Json -Text $tagText -Description 'Annotated tag lookup'
        $object = Get-BLC004Property $tagObject 'object'
    }
    Throw-BLC004Error "Tag '$Tag' has too many annotation levels."
}

function Get-BLC004Release {
    param(
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [string] $Tag,
        [Nullable[long]] $ReleaseId,
        [Parameter(Mandatory = $true)] [string] $Kind,
        [Parameter(Mandatory = $false)] [scriptblock] $GhInvoker
    )

    if ($Kind -ceq 'release') {
        if ([string]::IsNullOrEmpty($Tag) -or $null -eq $ReleaseId) {
            Throw-BLC004Error 'Published release events require ReleaseTag and ReleaseId.'
        }
        $text = Invoke-BLC004GhChecked -Arguments @('api', "repos/$Repository/releases/$ReleaseId") `
            -GhInvoker $GhInvoker -Description 'Event release lookup'
        $release = ConvertFrom-BLC004Json -Text $text -Description 'Event release lookup'
        if ([long](Get-BLC004Property $release 'id') -ne [long]$ReleaseId -or
            [string](Get-BLC004Property $release 'tag_name') -cne $Tag) {
            Throw-BLC004Error 'The event release identity does not match ReleaseId and ReleaseTag.'
        }
        return $release
    }

    $text = Invoke-BLC004GhChecked -Arguments @('api', '--paginate', '--slurp', '--jq', 'flatten',
        "repos/$Repository/releases?per_page=100") `
        -GhInvoker $GhInvoker -Description 'Automatic release lookup'
    $items = ConvertFrom-BLC004Json -Text $text -Description 'Automatic release lookup' -AllowEmpty
    if ($null -eq $items) { return $null }
    $matches = @($items | Where-Object { [string](Get-BLC004Property $_ 'tag_name') -ceq $Tag })
    if ($matches.Count -gt 1) { Throw-BLC004Error "More than one release uses tag '$Tag'." }
    if ($matches.Count -eq 0) { return $null }
    return $matches[0]
}

function Get-BLC004BodyState {
    param(
        [AllowEmptyString()] [string] $Body,
        [Parameter(Mandatory = $true)] [string] $SourceCommit,
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [string] $ExpectedNotes
    )

    $markerPattern = '<!--\s*/?netspy-build(?::[^>]*?)?\s*-->'
    $markers = @([regex]::Matches($Body, $markerPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase))
    if ($markers.Count -eq 0) {
        return [pscustomobject]@{ Present = $false; Body = $Body }
    }
    if ($markers.Count -ne 2 -or
        $markers[0].Value -cne "<!-- netspy-build:$SourceCommit -->" -or
        $markers[1].Value -cne '<!-- /netspy-build -->' -or
        $markers[1].Index -le $markers[0].Index) {
        Throw-BLC004Error 'The release body has malformed, duplicate, nested, or conflicting build markers.'
    }

    $sectionLength = ($markers[1].Index + $markers[1].Length) - $markers[0].Index
    $section = $Body.Substring($markers[0].Index, $sectionLength)
    $normalized = $section.Replace("`r`n", "`n")
    $urlMatch = [regex]::Match($normalized, '(?m)^Workflow run: (?<url>https?://[^\r\n]+)$')
    if (-not $urlMatch.Success) {
        Throw-BLC004Error 'The release build section has no valid workflow provenance URL.'
    }
    $run = Get-BLC004RunUrlParts -RunUrl $urlMatch.Groups['url'].Value -Repository $Repository `
        -Description 'Existing workflow URL'
    $expectedUrlMatch = [regex]::Match($ExpectedNotes.Replace("`r`n", "`n"),
        '(?m)^Workflow run: (?<url>https?://[^\r\n]+)$')
    if (-not $expectedUrlMatch.Success) {
        Throw-BLC004Error 'The expected release notes have no valid workflow provenance URL.'
    }
    $expectedRun = Get-BLC004RunUrlParts -RunUrl $expectedUrlMatch.Groups['url'].Value `
        -Repository $Repository -Description 'Current workflow URL'
    if ($run.Scheme -cne $expectedRun.Scheme -or
        $run.Authority -cne $expectedRun.Authority -or
        $run.Host -cne $expectedRun.Host -or
        $run.RepositoryPath -cne $expectedRun.RepositoryPath) {
        Throw-BLC004Error 'The existing workflow provenance URL does not match the current workflow origin and repository.'
    }
    $expectedForExistingRun = New-ReleaseNotes -SourceCommit $SourceCommit -RunUrl $run.Canonical -Repository $Repository
    if ($normalized -cne $expectedForExistingRun) {
        Throw-BLC004Error 'The release build section conflicts with the required provenance, variants, or usage text.'
    }
    return [pscustomobject]@{ Present = $true; Body = $Body; Section = $section }
}

function Get-BLC004ReleaseBody {
    param(
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [string] $Tag,
        [Parameter(Mandatory = $false)] [scriptblock] $GhInvoker
    )
    $text = Invoke-BLC004GhChecked -Arguments @('release', 'view', $Tag, '--repo', $Repository, '--json', 'body') `
        -GhInvoker $GhInvoker -Description 'Release body lookup'
    $json = ConvertFrom-BLC004Json -Text $text -Description 'Release body lookup'
    $body = Get-BLC004Property $json 'body'
    if ($null -eq $body) { return '' }
    return [string]$body
}

function Get-BLC004RemoteAssets {
    param(
        [Parameter(Mandatory = $true)] [object] $Release,
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [string] $Tag,
        [Parameter(Mandatory = $true)] [object] $Local,
        [Parameter(Mandatory = $true)] [string] $TemporaryDirectory,
        [Parameter(Mandatory = $false)] [scriptblock] $GhInvoker
    )

    $assets = @(Get-BLC004Property $Release 'assets')
    $expected = @{}
    foreach ($name in $script:BLC004ExpectedAssetNames) { $expected[$name.ToLowerInvariant()] = $name }
    $present = @{}
    foreach ($asset in $assets) {
        $name = [string](Get-BLC004Property $asset 'name')
        $lower = $name.ToLowerInvariant()
        if (-not $expected.ContainsKey($lower)) { continue }
        $canonical = $expected[$lower]
        if ($name -cne $canonical) { Throw-BLC004Error "Remote asset '$name' is a case alias for '$canonical'." }
        if ($present.ContainsKey($canonical)) { Throw-BLC004Error "Remote asset '$canonical' is duplicated." }
        $present[$canonical] = [string](Get-BLC004Property $asset 'id')
    }

    foreach ($name in @($present.Keys)) {
        $downloadDirectory = [System.IO.Path]::Combine($TemporaryDirectory, ([System.Guid]::NewGuid().ToString('N')))
        [System.IO.Directory]::CreateDirectory($downloadDirectory) | Out-Null
        Invoke-BLC004GhChecked -Arguments @('release', 'download', $Tag, '--repo', $Repository,
            '--pattern', $name, '--dir', $downloadDirectory) -GhInvoker $GhInvoker `
            -Description "Download of release asset '$name'" | Out-Null
        $downloaded = @(Get-ChildItem -LiteralPath $downloadDirectory -File -Force)
        if ($downloaded.Count -ne 1 -or $downloaded[0].Name -cne $name) {
            Throw-BLC004Error "Download of release asset '$name' did not produce exactly that file."
        }
        $localAsset = $Local.Assets[$name]
        if ((Get-BLC004FileHash $downloaded[0].FullName) -cne $localAsset.Hash -or
            -not (Test-BLC004FileBytesEqual $downloaded[0].FullName $localAsset.Path)) {
            Throw-BLC004Error "Remote asset '$name' conflicts with the validated local bytes."
        }
    }
    return $present
}

function Get-BLC004RemoteSnapshot {
    param(
        [Parameter(Mandatory = $true)] [string] $Kind,
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [string] $Tag,
        [Nullable[long]] $ReleaseId,
        [Parameter(Mandatory = $true)] [string] $SourceCommit,
        [Parameter(Mandatory = $true)] [object] $Local,
        [Parameter(Mandatory = $true)] [string] $Notes,
        [Parameter(Mandatory = $true)] [string] $TemporaryDirectory,
        [Parameter(Mandatory = $false)] [scriptblock] $GhInvoker
    )

    $tagCommit = Get-BLC004TagCommit -Repository $Repository -Tag $Tag -GhInvoker $GhInvoker
    $release = Get-BLC004Release -Repository $Repository -Tag $Tag -ReleaseId $ReleaseId -Kind $Kind -GhInvoker $GhInvoker
    if ($Kind -ceq 'release' -and ($null -eq $tagCommit -or $null -eq $release)) {
        Throw-BLC004Error 'The published event tag or release is missing.'
    }
    if ($null -ne $tagCommit -and $tagCommit -cne $SourceCommit) {
        Throw-BLC004Error "Tag '$Tag' does not resolve to SourceCommit."
    }
    if ($null -ne $release) {
        if ($null -eq $tagCommit) { Throw-BLC004Error 'A release exists without its required tag.' }
        if ($Kind -ceq 'automatic') {
            $expectedTitle = "netSpy build $($SourceCommit.Substring(0, 12))"
            if ([string](Get-BLC004Property $release 'name') -cne $expectedTitle -or
                -not [bool](Get-BLC004Property $release 'prerelease')) {
                Throw-BLC004Error 'The automatic release title or prerelease flag conflicts with the required identity.'
            }
        }
        $body = Get-BLC004ReleaseBody -Repository $Repository -Tag $Tag -GhInvoker $GhInvoker
        $bodyState = Get-BLC004BodyState -Body $body -SourceCommit $SourceCommit -Repository $Repository -ExpectedNotes $Notes
        $assets = Get-BLC004RemoteAssets -Release $release -Repository $Repository -Tag $Tag -Local $Local `
            -TemporaryDirectory $TemporaryDirectory -GhInvoker $GhInvoker
        $assetFingerprint = (@($assets.Keys | Sort-Object) | ForEach-Object { "$_=$($assets[$_])" }) -join ';'
        $fingerprint = "tag=$tagCommit|id=$([string](Get-BLC004Property $release 'id'))|name=$([string](Get-BLC004Property $release 'name'))|pre=$([bool](Get-BLC004Property $release 'prerelease'))|body=$([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($body)))|assets=$assetFingerprint"
        return [pscustomobject]@{ TagCommit = $tagCommit; Release = $release; BodyState = $bodyState; Assets = $assets; Fingerprint = $fingerprint }
    }
    return [pscustomobject]@{ TagCommit = $tagCommit; Release = $null; BodyState = $null; Assets = @{}; Fingerprint = "tag=$tagCommit|release=absent" }
}

function Invoke-ReleasePublication {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $EventName,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $EventAction,

        [Parameter(Mandatory = $true)]
        [string] $Ref,

        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9a-fA-F]{40}$')]
        [string] $SourceCommit,

        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [string] $RunUrl,

        [Parameter(Mandatory = $true)]
        [string] $ArtifactDirectory,

        [Parameter(Mandatory = $false)]
        [string] $ReleaseTag,

        [Parameter(Mandatory = $false)]
        [Nullable[long]] $ReleaseId,

        [Parameter(Mandatory = $false)]
        [scriptblock] $GhInvoker
    )

    $eligibility = Test-ReleasePublicationEligibility -EventName $EventName `
        -EventAction $EventAction -Ref $Ref
    if (-not $eligibility.Eligible) {
        return [pscustomobject]@{
            Eligible = $false
            Mutated = $false
            Kind = 'ineligible'
        }
    }

    $sourceSha = ConvertTo-BLC004Sha -Value $SourceCommit -Description 'SourceCommit'
    $repositoryName = ConvertTo-BLC004Repository -Repository $Repository
    $run = Get-BLC004RunUrlParts -RunUrl $RunUrl -Repository $repositoryName -Description 'RunUrl'
    $temporaryDirectory = New-BLC004TemporaryDirectory -Prefix 'netspy-release-publication'
    try {
        $local = Test-LocalReleaseArtifacts -ArtifactDirectory $ArtifactDirectory `
            -SourceCommit $sourceSha -TemporaryDirectory $temporaryDirectory
        $notes = New-ReleaseNotes -SourceCommit $sourceSha -RunUrl $run.Canonical -Repository $repositoryName

        if ($eligibility.Kind -ceq 'release') {
            if ([string]::IsNullOrEmpty($ReleaseTag) -or $null -eq $ReleaseId) {
                Throw-BLC004Error 'Published release events require ReleaseTag and ReleaseId.'
            }
            $tag = $ReleaseTag
        }
        else {
            $tag = "build-$sourceSha"
            if (-not [string]::IsNullOrEmpty($ReleaseTag) -and $ReleaseTag -cne $tag) {
                Throw-BLC004Error 'Automatic publication cannot substitute a supplied release tag.'
            }
        }

        $firstSnapshotDirectory = [System.IO.Path]::Combine($temporaryDirectory, 'preflight-1')
        $secondSnapshotDirectory = [System.IO.Path]::Combine($temporaryDirectory, 'preflight-2')
        [System.IO.Directory]::CreateDirectory($firstSnapshotDirectory) | Out-Null
        [System.IO.Directory]::CreateDirectory($secondSnapshotDirectory) | Out-Null
        $first = Get-BLC004RemoteSnapshot -Kind $eligibility.Kind -Repository $repositoryName -Tag $tag `
            -ReleaseId $ReleaseId -SourceCommit $sourceSha -Local $local -Notes $notes `
            -TemporaryDirectory $firstSnapshotDirectory -GhInvoker $GhInvoker
        $second = Get-BLC004RemoteSnapshot -Kind $eligibility.Kind -Repository $repositoryName -Tag $tag `
            -ReleaseId $ReleaseId -SourceCommit $sourceSha -Local $local -Notes $notes `
            -TemporaryDirectory $secondSnapshotDirectory -GhInvoker $GhInvoker
        if ($first.Fingerprint -cne $second.Fingerprint) {
            Throw-BLC004Error 'The tag, release body, identity, or assets changed during preflight.'
        }

        $mutated = $false
        $created = $false
        if ($null -eq $second.Release) {
            if ($eligibility.Kind -cne 'automatic') {
                Throw-BLC004Error 'The published event release is missing.'
            }
            $notesPath = [System.IO.Path]::Combine($temporaryDirectory, 'create-notes.md')
            Write-BLC004Utf8NoBom -Path $notesPath -Text $notes
            $createArguments = @('release', 'create', $tag, '--repo', $repositoryName,
                '--title', "netSpy build $($sourceSha.Substring(0, 12))", '--prerelease', '--notes-file', $notesPath)
            if ($null -eq $second.TagCommit) {
                $createArguments += @('--target', $sourceSha)
            }
            Invoke-BLC004GhChecked -Arguments $createArguments -GhInvoker $GhInvoker `
                -Description 'Automatic release creation' | Out-Null
            $mutated = $true
            $created = $true
        }
        elseif (-not $second.BodyState.Present) {
            $newBody = if ([string]::IsNullOrEmpty($second.BodyState.Body)) {
                $notes
            }
            else {
                $second.BodyState.Body + "`n`n" + $notes
            }
            $notesPath = [System.IO.Path]::Combine($temporaryDirectory, 'edit-notes.md')
            Write-BLC004Utf8NoBom -Path $notesPath -Text $newBody
            Invoke-BLC004GhChecked -Arguments @('release', 'edit', $tag, '--repo', $repositoryName,
                '--notes-file', $notesPath) -GhInvoker $GhInvoker -Description 'Release notes update' | Out-Null
            $mutated = $true
        }

        foreach ($name in $script:BLC004ExpectedAssetNames) {
            if (-not $created -and $second.Assets.ContainsKey($name)) { continue }
            Invoke-BLC004GhChecked -Arguments @('release', 'upload', $tag, $local.Assets[$name].Path,
                '--repo', $repositoryName) -GhInvoker $GhInvoker -Description "Upload of release asset '$name'" | Out-Null
            $mutated = $true
        }

        return [pscustomobject]@{
            Eligible = $true
            Mutated = $mutated
            Kind = $eligibility.Kind
            SourceCommit = $sourceSha
            Repository = $repositoryName
            RunUrl = $run.Canonical
            ReleaseTag = $tag
            ReleaseId = if ($null -eq $second.Release) { $null } else { [long](Get-BLC004Property $second.Release 'id') }
            Created = $created
            NotesAdded = ($created -or ($null -ne $second.Release -and -not $second.BodyState.Present))
        }
    }
    finally {
        if ([System.IO.Directory]::Exists($temporaryDirectory)) {
            [System.IO.Directory]::Delete($temporaryDirectory, $true)
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        Invoke-ReleasePublication @PSBoundParameters
    }
    catch {
        Write-Error $_
        exit 1
    }
}
