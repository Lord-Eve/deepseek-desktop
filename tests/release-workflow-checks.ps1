$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility
$repoRoot = Split-Path $PSScriptRoot -Parent

# Exercise the actual workflow scripts with an in-memory GitHub and temporary files.
function Get-WorkflowScript([string]$Name) {
    $lines = [IO.File]::ReadAllLines((Join-Path $repoRoot '.github/workflows/release.yml'))
    $insideStep = $false
    $insideRun = $false
    $body = [Collections.Generic.List[string]]::new()
    foreach ($line in $lines) {
        if ($line -eq "      - name: $Name") { $insideStep = $true; continue }
        if (!$insideStep) { continue }
        if (!$insideRun) {
            if ($line -eq '        run: |') { $insideRun = $true }
            continue
        }
        if ($line.StartsWith('          ')) { $body.Add($line.Substring(10)) }
        elseif (!$line.Trim()) { $body.Add('') }
        else { break }
    }
    if (!$body.Count) { throw "Workflow script not found: $Name" }
    [scriptblock]::Create(($body -join "`n"))
}

$checkScript = Get-WorkflowScript 'Check existing release'
$publishScript = Get-WorkflowScript 'Publish prerelease'
$zipName = 'DeepSeekDesktop-0.4.1-win-x64.zip'
$hashName = "$zipName.sha256"
$localBytes = [Text.Encoding]::UTF8.GetBytes('rebuilt ZIP fixture')
$remoteBytes = [Text.Encoding]::UTF8.GetBytes('original published ZIP fixture')
function Get-Hash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
$localChecksum = "$(Get-Hash $localBytes)  $zipName`n"
$remoteChecksum = "$(Get-Hash $remoteBytes)  $zipName`n"
function New-Release([string[]]$Names = @(), [bool]$Draft = $false, [int]$Size = 20) {
    @{ draft = $Draft; assets = @($Names | ForEach-Object { @{ name = $_; size = $Size } }) }
}
function Assert([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Invoke-Scenario {
    param(
        [string]$Name,
        [scriptblock]$Code,
        [object[]]$Responses,
        [scriptblock]$Verify,
        [string]$ExpectedError = '',
        [int]$CreateExit = 0,
        [int]$UploadExit = 0,
        [int]$DownloadExit = 0,
        [string]$DownloadChecksum = $localChecksum,
        [string]$Tag = 'v0.4.1'
    )
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('release-check-' + [Guid]::NewGuid().ToString('N'))
    New-Item $tempRoot -ItemType Directory | Out-Null
    $saved = @{}
    foreach ($key in @('RELEASE_TAG', 'GITHUB_API_URL', 'GITHUB_REPOSITORY', 'GITHUB_OUTPUT', 'GH_TOKEN', 'RUNNER_TEMP')) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
    }
    Push-Location $tempRoot
    try {
        $env:RELEASE_TAG = $Tag
        $env:GITHUB_API_URL = 'https://api.example.invalid'
        $env:GITHUB_REPOSITORY = 'fixture/desktop'
        $env:GITHUB_OUTPUT = Join-Path $tempRoot 'output.txt'
        $env:GH_TOKEN = 'fake-token-never-sent'
        $env:RUNNER_TEMP = $tempRoot
        New-Item 'dist', 'docs/releases' -ItemType Directory -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $tempRoot "dist/$zipName"), $localBytes)
        [IO.File]::WriteAllText((Join-Path $tempRoot "dist/$hashName"), $localChecksum)
        [IO.File]::WriteAllText((Join-Path $tempRoot 'docs/releases/v0.4.1.md'), 'fixture')
        $httpCalls = [Collections.Generic.List[string]]::new()
        $ghCalls = [Collections.Generic.List[object]]::new()
        $uploaded = @{}
        $queue = [Collections.Generic.Queue[object]]::new()
        foreach ($response in $Responses) { $queue.Enqueue($response) }
        function Invoke-RestMethod([string]$Uri, [hashtable]$Headers) {
            $httpCalls.Add($Uri)
            Assert ($Uri -eq 'https://api.example.invalid/repos/fixture/desktop/releases/tags/v0.4.1') 'Unexpected API URL.'
            Assert ($Headers.Authorization -eq 'Bearer fake-token-never-sent') 'Missing API authentication.'
            if (!$queue.Count) { throw 'Unexpected extra API request.' }
            $response = $queue.Dequeue()
            if ($response -is [int]) {
                $httpResponse = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]$response)
                throw [Microsoft.PowerShell.Commands.HttpResponseException]::new("HTTP $response", $httpResponse)
            }
            $response
        }
        function gh {
            $ghCalls.Add(@($args))
            Assert ($args[0] -eq 'release') 'Unexpected gh command.'
            Assert ('--clobber' -notin $args) 'Existing assets must never be overwritten.'
            switch ($args[1]) {
                'create' { $global:LASTEXITCODE = $CreateExit }
                'download' {
                    $pattern = $args[[Array]::IndexOf($args, '--pattern') + 1]
                    $directory = $args[[Array]::IndexOf($args, '--dir') + 1]
                    if ($pattern -eq $zipName) {
                        [IO.File]::WriteAllBytes((Join-Path $directory $zipName), $remoteBytes)
                    } elseif ($pattern -eq $hashName) {
                        [IO.File]::WriteAllText((Join-Path $directory $hashName), $DownloadChecksum)
                    } else { throw 'Unexpected download.' }
                    $global:LASTEXITCODE = $DownloadExit
                }
                'upload' {
                    foreach ($asset in $args[3..([Array]::IndexOf($args, '--repo') - 1)]) {
                        $assetPath = if ([IO.Path]::IsPathRooted($asset)) { $asset } else { Join-Path (Get-Location) $asset }
                        $uploaded[[IO.Path]::GetFileName($asset)] = [IO.File]::ReadAllBytes($assetPath)
                    }
                    $global:LASTEXITCODE = $UploadExit
                }
                default { throw 'Unexpected gh operation.' }
            }
        }
        $failure = $null
        try { & $Code } catch { $failure = $_ }
        if ($ExpectedError) {
            Assert ($null -ne $failure -and $failure.ToString().Contains($ExpectedError)) "${Name}: expected '$ExpectedError', got '$failure'."
        } elseif ($failure) { throw "${Name}: $failure" }
        & $Verify
        Write-Host "PASS: $Name"
    } finally {
        Pop-Location
        foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
        Remove-Item $tempRoot -Recurse -Force
    }
}

