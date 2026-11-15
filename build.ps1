# Builds the plugin, zips to dist\, installs to SPT.
#   .\build.ps1 [-SPTPath D:\SPT] [-NoInstall] [-VirusTotal]
# SPT path also read from spt-path.txt or SPT_PATH.
# -VirusTotal uploads each shipped DLL to VirusTotal (public) and writes "name: report link" lines to dist\virustotal.txt.
# It needs the API key in the user environment variable VT_API_KEY.
param(
    [string] $SPTPath = '',
    [switch] $NoInstall,
    [switch] $VirusTotal
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $SPTPath) { $SPTPath = $env:SPT_PATH }
$pathFile = Join-Path $root 'spt-path.txt'
if (-not $SPTPath -and (Test-Path $pathFile)) { $SPTPath = (Get-Content $pathFile -TotalCount 1).Trim() }
if (-not $SPTPath -or -not (Test-Path (Join-Path $SPTPath 'EscapeFromTarkov_Data'))) {
    throw 'Point the script at your SPT folder (the one with EscapeFromTarkov_Data in it): -SPTPath D:\SPT, or put the path in spt-path.txt.'
}
$project = Join-Path $root 'StatIndex\StatIndex.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$pluginVersion = Select-String -Path (Join-Path $root 'StatIndex\Plugin.cs') -Pattern 'Version = "(.+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value }
if ($pluginVersion -ne $version) {
    throw "Version mismatch: StatIndex.csproj $version, Plugin.cs $pluginVersion."
}

# the SDK may be installed per-user, so it may not be on PATH
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }

& $dotnet build $project -c Release --nologo "-p:SPTPath=$SPTPath"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$dll = Join-Path $root 'StatIndex\bin\Release\StatIndex.dll'

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist "StatIndex-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$plugin = Join-Path $stage 'BepInEx\plugins\StatIndex'
New-Item -ItemType Directory -Force $plugin | Out-Null
Copy-Item $dll $plugin
Copy-Item (Join-Path $root 'LICENSE') $plugin
$shippedData = Join-Path $root 'files\statindex-data.json'
Copy-Item $shippedData $plugin

$zip = Join-Path $dist "StatIndex-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# forward slashes so every unzip tool sees folders
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
Write-Host "Packed $zip"

if ($VirusTotal) {
    $key = [Environment]::GetEnvironmentVariable('VT_API_KEY', 'User')
    if (-not $key) { $key = $env:VT_API_KEY }
    if (-not $key) { throw 'Set the VirusTotal API key first: [Environment]::SetEnvironmentVariable(''VT_API_KEY'', ''<key>'', ''User'')' }

    # the DLLs that ship, from the zip's staging folder
    $dlls = Get-ChildItem $stage -Recurse -File -Filter *.dll
    if (-not $dlls) { throw 'No DLLs to upload.' }

    # VirusTotal uploads are public: stop if a DLL holds a local path or a private word.
    # private-terms.txt (gitignored) holds your own words, one per line.
    $private = @('C:\Users', 'C:/Users')
    $termsFile = Join-Path $root 'private-terms.txt'
    if (Test-Path $termsFile) { $private += Get-Content $termsFile | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() } }
    foreach ($file in $dlls) {
        $text = [System.Text.Encoding]::GetEncoding(28591).GetString([System.IO.File]::ReadAllBytes($file.FullName))
        $wide = [System.Text.Encoding]::Unicode.GetString([System.IO.File]::ReadAllBytes($file.FullName))
        foreach ($p in $private) {
            if ($text.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $wide.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Not uploading: $($file.Name) contains '$p'."
            }
        }
    }

    $lines = @()
    foreach ($file in $dlls) {
        # upload from the DLL's folder by bare name, so only the file name is sent
        Push-Location $file.DirectoryName
        try { $response = & curl.exe -s -S --fail-with-body -X POST 'https://www.virustotal.com/api/v3/files' -H "x-apikey: $key" -F "file=@$($file.Name)" }
        finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw "VirusTotal upload of $($file.Name) failed: $response" }
        $sha = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines += "$($file.Name): https://www.virustotal.com/gui/file/$sha"
    }
    Set-Content -Path (Join-Path $dist 'virustotal.txt') -Value $lines -Encoding ascii
    Write-Host 'VirusTotal (scans take a minute or two to finish):'
    $lines | ForEach-Object { Write-Host "  $_" }
}

if (-not $NoInstall) {
    $dest = Join-Path $SPTPath 'BepInEx\plugins\StatIndex'
    New-Item -ItemType Directory -Force $dest | Out-Null
    # learned-locks.json is the player's, leave it
    try { Copy-Item $dll, $shippedData -Destination $dest -Force -ErrorAction Stop; Write-Host "Installed to $dest" }
    catch { Write-Warning "Couldn't copy the plugin into $dest. Close the game and build again. ($($_.Exception.Message))" }

}
