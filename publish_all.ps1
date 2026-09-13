param(
    [string]$Version = "1.0.0",
    [string]$Changelog = "• Автоматическое обновление лаунчера через VDS`n• Оптимизация синхронизации модов и ресурсов`n• Улучшена стабильность и производительность"
)

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " SBORKA MON SERVER LAUNCHER & SETUP   " -ForegroundColor Cyan
Write-Host " Versiya: v$Version                   " -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan

$Dotnet = "D:\dotnet\dotnet.exe"
$BaseDir = "D:\launcher"

Write-Host "`n[1/3] Sborka launchera MonLauncher.exe..." -ForegroundColor Yellow
& $Dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$BaseDir\dist" "$BaseDir\src\MonLauncher.csproj"
Copy-Item "$BaseDir\dist\MonLauncher.exe" -Destination "$BaseDir\MonLauncher.exe" -Force
Write-Host "[OK] MonLauncher.exe gotov: $BaseDir\MonLauncher.exe" -ForegroundColor Green

Write-Host "`n[2/3] Generatsiya manifesta obnovleniya launcher_version.json..." -ForegroundColor Yellow
$ManifestObj = [ordered]@{
    version = $Version
    url = "https://site.moncraft.space/monl/MonLauncher.exe"
    changelog = $Changelog
    mandatory = $false
}
$ManifestJson = $ManifestObj | ConvertTo-Json -Depth 4
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText("$BaseDir\dist\launcher_version.json", $ManifestJson, $Utf8NoBom)
Copy-Item "$BaseDir\dist\launcher_version.json" -Destination "$BaseDir\launcher_version.json" -Force
Write-Host "[OK] launcher_version.json sozdan: $BaseDir\dist\launcher_version.json" -ForegroundColor Green

Write-Host "`n[3/3] Sborka ustanovshika MonSetup.exe..." -ForegroundColor Yellow
& $Dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$BaseDir\dist_installer" "$BaseDir\installer\MonInstaller.csproj"
Copy-Item "$BaseDir\dist_installer\MonSetup.exe" -Destination "$BaseDir\MonSetup.exe" -Force
Write-Host "[OK] MonSetup.exe gotov: $BaseDir\MonSetup.exe" -ForegroundColor Green

Write-Host "`n======================================" -ForegroundColor Cyan
Write-Host " VSE USPESHNO SOBRANO!               " -ForegroundColor Cyan
Write-Host " Fayl dlya pervoy ustanovki: $BaseDir\MonSetup.exe" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan
Write-Host "`nDlya obnovleniya igrokov zagruzite fayly na VDS:" -ForegroundColor Yellow
Write-Host "scp $BaseDir\dist\MonLauncher.exe root@site.moncraft.space:/monl/MonLauncher.exe" -ForegroundColor Gray
Write-Host "scp $BaseDir\dist\launcher_version.json root@site.moncraft.space:/monl/launcher_version.json" -ForegroundColor Gray


