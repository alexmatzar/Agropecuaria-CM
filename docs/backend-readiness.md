# Estado del backend MVP

Fecha: 2026-11-01. Alcance: primer POS de una tienda, una autoridad de inventario, operación offline y réplica central. No representa un SaaS mundial terminado.

Se verifican 17 de 24 criterios funcionales/técnicos de esta lista: 70.8%, aproximadamente 71%. Es un conteo sin ponderación; no mide horas ni esfuerzo restante. Los pendientes incluyen tareas grandes y requisitos de producción.

| # | Criterio | Estado |
|---|---|---|
| 1 | Capas y contratos sin acceso a DB desde WPF | Verificado |
| 2 | Unidades comerciales y dinero exactos | Verificado |
| 3 | Catálogo, precios y entrada inicial | Verificado |
| 4 | Códigos de barras y metadatos | Verificado |
| 5 | Venta, líneas y recibo persistidos | Verificado |
| 6 | Movimientos y descuento transaccional | Verificado |
| 7 | Operaciones idempotentes y reintentos | Verificado |
| 8 | SQLite cifrada, sobre y recuperación de claves | Verificado |
| 9 | Integración Firebase y permisos centrales | Verificado con emulador |
| 10 | Sesión offline limitada y revocación al reconectar | Verificado |
| 11 | Alta de cuentas recuperable y auditoría central | Implementado y probado |
| 12 | Aislamiento entre negocios y sucursales | Verificado |
| 13 | Dispositivo revocable y un escritor | Verificado |
| 14 | Outbox/inbox y reanudación automática | Verificado |
| 15 | Descarga y reconstrucción de datos enviados | Verificado |
| 16 | Conversión de legado con informe y conciliación | Verificado con datos ficticios |
| 17 | Snapshot consistente y cifrado sin clonar sesiones | Verificado |
| 18 | Imágenes con almacenamiento y sincronización | Pendiente |
| 19 | Servicio Windows con TLS, ACL e identidad validados | Pendiente |
| 20 | Firebase real, API y PostgreSQL desplegados y protegidos | Pendiente |
| 21 | Conciliación/activación de backups con ventas no enviadas | Pendiente |
| 22 | Compras, reabastecimiento y ajustes autorizados | Pendiente |
| 23 | Pagos, caja, descuentos y devoluciones | Pendiente |
| 24 | Instalación/actualización y aceptación en Windows limpio | Pendiente |

El porcentaje corresponde al backend MVP de esta lista. La UI, impresión, hardware, requisitos fiscales y la plataforma comercial SaaS requieren su propia aceptación. No se trasladan automáticamente a este porcentaje.

## Alta de cuentas y auditoría

La migración SQL 003 añade cloud_user_provisioning y cloud_account_audit. Cada alta online requiere OperationId. Se reserva el intento y un UID determinista por negocio/sucursal/operación antes de llamar a Firebase.

Si Firebase creó la identidad y se pierde la respuesta, el reintento reconoce ese UID y completa la membresía una sola vez. Revalida al administrador antes del commit; una revocación durante la llamada impide conceder acceso. Una identidad ya existente con otro UID requiere un flujo de invitación pendiente: no se adopta ni se cambia su contraseña.

El diario guarda identificadores, metadatos y resultado, no contraseña ni hash derivado de ella. Una contraseña temporal se utiliza solo para el primer alta real del proveedor; cambiarla en un reintento no restablece una identidad existente. Para eso se usa el endpoint explícito de reset.

Altas, cambios de rol/estado y operaciones de contraseña quedan auditados. GET /api/admin/cloud-audit consulta esa auditoría online con permisos centrales de Admin; GET /api/admin/audit conserva la auditoría local.

## Pendientes externos

Una sesión GitHub en el llavero no concede acceso a Google. Firebase real requiere autorización Google, proyecto nuevo y configuración de proveedor. La API y PostgreSQL necesitan alojamiento, región, gestión de secretos, respaldos y presupuesto aprobados antes de activar recursos facturables.

No habilitar Production para ocultar pendientes de TLS/ACL o despliegue. Las pruebas de DPAPI CurrentUser en un runner Windows verifican esa cuenta, no una identidad de servicio dedicada en la laptop del cliente.
