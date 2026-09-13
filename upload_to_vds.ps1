param(
    [string]$VdsHost = "site.moncraft.space",
    [string]$VdsUser = "root",
    [string]$RemoteDir = "/monl"
)

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " EXPORT & UPLOAD TO VDS               " -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan

$BaseDir = "D:\launcher"
$ExePath = "$BaseDir\dist\MonLauncher.exe"

if (-not (Test-Path $ExePath)) {
    Write-Host "[*] Sborka MonLauncher.exe..." -ForegroundColor Yellow
    & "D:\dotnet\dotnet.exe" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$BaseDir\dist" "$BaseDir\src\MonLauncher.csproj"
}

Write-Host "[*] Zagruzka new.exe na VDS ($VdsUser@$VdsHost:$RemoteDir)..." -ForegroundColor Yellow
scp "$ExePath" "$($VdsUser)@$($VdsHost):$RemoteDir/new.exe"

if ($LASTEXITCODE -eq 0) {
    Write-Host "
[OK] Fayl new.exe uspeshno zagruzhen!" -ForegroundColor Green
    Write-Host "[*] Zapusk obrabotki na VDS..." -ForegroundColor Yellow
    ssh "$($VdsUser)@$($VdsHost)" "python3 $RemoteDir/update.py"
} else {
    Write-Host "[ERROR] Ne udalos zagruzit fayl po SCP" -ForegroundColor Red
}