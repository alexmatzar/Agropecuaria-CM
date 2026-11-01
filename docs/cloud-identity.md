# ADR 003 — identidad central y sesión local sin conexión

Fecha: 2026-10-31. Aceptada e implementada como integración local con emulador. Sustituye la elección de contraseñas y operadores locales de ADR 002; conserva su diseño de cifrado y recuperación de datos.

Actualización 2026-11-01: [ADR 004](database-sync.md) incorpora PostgreSQL probado, credenciales de dispositivo y sincronización comercial. Continúa sin proyecto Firebase real o nube desplegada.

## Responsabilidades

| Componente | Responsabilidad | Estado |
|---|---|---|
| Firebase Authentication | Cuenta, contraseña, ID token y renovación | REST y Admin SDK integrados; probado con emulador oficial |
| Cloud.Api | Verificar identidad y consultar autorización vigente por negocio/sucursal | HTTP local de desarrollo, producción bloqueada |
| CloudDirectory | Membresías, rol, activo, cambio obligatorio y versión | Probado con PostgreSQL 18.6 real y SQLite de desarrollo |
| LocalApi / futuro servicio Windows | Sesión opaca local, casos de uso, persistencia y outbox | Implementado y probado en Mac |
| SQLite cifrada | Negocio local y caché de permisos/sesión, sin contraseñas Firebase | Esquema v4, probado |
| WPF | Presentación mediante contratos REST | Base de consulta; login pendiente |
| PostgreSQL / almacenamiento de objetos | Datos comerciales centrales / imágenes | Sync comercial probado localmente; imágenes y despliegue pendientes |

Firebase soporta custom claims, pero guardamos la autoridad de roles en nuestra base central: el mismo UID puede pertenecer a distintos negocios con permisos diferentes. Los permisos no se deciden desde un rol enviado por WPF ni se almacenan solo en una máquina.

## Login, desconexión y revocación

1. WPF envía correo y contraseña al servicio local por su canal protegido (HTTP loopback solo en desarrollo).
2. LocalApi llama a Firebase REST. Obtiene ID token y refresh token; nunca usa Admin SDK en el equipo POS.
3. La autoridad verifica el ID token con Admin SDK, revocación y estado de la cuenta. Consulta membresía activa para negocio/sucursal y emite una concesión de hasta 24 horas.
4. LocalApi valida alcance, UID, versión y fechas; cachea el perfil y renovación en SQLite cifrada. Entrega a WPF un token local aleatorio de 256 bits. Solo su hash se persiste.
5. La cuenta anterior de ese terminal se cierra **después** del login nuevo exitoso. Un fallo de conexión no destruye la sesión anterior.
6. Las peticiones de negocio consultan sesión, caducidad y rol local en servidor. No hacen viajes a la nube para cada venta.
7. Un trabajador cada minuto renueva la identidad y consulta nuevamente la autoridad. Solo una aprobación online extiende la concesión. Una respuesta de identidad revocada o membresía denegada cierra la sesión. Una caída de proveedor/red conserva únicamente el plazo ya otorgado.

Login, cambio de cuenta, administración y cambio de contraseña requieren red. Cerrar sesión funciona offline. Una sesión vencida exige nuevo login online; no existe contraseña local alternativa. La continuidad después de reiniciar la UI requerirá guardar su token en un almacén protegido Windows, aún pendiente.

**Límite de revocación:** un equipo desconectado no puede recibir una baja ni cambio de rol inmediatamente. Puede operar con permisos anteriores hasta que caduque su concesión, como máximo 24 horas desde la última validación. Con conexión se intenta comprobar cada minuto, sin promesa de plazo exacto ante fallos. El valor actual no es una política de operación indefinida.

No se implementó todavía bloqueo por inactividad humana ni detección resistente de manipulación del reloj. La seguridad local depende también del usuario estándar, permisos Windows y protección del sistema.

## Administración

Roles: Admin (operación y gestión), Cashier (consulta y venta) y Viewer (consulta). La autoridad vuelve a leer permisos administrativos; la caché local no basta para crear cuentas o subir roles.

- Primer propietario: CLI del servidor fuera de HTTP. No hay endpoint público de bootstrap.
- Nuevos usuarios: Firebase Auth más membresía central, con contraseña temporal y cambio obligatorio.
- Cambiar rol o desactivar incrementa la versión de membresía. Se impide quitar el último Admin activo.
- Cambio de contraseña propia: reautenticación Firebase reciente, cambio en proveedor y revocación.
- Reset por Admin: no devuelve contraseñas anteriores. Se bloquea si el UID ya pertenece a varias sucursales/negocios para evitar que un administrador se apropie de una identidad compartida.
- Una cuenta Firebase ya existente no se sobrescribe al darla de alta. El flujo de invitación para vincularla a otra sucursal queda pendiente.

El alta Firebase y la escritura PostgreSQL son dos sistemas distintos. La migración 003 añade un diario persistente con OperationId y UID por negocio/sucursal/operación: reanuda una identidad creada aunque se pierda la respuesta y vuelve a comprobar al Admin antes de conceder membresía. El diario no guarda contraseñas. La auditoría central registra altas, cambios de rol/estado y operaciones de contraseña. El flujo de invitación para cuentas existentes sigue pendiente. Ver [estado del backend](backend-readiness.md).

El modo antiguo de cuentas locales queda reservado a pruebas: exige `Identity__Mode=LocalDevelopment` y `Identity__AllowLegacyLocalForTests=true`. Sus CLI de administración requieren además `AGROPECUARIA_ALLOW_LEGACY_LOCAL_TESTS=true`. No se utiliza como fallback ni es la política de la aplicación nueva.

