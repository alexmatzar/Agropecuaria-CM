using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Data.SQLite;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using System.Text.Json;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Agropecuaria_CM
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class NotifiPage8 : Page
    {
        private List<NotificacionProducto> listaNotificaciones;
        public NotifiPage8()
        {
            this.InitializeComponent();
            LoadNotifications();
        }

        private void LoadNotifications()
        {
            listaNotificaciones = ObtenerNotificaciones();

            if (listaNotificaciones != null && listaNotificaciones.Count > 0)
            {
                NotificacionesListView.ItemsSource = listaNotificaciones;
                NotificacionesListView.Visibility = Visibility.Visible;
                NoNotificationsText.Visibility = Visibility.Collapsed;
            }
            else
            {
                MostrarTextoSinNotificaciones();
            }
        }

        private List<NotificacionProducto> ObtenerNotificaciones()
        {
            var notificaciones = new List<NotificacionProducto>();

            using (var dbConnection = new DatabaseConnection())
            {
                dbConnection.OpenConnection();
                string query = "SELECT codigo_barras, marca, descripcion, stock, fecha_hora FROM notificaciones";

                using (var command = new SqliteCommand(query, dbConnection.GetConnection()))
                {
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            notificaciones.Add(new NotificacionProducto
                            {
                                CodigoBarras = reader["codigo_barras"].ToString(),
                                Marca = reader["marca"].ToString(),
                                Descripcion = reader["descripcion"].ToString(),
                                Stock = Convert.ToInt32(reader["stock"]),
                                FechaHora = reader["fecha_hora"].ToString()
                            });
                        }
                    }
                }

                dbConnection.CloseConnection();
            }

            return notificaciones;
        }

        private void MostrarTextoSinNotificaciones()
        {
            NotificacionesListView.ItemsSource = null;
            NotificacionesListView.Visibility = Visibility.Collapsed;
            NoNotificationsText.Visibility = Visibility.Visible;
        }
    }
    public class NotificacionProducto
    {
        public string CodigoBarras { get; set; }
        public string Marca { get; set; }
        public string Descripcion { get; set; }
        public int Stock { get; set; }
        public string FechaHora { get; set; }
    }
}
