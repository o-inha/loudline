param(
    [Parameter(Mandatory = $true)][string]$SingleExePath,
    [Parameter(Mandatory = $true)][string]$FolderExePath,
    [string]$OutputPath = "startup-performance.json",
    [ValidateRange(1, 20)][int]$FreshLaunches = 3,
    [ValidateRange(1, 20)][int]$RepeatLaunches = 5,
    [ValidateRange(1, 60)][int]$TimeoutSeconds = 20
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not [OperatingSystem]::IsWindows()) { throw "Startup measurement requires Windows' shared QPC clock." }
if (-not [Diagnostics.Stopwatch]::IsHighResolution) { throw "The startup harness requires a high-resolution Stopwatch clock." }

$single = [IO.Path]::GetFullPath($SingleExePath)
$folder = [IO.Path]::GetFullPath($FolderExePath)
foreach ($exe in @($single, $folder)) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Published executable not found: $exe" }
}
if ($single.Equals($folder, [StringComparison]::OrdinalIgnoreCase)) { throw "Provide distinct single-EXE and folder-comparison executables." }
$output = [IO.Path]::GetFullPath($OutputPath)
if ($output.Equals($single, [StringComparison]::OrdinalIgnoreCase) -or $output.Equals($folder, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The report output cannot replace either measured executable."
}
$outputDirectory = [IO.Path]::GetDirectoryName($output)
if (-not [IO.Directory]::Exists($outputDirectory)) { [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null }

$owner = [Guid]::NewGuid().ToString("N")
$ownedRoot = Join-Path ([IO.Path]::GetTempPath()) "Loudline-startup-bench-$owner"
[IO.Directory]::CreateDirectory($ownedRoot) | Out-Null
$ownershipFile = Join-Path $ownedRoot ".loudline-startup-bench-owner"
[IO.File]::WriteAllText($ownershipFile, $owner)
$bundleVariable = "DOTNET_BUNDLE_EXTRACT_BASE_DIR"
$previousBundleDirectory = [Environment]::GetEnvironmentVariable($bundleVariable, "Process")
$frequency = [Diagnostics.Stopwatch]::Frequency
$runs = [Collections.Generic.List[object]]::new()

function Invoke-FrameProbe {
    param([string]$Variant, [string]$Mode, [int]$Iteration, [string]$Executable, [string]$CacheDirectory)
    [IO.Directory]::CreateDirectory($CacheDirectory) | Out-Null
    $probeFile = Join-Path $ownedRoot "$Variant-$Mode-$Iteration.json"
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $info.UseShellExecute = $false
    $info.ArgumentList.Add("--startup-probe")
    $info.ArgumentList.Add($probeFile)
    # Scope extraction to this benchmark's owned directories, never the user's
    # regular .NET extraction cache. The outer finally restores the old setting.
    [Environment]::SetEnvironmentVariable($bundleVariable, $CacheDirectory, "Process")
    $info.Environment[$bundleVariable] = $CacheDirectory
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $started = $false
    $processStartTimestamp = [Diagnostics.Stopwatch]::GetTimestamp()
    try {
        if (-not $process.Start()) { throw "Could not start $Variant startup probe." }
        $started = $true
        $deadline = $processStartTimestamp + [long]($TimeoutSeconds * $frequency)
        while (-not $process.HasExited) {
            if ([Diagnostics.Stopwatch]::GetTimestamp() -ge $deadline) {
                $process.Kill($true)
                $process.WaitForExit(5000) | Out-Null
                throw "$Variant $Mode run $Iteration exceeded the $TimeoutSeconds second startup deadline."
            }
            Start-Sleep -Milliseconds 20
        }
        $observedExitTimestamp = [Diagnostics.Stopwatch]::GetTimestamp()
        if ($observedExitTimestamp -gt $deadline) { throw "$Variant $Mode run $Iteration did not exit before its startup deadline." }
        if ($process.ExitCode -ne 0) { throw "$Variant $Mode run $Iteration failed with exit code $($process.ExitCode)." }
        if (-not [IO.File]::Exists($probeFile)) { throw "$Variant $Mode run $Iteration exited without a first-frame probe." }
        $probe = [IO.File]::ReadAllText($probeFile) | ConvertFrom-Json -AsHashtable
        foreach ($key in @("schemaVersion", "frameEvent", "firstFrameTimestamp", "frequency", "isHighResolution", "managedStartupTimestamp", "processId", "executablePath", "version", "editorInitialized", "frameWidth", "frameHeight")) {
            if (-not $probe.ContainsKey($key)) { throw "Startup probe is missing $key." }
        }
        if ($probe.schemaVersion -ne 1 -or $probe.frameEvent -ne "MainWindow.ContentRendered" -or
            [long]$probe.frequency -ne $frequency -or $probe.isHighResolution -ne $true) {
            throw "$Variant startup probe did not use the same Windows QPC clock and frame event."
        }
        if ([int]$probe.processId -ne $process.Id -or
            -not ([IO.Path]::GetFullPath([string]$probe.executablePath)).Equals($Executable, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Variant startup probe belongs to a different process or executable."
        }
        $frameTimestamp = [long]$probe.firstFrameTimestamp
        if ($frameTimestamp -lt $processStartTimestamp -or $frameTimestamp -gt $observedExitTimestamp -or
            [long]$probe.managedStartupTimestamp -lt $processStartTimestamp -or [long]$probe.managedStartupTimestamp -gt $frameTimestamp -or
            $probe.editorInitialized -ne $false -or [double]$probe.frameWidth -le 0 -or [double]$probe.frameHeight -le 0) {
            throw "$Variant startup probe has an invalid timestamp, frame size or editor initialization order."
        }
        $elapsed = ($frameTimestamp - $processStartTimestamp) * 1000.0 / $frequency
        if ($elapsed -gt $TimeoutSeconds * 1000) { throw "$Variant startup frame exceeded its deadline." }
        return [ordered]@{
            variant = $Variant
            cacheMode = $Mode
            iteration = $Iteration
            processId = $process.Id
            processStartTimestamp = $processStartTimestamp
            firstFrameTimestamp = $frameTimestamp
            observedExitTimestamp = $observedExitTimestamp
            frequency = $frequency
            firstFrameMilliseconds = [Math]::Round($elapsed, 3)
            observedExitMilliseconds = [Math]::Round(($observedExitTimestamp - $processStartTimestamp) * 1000.0 / $frequency, 3)
            exitCode = $process.ExitCode
            probe = $probe
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
        }
        $process.Dispose()
    }
}

function Get-MedianMilliseconds {
    param([object[]]$Values)
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 0) { throw "Cannot summarize an empty startup sample." }
    $middle = [int][Math]::Floor($ordered.Count / 2)
    $median = if ($ordered.Count % 2) { [double]$ordered[$middle] }
        else { ([double]$ordered[$middle - 1] + [double]$ordered[$middle]) / 2 }
    return [Math]::Round($median, 3)
}

function Get-ExecutableMetadata {
    param([string]$Path)
    $file = Get-Item -LiteralPath $Path
    $files = @(Get-ChildItem -LiteralPath $file.DirectoryName -File -Recurse)
    $totalBytes = [long]0
    foreach ($item in $files) { $totalBytes += $item.Length }
    # Hash after measurement so the metadata read does not pre-warm the large EXE.
    return [ordered]@{
        path = $file.FullName
        lengthBytes = $file.Length
        lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString("O")
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        directoryFileCount = $files.Count
        directoryBytes = $totalBytes
    }
}

try {
    $variants = @(
        [ordered]@{ name = "singleExe"; path = $single },
        [ordered]@{ name = "folder"; path = $folder }
    )
    # Alternate variants to avoid running every sample of one distribution first.
    for ($iteration = 1; $iteration -le $FreshLaunches; $iteration++) {
        foreach ($variant in $variants) {
            $cache = Join-Path $ownedRoot "$($variant.name)-fresh-$iteration"
            $run = Invoke-FrameProbe $variant.name "freshExtractionDirectory" $iteration $variant.path $cache
            $runs.Add($run)
            Write-Host ("{0} fresh {1}: {2:N3} ms" -f $variant.name, $iteration, $run.firstFrameMilliseconds)
        }
    }
    for ($iteration = 1; $iteration -le $RepeatLaunches; $iteration++) {
        foreach ($variant in $variants) {
            $cache = Join-Path $ownedRoot "$($variant.name)-fresh-$FreshLaunches"
            $run = Invoke-FrameProbe $variant.name "reusedExtractionDirectory" $iteration $variant.path $cache
            $runs.Add($run)
            Write-Host ("{0} repeat {1}: {2:N3} ms" -f $variant.name, $iteration, $run.firstFrameMilliseconds)
        }
    }
    $summary = [ordered]@{}
    foreach ($variant in $variants) {
        $fresh = @($runs | Where-Object { $_.variant -eq $variant.name -and $_.cacheMode -eq "freshExtractionDirectory" })
        $repeat = @($runs | Where-Object { $_.variant -eq $variant.name -and $_.cacheMode -eq "reusedExtractionDirectory" })
        $summary[$variant.name] = [ordered]@{
            executable = Get-ExecutableMetadata $variant.path
            freshExtractionDirectory = [ordered]@{
                count = $fresh.Count
                medianMilliseconds = Get-MedianMilliseconds @($fresh | ForEach-Object { $_.firstFrameMilliseconds })
            }
            reusedExtractionDirectory = [ordered]@{
                count = $repeat.Count
                medianMilliseconds = Get-MedianMilliseconds @($repeat | ForEach-Object { $_.firstFrameMilliseconds })
            }
        }
    }
    $report = [ordered]@{
        schemaVersion = 1
        measurement = "Windows CI machine: external Process.Start request to the early MainWindow.ContentRendered event, using the shared QPC clock."
        limitations = @(
            "This is not physical input-to-photon or compositor presentation timing.",
            "These timings describe this CI machine, not the user's hardware.",
            "Fresh means a new isolated native bundle extraction directory; OS filesystem and antivirus caches are not flushed.",
            "Folder launches use the same fresh/reused directory controls, although that distribution does not extract a bundle.",
            "The process exits before editor initialization. This does not measure full editor readiness or prove virtual desktop placement."
        )
        measuredAtUtc = [DateTime]::UtcNow.ToString("O")
        machine = [ordered]@{
            os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
            architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            logicalProcessors = [Environment]::ProcessorCount
            githubRunId = [Environment]::GetEnvironmentVariable("GITHUB_RUN_ID")
        }
        clock = [ordered]@{ frequency = $frequency; isHighResolution = [Diagnostics.Stopwatch]::IsHighResolution }
        launchOrder = "Alternating singleExe/folder, three fresh then five reused by default."
        variants = $summary
        runs = $runs.ToArray()
    }
    $temporary = "$output.$owner.partial"
    try {
        $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding utf8
        Move-Item -LiteralPath $temporary -Destination $output -Force
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    Write-Host "Startup measurement written to $output"
}
finally {
    [Environment]::SetEnvironmentVariable($bundleVariable, $previousBundleDirectory, "Process")
    if ([IO.File]::Exists($ownershipFile) -and [IO.File]::ReadAllText($ownershipFile) -eq $owner) {
        Remove-Item -LiteralPath $ownedRoot -Recurse -Force
    }
}
