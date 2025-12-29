@echo off
echo ===============================
echo  INSTALANDO MP TRANSFERENCIAS
echo ===============================
echo.

REM 1) Verificar .NET
dotnet --info >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET Runtime NO esta instalado.
    echo Instalar .NET 8 Runtime y volver a ejecutar.
    pause
    exit /b
)

REM 2) Crear acceso directo en escritorio
set DESKTOP=%USERPROFILE%\Desktop
set TARGET=C:\MPTransferenciasLocal_Instalable\INICIAR_MP_1CLICK.bat
set LINK=%DESKTOP%\MP Transferencias.lnk

powershell -command ^
"$s=(New-Object -COM WScript.Shell).CreateShortcut('%LINK%'); ^
$s.TargetPath='%TARGET%'; ^
$s.WorkingDirectory='C:\MPTransferenciasLocal_Instalable'; ^
$s.IconLocation='%SystemRoot%\system32\shell32.dll,167'; ^
$s.Save()"

REM 3) Abrir puerto firewall 5286
netsh advfirewall firewall add rule name="MP Transferencias 5286" dir=in action=allow protocol=TCP localport=5286 >nul 2>&1

REM 4) Mensaje final
echo.
echo ======================================
echo  INSTALACION COMPLETADA
echo ======================================
echo.
echo Ya podes usar el acceso:
echo   "MP Transferencias"
echo en el Escritorio.
echo.
pause
