using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.Data.Sqlite;
using System.Globalization;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Agropecuaria_CM
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class DashPage7 : Page
    {
        public DashPage7()
        {
            this.InitializeComponent();
            SetDefaultDates();
        }

        private void SetDefaultDates()
        {
            using var db = new DatabaseConnection();
            {
                db.OpenConnection();

                // Obtener la primera fecha de venta
                string queryFirstDate = "SELECT MIN(fecha) FROM ventas";
                DateTime firstSaleDate;
                using (var command = new SqliteCommand(queryFirstDate, db.GetConnection()))
                {
                    var result = command.ExecuteScalar();
                    firstSaleDate = result != DBNull.Value ? DateTime.Parse(result.ToString()) : DateTime.Now;
                }

                // Obtener la última fecha de venta
                string queryLastDate = "SELECT MAX(fecha) FROM ventas";
                DateTime lastSaleDate;
                using (var command = new SqliteCommand(queryLastDate, db.GetConnection()))
                {
                    var result = command.ExecuteScalar();
                    lastSaleDate = result != DBNull.Value ? DateTime.Parse(result.ToString()) : DateTime.Now;
                }

                db.CloseConnection();

                // Establecer las fechas en los DatePickers
                StartDatePicker.Date = new DateTimeOffset(firstSaleDate);
                EndDatePicker.Date = new DateTimeOffset(lastSaleDate);

                LoadSalesBetweenDates(firstSaleDate, lastSaleDate);
            }
        }

        private void OnDateChanged(object sender, DatePickerValueChangedEventArgs e)
        {
            DateTime startDate = StartDatePicker.Date.DateTime;
            DateTime endDate = EndDatePicker.Date.DateTime != DateTime.MinValue ? EndDatePicker.Date.DateTime : DateTime.Now;
            LoadSalesBetweenDates(startDate, endDate);
        }
        private void LoadSalesBetweenDates(DateTime startDate, DateTime endDate)
        {
            List<Sale> salesList = [];
            decimal totalSales = 0;
            decimal totalGanancias = 0;

            var cultureInfo = new System.Globalization.CultureInfo("es-GT");

            using (var db = new DatabaseConnection())
            {
                db.OpenConnection();

                // Consulta SQL para obtener las ventas y ganancias entre las fechas
                string query = "SELECT productosvendidos, total, totalganancias, nombrecliente, fecha FROM ventas WHERE fecha BETWEEN @startDate AND @endDate";
                using (var command = new SqliteCommand(query, db.GetConnection()))
                {
                    command.Parameters.AddWithValue("@startDate", startDate.ToString("dd-MM-yyyy"));
                    command.Parameters.AddWithValue("@endDate", endDate.ToString("dd-MM-yyyy"));

                    using var reader = command.ExecuteReader();
                    {
                        while (reader.Read())
                        {
                            // Llenar los datos de ventas
                            Sale sale = new()
                            {
                                Fecha = reader.GetString(4), // Columna 4 corresponde a la fecha
                                NombreCliente = reader.GetString(3), // Columna 3 corresponde al nombre del cliente
                                ProductosVendidos = reader.GetString(0),
                                Ganancias = reader.GetDecimal(2).ToString("C", cultureInfo),
                                Total = reader.GetDecimal(1).ToString("C", cultureInfo) // Columna 1 es el total de la venta
                            };

                            salesList.Add(sale);
                            totalSales += reader.GetDecimal(1);  // Acumular el total de ventas
                            totalGanancias += reader.GetDecimal(2);  // Acumular el total de ganancias
                        }
                    }
                }

                db.CloseConnection();
            }

            // TotalAPagarTextBlock.Text = $"Total a Pagar: {total.ToString("C", cultureInfo)}";
            SalesListView.ItemsSource = salesList;
            TotalSalesTextBlock.Text = $"{totalSales.ToString("C", cultureInfo)}";
            TotalGananciasTextBlock.Text = $"{totalGanancias.ToString("C", cultureInfo)}";

            if (salesList.Count > 0)
            {
                Grid2.Visibility = Visibility.Visible;
                Grid3.Visibility = Visibility.Visible;
                NoSalesTextBlock.Visibility = Visibility.Collapsed;
            }
            else
            {
                Grid2.Visibility = Visibility.Collapsed;
                Grid3.Visibility = Visibility.Collapsed;
                NoSalesTextBlock.Visibility = Visibility.Visible;
            }
        }

        public class Sale
        {
            public string Fecha { get; set; }
            public string NombreCliente { get; set; }
            public string ProductosVendidos { get; set; }
            public string Ganancias { get; set; }
            public string Total { get; set; }
        }
    }
}
