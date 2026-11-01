# Agropecuaria CM

**v2.0.0-alpha.1 · desarrollo.** Backend y WPF permanecen en un monorepositorio. La v1 WinUI se conserva en main/v1.0.0 y el código nuevo está en la rama v2. Ver [versiones](docs/versioning.md), [prueba en Windows](docs/windows-testing.md) y [avance del backend](docs/backend-readiness.md).

POS para Windows con servicio REST local, SQLite cifrada y operación sin conexión después de iniciar sesión online. Un repositorio contiene backend, contratos, cliente y WPF. Desarrollo y pruebas de backend en macOS ARM64 con .NET 10 y VS Code.

Esta entrega incluye **PostgreSQL real en pruebas, sincronización comercial, restauración de datos, conversor de la SQLite antigua, aprovisionamiento recuperable de cuentas y auditoría central**. Identidad central con Firebase Auth, permisos por negocio/sucursal y sesiones locales se verifican con el emulador oficial. La conexión y el despliegue reales se documentan por separado; el código sigue bloqueando Production. La caja WPF y el despliegue Windows están pendientes.

El WinUI original se conserva como referencia en el commit `2ab38641af73c7bc6ef792cb0dda98b7dbdecb95` de [alexmatzar/Agropecuaria-CM](https://github.com/alexmatzar/Agropecuaria-CM). No se importaron sus datos ni secretos.

## Comportamiento actual

- Login y cambio de cuenta requieren Firebase y la autoridad de permisos disponibles. No hay fallback a contraseñas locales.
- Roles Admin, Cashier y Viewer aplicados en las APIs. Alta, cambio de rol, desactivación y contraseñas requieren conexión.
- Una sesión validada permite continuar vendiendo sin conexión hasta 24 horas desde la última validación online. El servicio intenta renovarla cada minuto; una caída no extiende el plazo.
- SQLite local cifrada con SQLite3 Multiple Ciphers/ChaCha20-Poly1305, clave por instalación, sobre recuperable y respaldo cifrado. Los tokens de renovación quedan dentro de la base cifrada.
- Alta de productos, precios por unidad, inventario exacto y ventas. Venta, movimientos, resultado idempotente y outbox se confirman juntos.
- Envío automático a Cloud.Api/PostgreSQL con credenciales revocables por dispositivo, recepción idempotente y proyecciones transaccionales de catálogo, ventas y movimientos.
- Descarga por cursor hacia otra SQLite cifrada; reconstruye datos sin descontar dos veces ni crear eventos de regreso. Una instalación nueva se mantiene bloqueada hasta completar la descarga.
- Una autoridad de inventario por sucursal. Réplicas de consulta y reemplazo explícito del equipo escritor por Admin.
- Conversor de legado con informe previo, selección explícita de unidades, conciliación y publicación de una copia cifrada nueva. Historial textual y fotos originales se conservan separados de las ventas operativas nuevas.
- Cliente HTTP y base WPF de consulta, sin acceso a SQLite ni su clave.
- WPF con modo explícito --preview y datos ficticios para revisar la ventana sin configurar el backend. GitHub Actions produce un paquete Windows x64 autocontenido y una captura WPF real.

Cloud.Api reúne cuentas y sincronización en módulos del mismo despliegue. La sincronización comercial ya funciona entre procesos locales usando PostgreSQL; aún no hay servidor público. Las imágenes se conservan localmente: su sincronización mediante almacenamiento de objetos está pendiente. Datos que nunca se enviaron o respaldaron fuera del equipo no se recuperan por iniciar sesión.

## Preparar y verificar en Mac

```bash
cd /Users/alex/Desktop/Agropecuaria-CM
bash scripts/check.sh
bash scripts/cloud-dev.sh install
```

El primer comando compila backend, ejecuta pruebas sin Firebase y compila WPF para Windows. El segundo instala la herramienta Firebase fijada en `tools/firebase/package-lock.json`, dentro del proyecto; no requiere iniciar sesión en Google ni instalar un servicio de sistema. Requiere Node compatible con Firebase CLI; la documentación del entorno está en [ADR 003](docs/cloud-identity.md).

Para la demostración completa, mantener el emulador en una terminal:

```bash
bash scripts/cloud-dev.sh firebase
```

En otra terminal ejecutar las pruebas que usan procesos HTTP y el emulador real:

```bash
bash scripts/cloud-dev.sh test
```

Las pruebas crean usuarios y datos ficticios aislados, no piden secretos reales y no despliegan recursos.

Para verificar PostgreSQL, abrir Docker Desktop y ejecutar:

```bash
bash scripts/postgres-dev.sh docker-start
bash scripts/postgres-dev.sh test
```

Con el emulador Firebase también activo, comprobar la integración HTTP completa:

```bash
bash scripts/cloud-dev.sh test-postgres
```

PostgreSQL 18.6 se ejecuta solo en loopback, con contraseña aleatoria y volumen persistente del proyecto. La imagen está fijada por digest. `bash scripts/postgres-dev.sh stop` detiene este contenedor sin borrar sus datos ni cerrar Docker. El compilador nativo también está preparado en `.cache`, pero su arranque fue bloqueado por la memoria compartida del entorno; la verificación efectiva se hizo con Docker.

## Usar una instalación de demostración

Con el emulador activo:

1. Ejecutar `bash scripts/setup-security.sh`: pide una frase maestra oculta y un archivo NUEVO de recuperación. Guardar el material de recuperación fuera del equipo. No crea usuarios locales.
2. Ejecutar una sola vez `bash scripts/cloud-dev.sh bootstrap-owner`: pide correo y contraseña de PRUEBA y vincula el propietario a la sucursal de esta instalación. No hay alta de primer administrador por HTTP.
3. Mantener `bash scripts/cloud-dev.sh authority` en otra terminal.
4. Ejecutar `bash scripts/cloud-dev.sh api`: solicita la frase maestra y abre la API local en `127.0.0.1:5187`.

Para una instalación **nueva** que deba recuperar la misma cuenta y sucursal, preparar sus claves/configuración y ejecutar `bash scripts/cloud-dev.sh enroll` antes del primer arranque de la API. Requiere la autoridad disponible; descubre solo sucursales autorizadas y asigna un identificador nuevo al dispositivo. Si hay varias, seleccionar mediante `AGROPECUARIA_ENROLL_STORE_ID`. Rechaza modificar una instalación con `local.secure.db` existente. No descarga datos comerciales.

La secuencia anterior usa SQLite para probar exclusivamente identidad. Para cuentas **y datos comerciales** usar `bootstrap-owner-postgres`, `authority-postgres` y `api-cloud` en lugar de sus variantes anteriores. Tras login, `POST /api/admin/sync/enroll` con `{"canWrite":true,"replaceWriter":false}` registra el equipo escritor. Una réplica nueva usa `canWrite:false`. `replaceWriter:true` revoca al escritor anterior: usar únicamente como recuperación explícita, revisando antes sus ventas pendientes.

Las cuentas del prototipo de autoridad SQLite no se transfieren automáticamente a PostgreSQL. El aprovisionamiento sigue siendo de prueba y no se eligieron credenciales personales.

No se eligieron contraseñas ni se aprovisionó una instancia personal por ti. Las pruebas funcionan sin ejecutar estos asistentes. La configuración se lee al arrancar; reiniciar tras cambios.

VS Code incluye **Backend local (Mac)**, **Cloud.Api (PostgreSQL local + Firebase emulado)** y perfiles antiguos de identidad SQLite/receptor de prueba. El perfil Backend local activa sincronización Cloud; requiere la autoridad PostgreSQL. El emulador debe estar activo para login. Las cachés y los datos de desarrollo quedan excluidos de Git.

## API local

`POST /api/auth/login` recibe `username` (correo) y `password`. Devuelve un token opaco local y el usuario con su rol. Las operaciones siguientes usan `Authorization: Bearer <accessToken>`. La UI nunca recibe credenciales administrativas Firebase, refresh tokens ni claves de SQLite.

| Método | Ruta | Uso |
|---|---|---|
| GET | `/health` | Disponibilidad local, sin datos de negocio |
| POST | `/api/auth/login` | Login online |
| GET | `/api/auth/me` | Operador actual |
| POST | `/api/auth/logout` | Cerrar sesión, también offline |
| POST | `/api/auth/revalidate` | Renovar permisos online |
| POST | `/api/auth/password` | Cambiar contraseña online y revocar sesión |
| GET / POST | `/api/admin/users` | Listar / crear cuentas centrales |
| PATCH | `/api/admin/users/{id}` | Rol y estado activo |
| POST | `/api/admin/users/{id}/reset-password` | Contraseña temporal online |
| GET | `/api/admin/audit` | Auditoría local |
| GET | `/api/admin/cloud-audit` | Auditoría central, requiere conexión y Admin |
| GET / POST | `/api/products` | Inventario / entrada inicial |
| POST | `/api/sales` | Venta transaccional |
| GET | `/api/sync/status` | Estado del envío de eventos |
| GET | `/api/sales` | Últimos 100 recibos estructurados |
| POST | `/api/admin/sync/enroll` | Registrar el dispositivo con aprobación online de Admin |
| POST | `/api/admin/sync/run` | Ejecutar un ciclo y consultar su estado |

Una cuenta con contraseña temporal solo accede a su perfil, cambio de contraseña y cierre de sesión hasta cambiarla. Ocultar controles WPF no concede permisos: el servidor valida cada petición.

El alta de cuentas requiere OperationId estable. La migración SQL 003 registra el intento antes de llamar a Firebase, lo reanuda si se pierde la respuesta y confirma membresía/auditoría juntas. No almacena contraseñas ni permite adoptar otra identidad por correo. Ver [contrato y límites](docs/backend-readiness.md).

Durante una restauración no se muestran existencias parciales: productos y ventas devuelven 409 hasta completar el cursor. La administración y el estado siguen disponibles. El trabajador opera con una credencial de dispositivo guardada dentro de SQLite cifrada; no depende de mantener abierta la sesión de un administrador.

## Conversión de la base antigua

El esquema SQLite nuevo está en versión 4, con 17 tablas de aplicación. Conserva un motor local sencillo, pero separa datos, movimientos, idempotencia, seguridad y replicación. Ver [comparación de esquemas y procedimiento de conversión](docs/database-sync.md).

El conversor exige una copia consolidada y una convención de stock confirmada. No se seleccionó una automáticamente ni se reemplazó la base original o la del cliente. `legacy-plan` genera un informe; `legacy-import` comprueba ese informe y publica una copia cifrada nueva con manifiesto y sobre de recuperación. Las ventas antiguas textuales se conservan como archivo, sin inventar líneas ni volver a descontar inventario.

## Unidades y venta

Cinco quintales de Guatemala equivalen a 500 lb. Tras vender 18 lb quedan **482 lb**, almacenadas exactamente como 482,000,000 unidades base (millonésimas de libra). No se usa punto flotante para existencias ni dinero. Dinero en centavos GTQ; precios y redondeo controlados por el servidor.

```json
{
  "operationId": "91acb475-b2d7-42a6-b26f-c011fef26302",
  "productId": "29405494-15e2-4769-9a06-532b0f365f9c",
  "name": "Concentrado para ganado",
  "dimension": "Weight",
  "openingUnit": "Quintal",
  "openingQuantity": 5,
  "prices": [{ "unit": "Pound", "unitPriceMinor": 500 }]
}
```

Venta de 18 lb a Q5/lb:

```json
{
  "operationId": "98812869-a2a2-44e1-8c55-cac54637ea58",
  "lines": [{ "productId": "29405494-15e2-4769-9a06-532b0f365f9c", "unit": "Pound", "quantity": 18 }]
}
```

Total Q90.00. Usar identificadores nuevos para operaciones nuevas y conservar el mismo `operationId` y contenido en reintentos. Otro contenido con el mismo identificador devuelve 409. Unidades: Piece, Hundred, Ounce, Pound, Arroba (25 lb), Quintal (100 lb).

## Límites y siguiente entrega

LocalApi y Cloud.Api mantienen el bloqueo de Production. HTTP loopback, Firebase emulado y PostgreSQL del contenedor son de desarrollo. El diario de alta y la auditoría de cambios de cuentas ya están implementados. Faltan Firebase real, despliegue administrado, TLS/ACL/identidad de servicio, instalador, invitaciones, observabilidad/retención, copias/PITR y ensayos de recuperación operativa.

La sincronización actual cubre altas de catálogo, ventas nuevas y archivo histórico convertido; no implementa aún edición remota de precios, compras, devoluciones, lotes o resolución de múltiples escritores offline. Un backup de un equipo enlazado elimina su credencial y queda sin autoridad de escritura; no se puede clonar para abrir una segunda caja. La reactivación de backups con ventas no enviadas requiere un procedimiento de conciliación todavía pendiente.

WPF compila desde Mac, pero todavía no incorpora login ni caja completa. Ejecución visual, DPAPI, instalación y hardware deben probarse en Windows. La referencia AddWindowsService no equivale a un servicio ya instalado ni genera un MSI.

La base antigua `.data/local.db` permanece intacta. No se migraron compras, descuentos, caja, impuestos, devoluciones o impresión.

Ver [arquitectura](docs/architecture.md), [persistencia y sincronización — ADR 004](docs/database-sync.md), [identidad central — ADR 003](docs/cloud-identity.md), [cifrado y recuperación — ADR 002](docs/backend-security.md), [seguridad](docs/security.md) y [siguientes entregas](docs/roadmap.md).
