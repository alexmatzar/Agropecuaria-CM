# Entregas siguientes

## 1. Base técnica — esta entrega

Backend y frontend separados en una solución, funcionamiento local transaccional, cantidades exactas, outbox y demostración automática de recuperación de conexión con receptor local. WPF inicial solo consulta datos y estado. No se migraron todavía las pantallas WinUI existentes.

## 2. Seguridad local y migración de datos

Seguridad backend implementada: SQLite cifrada con clave aleatoria, sobre recuperable, respaldos cifrados, sesiones revocables y auditoría local. Identidad central Firebase y roles integrados con emulador, PostgreSQL real en pruebas y dispositivos revocables. Conversor de datos antiguos implementado con informe, convención explícita y copia nueva; ninguna base del cliente se adoptó. Pendientes: conciliar copia del negocio real, WPF login/usuarios y validación Windows de DPAPI, permisos y despliegue.

Login y cambio de cuenta requieren internet; una sesión validada permite continuidad offline hasta 24 horas. La autoridad central conserva cuentas/permisos. El conversor rechaza ambigüedades y concilia unidades, precios y archivo histórico antes de publicar. Revisar la convención contra datos/recuento físico del negocio y definir valoración de costos; no reconstruir ventas textuales automáticamente.

## 3. Operación de caja completa

Carrito WPF, entrada manual/lector sin asumir longitud única, medios de pago, caja, compras/reabastecimiento, descuentos autorizados, cancelaciones/devoluciones mediante movimientos compensatorios, lotes/vencimientos, recibos persistidos e impresión reintentable independiente de la venta. Acordar impuestos y requisitos fiscales antes de implementar facturación.

## 4. Nube real y sincronización bidireccional

Implementado y probado: migraciones SQL verificadas, outbox hacia Cloud.Api/PostgreSQL, dispositivos revocables, inbox y aplicación de eventos en una transacción del servidor, deduplicación y descarga por cursor hacia otra SQLite cifrada. Pruebas de aislamiento, cortes/reanudación automática, duplicados, revocaciones y reemplazo de escritor. Ver ADR 004.

Implementados después: diario recuperable de aprovisionamiento Firebase y auditoría central de cambios de cuentas. Siguiente entrega: invitaciones, imágenes con almacenamiento de objetos, conciliación de backups con operaciones nunca enviadas, snapshots/cortes de restauración para historiales grandes y operación administrada de backups/PITR. Mantener una sola autoridad de stock por sucursal hasta diseñar explícitamente múltiples cajas conectadas por LAN.

Crear un proyecto Firebase NUEVO según la preferencia confirmada por el usuario, después de preparar la integración local. Configurar proveedor email/password y despliegue seguro, elegir región, backups y costes antes de contratar recursos. El prototipo local no equivale a un despliegue cloud ni a respaldo fuera del equipo.

## 5. Entrega Windows y piloto

Servicio con identidad dedicada/ACL, HTTPS local aprovisionado, instalador firmado que incluya dependencias, actualización que preserve datos y esquema, recuperación de arranque y logs persistentes. Pruebas en Windows limpio, como usuario estándar, sin SDK/Visual Studio, con impresora y lector reales. Ejecutar pilotos con copia de datos y plan de recuperación.

Un solo código fuente y versiones coordinadas; configuración/licencias diferentes por cliente. No mantener una rama divergente para la familia y otra para SaaS.
