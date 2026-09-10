param(
	[ValidateSet("all","netframework","net","net-x86","net-x64")]
	[string]$buildtfm = 'all',
	[switch]$NoMsbuild
	)
$ErrorActionPreference = 'Stop'

$netframework_tfm = 'net48'
$net_tfm = 'net10.0-windows'
$configuration = 'Release'
$net_baseoutput = "dnSpy\dnSpy\bin\$configuration"
$apphostpatcher_dir = "Build\AppHostPatcher"
$solution = 'dnSpy.sln'
$product_project = 'dnSpy\dnSpy\dnSpy.csproj'
$x86_project = 'dnSpy\dnSpy-x86\dnSpy-x86.csproj'
$console_project = 'dnSpy\dnSpy.Console\dnSpy.Console.csproj'
$framework_dependent_projects = @(
	$product_project,
	$x86_project,
	$console_project,
	'Extensions\dnSpy.Analyzer\dnSpy.Analyzer.csproj',
	'Extensions\dnSpy.AsmEditor\dnSpy.AsmEditor.csproj',
	'Extensions\dnSpy.BamlDecompiler\dnSpy.BamlDecompiler.csproj',
	'Extensions\dnSpy.Bundles\dnSpy.Bundles.Extension.csproj',
	'Extensions\dnSpy.Debugger\dnSpy.Debugger\dnSpy.Debugger.csproj',
	'Extensions\dnSpy.Debugger\dnSpy.Debugger.DotNet\dnSpy.Debugger.DotNet.csproj',
	'Extensions\dnSpy.Debugger\dnSpy.Debugger.DotNet.CorDebug\dnSpy.Debugger.DotNet.CorDebug.csproj',
	'Extensions\dnSpy.Debugger\dnSpy.Debugger.DotNet.Mono\dnSpy.Debugger.DotNet.Mono.csproj',
	'Extensions\ILSpy.Decompiler\dnSpy.Decompiler.ILSpy\dnSpy.Decompiler.ILSpy.csproj',
	'Extensions\dnSpy.Scripting.Roslyn\dnSpy.Scripting.Roslyn.csproj',
	'Extensions\dnSpy.StringSearcher\dnSpy.StringSearcher.csproj'
)
$self_contained_projects = @(
	$product_project,
	$console_project,
	'Extensions\dnSpy.Analyzer\dnSpy.Analyzer.csproj',
	'Extensions\dnSpy.AsmEditor\dnSpy.AsmEditor.csproj',
	'Extensions\dnSpy.BamlDecompiler\dnSpy.BamlDecompiler.csproj',
	'Extensions\dnSpy.Bundles\dnSpy.Bundles.Extension.csproj',
	'Extensions\dnSpy.Debugger\dnSpy.Debugger\dnSpy.Debugger.csproj',
	'Extensions\dnSpy.Debugger\dnSpy.Debugger.DotNet\dnSpy.Debugger.DotNet.csproj',
	'Extensions\dnSpy.Debugger\dnSpy.Debugger.DotNet.CorDebug\dnSpy.Debugger.DotNet.CorDebug.csproj',
	'Extensions\dnSpy.Debugger\dnSpy.Debugger.DotNet.Mono\dnSpy.Debugger.DotNet.Mono.csproj',
	'Extensions\ILSpy.Decompiler\dnSpy.Decompiler.ILSpy\dnSpy.Decompiler.ILSpy.csproj',
	'Extensions\dnSpy.Scripting.Roslyn\dnSpy.Scripting.Roslyn.csproj',
	'Extensions\dnSpy.StringSearcher\dnSpy.StringSearcher.csproj'
)
$script:appHostPatcherBuilt = $false

#
# The reason we don't use dotnet build is that dotnet build doesn't support COM references yet https://github.com/dnSpy/dnSpy/issues/1053
#