Invoke-Scenario 'New tag needs a build' $checkScript @(404) {
    Assert ((Get-Content $env:GITHUB_OUTPUT -Raw).Trim() -eq 'complete=false') 'New release skipped.'
}
Invoke-Scenario 'Complete release skips the entire build' $checkScript @((New-Release @($zipName, $hashName))) {
    Assert ((Get-Content $env:GITHUB_OUTPUT -Raw).Trim() -eq 'complete=true') 'Complete release was not skipped.'
    Assert ($ghCalls.Count -eq 0) 'Complete release was modified.'
}
Invoke-Scenario 'Missing checksum needs repair' $checkScript @((New-Release @($zipName))) {
    Assert ((Get-Content $env:GITHUB_OUTPUT -Raw).Trim() -eq 'complete=false') 'Incomplete release skipped.'
}
Invoke-Scenario 'Invalid tag is rejected before API access' $checkScript @() {
    Assert ($httpCalls.Count -eq 0) 'Invalid tag reached GitHub.'
} -Tag 'main' -ExpectedError 'Expected an existing numeric version tag.'
foreach ($status in @(401, 403, 429, 500)) {
    Invoke-Scenario "HTTP $status is not mistaken for a missing release" $checkScript @($status) {
        Assert (!(Test-Path $env:GITHUB_OUTPUT)) 'API error produced a success output.'
    } -ExpectedError "HTTP $status"
}
Invoke-Scenario 'Draft is not silently accepted' $checkScript @((New-Release @($zipName, $hashName) $true)) {} -ExpectedError 'is a draft'
Invoke-Scenario 'First publication creates one release' $publishScript @(404) {
    Assert ($ghCalls.Count -eq 1 -and $ghCalls[0][1] -eq 'create') 'First release was not created exactly once.'
}
Invoke-Scenario 'Existing complete release is untouched at publish time' $publishScript @((New-Release @($zipName, $hashName))) {
    Assert ($ghCalls.Count -eq 0) 'Existing release was changed.'
}
Invoke-Scenario 'Concurrent creation is recovered without another upload' $publishScript @(404, (New-Release @($zipName, $hashName))) {
    Assert ($ghCalls.Count -eq 1 -and $ghCalls[0][1] -eq 'create') 'Concurrent publication was not recovered.'
    Assert ($global:LASTEXITCODE -eq 0) 'Recovered creation left a failing native exit code for the Actions runner.'
} -CreateExit 1
Invoke-Scenario 'Creation failure remains a real failure' $publishScript @(404, 404) {} -CreateExit 1 -ExpectedError 'Release publication failed.'
Invoke-Scenario 'Release missing both assets gets the pair' $publishScript @((New-Release)) {
    Assert ($uploaded.Count -eq 2 -and $uploaded.ContainsKey($zipName) -and $uploaded.ContainsKey($hashName)) 'Missing asset pair not uploaded.'
}
Invoke-Scenario 'Missing checksum is derived from the original published ZIP' $publishScript @((New-Release @($zipName))) {
    Assert ($uploaded.Count -eq 1 -and $uploaded.ContainsKey($hashName)) 'Only the missing checksum should be uploaded.'
    Assert ([Text.Encoding]::UTF8.GetString($uploaded[$hashName]) -ceq $remoteChecksum) 'Checksum describes the rebuild instead of the published ZIP.'
}
Invoke-Scenario 'Missing ZIP is restored only if its checksum matches' $publishScript @((New-Release @($hashName))) {
    Assert ($uploaded.Count -eq 1 -and $uploaded.ContainsKey($zipName)) 'Only the missing ZIP should be uploaded.'
}
Invoke-Scenario 'Mismatched rebuilt ZIP preserves existing assets' $publishScript @((New-Release @($hashName))) {
    Assert ($uploaded.Count -eq 0) 'Mismatched ZIP was uploaded.'
} -DownloadChecksum $remoteChecksum -ExpectedError 'does not match the published checksum'
Invoke-Scenario 'Upload failure remains visible' $publishScript @((New-Release)) {} -UploadExit 1 -ExpectedError 'Missing release asset upload failed.'
Invoke-Scenario 'Download failure cannot produce a replacement checksum' $publishScript @((New-Release @($zipName))) {
    Assert ($uploaded.Count -eq 0) 'Checksum uploaded after a failed download.'
} -DownloadExit 1 -ExpectedError 'Could not download the published ZIP'
Invoke-Scenario 'Empty existing asset is preserved for review' $publishScript @((New-Release @($zipName) $false 0)) {
    Assert ($uploaded.Count -eq 0) 'Empty existing asset was overwritten.'
} -ExpectedError 'existing release asset is empty'
Invoke-Scenario 'Publish permission error cannot create a new release' $publishScript @(403) {
    Assert ($ghCalls.Count -eq 0) 'API permission error reached a mutation.'
} -ExpectedError 'HTTP 403'
Write-Host 'PASS: 21 release workflow scenarios (mock GitHub only; no real assets or credentials)'
$global:LASTEXITCODE = 0
