param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$engineLock = Get-Content (Join-Path $repoRoot 'vendor/engine-lock.json') -Raw | ConvertFrom-Json
$archivePath = Join-Path $repoRoot 'vendor/engine-win-x64.zip'
$engineDir = Join-Path $repoRoot 'engine'
$engineExe = Join-Path $engineDir 'ds-free-api.exe'
$sourcePath = Join-Path $repoRoot ('vendor/' + $engineLock.sourceArchive)
if (!(Test-Path $sourcePath) -or (Get-FileHash $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $engineLock.sourceSha256) {
    throw 'Missing or altered corresponding engine source archive.'
}
if ((Test-Path $engineExe) -and (Get-FileHash $engineExe -Algorithm SHA256).Hash.ToLowerInvariant() -eq $engineLock.binarySha256) {
    Write-Host 'Pinned engine verified.'
    return
}
if (!(Test-Path $archivePath)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $downloadUrl = $engineLock.repository + '/releases/download/' + $engineLock.version + '/' + $engineLock.asset
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing
}
if ((Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $engineLock.archiveSha256) {
    throw 'Engine archive SHA-256 mismatch. Remove the archive and retry.'
}
$extractDir = Join-Path ([IO.Path]::GetTempPath()) ('deepseek-engine-' + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDir
    $executable = Get-ChildItem $extractDir -Filter 'ds-free-api.exe' -Recurse | Select-Object -First 1
    if ($null -eq $executable -or (Get-FileHash $executable.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $engineLock.binarySha256) { throw 'Engine binary SHA-256 mismatch.' }
    New-Item $engineDir -ItemType Directory -Force | Out-Null
    Copy-Item $executable.FullName $engineExe -Force
    Write-Host 'Pinned engine prepared.'
} finally {
    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
}
