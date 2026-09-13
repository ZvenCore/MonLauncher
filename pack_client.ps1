param(
    [switch]$IncludeAssets = $false
)

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " UPAKOWKA BAZOVOGO KLIENTA .MINECRAFT DLYA VDS    " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

$McDir = "D:\launcher\.minecraft"
$OutputFile = "D:\launcher\client.zip"
$TempDir = "D:\launcher\temp_client_pack"

if (!(Test-Path $McDir)) {
    Write-Error "Papka .minecraft ne naydena v $McDir"
    exit 1
}

if (Test-Path $TempDir) {
    Remove-Item $TempDir -Recurse -Force
}
New-Item -ItemType Directory -Path $TempDir | Out-Null

Write-Host "`n[1/4] Kopirovanie komponentov..." -ForegroundColor Yellow

$itemsToCopy = @("versions", "libraries", "config", "defaultconfigs", "options.txt", "servers.dat")

if ($IncludeAssets) {
    Write-Host "  -> Vklyuchaem assets..." -ForegroundColor Gray
    $itemsToCopy += "assets"
} else {
    Write-Host "  -> Propuskaem assets (zagruzhayutsya cherez Mojang CDN)" -ForegroundColor Gray
}

foreach ($item in $itemsToCopy) {
    $source = Join-Path $McDir $item
    if (Test-Path $source) {
        Write-Host "  + Kopirovanie: $item" -ForegroundColor Green
        Copy-Item -Path $source -Destination (Join-Path $TempDir $item) -Recurse -Force
    } else {
        Write-Host "  - Propusk: $item" -ForegroundColor DarkGray
    }
}

Write-Host "`n[2/4] Sozdanie arhiva $OutputFile..." -ForegroundColor Yellow
if (Test-Path $OutputFile) {
    Remove-Item $OutputFile -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($TempDir, $OutputFile, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "`n[3/4] Ochistka..." -ForegroundColor Yellow
Remove-Item $TempDir -Recurse -Force

$zipItem = Get-Item $OutputFile
$sizeMb = [math]::Round($zipItem.Length / 1MB, 2)

Write-Host "`n==================================================" -ForegroundColor Cyan
Write-Host " client.zip USPESHNO SOZDAN: $sizeMb MB" -ForegroundColor Green
Write-Host " Raspolozhenie: $OutputFile" -ForegroundColor Yellow
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "Zagruzite etot fayl na VDS v papku /monl/client.zip:" -ForegroundColor White
Write-Host "scp $OutputFile root@site.moncraft.space:/monl/client.zip" -ForegroundColor Gray
