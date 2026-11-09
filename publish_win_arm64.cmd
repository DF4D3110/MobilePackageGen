@echo off

set "PUBLISH_ROOT=D:\Test.MPG"
if not "%MOBILEPACKAGEGEN_PUBLISH_ROOT%"=="" set "PUBLISH_ROOT=%MOBILEPACKAGEGEN_PUBLISH_ROOT%"
if exist "%PUBLISH_ROOT%" rmdir /s /q "%PUBLISH_ROOT%"
mkdir "%PUBLISH_ROOT%"

msbuild /m /t:restore,cbstoreg:publish,secwim2wim:publish,mobilepackagegen:publish,wpbuildinfo:publish,ezcab:publish,cbscatverifier:publish,cbsmanverifier:publish /p:Platform=arm64 /p:RuntimeIdentifier=win-arm64 /p:PublishDir="%PUBLISH_ROOT%" /p:PublishTrimmed=false /p:Configuration=Release /p:IncludeNativeLibrariesForSelfExtract=true MobilePackageGen.sln
dotnet publish src\MobilePackageGen.LegacyGUI\MobilePackageGen.LegacyGUI.csproj -c Release -r win-arm64 --self-contained true -p:Platform=ARM64 -o "%PUBLISH_ROOT%"

mkdir "%PUBLISH_ROOT%\symbols"
move "%PUBLISH_ROOT%\*.pdb" "%PUBLISH_ROOT%\symbols\"