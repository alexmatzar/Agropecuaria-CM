using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Windows.Storage;

namespace Agropecuaria_CM
{
    internal partial class DatabaseConnection : IDisposable
    {
        private SqliteConnection connection;
        private string dbPath;

        public DatabaseConnection()
        {
            // CORRECCIÓN CRÍTICA:
            // Usar ApplicationData.Current.LocalFolder garantiza que funcione en modo Debug y en MSIX.
            dbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "AgroComercialMendoza.db");

            string connectionString = $"Data Source={dbPath}";
            connection = new SqliteConnection(connectionString);

            // No inicializamos la BD en el constructor para evitar bloqueos al instanciar.
            // Es mejor llamar a InitializeDatabase explícitamente al inicio de la App o dejar que OpenConnection lo maneje.
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            // Sqlite crea el archivo automáticamente al abrir si no existe, 
            // pero necesitamos crear las tablas.
            bool isNewDb = !File.Exists(dbPath);

            // Aunque SQLite crea el archivo, ejecutamos el script si detectamos que no hay tablas o es nueva.
            // Para optimizar, asumimos que si el archivo existe, la estructura base está. 
            // (Idealmente usarías migraciones, pero para este nivel está bien así).

            try
            {
                connection.Open();

                // PRAGMA journal_mode = WAL mejora mucho la concurrencia y velocidad en SQLite
                using (var pragmaCmd = new SqliteCommand("PRAGMA journal_mode = WAL;", connection))
                {
                    pragmaCmd.ExecuteNonQuery();
                }

                string tableQuery = @"
                    CREATE TABLE IF NOT EXISTS productos (
                        codigo_barras TEXT PRIMARY KEY,
                        tipo TEXT NOT NULL,
                        marca TEXT NOT NULL,
                        descripcion TEXT NOT NULL,
                        unidad_comprada TEXT NOT NULL,
                        stock NUMERIC,
                        preciocompra NUMERIC NOT NULL,
                        foto BLOB
                    );
                    CREATE TABLE IF NOT EXISTS unidades_venta (
                        codigo_barras TEXT NOT NULL,
                        unidad_venta TEXT NOT NULL,
                        precio_venta NUMERIC NOT NULL,
                        FOREIGN KEY (codigo_barras) REFERENCES productos(codigo_barras) ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS notificaciones (
                        codigo_barras TEXT NOT NULL,
                        marca TEXT,
                        descripcion TEXT,
                        stock NUMERIC,
                        fecha_hora TEXT,
                        PRIMARY KEY (codigo_barras),
                        FOREIGN KEY (codigo_barras) REFERENCES productos (codigo_barras) ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS clientes (
                        nit TEXT PRIMARY KEY,
                        nombre TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS ventas (
                        codigo_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ventashechas TEXT NOT NULL,
                        productosvendidos TEXT NOT NULL,
                        total NUMERIC NOT NULL,
                        totalganancias NUMERIC NOT NULL,
                        monto NUMERIC NOT NULL,
                        cambio NUMERIC NOT NULL,
                        nitcliente TEXT NOT NULL,
                        nombrecliente TEXT NOT NULL,
                        fecha DATE NOT NULL,
                        hora TIME NOT NULL
                    );
                ";

                using (var command = new SqliteCommand(tableQuery, connection))
                {
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                // Aquí deberías loguear el error si pasa algo creando la BD
                System.Diagnostics.Debug.WriteLine($"Error DB init: {ex.Message}");
                throw; // Relanzar para que la app sepa que falló la BD
            }
            finally
            {
                // Cerramos conexión tras inicializar. Se abrirá bajo demanda.
                connection.Close();
            }
        }

        public SqliteConnection GetConnection()
        {
            // Siempre devolver la conexión. El que la pide es responsable de abrirla si está cerrada,
            // aunque podemos facilitar eso.
            return connection;
        }

        // Método helper para abrir de forma segura
        public void OpenConnection()
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                connection.Open();
            }
        }

        public void CloseConnection()
        {
            if (connection.State != System.Data.ConnectionState.Closed)
            {
                connection.Close();
            }
        }

        public void Dispose()
        {
            connection?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
