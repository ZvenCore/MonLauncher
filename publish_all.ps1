Write-Host "======================================" -ForegroundColor Cyan
Write-Host " SBORKA MON SERVER LAUNCHER & SETUP   " -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan

$Dotnet = "D:\dotnet\dotnet.exe"
$BaseDir = "D:\launcher"

Write-Host "`n[1/2] Sborka launchera MonLauncher.exe..." -ForegroundColor Yellow
& $Dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$BaseDir\dist" "$BaseDir\src\MonLauncher.csproj"
Copy-Item "$BaseDir\dist\MonLauncher.exe" -Destination "$BaseDir\MonLauncher.exe" -Force
Write-Host "[OK] MonLauncher.exe gotov: $BaseDir\MonLauncher.exe" -ForegroundColor Green

Write-Host "`n[2/2] Sborka ustanovshika MonSetup.exe..." -ForegroundColor Yellow
& $Dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$BaseDir\dist_installer" "$BaseDir\installer\MonInstaller.csproj"
Copy-Item "$BaseDir\dist_installer\MonSetup.exe" -Destination "$BaseDir\MonSetup.exe" -Force
Write-Host "[OK] MonSetup.exe gotov: $BaseDir\MonSetup.exe" -ForegroundColor Green

Write-Host "`n======================================" -ForegroundColor Cyan
Write-Host " VSE USPESHNO SOBRANO!               " -ForegroundColor Cyan
Write-Host " Fayl dlya igrokov: $BaseDir\MonSetup.exe" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan

