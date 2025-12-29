MP Transferencias - Versión Local por Sucursal (Instalable)
==========================================================

Qué es esto
-----------
Este paquete deja tu sistema de Transferencias MP corriendo LOCAL en cada sucursal.
Cada sucursal tiene SU Access Token de Mercado Pago y SU base SQLite.

Requisitos en la PC de la sucursal
----------------------------------
1) Windows 10/11
2) .NET 8 Runtime (ASP.NET Core Hosting Bundle) instalado
   - Si tu app es .NET 7/6, cambiá la versión del runtime acorde.
3) Acceso a internet SOLO para consultar la API de Mercado Pago (no hay ngrok/webhooks).

Estructura de carpetas
----------------------
- app/         -> acá va el "publish" de tu proyecto (dll/exe + wwwroot + deps)
- config/      -> configuración por sucursal (Access Token, nombre sucursal, puerto)
- logs/        -> logs (si tu app los escribe)

PASO A PASO (rápido)
--------------------
1) PUBLICAR (en tu PC dev o en cada sucursal)
   Desde la carpeta del proyecto:
     dotnet publish -c Release -o "<ESTA_CARPETA>\app" /p:PublishSingleFile=false

   *Si querés que NO requiera runtime: publish self-contained (pesa más):
     dotnet publish -c Release -r win-x64 -o "<ESTA_CARPETA>\app" --self-contained true

2) CONFIGURAR SUCURSAL
   Editá: config\appsettings.Sucursal.json
   - Sucursal: nombre para identificar
   - MercadoPago:AccessToken: token de ESA cuenta
   - Hosting:Urls: dejalo en http://localhost:5286 (o cambiá puerto)

3) INSTALAR (1 click)
   Click derecho sobre install.ps1 -> "Run with PowerShell"
   Esto:
   - crea accesos directos en el Escritorio
   - agrega regla de firewall para el puerto (solo local)
   - opcional: crea inicio automático (si lo habilitás dentro del script)

4) USO DIARIO (empleados)
   - Doble click "MP Transferencias - Iniciar"
   - Doble click "MP Transferencias - Abrir"

Archivos principales
--------------------
- run.bat              -> inicia la app
- stop.bat             -> cierra la app
- install.ps1          -> crea accesos directos + firewall
- Abrir MP.url         -> atajo para abrir en el navegador
- config\appsettings.Sucursal.json -> configuración por sucursal

Notas
-----
- La base SQLite queda en app\pagosmp.db (o donde tu app la cree).
- Si querés que se vea desde otras PCs dentro de la sucursal, cambiá Hosting:Urls a:
    http://0.0.0.0:5286
  y en firewall permití red privada. (Yo lo dejé en localhost por seguridad, como pediste “local”.)
