param(
    [string]$DotNet = 'dotnet',
    [string]$Models = '.tools/speaker-models',
    [switch]$DownloadFixtures,
    [switch]$InstallModels,
    [switch]$Soak
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $output = Join-Path $projectRoot ('artifacts/speaker-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $archive = Join-Path $projectRoot '.tools/speaker-downloads/test-clean.tar.gz'
    $fixtureRoot = Join-Path $projectRoot '.tools/speaker-fixtures'
    if ($DownloadFixtures) {
        New-Item -ItemType Directory -Path (Split-Path $archive -Parent),$fixtureRoot -Force | Out-Null
        if (!(Test-Path -LiteralPath $archive)) {
            Invoke-WebRequest 'https://www.openslr.org/resources/12/test-clean.tar.gz' -OutFile $archive
        }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '39FDE525E59672DC6D1551919B1478F724438A95AA55F874B576BE21967E6C23') {
            throw 'LibriSpeech archive checksum mismatch.'
        }
        $clips = @(
            '61/70968/61-70968-0004.flac', '61/70968/61-70968-0011.flac',
            '260/123440/260-123440-0004.flac', '260/123440/260-123440-0018.flac',
            '121/121726/121-121726-0000.flac', '121/121726/121-121726-0004.flac',
            '61/70968/61-70968.trans.txt', '260/123440/260-123440.trans.txt',
            '121/121726/121-121726.trans.txt'
        ) | ForEach-Object { 'LibriSpeech/test-clean/' + $_ }
        $metadata = @('LibriSpeech/LICENSE.TXT','LibriSpeech/README.TXT','LibriSpeech/SPEAKERS.TXT')
        & tar -xf $archive -C $fixtureRoot @clips @metadata
        if ($LASTEXITCODE -ne 0) { throw 'Fixture extraction failed.' }
    }
    & $DotNet run --project tests/Reader.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    & $DotNet build tests/Speaker.Integration -c Release -o (Join-Path $output 'harness')
    if ($LASTEXITCODE -ne 0) { throw 'Integration harness build failed.' }
    $harness = Join-Path $output 'harness/Speaker.Integration.dll'
    if ($InstallModels) {
        & $DotNet $harness --install $Models
        if ($LASTEXITCODE -ne 0) { throw 'Model installation failed.' }
    }
    $generated = Join-Path $output 'fixtures'
    & $DotNet $harness --make-fixtures $fixtureRoot $generated
    if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed. Use -DownloadFixtures on first run.' }
    foreach ($name in @('clean-three','clean-two','same-speaker','rapid-turns','long-pauses','overlap')) {
        & $DotNet $harness $Models (Join-Path $generated "$name.wav") (Join-Path $output "$name.json")
        if ($LASTEXITCODE -ne 0) { throw "Scenario failed to execute: $name" }
    }
    if ($Soak) {
        & $DotNet $harness $Models (Join-Path $generated 'clean-three.wav') (Join-Path $output 'soak-30min.json') --realtime --seconds=1800
        if ($LASTEXITCODE -ne 0) { throw 'Soak test failed.' }
    }
    Write-Host "Reports: $output"
    Write-Host 'Execution success does not mean acceptance. Inspect speaker accuracy and commit latency in every report.'
}
finally { Pop-Location }
