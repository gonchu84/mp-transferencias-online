@echo off
REM Cierra el proceso por nombre de ventana o por puerto
echo Intentando cerrar el proceso de MP Transferencias...

REM Opcion 1: matar por proceso dotnet
taskkill /IM dotnet.exe /F >nul 2>&1

REM Opcion 2: matar exe si existe (si tu publish genero un exe propio)
for %%F in ("%~dp0app\*.exe") do (
  taskkill /IM "%%~nxF" /F >nul 2>&1
)

echo Listo.
