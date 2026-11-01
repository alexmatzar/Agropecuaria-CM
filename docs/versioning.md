# Versiones y repositorio

Un monorepositorio coordina Domain, Application, Infrastructure, Contracts, LocalApi, Cloud.Api, Client, WPF y pruebas. Separar procesos y dependencias no exige separar repositorios.

- v1.0.0 conserva el commit WinUI original. main permanece como referencia v1.
- v2 contiene el código nuevo en la raíz y mantiene la v1 como antecesor de Git.
- 2.0.0-alpha.1 es una versión preliminar de backend y vista previa WPF. No es una entrega de producción.

La rama v2 reemplaza el árbol antiguo en esa rama; no copia bin, obj, bases, certificados ni caches. El código de v1 sigue disponible en main y en su etiqueta. No se reescribe el historial ni se usa force-push.

VERSION y Directory.Build.props identifican la versión. GitHub Actions verifica backend y Windows y construye un ZIP autocontenido de vista previa. Las publicaciones preliminares adjuntan ese paquete y una captura WPF real.

La instalación del negocio se actualizará mediante paquetes firmados y migraciones coordinadas en una etapa posterior. Un push no modifica automáticamente una instalación del cliente.
