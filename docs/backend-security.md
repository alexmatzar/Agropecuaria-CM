# ADR 002 — autenticación local y SQLite cifrada

Fecha: 2026-10-31. Implementado en backend. La interfaz WPF y el despliegue Windows se validarán en otra entrega.

**Decisión de autenticación sustituida por [ADR 003](cloud-identity.md).** Las secciones de contraseñas/cuentas locales se conservan como historia y referencia de pruebas legacy. La aplicación nueva exige Firebase online para login y una autoridad central para roles, con sesión offline limitada. `setup-security.sh` ahora solo prepara claves; no crea un administrador local. El cifrado, respaldo y recuperación de la base descritos aquí continúan vigentes.

## Firebase y el trabajo sin internet

La cuenta de un propietario en la nube y los operadores de una caja son identidades con necesidades diferentes. Los operadores se crean en la instalación local y pueden iniciar sesión, cambiar contraseña y trabajar sin internet. El servicio local es quien decide permisos en cada petición.

Firebase admite roles mediante custom claims y verificación de tokens en servidor, así que el problema del sistema antiguo no era que Firebase impidiera tener roles. Para esta etapa se elige autenticación local porque no necesita red para crear operadores, validar contraseñas o revocar sesiones. No se reutilizan ni importan contraseñas/tokens de Firebase. El proveedor cloud del propietario se decidirá al construir el servidor de nube; una cuenta cloud no concede automáticamente administración de una instalación.

