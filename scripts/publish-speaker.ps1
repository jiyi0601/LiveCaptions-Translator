param(
    [string]$DotNet = 'dotnet',
    [string]$OutputDirectory = ('artifacts/speaker-package-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [string]$Models = '.tools/speaker-models',
    [switch]$IncludeModels
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $destination = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory)) }
    if (Test-Path -LiteralPath $destination) { throw 'Use a new output directory so settings or personal history cannot enter the package.' }
    & $DotNet publish LiveCaptionsTranslator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $destination
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath LICENSE,THIRD_PARTY_NOTICES.md -Destination $destination
    Copy-Item -LiteralPath licenses -Destination $destination -Recurse
    Copy-Item -LiteralPath docs -Destination $destination -Recurse
    Copy-Item -LiteralPath src/speech/models/manifest.json -Destination (Join-Path $destination 'speaker-models.manifest.json')
    $manifestDirectory = Join-Path $destination 'src/speech/models'
    New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
    Copy-Item -LiteralPath src/speech/models/manifest.json -Destination $manifestDirectory
    if ($IncludeModels) {
        $manifest = Get-Content -LiteralPath src/speech/models/manifest.json -Raw | ConvertFrom-Json
        foreach ($package in $manifest) {
            foreach ($file in $package.Files) {
                $from = Join-Path $Models $file.Path
                if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ne $file.Sha256) { throw "Model checksum mismatch: $from" }
                $to = Join-Path $destination ('models/speaker-v1/' + $file.Path)
                New-Item -ItemType Directory -Path (Split-Path $to -Parent) -Force | Out-Null
                Copy-Item -LiteralPath $from -Destination $to
            }
        }
    }
    $zip = $destination + '.zip'
    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $zip -CompressionLevel Optimal
    (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash + '  ' + (Split-Path $zip -Leaf) |
        Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
    Write-Host "Test package: $zip"
}
finally { Pop-Location }
