param(
    [switch]$SkipTests,
    [string]$OutputDirectory = "artifacts"
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    if (-not $SkipTests) {
        dotnet run --project tests/Multiloud.Tests/Multiloud.Tests.csproj --configuration Release
        if ($LASTEXITCODE -ne 0) { throw "DSP verification failed." }
        dotnet run --project tests/Multiloud.Playback.Tests/Multiloud.Playback.Tests.csproj --configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Playback and preview cache verification failed." }
    }
    $outputRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
    $publishDirectory = Join-Path $outputRoot "Loudline-win-x64"
    if (Test-Path $publishDirectory) { Remove-Item $publishDirectory -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
    [xml]$project = Get-Content "src/Multiloud.App/Multiloud.App.csproj"
    $appVersion = [string]$project.Project.PropertyGroup.Version
    dotnet publish src/Multiloud.App/Multiloud.App.csproj --configuration Release --runtime win-x64 --self-contained true --output $publishDirectory -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=None -p:DebugSymbols=false "-p:Version=$appVersion"
    if ($LASTEXITCODE -ne 0) { throw "Windows publish failed." }
    $publishedFiles = @(Get-ChildItem $publishDirectory -File -Recurse)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne "Loudline.exe") {
        throw "Single-file publish produced unexpected runtime files."
    }
    foreach ($file in @("README.md", "LICENSE", "THIRD-PARTY-NOTICES.md")) {
        Copy-Item $file $publishDirectory -Force
    }
    $licenses = Join-Path $publishDirectory "licenses"
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null
    $dotnetRoot = Split-Path (Get-Command dotnet).Source
    # Self-contained publish resolves runtime packs separately from the installed
    # shared runtime. Copy notices from the exact versions used by this build.
    $assets = Get-Content "src/Multiloud.App/obj/project.assets.json" -Raw | ConvertFrom-Json
    $runtimePackages = @{}
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        $downloads = $framework.Value.PSObject.Properties["downloadDependencies"]
        if ($downloads) {
            foreach ($package in $downloads.Value) {
                if ($package.name -in @("Microsoft.NETCore.App.Runtime.win-x64", "Microsoft.WindowsDesktop.App.Runtime.win-x64")) {
                    $runtimePackages[$package.name] = ($package.version -replace '^\[', '').Split(',')[0].Trim()
                }
            }
        }
    }
    foreach ($packageName in @("Microsoft.NETCore.App.Runtime.win-x64", "Microsoft.WindowsDesktop.App.Runtime.win-x64")) {
        if (-not $runtimePackages.ContainsKey($packageName)) { throw "The resolved runtime pack version is missing: $packageName" }
        $version = $runtimePackages[$packageName]
        $candidateRoots = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ "$($packageName.ToLowerInvariant())/$version" })
        $candidateRoots += Join-Path $dotnetRoot "packs/$packageName/$version"
        $copiedLicense = $false
        foreach ($root in $candidateRoots) {
            if (-not (Test-Path $root)) { continue }
            foreach ($notice in Get-ChildItem $root -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.(TXT|MD))?$' }) {
                Copy-Item $notice.FullName (Join-Path $licenses "$packageName-$($notice.Name)") -Force
                if ($notice.Name -match '^LICENSE') { $copiedLicense = $true }
            }
        }
        if (-not $copiedLicense) { throw "Runtime license could not be packaged for $packageName $version." }
    }
    $sdkVersion = (dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Could not resolve the .NET SDK version." }
    $desktopNotices = Join-Path $dotnetRoot "sdk/$sdkVersion/Sdks/Microsoft.NET.Sdk.WindowsDesktop"
    foreach ($notice in @("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT")) {
        $source = Join-Path $desktopNotices $notice
        if (-not (Test-Path $source)) { throw "Windows Desktop runtime notice is missing: $source" }
        Copy-Item $source (Join-Path $licenses "Microsoft.WindowsDesktop-$notice") -Force
    }
    $zipPath = Join-Path $outputRoot "Loudline-win-x64.zip"
    Compress-Archive -Path "$publishDirectory/*" -DestinationPath $zipPath -Force
    Write-Output "Portable Windows build: $zipPath"
}
finally { Pop-Location }