function Invoke-CheckedCommand {
	param(
		[Parameter(Mandatory)] [string]$Command,
		[Parameter(Mandatory)] [string[]]$Arguments,
		[Parameter(Mandatory)] [string]$Description
	)

	Write-Host "${Description}: $Command $($Arguments -join ' ')"
	& $Command @Arguments
	if ($LASTEXITCODE -ne 0) {
		throw "${Description} failed with exit code $LASTEXITCODE"
	}
}

function Restore-Product {
	param(
		[string]$RuntimeIdentifier = '',
		[bool]$SelfContained = $false
	)

	# Restore the complete graph without a global TargetFramework. This lets
	# portable projects select their declared net48/net10.0 targets even when
	# the product build below selects net10.0-windows.
	if ($NoMsbuild) {
		$arguments = @('restore', $solution, '--nologo')
		if ($RuntimeIdentifier -ne '') {
			$arguments += @('--runtime', $RuntimeIdentifier)
		}
		if ($SelfContained) {
			$arguments += '-p:SelfContained=True'
		}
		Invoke-CheckedCommand 'dotnet' $arguments 'Product graph restore'
	}
	else {
		$arguments = @('-v:m', '-m', '-t:Restore', "-p:Configuration=$configuration")
		if ($RuntimeIdentifier -ne '') {
			$arguments += "-p:RuntimeIdentifier=$RuntimeIdentifier"
			if ($SelfContained) {
				$arguments += '-p:SelfContained=True'
			}
		}
		$arguments += $solution
		Invoke-CheckedCommand 'msbuild' $arguments 'Product graph restore'
	}
}

function Build-AppHostPatcher {
	if ($script:appHostPatcherBuilt) {
		return
	}

	Write-Host 'Building AppHostPatcher tool'
	if ($NoMsbuild) {
		$arguments = @('build', "${apphostpatcher_dir}\AppHostPatcher.csproj", '-v:m', '-c', $configuration, '-f', $netframework_tfm, '--no-restore')
		Invoke-CheckedCommand 'dotnet' $arguments 'AppHostPatcher build'
	}
	else {
		$arguments = @('-v:m', '-m', '-t:Build', "-p:Configuration=$configuration", "-p:TargetFramework=$netframework_tfm", "${apphostpatcher_dir}\AppHostPatcher.csproj")
		Invoke-CheckedCommand 'msbuild' $arguments 'AppHostPatcher build'
	}
	$script:appHostPatcherBuilt = $true
}

function Build-NetFramework {
	Write-Host 'Building .NET Framework x86 and x64 binaries'
	Write-Host "Selected TFM: $netframework_tfm; RID: (none); SelfContained: false"
	Restore-Product

	$outdir = "$net_baseoutput\$netframework_tfm"

	if ($NoMsbuild) {
		$arguments = @('build', $solution, '-v:m', '-c', $configuration, '--no-restore')
		Invoke-CheckedCommand 'dotnet' $arguments '.NET Framework build'
	}
	else {
		$arguments = @('-v:m', '-m', '-t:Build', "-p:Configuration=$configuration", $solution)
		Invoke-CheckedCommand 'msbuild' $arguments '.NET Framework build'
	}

	# move all files to a bin sub dir but keep the exe files
	Rename-Item $outdir bin
	New-Item -ItemType Directory $outdir > $null
	Move-Item $net_baseoutput\bin $outdir
	foreach ($filename in 'dnSpy-x86.exe', 'dnSpy-x86.exe.config', 'dnSpy-x86.pdb',
			 'dnSpy.exe', 'dnSpy.exe.config', 'dnSpy.pdb',
			 'dnSpy.Console.exe', 'dnSpy.Console.exe.config', 'dnSpy.Console.pdb') {
		Move-Item $outdir\bin\$filename $outdir
	}
}