Referencias: [verificación de ID tokens](https://firebase.google.com/docs/auth/admin/verify-id-tokens), [custom claims](https://firebase.google.com/docs/auth/admin/custom-claims).

## Permisos iniciales

| Operación | Admin | Cashier | Viewer |
|---|---|---|---|
| Ver inventario y estado de sincronización | Sí | Sí | Sí |
| Registrar venta | Sí | Sí | No |
| Registrar producto / entrada inicial | Sí | No | No |
| Listar, crear y administrar usuarios | Sí | No | No |
| Restablecer contraseña de otro usuario | Sí | No | No |
| Consultar auditoría | Sí | No | No |
| Cambiar su propia contraseña / cerrar sesión | Sí | Sí | Sí |

Una contraseña temporal habilita únicamente `me`, cambio de contraseña y logout. No permite vender, consultar inventario o administrar usuarios hasta cambiarla. No se oculta un Grid para aplicar seguridad: las políticas ASP.NET Core rechazan la petición con `401` o `403` aunque se invoque directamente por HTTP.

## Credenciales y sesiones

- Nombre de acceso de 3–64 caracteres ASCII (`A-Z`, dígitos, `.`, `_`, `-`), normalizado sin distinguir mayúsculas. El nombre visible admite texto normal.
- Contraseñas de 15–128 caracteres, permitiendo espacios, sin recortarlas. Se usa `PasswordHasher` de ASP.NET Core Identity, PBKDF2-HMAC-SHA512 con 220,000 iteraciones y salt por hash. No se cifran contraseñas para descifrarlas después.
- Sesiones opacas aleatorias de 256 bits; solo se guarda SHA-256 del token. El token se devuelve una vez al iniciar sesión y debe protegerse en el cliente; no se incluye en logs, usuarios ni auditoría.
- Duración absoluta de 8 horas y vencimiento por 30 minutos sin actividad. Consultar una API con la sesión cuenta como actividad. Una interfaz con sondeo continuo evita la expiración por inactividad del protocolo; un futuro bloqueo de caja por inactividad humana debe medirse aparte y no se da por implementado aquí.
- El backend consulta sesión, estado activo, cambio obligatorio y rol actual en cada petición. Un rol en JSON, un header inventado o la navegación de WPF no concede permisos.
- Cambiar contraseña, rol, desactivar o restablecer contraseña revoca sesiones. La revocación se observa en la siguiente autenticación de petición; no cancela operaciones que ya estaban en curso.
- Cinco intentos fallidos bloquean el acceso del usuario durante 15 minutos; persiste al reiniciar. Mensajes de login genéricos y verificación dummy para nombres inexistentes. Login/cambio de contraseña tienen además un máximo global de 20 peticiones por minuto en este servicio local.
- No puede eliminarse/desactivarse/degradarse el último administrador activo. No hay borrado físico de usuarios ni alta inicial pública por HTTP.
- La auditoría conserva accesos, rechazos, altas, cambios, resets y ventas con actor. Las contraseñas, tokens y claves no se sincronizan como eventos de negocio. La auditoría no es un registro externo a prueba de administradores del sistema.

Referencia de coste de hash: [OWASP Password Storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html).

## API REST

Rutas públicas: `GET /health`, que solo informa disponibilidad, y `POST /api/auth/login`.

```json
{ "username": "cajero1", "password": "contraseña introducida por el operador" }
```

Respuesta: `accessToken`, `expiresAtUtc` y `user` con rol/estado. Enviar el token en `Authorization: Bearer ...`. Un token antiguo de instalación no sirve. No copiar credenciales a archivos `.http`, historiales o tickets.

Rutas autenticadas:

| Método / ruta | Cuerpo y comportamiento |
|---|---|
| `GET /api/auth/me` | Usuario actual, sin hash ni token |
| `POST /api/auth/logout` | Revoca el token utilizado, devuelve 204 |
| `POST /api/auth/password` | `currentPassword`, `newPassword`; revoca todas sus sesiones, exige nuevo login |
| `GET /api/admin/users` | Usuarios sin hashes ni tokens |
| `POST /api/admin/users` | `username`, `displayName`, `role`, `temporaryPassword`; cambio obligatorio |
| `PATCH /api/admin/users/{id}` | `role`, `isActive`; revoca sesiones del usuario afectado |
| `POST /api/admin/users/{id}/reset-password` | `temporaryPassword`; cambio obligatorio y revocación |
| `GET /api/admin/audit` | Últimos 200 eventos de auditoría |

Los endpoints de productos, ventas y sincronización mantienen sus contratos y agregan las políticas de la matriz. El ID del cajero de la venta proviene de la sesión, no del cuerpo de venta. Un reset es una contraseña temporal nueva, no la recuperación de una contraseña anterior.

## Cifrado de base y claves

Se usa `Microsoft.Data.Sqlite.Core` con **un solo** bundle: `SQLite3MC.PCLRaw.bundle` 2.6.1, mantenido por el proyecto SQLite3 Multiple Ciphers. Incluye binarios macOS ARM64 y Windows. El esquema elegido es ChaCha20-Poly1305, con clave aleatoria de 32 bytes por instalación y sin derivarla de la contraseña de un cajero. Los parámetros se fijan antes de leer el esquema; no se tolera un proveedor sin cifrado.

La clave aleatoria se entrega al motor como material raw de alta entropía. PBKDF2 se aplica a la frase maestra que protege el sobre; no se usa una contraseña humana directamente como clave raw del motor. La conexión no se comparte entre cuentas de sistema ni se registra su SQL de configuración.

La API solo usa `.data/local.secure.db`. El `.data/local.db` anterior se conserva y no se abre como fallback. Migrar datos previos exige un importador explícito y validado.

La clave de datos se conserva en `.data/security/keyring.json` mediante dos envolturas independientes:

1. Local Mac: AES-256-GCM con clave derivada de una frase maestra de al menos 20 caracteres mediante PBKDF2-HMAC-SHA256, 600,000 iteraciones y salt aleatorio. La frase no se escribe en configuración. Se solicita al arrancar o depurar.
2. Recuperación: otra envoltura AES-256-GCM, protegida por un secreto aleatorio independiente de 256 bits en un archivo externo `.key`/`.recovery-key`. El sobre por sí solo no abre la base; requiere la frase local o el material de recuperación.

Las envolturas incorporan versión, identificador y propósito como datos autenticados. Modificar una etiqueta, utilizar otro secreto o una frase incorrecta debe fallar. En Windows está implementada la opción DPAPI `CurrentUser` en lugar de la frase: debe generarse bajo la identidad real del servicio. No está validada todavía en una laptop Windows.

Cambiar la protección local con recuperación conserva la misma clave de datos; no es una rotación de la clave de la base. La rotación de clave de datos y de material de recuperación queda como operación futura explícita. Nunca perder simultáneamente frase maestra, identidad DPAPI y recuperación: no existe una puerta trasera para abrir datos cifrados.

Referencias: [bundle mantenido](https://github.com/utelle/SQLite3MultipleCiphers-NuGet), [cifrado ChaCha20-Poly1305](https://utelle.github.io/SQLite3MultipleCiphers/docs/ciphers/cipher_chacha20/).

## Aprovisionar esta Mac

```bash
cd /Users/alex/Desktop/Agropecuaria-CM
bash scripts/setup-security.sh
```

El asistente inicializa configuración si falta y pide los secretos ocultos de cifrado. Guardar la recuperación en un medio separado y fuera de Git. No crea usuarios: el propietario se aprovisiona en la autoridad central según ADR 003. Nunca regenerar claves para solucionar un error de autenticación.

Para iniciar: `bash scripts/dev.sh api`. Para depurar desde VS Code, la configuración solicita la frase con entrada oculta. No agregar secretos a `launch.json`. La variable de entorno de desarrollo existe solo durante la ejecución; Windows deberá usar identidad de servicio y DPAPI, sin intervención humana para cada arranque.

## Respaldar y recuperar

Las herramientas no sobrescriben destinos y no imprimen claves. Ingresar secretos interactivamente o mediante un mecanismo de secretos, nunca como argumentos de proceso ni literales en el historial de terminal.

Con `AGROPECUARIA_MASTER_PASSWORD` definida de forma segura:

```bash
bash scripts/security.sh backup /ruta/a/una-carpeta-nueva-de-respaldo
```

El respaldo usa la API de backup de SQLite, incluye `local.secure.db` cifrada, `recovery-envelope.json`, identidad de instalación y manifiesto con SHA-256. Verifica integridad y elimina sesiones del snapshot para impedir que una restauración reactive tokens antiguos. La clave de recuperación no se copia al respaldo. La carpeta se considera completa solo si la herramienta termina correctamente y existe el manifiesto. No copiar el `.db` vivo ignorando WAL.

Actualización ADR 004: cuando la instalación está enlazada a sincronización comercial, el snapshot elimina también su credencial de dispositivo y queda sin escritura. No se clona una autoridad activa mediante backup. La recuperación cloud en una instalación nueva está probada; reactivar un backup que tenga operaciones no enviadas exige una conciliación todavía pendiente. Los respaldos de instalaciones sin enrolamiento comercial conservan su estado local anterior.

Para recuperar el sobre en otra computadora (con una nueva frase en `AGROPECUARIA_MASTER_PASSWORD`):

```bash
bash scripts/security.sh recover /ruta/respaldo/recovery-envelope.json /ruta/medio-separado/recuperacion.key /ruta/nuevo-keyring.json
```

Este comando no instala ni reemplaza datos. Preparar una carpeta nueva de restauración, copiar la base cifrada e identidad, colocar el nuevo sobre como `.data/security/keyring.json` y validar identidad e inventario antes de usar. No sobrescribir la instalación actual. En Windows agregar `--windows-dpapi` y ejecutar bajo la identidad de destino del servicio.

Solo para las pruebas legacy de cuentas locales, la herramienta **exige material de recuperación** y `AGROPECUARIA_ALLOW_LEGACY_LOCAL_TESTS=true`. No restablece contraseñas Firebase. Con `AGROPECUARIA_ADMIN_USERNAME` y `AGROPECUARIA_ADMIN_PASSWORD` suministrados de forma segura:

```bash
bash scripts/security.sh recover-admin /ruta/medio-separado/recuperacion.key
```

Solo restablece un administrador existente, revoca todas las sesiones y obliga a cambiar la contraseña temporal. La acción queda auditada. No existe un endpoint HTTP equivalente.

## Pruebas y pendientes

Las pruebas verifican base y WAL sin texto de negocio legible, clave errónea/ausente, recuperación y alteración del sobre, copia cifrada, sesiones excluidas de restauración, migración de esquema, roles por HTTP, revocación, caducidad/lockout y sincronización tras corte/reinicio.

No declarar listo para cliente: transporte de desarrollo aún usa HTTP loopback; faltan TLS con certificados aprovisionados, ACL de directorios/servicio, validación DPAPI bajo la identidad dedicada del servicio Windows, instalador, actualizaciones y revisión de seguridad independiente. DPAPI CurrentUser ya se verificó en el runner Windows, sin demostrar todavía esa identidad de servicio. No se desplegó nube ni se cambiaron recursos Firebase. La interfaz de login y administración WPF no forma parte de esta entrega.
