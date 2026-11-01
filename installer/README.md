# Instalación Windows — pendiente

El instalador futuro distribuirá WPF y LocalApi autocontenidos, instalará el servicio bajo una identidad de privilegios mínimos, configurará ACL de datos y claves, certificados y recuperación del proceso, y conservará los datos durante actualizaciones.

No se ha creado un MSI ni instalado ningún servicio en esta etapa. SQLite ya está cifrada y hay operadores/roles, pero el host sigue bloqueando Production por falta de HTTPS local e identidad/ACL/instalador validados en Windows. `AddWindowsService` ya está integrado. DPAPI debe aprovisionarse bajo la identidad que ejecutará el servicio; copiar un sobre CurrentUser de otra cuenta no funciona.

La aceptación requiere Windows limpio sin Visual Studio/SDK, usuario estándar, primera apertura, venta, impresión, reinicio, actualización, respaldo y restauración. WPF necesita revisión visual en Windows; compilarlo en macOS no prueba esos comportamientos.
