param(
    [Parameter(Mandatory)][string]$Tag,
    [ValidateSet('win-x64','win-arm64')][string[]]$Runtime = @('win-x64','win-arm64'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/releases')
)
$ErrorActionPreference = 'Stop'
$versionPattern = '^v(?<version>(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)$'
if ($Tag -cnotmatch $versionPattern) { throw 'Use a tag such as v1.0.0 or v1.0.0-beta.1.' }
$version = $Matches.version
if ($version.Contains('-')) {
    foreach ($part in $version.Split('-',2)[1].Split('.')) {
        if ($part -match '^0[0-9]+$') { throw 'Numeric prerelease identifiers must not have leading zeroes.' }
    }
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$stagingRoot = Join-Path $outputRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
try {
    foreach ($rid in ($Runtime | Select-Object -Unique)) {
        $name = "Sumi-$Tag-$rid"
        $publish = Join-Path $stagingRoot $name
        & dotnet publish (Join-Path $repoRoot 'src/Sumi/Sumi.csproj') -c Release -r $rid --self-contained true -o $publish "-p:Version=$version" '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:PublishTrimmed=false' '-p:DebugType=none' '-p:DebugSymbols=false'
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid." }
        if (!(Test-Path (Join-Path $publish 'Sumi.exe')) -or (Test-Path (Join-Path $publish 'coreclr.dll'))) { throw 'Single-file publish is incomplete.' }
        $documents = Join-Path $publish 'docs'
        [IO.Directory]::CreateDirectory($documents) | Out-Null
        Copy-Item (Join-Path $repoRoot 'LICENSE') (Join-Path $documents 'LICENSE')
        $looseLicense = Join-Path $publish 'LICENSE'
        if (Test-Path -LiteralPath $looseLicense) { Remove-Item -LiteralPath $looseLicense }
        Copy-Item (Join-Path $repoRoot 'docs/distribution-readme.txt') (Join-Path $documents 'README.txt')
        # Include the runtime's license and third-party notices from the exact restored packages.
        $assetFile = Join-Path $repoRoot 'src/Sumi/obj/project.assets.json'
        $assets = Get-Content -LiteralPath $assetFile -Raw | ConvertFrom-Json
        $notices = Join-Path $documents 'licenses'
        [IO.Directory]::CreateDirectory($notices) | Out-Null
        $runtimePackages = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object { $_.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.' })
        if ($runtimePackages.Count -lt 2) { throw 'Runtime package metadata is missing.' }
        foreach ($library in $runtimePackages) {
            $found = $false
            $packageVersion = $library.version.Trim('[',']').Split(',')[0].Trim()
            $packagePath = "$($library.name.ToLowerInvariant())/$packageVersion"
            foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
                $package = Join-Path $folder $packagePath
                if (!(Test-Path -LiteralPath $package)) { continue }
                $packageNotices = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES|ThirdPartyNotices)(\.txt|\.TXT|\.md)?$' })
                foreach ($notice in $packageNotices) {
                    Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $notices ($library.name + '-' + $packageVersion + '-' + $notice.Name))
                    $found = $true
                }
            }
            if (!$found) { throw "Runtime license notices not found for $($library.name)." }
        }
        $unexpected = @(Get-ChildItem -LiteralPath $publish -Force | Where-Object { $_.Name -notin @('Sumi.exe','docs') })
        if ($unexpected.Count -gt 0) { throw "Unexpected loose files in single-file package: $($unexpected.Name -join ', ')" }
        $zip = Join-Path $outputRoot "$name.zip"
        Compress-Archive -Path $publish -DestinationPath $zip -CompressionLevel Optimal -Force
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n", [Text.UTF8Encoding]::new($false))
        Write-Output "Packaged $zip"
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($stagingRoot)
    if ($resolved.StartsWith($outputRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -match '^\.staging-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
