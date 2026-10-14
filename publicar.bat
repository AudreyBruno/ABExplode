@echo off
rem Gera o RESET EXPLODE pronto para levar a outro PC (nao precisa instalar o .NET la).
rem Saida: ..\ResetExplode-dist\ResetExplode.exe + LEIA-ME.txt
cd /d "%~dp0"
echo Gerando ResetExplode.exe ...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "..\ResetExplode-dist" -nologo -v q
if errorlevel 1 (
    echo.
    echo FALHOU. Veja os erros acima.
    pause
    exit /b 1
)
copy /y "LEIA-ME.txt" "..\ResetExplode-dist\LEIA-ME.txt" >nul
echo.
echo Pronto: %~dp0..\ResetExplode-dist
pause
