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
using System.Threading.Tasks;
using System.Drawing.Printing;
using System.Drawing;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Agropecuaria_CM
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class VentasPage5 : Page
    {
        private List<Venta> ventasDelDia;
        public VentasPage5()
        {
            this.InitializeComponent();
            ventasDelDia = new List<Venta>();
            LoadAllSales();

            CalendarDatePicker.DateChanged += CalendarDatePicker_DateChanged;
            SearchBox.TextChanged += SearchBox_TextChanged;
        }

        private async void LoadAllSales()
        {
            var cultureInfo = new System.Globalization.CultureInfo("es-GT");
            //TotalAPagarTextBlock.Text = $"Total a Pagar: {total.ToString("C", cultureInfo)}";
            ventasDelDia.Clear();
            try
            {
                using (var dbConnection = new DatabaseConnection())
                {
                    dbConnection.OpenConnection();
                    string query = @"
                    SELECT codigo_id, ventashechas, productosvendidos, total, monto, cambio, nitcliente, nombrecliente, fecha, hora
                    FROM ventas";

                    using var command = new SqliteCommand(query, dbConnection.GetConnection());
                    {
                        using var reader = command.ExecuteReader();
                        {
                            while (reader.Read())
                            {
                                ventasDelDia.Add(new Venta
                                {
                                    Idventa = reader.GetInt64(0),
                                    VentasHechas = reader.GetString(1),
                                    ProductosVendidos = reader.GetString(2),
                                    Total = reader.GetDecimal(3).ToString("C", cultureInfo),
                                    Monto = reader.GetString(4),
                                    Cambio = reader.GetString(5),
                                    NitCliente = reader.GetString(6),
                                    NombreCliente = reader.GetString(7),
                                    Fecha = reader.GetString(8),
                                    Hora = reader.GetString(9)
                                });
                            }
                        }
                    }
                }
                ventasDelDia = ventasDelDia.OrderByDescending(v => v.Idventa).ToList();

                SalesListView.ItemsSource = ventasDelDia;

                if (ventasDelDia.Count > 0)
                {
                    // Mostrar la lista y el StackPanel de filtros
                    SalesListView.Visibility = Visibility.Visible;
                    NoSalesTextBlock.Visibility = Visibility.Collapsed; // Ocultar el mensaje de no ventas
                }
                else
                {
                    // No hay ventas, ocultar la lista y el StackPanel de filtros
                    SalesListView.Visibility = Visibility.Collapsed;
                    NoSalesTextBlock.Visibility = Visibility.Visible; // Mostrar el mensaje de no ventas
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"Error al cargar las ventas: {ex.Message}");
            }
        }
        private void CalendarDatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        {
            if (args.NewDate.HasValue)
            {
                string selectedDate = args.NewDate.Value.ToString("yyyy-MM-dd");
                var filteredSales = ventasDelDia.Where(v => v.Fecha == selectedDate).ToList();
                UpdateSalesList(filteredSales);
            }
            else
            {
                LoadAllSales();
            }
        }
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchBox.Text.Trim().ToLower();

            var filteredSales = ventasDelDia
                .Where(v => v.NitCliente.ToLower().Contains(filter)
                            || v.ProductosVendidos.ToLower().Contains(filter))
                .ToList();

            UpdateSalesList(filteredSales);
        }
        private void SearchBoxx_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchBoxx.Text.Trim().ToLower();

            var filteredSales = ventasDelDia
                .Where(v => v.Idventa.ToString().Contains(filter))
            .ToList();

            UpdateSalesList(filteredSales);
        }
        private void UpdateSalesList(List<Venta> filteredSales)
        {
            SalesListView.ItemsSource = filteredSales;

            if (filteredSales.Count > 0)
            {
                // Mostrar la lista y el StackPanel de filtros
                SalesListView.Visibility = Visibility.Visible;
                NoSalesTextBlock.Visibility = Visibility.Collapsed; // Ocultar el mensaje de no ventas
            }
            else
            {
                // No hay resultados, ocultar la lista y el StackPanel de filtros
                SalesListView.Visibility = Visibility.Collapsed;
                NoSalesTextBlock.Visibility = Visibility.Visible; // Mostrar el mensaje de no ventas
            }
        }
        private async Task ShowErrorDialog(string message)
        {
            ContentDialog dialog = new()
            {
                Title = "Error",
                Content = message,
                CloseButtonText = "Ok",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        private async Task ShowNiceDialog(string message)
        {
            ContentDialog dialog = new()
            {
                Title = "Éxito",
                Content = message,
                CloseButtonText = "Ok",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        private void ClearFilters_Click(object sender, RoutedEventArgs e)
        {
            CalendarDatePicker.Date = null;
            SearchBox.Text = string.Empty;
            SearchBoxx.Text = string.Empty;
        }

        private async void ImprimirFactura_Click(object sender, RoutedEventArgs e)
        {
            // Recuperar la venta asociada
            var button = sender as Button;
            var ventaSeleccionada = button?.DataContext as Venta;

            if (ventaSeleccionada == null)
            {
                await ShowErrorDialog("No se pudo obtener la venta seleccionada.");
                return;
            }

            // Mostrar el diálogo de confirmación
            ContentDialog confirmDialog = new()
            {
                Title = "Imprimir Comprobante",
                Content = $"¿Imprimir el comprobante de la venta con el número {ventaSeleccionada.Idventa}?",
                PrimaryButtonText = "Imprimir",
                CloseButtonText = "Cancelar",
                XamlRoot = this.XamlRoot
            };

            var result = await confirmDialog.ShowAsync();

            if (result != ContentDialogResult.Primary)
                return;

            // Recuperar los valores desde el almacenamiento
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            string facturaTienda = localSettings.Values["FacturaTienda"] as string ?? "Nombre de la tienda";
            string facturaDireccion = localSettings.Values["FacturaDireccion"] as string ?? "Dirección de la tienda";
            string facturaMensaje = localSettings.Values["FacturaMensaje"] as string ?? "No se aceptan devoluciones";
            string facturaPie = localSettings.Values["FacturaPie"] as string ?? "Gracias por su compra.";

            // Llamar a la función para imprimir la factura
            ImprimirFacturaDirectamente(
                facturaTienda,
                facturaDireccion,
                ventaSeleccionada.NitCliente,
                ventaSeleccionada.NombreCliente,
                ventaSeleccionada.VentasHechas,
                ventaSeleccionada.Total,
                ventaSeleccionada.Monto,
                ventaSeleccionada.Cambio,
                facturaMensaje,
                facturaPie,
                ventaSeleccionada.Fecha,
                ventaSeleccionada.Hora,
                ventaSeleccionada.Idventa
            );
        }

        private static void ImprimirFacturaDirectamente(string facturaTienda, string facturaDireccion, string nitCliente, string nombreCliente, string VentasHechas, string totalVentaStr, string montoGuardar, string cambio, string facturaMensaje, string facturaPie, string fechaVenta, string horaVenta, long idFactura)
        {
            // Crear el documento de impresión
            System.Drawing.Printing.PrintDocument printDocument = new();

            // Asignar el controlador para el evento de impresión
            printDocument.PrintPage += (sender, e) => PrintPageHandler(sender, e, facturaTienda, facturaDireccion, nitCliente, nombreCliente, VentasHechas, totalVentaStr, montoGuardar, cambio, facturaMensaje, facturaPie, fechaVenta, horaVenta, idFactura);

            // Establecer la impresora predeterminada o asignar una específica
            // printDocument.PrinterSettings.PrinterName = "NombreDeTuImpresora"; // Descomenta y cambia si es necesario

            // Enviar el documento a la impresora
            printDocument.Print();
        }
        private static void PrintPageHandler(object sender, PrintPageEventArgs e, string facturaTienda, string facturaDireccion, string nitCliente, string nombreCliente, string VentasHechas, string totalVentaStr, string montoGuardar, string cambio, string facturaMensaje, string facturaPie, string fechaVenta, string horaVenta, long idFactura)
        {
            // Configurar fuentes
            Font fontRegular = new("Arial", 6);
            Font fontBold = new("Arial", 6, System.Drawing.FontStyle.Bold);
            Font fontSmall = new("Arial", 6);

            int margen = 20;
            int maxAncho = e.MarginBounds.Width - (2 * margen);

            // Posición inicial
            float yPos = margen;
            int leftMargin = e.MarginBounds.Left + margen;

            // Centrar el texto
            StringFormat formatCenter = new() { Alignment = StringAlignment.Center };

            // Separador
            string separador = new('-', 48);

            // Título de la factura (centrado y negrita)
            e.Graphics.DrawString($"COMPROBANTE DE COMPRAS", fontBold, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontBold.GetHeight(e.Graphics)), formatCenter);
            yPos += fontBold.GetHeight(e.Graphics);

            e.Graphics.DrawString($"Comprobante No.{idFactura}", fontBold, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontBold.GetHeight(e.Graphics)), formatCenter);
            yPos += fontBold.GetHeight(e.Graphics);

            // Separador
            e.Graphics.DrawString(separador, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // Espacio entre secciones
            yPos += fontRegular.GetHeight(e.Graphics);

            // DATOS DEL EMISOR (en negrita)
            e.Graphics.DrawString("DATOS DEL EMISOR", fontBold, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontBold.GetHeight(e.Graphics)), formatCenter);
            yPos += fontBold.GetHeight(e.Graphics);

            // Separador
            e.Graphics.DrawString(separador, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // Datos de la tienda (centrados)
            e.Graphics.DrawString(facturaTienda, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);
            e.Graphics.DrawString(facturaDireccion, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // Espacio entre secciones
            yPos += fontRegular.GetHeight(e.Graphics);

            // DATOS DEL RECEPTOR (en negrita)
            e.Graphics.DrawString("DATOS DEL RECEPTOR", fontBold, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontBold.GetHeight(e.Graphics)), formatCenter);
            yPos += fontBold.GetHeight(e.Graphics);

            // Separador
            e.Graphics.DrawString(separador, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // Datos del cliente (centrados)
            e.Graphics.DrawString($"NIT: {nitCliente}", fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);
            e.Graphics.DrawString($"Nombre: {nombreCliente}", fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // Espacio entre secciones
            yPos += fontRegular.GetHeight(e.Graphics);

            // DESCRIPCIÓN DEL DOCUMENTO (en negrita)
            e.Graphics.DrawString("DESCRIPCIÓN DEL COMPROBANTE", fontBold, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontBold.GetHeight(e.Graphics)), formatCenter);
            yPos += fontBold.GetHeight(e.Graphics);

            // Separador
            e.Graphics.DrawString(separador, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // Ancho de cada columna en función del ancho disponible
            int anchoTotalColumnas = 230;  // Ajuste manual de un ancho total estimado de las columnas
            int espacioLibre = (maxAncho - anchoTotalColumnas) / 2;  // Espacio para centrar las columnas

            // Ajuste de posiciones para centrar las columnas
            int columnaMarca = leftMargin + espacioLibre + 20;
            int columnaDescripcion = leftMargin + espacioLibre + 80;
            int columnaCantidad = leftMargin + espacioLibre + 140;
            int columnaPrecio = leftMargin + espacioLibre + 190;

            // Dibujar encabezados de columna
            e.Graphics.DrawString("Producto", fontBold, Brushes.Black, new PointF(columnaMarca, yPos));
            e.Graphics.DrawString("Marca", fontBold, Brushes.Black, new PointF(columnaDescripcion, yPos));
            e.Graphics.DrawString("Cant.", fontBold, Brushes.Black, new PointF(columnaCantidad, yPos));
            e.Graphics.DrawString("P. Unidad", fontBold, Brushes.Black, new PointF(columnaPrecio, yPos));
            yPos += fontBold.GetHeight(e.Graphics);

            var cultureInfo = new System.Globalization.CultureInfo("es-GT");

            var productos = VentasHechas.Split(';'); // Los productos están separados por punto y coma.
            foreach (var producto in productos)
            {
                var detalles = producto.Split('~'); // Cada detalle del producto está separado por coma.
                if (detalles.Length == 4)
                {
                    string tipo = detalles[0];
                    string marca = detalles[1];
                    string cantidad = detalles[2];
                    string precio = detalles[3];

                    // Limitar la marca a 7 caracteres y añadir "**" si es más larga
                    tipo = tipo.Length > 9 ? string.Concat(tipo.AsSpan(0, 9), "**") : tipo;

                    // Limitar la descripción a 10 caracteres y añadir "**" si es más larga
                    marca = marca.Length > 10 ? string.Concat(marca.AsSpan(0, 10), "**") : marca;

                    decimal precioDecimal = decimal.Parse(precio, cultureInfo);
                    string precioFormateado = precioDecimal.ToString("C", cultureInfo);

                    // Dibujar cada columna en su posición
                    e.Graphics.DrawString(tipo, fontSmall, Brushes.Black, new PointF(columnaMarca, yPos));
                    e.Graphics.DrawString(marca, fontSmall, Brushes.Black, new PointF(columnaDescripcion, yPos));
                    e.Graphics.DrawString(cantidad, fontSmall, Brushes.Black, new PointF(columnaCantidad, yPos));
                    e.Graphics.DrawString(precioFormateado, fontSmall, Brushes.Black, new PointF(columnaPrecio, yPos));
                    yPos += fontSmall.GetHeight(e.Graphics);
                }
            }

            // Separador
            e.Graphics.DrawString(separador, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // Total (centrado y negrita)
            e.Graphics.DrawString($"Total: {totalVentaStr}", fontBold, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontBold.GetHeight(e.Graphics)), formatCenter);
            yPos += fontBold.GetHeight(e.Graphics);

            // Monto y cambio (alineados a la izquierda)
            e.Graphics.DrawString($"Monto: {montoGuardar}", fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);
            e.Graphics.DrawString($"Cambio: {cambio}", fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // Separador
            e.Graphics.DrawString(separador, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // FECHA Y HORA DE EMISIÓN (centrado y negrita)
            e.Graphics.DrawString("FECHA Y HORA DE LA COMPRA", fontBold, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontBold.GetHeight(e.Graphics)), formatCenter);
            yPos += fontBold.GetHeight(e.Graphics);

            e.Graphics.DrawString($"Fecha: {fechaVenta}", fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);
            e.Graphics.DrawString($"Hora: {horaVenta}", fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // Separador
            e.Graphics.DrawString(separador, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            e.Graphics.DrawString(facturaMensaje, fontBold, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontBold.GetHeight(e.Graphics)), formatCenter);
            yPos += fontBold.GetHeight(e.Graphics);

            // Separador
            e.Graphics.DrawString(separador, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
            yPos += fontRegular.GetHeight(e.Graphics);

            // Pie de la factura (centrado)
            e.Graphics.DrawString(facturaPie, fontRegular, Brushes.Black, new RectangleF(leftMargin, yPos, maxAncho, fontRegular.GetHeight(e.Graphics)), formatCenter);
        }

        private async void EliminarVenta_Click(object sender, RoutedEventArgs e)
        {
            // Obtener la venta seleccionada a través del botón presionado
            var button = sender as Button;
            var ventaSeleccionada = button?.DataContext as Venta;

            if (ventaSeleccionada == null)
            {
                await ShowErrorDialog("No se pudo obtener la venta seleccionada.");
                return;
            }

            // Mostrar el diálogo de confirmación
            ContentDialog confirmDialog = new()
            {
                Title = "Eliminar Venta",
                Content = $"¿Está seguro de que desea eliminar la venta con el número {ventaSeleccionada.Idventa}?",
                PrimaryButtonText = "Eliminar",
                CloseButtonText = "Cancelar",
                XamlRoot = this.XamlRoot
            };

            var result = await confirmDialog.ShowAsync();

            if (result != ContentDialogResult.Primary) return;

            // Eliminar la venta de la base de datos
            try
            {
                using var dbConnection = new DatabaseConnection();
                {
                    dbConnection.OpenConnection();

                    string deleteQuery = @"DELETE FROM ventas WHERE codigo_id = @idVenta";

                    using (var command = new SqliteCommand(deleteQuery, dbConnection.GetConnection()))
                    {
                        command.Parameters.AddWithValue("@idVenta", ventaSeleccionada.Idventa);
                        int rowsAffected = command.ExecuteNonQuery();

                        if (rowsAffected > 0)
                        {
                            // Actualizar la lista visualmente
                            ventasDelDia.Remove(ventaSeleccionada);
                            SalesListView.ItemsSource = null; // Forzar refresco
                            SalesListView.ItemsSource = ventasDelDia;

                            await ShowNiceDialog($"La venta con el número {ventaSeleccionada.Idventa} ha sido eliminada."); /////////
                        }
                        else
                        {
                            await ShowErrorDialog("No se pudo eliminar la venta.");
                        }
                    }

                    dbConnection.CloseConnection();
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"Error al eliminar la venta: {ex.Message}");
            }
        }
    }
    public class Venta
    {
        public long Idventa { get; set; }
        public string VentasHechas { get; set; }
        public string ProductosVendidos { get; set; }
        public string Total { get; set; }
        public string Monto { get; set; }
        public string Cambio { get; set; }
        public string NitCliente { get; set; }
        public string NombreCliente { get; set; }
        public string Fecha { get; set; }
        public string Hora { get; set; }
    }
}
