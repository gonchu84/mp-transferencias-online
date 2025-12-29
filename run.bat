@echo off
setlocal enabledelayedexpansion

REM Carpeta base (donde está este .bat)
set "BASE=%~dp0"
set "APP=%BASE%app"
set "CFG=%BASE%config\appsettings.Sucursal.json"

IF NOT EXIST "%APP%" (
  echo [ERROR] No existe la carpeta app\. Ahi debe ir el publish.
  pause
  exit /b 1
)

IF NOT EXIST "%CFG%" (
  echo [ERROR] No existe %CFG%
  pause
  exit /b 1
)

REM Detectar ejecutable o dll
set "EXE="
for %%F in ("%APP%\*.exe") do (
  set "EXE=%%~fF"
  goto :found
)
:found

if NOT "!EXE!"=="" (
  echo Iniciando EXE: !EXE!
  start "MP Transferencias" /D "%APP%" "!EXE!" --environment Production --urls "" --contentRoot "%APP%" --configuration "%CFG%"
  goto :eof
)

REM Buscar dll
set "DLL="
for %%F in ("%APP%\*.dll") do (
  set "DLL=%%~fF"
  goto :founddll
)
:founddll

if "!DLL!"=="" (
  echo [ERROR] No encontre .exe ni .dll en app\
  echo Asegurate de haber hecho publish hacia app\
  pause
  exit /b 1
)

echo Iniciando DLL: !DLL!
REM Pasamos config por variable de entorno (recomendado)
set ASPNETCORE_URLS=
set DOTNET_ENVIRONMENT=Production
set "ASPNETCORE_URLS="
set "APPSETTINGS_SUCURSAL=%CFG%"

REM Si tu app no lee APPSETTINGS_SUCURSAL, entonces copialo como appsettings.json dentro de app\
REM o adaptamos el código para cargarlo (te lo hago cuando me pases el proyecto completo).

start "MP Transferencias" /D "%APP%" cmd /c dotnet "!DLL!"
goto :eof
