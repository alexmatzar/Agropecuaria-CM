# Seguridad — backend local e identidad central

La identidad actual está descrita en [ADR 003](cloud-identity.md): Firebase Auth, autoridad central de permisos y sesión offline limitada. Cifrado y recuperación de datos permanecen en [ADR 002](backend-security.md); su elección anterior de contraseñas locales queda sustituida. Se conserva el bloqueo de producción en ambos hosts hasta validar despliegue, PostgreSQL, Windows y TLS.

## Barreras actuales de desarrollo

El servicio escucha solo IPv4 loopback. Las rutas de negocio requieren sesión de operador y política de autorización; `/health` y login son las únicas rutas públicas. No se habilita CORS ni acceso LAN. Las rutas SQL usan parámetros. Las cantidades y los precios se validan antes de escribir; se comprueba stock en la misma operación de actualización. Los secretos y datos locales están excluidos de Git.

HTTP loopback es exclusivamente de desarrollo. Las sesiones identifican operadores, caducan y son revocables; una baja remota durante desconexión solo se observa al revalidar o vencer el plazo offline (máximo 24 horas desde la última validación). Login y gestión de usuarios requieren conexión. El host rechaza Production mientras faltan TLS, identidad/ACL e instalador validados. WindowsServices permite alojar el proceso, pero no constituye un despliegue protegido ya terminado.

La replicación comercial usa una credencial diferente por dispositivo, guardada dentro de SQLite cifrada y como hash en PostgreSQL. El servidor verifica alcance y estado en cada operación; una sola autoridad escribe por sucursal. Registro/reemplazo requieren Admin online y quedan auditados. Un error de revocación conocido suspende escrituras; un equipo offline conserva pendientes hasta reconectar y no puede recibir una revocación instantánea. Ver [ADR 004](database-sync.md).

## Diseño de producción

1. Identidad de servicio dedicada con permisos mínimos; nunca depender de que el cajero tenga privilegios administrativos.
2. SQLite y sus archivos auxiliares en una carpeta de datos con ACL para la identidad del servicio. WPF no recibe ruta ni clave de la base.
3. Proveedor SQLite cifrado mantenido, elegido por soporte, licencias y bibliotecas nativas Windows/macOS ARM64. No se comprarán licencias automáticamente. SQLite normal no cifra archivos; el parámetro Password no añade cifrado por sí solo. [Microsoft](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/encryption).
4. Clave aleatoria por instalación y protección del secreto bajo la identidad apropiada del servicio. La API de protección del sistema no elimina la necesidad de un mecanismo de recuperación al cambiar de equipo.
5. Comunicación local autenticada y cifrada; aprovisionar certificado y confianza durante instalación. No usar certificados de desarrollo ni desactivar validación TLS.
6. Usuarios/cajeros con autorización en cada caso de uso; política explícita para sesión sin internet, bloqueo, caducidad y revocación diferida.
7. Credencial independiente para dispositivo/nube, asociada a negocio y sucursal por el servidor. No confiar en TenantId recibido en JSON para autorizar acceso.
8. Nube con autorización por recurso, pruebas de aislamiento entre negocios, rotación de credenciales, límites de carga y logs sin datos sensibles.
9. Instalador y actualizaciones firmados; migración de datos coordinada entre versiones de servicio, UI y contratos.
10. Respaldos cifrados fuera del equipo, retención, gestión de claves y restauraciones ensayadas.

El código compilado no es secreto inexpugnable. Un administrador de la máquina puede inspeccionar procesos y acceder a material descifrado durante uso; ofuscación o un servicio no eliminan ese límite. El diseño protege frente a usuarios sin privilegios, pérdida de archivos/disco, accesos remotos y fallos operativos según controles implementados y probados.

## Decisiones que faltan antes de usar datos reales

Validar en Windows DPAPI bajo la identidad real del servicio y ACL; aprovisionar TLS; revisar recuperación operativa y custodia de material; escoger proveedor de nube/región y objetivos de recuperación; validar hardware. La variante elegida es SQLite3 Multiple Ciphers con su distribución mantenida por el proyecto. Ninguna credencial del Firebase antiguo se importa a esta base.

Las distribuciones oficiales .NET de SQLCipher tienen condiciones comerciales. Los bundles públicos `e_sqlcipher` son de terceros y no cuentan con soporte de Zetetic; se evita integrarlos automáticamente como si fueran la opción oficial. [Proveedor SQLCipher](https://www.zetetic.net/sqlcipher/sqlcipher-for-dotnet/).
