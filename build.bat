@echo off
echo ==========================================
echo  Building MediaController.exe (Single-File)
echo ==========================================

dotnet publish MediaController.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

echo.
echo Build finished! Check the 'publish' folder for MediaController.exe.
pause
