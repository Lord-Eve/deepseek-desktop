param([switch]$SkipNativeChecks, [string]$ExpectedVersion = "", [switch]$SkipValidatedChecks)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$fixturePath = $null
Push-Location $repoRoot
try {
    if ($env:OS -ne 'Windows_NT') { throw 'Build this package on Windows.' }
    $version = ([xml](Get-Content 'src/DeepSeekDesktop/DeepSeekDesktop.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
    if ($ExpectedVersion -and $ExpectedVersion -ne $version) { throw 'Tag and project version do not match.' }
    if (!(Select-String -Path CHANGELOG.md -Pattern ('^## ' + [regex]::Escape($version) + ' '))) { throw 'Missing changelog section for this version.' }
    if (!(Test-Path ('docs/releases/v' + $version + '.md'))) { throw 'Missing release notes for this version.' }
    & (Join-Path $PSScriptRoot 'prepare-engine.ps1')
    # Local packaging may reuse checks already completed for the current changes. CI never skips checks.
    if (!$SkipValidatedChecks) {
        if (!(Get-Command python -ErrorAction SilentlyContinue)) { throw 'Python 3.11+ is required for protocol checks.' }
        $fixturePath = Join-Path ([IO.Path]::GetTempPath()) ('deepseek-fixture-' + [Guid]::NewGuid().ToString('N') + '.toml')
        dotnet run --project tests/DeepSeekDesktop.Checks -c Release -- --fixture $fixturePath
        if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
        if (!$SkipNativeChecks) {
            dotnet run --project tests/DeepSeekDesktop.WindowsChecks -c Release
            if ($LASTEXITCODE -ne 0) { throw 'Native Windows checks failed.' }
        }
        python tests/engine_contract.py --engine engine/ds-free-api.exe --config $fixturePath
        if ($LASTEXITCODE -ne 0) { throw 'Real-engine protocol checks failed.' }
        dotnet run --project tests/DeepSeekDesktop.GatewayChecks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Qingyan gateway checks failed.' }
        dotnet run --project tests/DeepSeekDesktop.GrokChecks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Grok OAuth and protocol checks failed.' }
        dotnet run --project tests/DeepSeekDesktop.UiChecks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'WPF UI checks failed.' }
    }
    $packageName = 'DeepSeekDesktop-' + $version + '-win-x64'
    $outputDir = Join-Path $repoRoot ('dist/' + $packageName)
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($outputDir)) -ne [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist'))) { throw 'Package output must stay in this workspace dist directory.' }
    if ((Test-Path $outputDir) -and ((Get-Item -LiteralPath $outputDir).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Package output cannot be a directory link.' }
    if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
    dotnet publish src/DeepSeekDesktop/DeepSeekDesktop.csproj -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --disable-build-servers -m:1 -o $outputDir
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    $publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $outputDir 'DeepSeekDesktop.exe'))
    if ($publishedVersion.ProductVersion.Split('+')[0] -ne $version -or $publishedVersion.FileVersion -ne ($version + '.0')) {
        throw 'Published executable and project version do not match.'
    }
    New-Item (Join-Path $outputDir 'engine') -ItemType Directory -Force | Out-Null
    Copy-Item engine/ds-free-api.exe (Join-Path $outputDir 'engine/')
    Copy-Item README.md,LICENSE,THIRD-PARTY-NOTICES.md,CHANGELOG.md $outputDir
    Copy-Item vendor/engine-lock.json (Join-Path $outputDir 'engine-lock.json')
    Get-ChildItem $outputDir -Filter '*.pdb' | Remove-Item
    $sourceDir = Join-Path $outputDir 'source'
    New-Item $sourceDir -ItemType Directory -Force | Out-Null
    $sourceRoots = @('src', 'tests', 'tools', '.github', 'vendor', 'docs')
    foreach ($sourceRoot in $sourceRoots) {
        if (!(Test-Path $sourceRoot)) { continue }
        Get-ChildItem $sourceRoot -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|__pycache__)[\\/]' -and $_.Extension -notin @('.zip', '.pyc', '.pdb') } | ForEach-Object {
            $relative = $_.FullName.Substring($repoRoot.Length + 1)
            $target = Join-Path $sourceDir $relative
            New-Item (Split-Path $target -Parent) -ItemType Directory -Force | Out-Null
            Copy-Item $_.FullName $target
        }
    }
    Copy-Item README.md,LICENSE,THIRD-PARTY-NOTICES.md,CHANGELOG.md,global.json,.gitignore,.gitattributes,.editorconfig,AGENTS.md $sourceDir
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $noticesDir = Join-Path $outputDir 'runtime-notices'
    New-Item $noticesDir -ItemType Directory -Force | Out-Null
    foreach ($runtime in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64', 'microsoft.aspnetcore.app.runtime.win-x64')) {
        $pack = Get-ChildItem (Join-Path $nugetRoot $runtime) -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        foreach ($noticeFile in @('LICENSE.TXT', 'LICENSE', 'THIRD-PARTY-NOTICES.TXT')) {
            $noticePath = Join-Path $pack.FullName $noticeFile
            if (Test-Path $noticePath) { Copy-Item $noticePath (Join-Path $noticesDir ($runtime + '-' + $noticeFile)) }
        }
    }
    $zipPath = $outputDir + '.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -LiteralPath $outputDir -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zipPath + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($zipPath) + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host ('Package: ' + $zipPath)
} finally {
    if ($fixturePath -and (Test-Path $fixturePath)) { Remove-Item $fixturePath -Force }
    Pop-Location
}