function Build-Net {
    Write-Host 'Building .NET x86 and x64 binaries'
    Write-Host "Selected TFM: $net_tfm; RID: (none); SelfContained: false"
    Restore-Product
    Build-AppHostPatcher

	$outdir = "$net_baseoutput\$net_tfm"

	if ($NoMsbuild) {
		foreach ($project in $framework_dependent_projects) {
			$arguments = @('build', $project, '-v:m', '-c', $configuration, '-f', $net_tfm, '--no-restore')
			Invoke-CheckedCommand 'dotnet' $arguments ".NET framework-dependent build ($project)"
		}
	}
	else {
		foreach ($project in $framework_dependent_projects) {
			$arguments = @('-v:m', '-m', '-t:Build', "-p:Configuration=$configuration", "-p:TargetFramework=$net_tfm", $project)
			Invoke-CheckedCommand 'msbuild' $arguments ".NET framework-dependent build ($project)"
		}
	}

    Write-Host "Patching .NET apphosts"

    # move all files to a bin sub dir but keep the exe apphosts
    Rename-Item $outdir bin
    New-Item -ItemType Directory $outdir > $null
    Move-Item $net_baseoutput\bin $outdir
    foreach ($exe in 'dnSpy.exe', 'dnSpy-x86.exe', 'dnSpy.Console.exe') {
        Move-Item $outdir\bin\$exe $outdir
        & $apphostpatcher_dir\bin\$configuration\$netframework_tfm\AppHostPatcher.exe $outdir\$exe -d bin
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
}

function Build-SelfContainedNet {
	param([string]$arch)

	Write-Host "Building self contained .NET $arch binaries"

	$rid = "win-$arch"
	$outdir = "$net_baseoutput\$net_tfm\$rid"
	$publishDir = "$outdir\publish"
	Write-Host "Selected TFM: $net_tfm; RID: $rid; SelfContained: true"
	Restore-Product -RuntimeIdentifier $rid -SelfContained $true
	Build-AppHostPatcher

	if ($NoMsbuild) {
		foreach ($project in $self_contained_projects) {
			$arguments = @('publish', $project, '-v:m', '-c', $configuration, '-f', $net_tfm, '-r', $rid, '-p:SelfContained=True', '--no-restore')
			Invoke-CheckedCommand 'dotnet' $arguments ".NET self-contained $arch publish ($project)"
		}
	}
	else {
		foreach ($project in $self_contained_projects) {
			$arguments = @('-v:m', '-m', '-t:Publish', "-p:Configuration=$configuration", "-p:TargetFramework=$net_tfm", "-p:RuntimeIdentifier=$rid", '-p:SelfContained=True', $project)
			Invoke-CheckedCommand 'msbuild' $arguments ".NET self-contained $arch publish ($project)"
		}
	}

    Write-Host "Patching self contained .NET $arch apphosts"

	# move all files to a bin sub dir but keep the exe apphosts
	$tmpbin = 'tmpbin'
	Rename-Item $publishDir $tmpbin
	New-Item -ItemType Directory $publishDir > $null
	Move-Item $outdir\$tmpbin $publishDir
	Rename-Item $publishDir\$tmpbin bin
	foreach ($exe in 'dnSpy.exe', 'dnSpy.Console.exe') {
		Move-Item $publishDir\bin\$exe $publishDir
		& $apphostpatcher_dir\bin\$configuration\$netframework_tfm\AppHostPatcher.exe $publishDir\$exe -d bin
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
}

$buildNetFramework  = $buildtfm -eq 'all' -or $buildtfm -eq 'netframework'
$buildNet           = $buildtfm -eq 'all' -or $buildtfm -eq 'net'
$buildNetX86        = $buildtfm -eq 'all' -or $buildtfm -eq 'net-x86'
$buildNetX64        = $buildtfm -eq 'all' -or $buildtfm -eq 'net-x64'

if ($buildNetFramework) {
	Build-NetFramework
}

if ($buildNet) {
    Build-Net
}

if ($buildNetX86) {
	Build-SelfContainedNet x86
}

if ($buildNetX64) {
	Build-SelfContainedNet x64
}