## Equipo perdido, restauración y enrolamiento

Usuarios y roles deben vivir en Firebase y la base de autoridad **desplegados y respaldados**. El emulador actual guarda solamente datos ficticios en esta Mac y no protege frente a perderla. El script exporta el emulador al detenerlo normalmente; no equivale a nube.

En una instalación nueva, `Security.Cli enroll` autentica online y descubre sucursales autorizadas. Asigna TenantId/StoreId y un nuevo DeviceId antes de crear la base local; no toca una base existente ni persiste la contraseña usada. Después del login, `/api/admin/sync/enroll` registra el dispositivo comercial con aprobación online de Admin y secreto aleatorio independiente. La clave de dispositivo se guarda cifrada localmente; PostgreSQL conserva solo su hash. El servidor permite revocación y una sola autoridad de escritura por sucursal.

Las pruebas de identidad recuperan usuario/rol. Las pruebas PostgreSQL + Firebase recuperan además catálogo, precios y ventas estructuradas después de registrar la réplica, por páginas con cursor persistente. El login por sí solo no activa esta descarga. Imágenes y datos nunca enviados siguen dependiendo del backup y su recuperación. No sincronizar archivos SQLite activos mediante carpetas compartidas.

## Emulador reproducible

Requisitos: .NET 10 y Node soportado por Firebase CLI. Herramienta fijada `firebase-tools 15.34.2`; la dependencia superstatic declara Node 20/22/24. En esta Mac se verificó con Node 26.5 aunque npm avisa sobre ese rango; para un entorno repetible se recomienda Node 24 compatible con ambas. No se cambió Node del sistema.

```bash
bash scripts/cloud-dev.sh install
bash scripts/cloud-dev.sh firebase
```

Mantener el segundo proceso activo y, desde otra terminal:

```bash
bash scripts/cloud-dev.sh test
```

Proyecto exclusivo `demo-agropecuaria-cm`, Auth en `127.0.0.1:9099`, UI desactivada. No se llama a un proyecto real ni se requiere login Firebase CLI. Las pruebas crean instancias temporales con correos aleatorios, prueban HTTP real, cortan la autoridad y verifican ventas offline, recuperación de cuenta, cambio de contraseña, roles y aislamiento.

La validación del SDK en modo emulador admite tokens sin firma únicamente para pruebas. Cloud.Api restringe la variable de emulador a loopback/proyecto demo y mantiene bloqueo de producción. No copiar esa configuración a un despliegue real.

## Preparar el nuevo proyecto real

No se creó un proyecto Google/Firebase, cuenta de facturación ni recurso de pago. Próximo aprovisionamiento: proyecto nuevo, proveedor email/password y configuración de identidad, permisos mínimos y recuperación del propietario. No pegar claves privadas en chats o Git.

| Dónde | Configuración |
|---|---|
| POS | `Firebase__ApiKey` de cliente y `Identity__AuthorityEndpoint` HTTPS |
| Servidor | `Firebase__ProjectId` del proyecto nuevo y credenciales mediante identidad de servicio/ADC |
| Servidor | `ConnectionStrings__Cloud` PostgreSQL mediante gestor de secretos |
| Pruebas solo | `Firebase__EmulatorEndpoint`, `FIREBASE_AUTH_EMULATOR_HOST`, `Cloud__UseSqliteDevelopment=true` |

El POS no recibe credenciales Admin SDK ni conexión PostgreSQL. La API key de cliente identifica el proyecto, no sustituye autorización. PostgreSQL real ya tiene migraciones SQL verificadas y pruebas de aislamiento/concurrencia; la operación de producción, auditoría completa, límites, TLS, copias/PITR y recuperación de cuenta siguen pendientes.

## Datos comerciales e imágenes

Mantener SQLite como autoridad local de una tienda; subir eventos de negocio por outbox hacia una API cloud con inbox y transacción PostgreSQL. Descargar cambios con versiones y cursor persistente. No aplicar "el último gana" al inventario ni borrar pendientes en fallos.

`tools/Agropecuaria.SyncReceiver.Dev` se conserva como receptor aislado de pruebas y no debe publicarse. Con `Sync:Mode=Cloud`, la outbox usa Cloud.Api/PostgreSQL, credenciales revocables de dispositivo y descarga transaccional por cursor. Los eventos soportados y las limitaciones de restauración están en ADR 004.

Para imágenes usar almacenamiento de objetos y guardar referencias/metadatos en la base. No guardar Base64 en filas ni eventos. Dimensionar costes con GB, tamaño/cantidad de imágenes, tráfico, retención y volumen de operaciones; porcentajes de datos no bastan.

El 1 GiB de Firestore corresponde a cuota gratuita, no a un límite total de Firebase ni de Firebase Auth. No dependemos de Firestore para el inventario. Cloud Storage for Firebase exige Blaze según los cambios de facturación vigentes; no habilitarlo automáticamente.

Fuentes: [Firebase custom claims](https://firebase.google.com/docs/auth/admin/custom-claims), [verificación de tokens](https://firebase.google.com/docs/auth/admin/verify-id-tokens), [emulador Auth](https://firebase.google.com/docs/emulator-suite/connect_auth), [cuotas Firestore](https://firebase.google.com/docs/firestore/quotas), [Cloud Storage y facturación](https://firebase.google.com/docs/storage/faqs-storage-changes-announced-sept-2024).
