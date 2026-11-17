param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$BuildDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
if($Version -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+$'){throw 'Use a development prerelease version.'}
$projectRoot=Split-Path -Parent $PSScriptRoot
$build=if($BuildDirectory){[IO.Path]::GetFullPath($BuildDirectory)}else{Join-Path $projectRoot 'build'}
$output=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $projectRoot 'dist'}
$manifestPath=Join-Path $projectRoot 'source-manifest.json'
$manifest=@(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
if($manifest.Count -lt 25){throw 'Reviewed source manifest is missing or incomplete.'}
$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$sourceRows=@()
$secretPattern='gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[0-9A-Z]{16}|-----BEGIN (RSA |OPENSSH |EC )?PRIVATE KEY-----'
function ReviewedBytes([string]$path){
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw ('Missing package input: '+$path)}
    if(([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Redirected package inputs are not accepted.'}
    $bytes=[IO.File]::ReadAllBytes($path)
    foreach($encoding in @([Text.Encoding]::UTF8,[Text.Encoding]::Unicode)){
        if($encoding.GetString($bytes) -match $secretPattern){throw 'A package input contains a credential pattern.'}
    }
    return ,$bytes
}
foreach($row in $manifest){
    $relative=[string]$row.file
    if($relative -match '(^|/)\.\.?(/|$)|\\|:|^/' -or -not $names.Add($relative)){throw 'Invalid source manifest path.'}
    $path=Join-Path $projectRoot $relative
    if($relative -match '\.(exe|dll|pak|sav|bin|db|so|apk|zip|jsonl|log)$' -or $relative -match '(^|/)(research|TwinKartPrivate)/'){
        throw 'Non-source input in source manifest.'
    }
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $row.sha256){throw ('Source differs from reviewed manifest: '+$relative)}
    $sourceRows+=@{Name=$relative;Bytes=(ReviewedBytes $path)}
}
$sourceRows+=@{Name='source-manifest.json';Bytes=(ReviewedBytes $manifestPath)}
$toolNames=@('TwinKartLauncher.exe','TwinKartGateway.exe','TwinKartSession.exe','TwinKartAudit.exe',
    'TwinKartSdkCopy.exe','TwinKartInspectIntegration.exe','TwinKartTrackAudit.exe','TwinKartSetup.exe',
    'TwinKartDriftCamera.dll','TwinKartDriftRaceBridge.dll','TwinKartDriftAssetInventory.dll')
$releaseRows=@()
foreach($name in $toolNames){$releaseRows+=@{Name='build/'+$name;Bytes=(ReviewedBytes (Join-Path $build $name))}}
foreach($relative in @('README.md','README.en.md','CHANGELOG.md','LICENSE','SOURCE_PROVENANCE.md','tools/run-macos.sh','tools/PrepareDriftClient.ps1')){
    $releaseRows+=@{Name=$relative;Bytes=(ReviewedBytes (Join-Path $projectRoot $relative))}
}
foreach($configuration in @('drift-handling.json','drift-catalog.json')) {
    $path=Join-Path $build ('config/'+$configuration)
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $projectRoot ('config/'+$configuration)) -Algorithm SHA256).Hash){throw 'Built compatibility configuration differs from source.'}
    $releaseRows+=@{Name=('build/config/'+$configuration);Bytes=(ReviewedBytes $path)}
}
function HashBytes([byte[]]$bytes){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
}
$releaseManifest=[ordered]@{version=$Version;development_prerelease=$true;game_assets_included=$false;
    account_data_included=$false;vendor_sdk_included=$false;vendor_graphics_runtime_included=$false;
    readable_compatibility_configuration_included=$true;factory_vehicle_parameters_verified=$false;
    files=@($releaseRows|ForEach-Object{[ordered]@{file=$_.Name;sha256=(HashBytes $_.Bytes)}})}
$releaseRows+=@{Name='release-manifest.json';Bytes=[Text.UTF8Encoding]::new($false).GetBytes(($releaseManifest|ConvertTo-Json -Depth 8))}
New-Item -ItemType Directory -Path $output -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function WriteArchive([string]$path,[object[]]$rows){
    $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
        $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
        try{foreach($row in $rows){
            $entry=$zip.CreateEntry('TwinKartLauncher/'+$row.Name,[IO.Compression.CompressionLevel]::Optimal)
            $writer=$entry.Open();try{$writer.Write($row.Bytes,0,$row.Bytes.Length)}finally{$writer.Dispose()}
        }}finally{$zip.Dispose()}
    }finally{$stream.Dispose()}
    $zip=[IO.Compression.ZipFile]::OpenRead($path)
    try{
        if($zip.Entries.Count -ne $rows.Count){throw 'Package entry count differs.'}
        foreach($row in $rows){
            $reader=$zip.GetEntry('TwinKartLauncher/'+$row.Name).Open();$copy=[IO.MemoryStream]::new()
            try{$reader.CopyTo($copy);if((HashBytes $copy.ToArray()) -ne (HashBytes $row.Bytes)){throw 'Package verification failed.'}}
            finally{$reader.Dispose();$copy.Dispose()}
        }
    }finally{$zip.Dispose()}
}
$archives=@((Join-Path $output ('TwinKartLauncher-'+$Version+'-source.zip')),(Join-Path $output ('TwinKartLauncher-'+$Version+'-windows-x64.zip')))
$sums=Join-Path $output ('TwinKartLauncher-'+$Version+'-SHA256SUMS.txt')
foreach($path in @($archives)+@($sums)){if(Test-Path -LiteralPath $path){throw 'Preserve existing release artifacts; use a new output directory.'}}
WriteArchive $archives[0] $sourceRows
WriteArchive $archives[1] $releaseRows
$lines=@($archives|ForEach-Object{(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+(Split-Path $_ -Leaf)})
[IO.File]::WriteAllText($sums,($lines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
Write-Output ('Source files: '+$sourceRows.Count+'; Windows files: '+$releaseRows.Count)
Write-Output $lines
