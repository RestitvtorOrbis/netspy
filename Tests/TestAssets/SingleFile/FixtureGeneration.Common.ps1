# Shared restore/build/publish contract for single-file fixture generation.
# Keep the phase arguments here so every SDK generation exercises the same
# restore graph and publish properties.

function Invoke-SingleFileFixturePhases {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ProjectPath,
        [Parameter(Mandatory)] [string] $TargetFramework,
        [Parameter(Mandatory)] [string] $RuntimeIdentifier,
        [Parameter(Mandatory)] [bool] $SelfContained,
        [Parameter(Mandatory)] [string] $PublishRoot,
        [Parameter(Mandatory)] [string[]] $MSBuildProperties
    )

    if ([string]::IsNullOrWhiteSpace($ProjectPath) -or
        -not [IO.Path]::IsPathFullyQualified($ProjectPath)) {
        throw "ProjectPath must be an absolute path: '$ProjectPath'"
    }
    if ([string]::IsNullOrWhiteSpace($PublishRoot) -or
        -not [IO.Path]::IsPathFullyQualified($PublishRoot)) {
        throw "PublishRoot must be an absolute path: '$PublishRoot'"
    }
    if ([string]::IsNullOrWhiteSpace($TargetFramework)) {
        throw 'TargetFramework must not be empty.'
    }
    if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        throw 'RuntimeIdentifier must not be empty.'
    }

    $reservedProperties = @(
        'SelfContained',
        'TargetFramework',
        'TargetFrameworks',
        'RuntimeIdentifier',
        'OutputPath',
        'NoBuild',
        'NoRestore'
    )
    $properties = @($MSBuildProperties)
    for ($propertyIndex = 0; $propertyIndex -lt $properties.Count; $propertyIndex++) {
        if ($null -eq $properties[$propertyIndex]) {
            throw 'MSBuildProperties cannot contain a null value.'
        }

        $propertyText = ([string]$properties[$propertyIndex]).Trim()
        if ([string]::IsNullOrWhiteSpace($propertyText)) {
            throw 'MSBuildProperties cannot contain an empty value.'
        }
        $propertyText = $propertyText -replace '^(?i)(?:-p:|/p:|--property:)', ''
        foreach ($assignment in $propertyText -split ';') {
            $equalsIndex = $assignment.IndexOf('=')
            $propertyName = if ($equalsIndex -ge 0) {
                $assignment.Substring(0, $equalsIndex).Trim()
            }
            else {
                $assignment.Trim()
            }
            if ($reservedProperties -contains $propertyName) {
                throw "MSBuildProperties must not supply reserved property '$propertyName'; it is controlled by the fixture phase contract."
            }
        }
    }

    $selfContainedValue = $SelfContained.ToString().ToLowerInvariant()
    $restoreArguments = @(
        'restore', $ProjectPath, '--nologo', '--runtime', $RuntimeIdentifier,
        "-p:SelfContained=$selfContainedValue"
    ) + $properties
    $buildArguments = @(
        'build', $ProjectPath, '--nologo', '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--no-restore', "-p:SelfContained=$selfContainedValue"
    ) + $properties
    $publishArguments = @(
        'publish', $ProjectPath, '--nologo', '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--output', $PublishRoot, '--no-build', '--no-restore',
        "-p:SelfContained=$selfContainedValue"
    ) + $properties

    $phases = @(
        [pscustomobject]@{ Name = 'restore'; Arguments = [string[]]$restoreArguments },
        [pscustomobject]@{ Name = 'build'; Arguments = [string[]]$buildArguments },
        [pscustomobject]@{ Name = 'publish'; Arguments = [string[]]$publishArguments }
    )
    foreach ($phase in $phases) {
        $phaseArguments = [string[]]$phase.Arguments
        try {
            & dotnet @phaseArguments
            $exitCode = $LASTEXITCODE
        }
        catch {
            throw "dotnet $($phase.Name) failed for project '$ProjectPath' (TargetFramework '$TargetFramework', RuntimeIdentifier '$RuntimeIdentifier', SelfContained '$selfContainedValue'): $($_.Exception.Message)"
        }
        if ($exitCode -ne 0) {
            throw "dotnet $($phase.Name) failed for project '$ProjectPath' (TargetFramework '$TargetFramework', RuntimeIdentifier '$RuntimeIdentifier', SelfContained '$selfContainedValue') with exit code $exitCode."
        }
    }
}
