# Probar v2 en Windows

La v2 conserva backend y WPF en un repositorio. La v1 WinUI queda en main y en la etiqueta v1.0.0; la rama v2 contiene la nueva arquitectura. La versión actual es 2.0.0-alpha.1, de desarrollo.

## Ver la ventana sin instalar herramientas de programación

Desde la publicación preliminar de GitHub, descargar el ZIP windows-x64-preview, extraer TODA la carpeta y abrir Abrir-vista-previa.cmd.

El paquete es autocontenido: incluye .NET para Windows x64. No requiere Visual Studio, VS Code, SDK, Firebase, PostgreSQL o certificados. No instala servicios ni toca bases del cliente.

La ventana es WPF real, con una tabla de inventario y estado. La vista previa usa datos ficticios y no realiza solicitudes HTTP. No es la UI WinUI anterior completa: aún faltan navegación, login, caja y administración visual. El indicador visible distingue este modo de una aplicación conectada.

El paquete no está firmado todavía; es una prueba de desarrollo. No desactivar antivirus ni políticas del equipo para ejecutarlo. Si Windows impide abrirlo, conservar el mensaje exacto para revisar el bloqueo.

## Ejecutar desde código

Para desarrollar/probar código se necesitan Git y el SDK .NET 10 fijado por global.json. El IDE es opcional. En PowerShell:

~~~powershell
git clone --branch v2 --single-branch --depth 1 https://github.com/alexmatzar/Agropecuaria-CM.git Agropecuaria-CM-v2
cd Agropecuaria-CM-v2
$env:NUGET_HTTP_CACHE_PATH = "$PWD\.cache\nuget-http"
dotnet run --project src/Agropecuaria.Desktop.Wpf -- --preview
~~~

Elegir el SDK x64 para una PC Intel/AMD x64. Preferir Windows 11 actualizado. En Windows 10, confirmar edición/versión con winver y comprobar la tabla de sistemas admitidos por .NET 10: no todas sus ediciones conservan soporte. Para ARM64 habría que producir otro paquete. No instalar Visual Studio 2016 para este proyecto: el repositorio usa .NET 10.

[SDK oficial](https://dotnet.microsoft.com/download/dotnet/10.0) y [sistemas admitidos](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).

## Pruebas del backend en esa PC

~~~powershell
dotnet test tests/Agropecuaria.Tests --filter "Category!=FirebaseEmulator&Category!=PostgreSQL&Category!=CloudIntegration"
~~~

Estas pruebas crean instalaciones temporales con datos ficticios. Comprueban SQLite cifrada, operaciones, HTTP y recuperación. La prueba DPAPI se ejecuta solo en Windows; no demuestra todavía el funcionamiento bajo la identidad futura del servicio.

PostgreSQL/Firebase y la integración de sincronización se prueban en el trabajo Linux de GitHub Actions. El trabajo Windows comprueba backend, WPF, DPAPI, publicación autocontenida y render real de la ventana. El PNG producido es una captura del árbol visual de WPF en Windows, no una maqueta dibujada en Mac.

## Aplicación conectada y servicio final

La WPF conectada actual requiere una sesión local vigente y el servicio en loopback; la pantalla de login está pendiente. Clonar el repositorio no copia usuarios, claves ni datos: .data y .cache están excluidos.

El diseño final es WPF → HTTPS local → Agropecuaria.LocalApi como servicio Windows → SQLite cifrada. El servicio se ocupa de las ventas y sincronización; Cloud.Api/PostgreSQL se ejecutarán fuera de la PC y las credenciales Admin SDK nunca se entregarán a WPF.

AddWindowsService ya existe en el host, pero la instalación protegida no está terminada. Antes del piloto faltan identidad dedicada, DPAPI bajo esa identidad, ACL, certificado TLS, instalador/actualizaciones, logs y pruebas en Windows limpio. Production permanece bloqueado. No registrar a mano el ejecutable de desarrollo como LocalSystem para simular una instalación terminada.

Después de completar backend/despliegue se migrarán las pantallas y se probarán lector, impresión, ventas, cortes de red, reinicio, actualización y recuperación con una copia del negocio.
