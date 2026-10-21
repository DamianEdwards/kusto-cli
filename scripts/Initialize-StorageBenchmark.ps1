[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('default', 'relocated', 'devdrive')]
    [string]$Mode
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')
{
    throw 'This experiment is restricted to disposable GitHub-hosted Windows runners.'
}

$timer = [Diagnostics.Stopwatch]::StartNew()
$resultsDirectory = Join-Path $env:RUNNER_TEMP 'storage-benchmark'
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$resultPath = Join-Path $resultsDirectory 'result.json'
$workspace = $env:GITHUB_WORKSPACE
$workspaceDrive = [IO.Path]::GetPathRoot($workspace).TrimEnd('\').TrimEnd(':')
$partition = Get-Partition -DriveLetter $workspaceDrive
$disk = $partition | Get-Disk
$volume = Get-Volume -DriveLetter $workspaceDrive
$defender = Get-MpComputerStatus
$preferences = Get-MpPreference

$result = [ordered]@{
    mode = $Mode
    sourceCommit = $env:GITHUB_SHA
    imageOS = $env:ImageOS
    imageVersion = $env:ImageVersion
    processor = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    disks = @(Get-Disk | Select-Object Number, FriendlyName, BusType, PartitionStyle, Size, IsBoot, IsSystem)
    volumes = @(Get-Volume | Select-Object DriveLetter, FileSystem, Size, SizeRemaining)
    workspaceDisk = [ordered]@{
        number = $disk.Number
        friendlyName = $disk.FriendlyName
        busType = [string]$disk.BusType
        size = $disk.Size
        fileSystem = $volume.FileSystem
    }
    originalPaths = [ordered]@{
        workspace = $workspace
        temp = $env:TEMP
        tmp = $env:TMP
        nugetPackages = $env:NUGET_PACKAGES
    }
    defender = [ordered]@{
        antivirusEnabled = $defender.AntivirusEnabled
        realTimeProtectionEnabled = $defender.RealTimeProtectionEnabled
        exclusions = @($preferences.ExclusionPath)
    }
}

try
{
    if ($Mode -ne 'default')
    {
        $storageRoot = Join-Path ([IO.Path]::GetPathRoot($workspace)) 'kusto-storage-benchmark'
        if (Test-Path -LiteralPath $storageRoot)
        {
            throw "Benchmark path already exists: '$storageRoot'."
        }
        New-Item -ItemType Directory -Path $storageRoot | Out-Null

        if ($Mode -eq 'devdrive')
        {
            if ($volume.SizeRemaining -lt 60GB)
            {
                throw 'The workspace drive needs at least 60 GiB free for the Dev Drive experiment.'
            }
            if (Get-Volume -DriveLetter R -ErrorAction SilentlyContinue)
            {
                throw 'Drive letter R: is occupied; refusing to alter an existing volume.'
            }

            # Only format a new VHD owned by this experiment, never the runner's D: volume.
            $vhdPath = Join-Path $storageRoot 'benchmark.vhdx'
            $diskpartScript = Join-Path $resultsDirectory 'create-vhd.txt'
            @(
                "create vdisk file=`"$vhdPath`" maximum=51200 type=expandable"
                "select vdisk file=`"$vhdPath`""
                'attach vdisk'
            ) | Set-Content -LiteralPath $diskpartScript -Encoding ascii
            & diskpart.exe /s $diskpartScript
            if ($LASTEXITCODE -ne 0)
            {
                throw "Creating the benchmark VHD failed with exit code $LASTEXITCODE."
            }

            $vhdDisk = Get-DiskImage -ImagePath $vhdPath | Get-Disk
            if ($vhdDisk.PartitionStyle -ne 'RAW' -or $vhdDisk.IsBoot -or $vhdDisk.IsSystem)
            {
                throw 'The newly created benchmark VHD did not resolve to an empty non-system disk.'
            }
            $vhdDisk |
                Initialize-Disk -PartitionStyle GPT -PassThru |
                New-Partition -DriveLetter R -UseMaximumSize |
                Format-Volume -DevDrive -FileSystem ReFS -NewFileSystemLabel KustoBenchmark -Confirm:$false -Force |
                Out-Null
            $storageRoot = 'R:\'
            & fsutil.exe devdrv query R: | Set-Content (Join-Path $resultsDirectory 'devdrive.txt')
            if ($LASTEXITCODE -ne 0)
            {
                throw 'The benchmark volume could not be verified as a Dev Drive.'
            }

            $workspace = Join-Path $storageRoot 'workspace'
            New-Item -ItemType Directory -Path $workspace | Out-Null
            Get-ChildItem -LiteralPath $env:GITHUB_WORKSPACE -Force |
                Copy-Item -Destination $workspace -Recurse -Force
        }

        $paths = [ordered]@{
            TEMP = Join-Path $storageRoot 'temp'
            TMP = Join-Path $storageRoot 'temp'
            NUGET_PACKAGES = Join-Path $storageRoot 'nuget\packages'
            NUGET_HTTP_CACHE_PATH = Join-Path $storageRoot 'nuget\http-cache'
            NUGET_SCRATCH = Join-Path $storageRoot 'nuget\scratch'
            NUGET_PLUGINS_CACHE_PATH = Join-Path $storageRoot 'nuget\plugins-cache'
        }
        foreach ($entry in $paths.GetEnumerator())
        {
            New-Item -ItemType Directory -Path $entry.Value -Force | Out-Null
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
            "$($entry.Key)=$($entry.Value)" | Add-Content -LiteralPath $env:GITHUB_ENV
        }
    }

    $result.effectivePaths = [ordered]@{
        workspace = $workspace
        temp = $env:TEMP
        tmp = $env:TMP
        nugetPackages = $env:NUGET_PACKAGES
        nugetHttpCache = $env:NUGET_HTTP_CACHE_PATH
    }
    $result.effectiveFileSystem = (Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($workspace).Substring(0, 1))).FileSystem
    "workspace=$workspace" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
}
finally
{
    $timer.Stop()
    $result.setupSeconds = $timer.Elapsed.TotalSeconds
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath
}
