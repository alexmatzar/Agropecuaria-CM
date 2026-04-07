using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using System.Globalization;
using System.Drawing;
using System.Drawing.Printing;

namespace Agropecuaria_CM
{
    public sealed partial class BarcodePage1 : Page
    {
        private List<ProductoEscaneado> productosEscaneados = [];
        private readonly DatabaseConnection _dbConnection;
        private DispatcherTimer timer;

        private MainWindow _mainWindow;

        public BarcodePage1()
        {
            this.InitializeComponent();
            _dbConnection = new DatabaseConnection();
            this.Loaded += Page_Loaded;
            ProductsListView.ItemsSource = productosEscaneados;
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is MainWindow mainWindow)
            {
                _mainWindow = mainWindow;  // Guardar la referencia de MainWindow
            }
        }
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // Configurando el temporizador
            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += Timer_Tick;
            timer.Start();

            // Inicializa la hora y la fecha cuando la página se carga
            UpdateDateTime();
        }
        private void Timer_Tick(object sender, object e)
        {
            UpdateDateTime(); // Actualiza la hora y la fecha en cada tick del temporizador
        }
        private void UpdateDateTime()
        {
            // Verifica que los elementos existan en el XAML
            if (ClockTexto != null && DateTexto != null)
            {
                // Actualizando la hora
                ClockTexto.Text = DateTime.Now.ToString("HH:mm");

                // Actualizando la fecha en el formato deseado
                var fecha = DateTime.Now.ToString("dddd, dd 'de' MMMM 'del' yyyy");
                // Primera letra en mayúscula
                DateTexto.Text = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(fecha);
            }

            // Verifica que los elementos existan en el XAML
            if (GreetingText != null)
            {
                // Obtiene la hora actual
                var now = DateTime.Now;
                var hour = now.Hour;
                var minute = now.Minute;

                // Determina el saludo basado en la hora
                string greeting;

                if (hour >= 0 && hour < 4)
                {
                    greeting = "¡Feliz Madrugada! Es hora de descansar.";
                }
                else if (hour == 4 && minute >= 1 || hour > 4 && hour < 6)
                {
                    greeting = "¡Feliz Madrugada!";
                }
                else if (hour >= 6 && hour < 12)
                {
                    greeting = "¡Buenos días!";
                }
                else if (hour >= 12 && hour < 19)
                {
                    greeting = "¡Buenas tardes!";
                }
                else if (hour >= 19 && hour < 23)
                {
                    greeting = "¡Buenas noches!";
                }
                else
                {
                    greeting = "¡Buenas noches! Es hora de descansar.";
                }

                // Actualiza el texto del encabezado
                GreetingText.Text = greeting;
            }
        }
        //Validaciones y código.
        private void Validacion1_13num_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            // Permitir solo dígitos numéricos
            sender.Text = new string(sender.Text.Where(char.IsDigit).ToArray());

            // Limitar a 13 caracteres
            if (sender.Text.Length > 13)
            {
                sender.Text = sender.Text[..13];
            }
        }
        private void Validacion2_9num_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            // Permitir solo dígitos numéricos
            sender.Text = new string(sender.Text.Where(char.IsDigit).ToArray());

            // Limitar a 9 caracteres
            if (sender.Text.Length > 9)
            {
                sender.Text = sender.Text[..9];
            }
        }
        private void Validacion3_4num_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            string currentText = sender.Text;
            string newText = new(currentText.Where(c => char.IsDigit(c) || c == '.').ToArray());

            // Asegurar que solo haya un punto
            if (newText.Count(c => c == '.') > 1)
            {
                newText = newText.Remove(newText.LastIndexOf('.'));
            }

            // Limitar a 4 caracteres en total
            if (newText.Length > 6)
            {
                newText = newText[..6];
            }

            sender.Text = newText;

            // Posicionar el cursor al final del texto
            sender.SelectionStart = sender.Text.Length;
        }

        //EMPIEZA EL ESCANEO ++++++++++++++++++++++++++++++++++++++++++++++++*********************************//////////////////////*****************+++
        private bool _isTextChangedHandling;

        private void ProductBarcodeTextBox_TextChanged(Object sender, TextChangedEventArgs e)
        {
            if (_isTextChangedHandling) return;

            try
            {
                _isTextChangedHandling = true; // Marcar como manejando el evento.
                string barcode = ProductBarcodeTextBox.Text;

                if (!string.IsNullOrEmpty(barcode))
                {
                    // Llamar al método para buscar el producto en la base de datos.
                    BuscarProductoPorCodigoDeBarras(barcode);
                }
            }
            finally
            {
                _isTextChangedHandling = false; // Restablecer el estado al terminar.
            }
        }

        private async void BuscarProductoPorCodigoDeBarras(string barcode)
        {
            if (barcode.Length != 13) return;

            try
            {
                using var connection = _dbConnection.GetConnection();
                _dbConnection.OpenConnection();

                var query = @"SELECT codigo_barras, tipo, marca, descripcion, stock, unidad_comprada, preciocompra 
                      FROM productos WHERE codigo_barras = @codigo_barras";

                using var command = new SqliteCommand(query, connection);
                command.Parameters.AddWithValue("@codigo_barras", barcode);

                using var reader = await command.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    // 1. LEER DATOS
                    string codigoBarras = reader.GetString(0);
                    string tipo = reader.GetString(1);
                    string marca = reader.GetString(2);
                    string descripcion = reader.GetString(3);
                    decimal stock = reader.GetDecimal(4);
                    string unidadComprada = reader.GetString(5);
                    decimal precioCompra = reader.GetDecimal(6);

                    // 2. CERRAR READER (Importante antes de hacer otra consulta)
                    reader.Close();

                    if (stock <= 0)
                    {
                        await MostrarMensajeAsync("Producto no disponible", "Stock agotado.");
                        return;
                    }

                    // 3. OBTENER UNIDADES (Reusando la conexión abierta)
                    var unidadesVenta = ObtenerUnidadesVenta(codigoBarras, connection);

                    // 4. LÓGICA DE NEGOCIO (Igual a la tuya)
                    var productoExistente = productosEscaneados.FirstOrDefault(p => p.CodigoBarras == codigoBarras);

                    if (productoExistente != null)
                    {
                        await IncreaseQuantity(productoExistente);
                    }
                    else
                    {
                        if (unidadesVenta.Count == 1)
                        {
                            var unidadVenta = unidadesVenta.First();
                            var producto = new ProductoEscaneado
                            {
                                CodigoBarras = codigoBarras,
                                Tipo = tipo,
                                Marca = marca,
                                Descripcion = descripcion,
                                Stock = CalcularStockLimite(stock, unidadComprada, unidadVenta.unidad),
                                Precio = unidadVenta.precio,
                                PrecioCompra = precioCompra,
                                Cantidad = 1,
                                UnidadComprada = unidadComprada,
                                UnidadSeleccionada = unidadVenta.unidad,
                                CantidadConvertida = CalcularCantidadConvertida(unidadComprada, unidadVenta.unidad, 1)
                            };
                            AñadirOActualizarProductoEnLista(producto);
                        }
                        else
                        {
                            await MostrarDialogoSeleccionUnidad(codigoBarras, tipo, marca, descripcion, stock, unidadComprada, unidadesVenta, precioCompra);
                        }
                    }

                    // Limpiar caja de texto
                    ProductBarcodeTextBox.Text = string.Empty;
                }
                else
                {
                    await MostrarMensajeAsync("No encontrado", "Producto no registrado.");
                    ProductBarcodeTextBox.Text = string.Empty;
                }
            }
            catch (Exception ex)
            {
                await MostrarMensajeAsync("Error de Base de Datos", ex.Message);
            }
        }

        private Decimal CalcularCantidadConvertida(string unidadComprada, string unidadVenta, int cantidad)
        {
            return unidadComprada switch
            {
                "Unidad(es)" => unidadVenta switch
                {
                    "Unidad" => cantidad,
                    _ => throw new Exception($"Conversión no válida: UnidadComprada {unidadComprada} a UnidadVenta {unidadVenta}."),
                },
                "Libra(s)" => unidadVenta switch
                {
                    "Onza" => cantidad * 0.0625m,
                    "Libra" => cantidad,
                    _ => throw new Exception($"Conversión no válida: UnidadComprada {unidadComprada} a UnidadVenta {unidadVenta}."),
                },
                "Ciento(s)" => unidadVenta switch
                {
                    "Unidad" => cantidad * 0.01m,
                    "Ciento" => cantidad,
                    _ => throw new Exception($"Conversión no válida: UnidadComprada {unidadComprada} a UnidadVenta {unidadVenta}."),
                },
                "Quintal(es)" => unidadVenta switch
                {
                    "Libra" => cantidad * 0.01m,
                    "Arroba" => cantidad * 0.25m,
                    "Quintal" => cantidad,
                    _ => throw new Exception($"Conversión no válida: UnidadComprada {unidadComprada} a UnidadVenta {unidadVenta}."),
                },
                _ => throw new Exception($"Unidad de compra desconocida: {unidadComprada}.")
            };
        }

        private List<(string unidad, decimal precio)> ObtenerUnidadesVenta(string codigoBarras, SqliteConnection existingConnection = null)
        {
            var unidadesVenta = new List<(string unidad, decimal precio)>();

            // Usar conexión existente o pedir una nueva si es null
            var conn = existingConnection ?? _dbConnection.GetConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();

            string query = "SELECT unidad_venta, precio_venta FROM unidades_venta WHERE codigo_barras = @codigo_barras";

            // NOTA: No usamos 'using' en la conexión aquí porque si nos la prestaron, no debemos cerrarla.
            using var command = new SqliteCommand(query, conn);
            command.Parameters.AddWithValue("@codigo_barras", codigoBarras);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                unidadesVenta.Add((reader.GetString(0), reader.GetDecimal(1)));
            }

            return unidadesVenta;
        }

        private Decimal CalcularStockLimite(Decimal stock, string unidadComprada, string unidadVenta)
        {
            return unidadComprada switch
            {
                "Unidad(es)" => stock,
                "Libra(s)" => unidadVenta == "Onza" ? Math.Round(stock * 16, 3) : stock,
                "Ciento(s)" => unidadVenta == "Unidad" ? Math.Round(stock * 100, 3) : stock,
                "Quintal(es)" => unidadVenta switch
                {
                    "Libra" => Math.Round(stock * 100, 3),
                    "Arroba" => Math.Round(stock * 4, 3),
                    "Quintal" => stock,
                    _ => stock
                },
                _ => stock
            };
        }

        private async Task MostrarDialogoSeleccionUnidad(string codigoBarras, string tipo, string marca, string descripcion, Decimal stock, string unidadComprada, List<(string unidad, decimal precio)> unidadesVenta, decimal precioCompra)
        {
            var dialog = new ContentDialog
            {
                Title = "Seleccionar unidad de venta",
                CloseButtonText = "Cancelar",
                PrimaryButtonText = "Agregar",
                XamlRoot = this.Content.XamlRoot
            };

            var comboBox = new ComboBox
            {
                ItemsSource = unidadesVenta.Select(u => u.unidad).ToList(),
                PlaceholderText = "Seleccione una unidad"
            };

            var cantidadPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var decrementButton = new Button { Content = "-", Width = 30 };
            var cantidadTextBox = new TextBox { Text = "1", Width = 50, HorizontalAlignment = HorizontalAlignment.Center };
            var incrementButton = new Button { Content = "+", Width = 30 };

            cantidadPanel.Children.Add(decrementButton);
            cantidadPanel.Children.Add(cantidadTextBox);
            cantidadPanel.Children.Add(incrementButton);

            decimal limiteStock = stock;

            comboBox.SelectionChanged += (_, _) =>
            {
                if (comboBox.SelectedItem is string unidadSeleccionada)
                {
                    limiteStock = CalcularStockLimite(stock, unidadComprada, unidadSeleccionada);
                }
            };

            decrementButton.Click += (_, _) =>
            {
                if (int.TryParse(cantidadTextBox.Text, out int cantidad) && cantidad > 1)
                {
                    cantidadTextBox.Text = (cantidad - 1).ToString();
                }
            };

            incrementButton.Click += (_, _) =>
            {
                if (int.TryParse(cantidadTextBox.Text, out int cantidad))
                {
                    if (cantidad < limiteStock)
                    {
                        cantidadTextBox.Text = (cantidad + 1).ToString();
                    }
                }
            };

            var dialogContent = new StackPanel { Spacing = 15 };
            dialogContent.Children.Add(new TextBlock { Text = "Seleccione la unidad de venta y la cantidad:" });
            dialogContent.Children.Add(comboBox);
            dialogContent.Children.Add(new TextBlock { Text = "Cantidad:" });
            dialogContent.Children.Add(cantidadPanel);

            dialog.Content = dialogContent;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var unidadSeleccionada = comboBox.SelectedItem?.ToString();
                if (string.IsNullOrEmpty(unidadSeleccionada))
                {
                    await MostrarMensajeAsync("Error", "Debe seleccionar una unidad de venta.");
                    return;
                }

                if (!int.TryParse(cantidadTextBox.Text, out int cantidad) || cantidad <= 0)
                {
                    await MostrarMensajeAsync("Error", "Debe ingresar una cantidad válida.");
                    return;
                }

                Decimal cantidadConvertida = unidadComprada switch
                {
                    "Unidad(es)" => unidadSeleccionada switch
                    {
                        "Unidad" => cantidad,
                        _ => throw new Exception("Unidad no válida para el producto comprado por unidades."),
                    },
                    "Libra(s)" => cantidad * (unidadSeleccionada == "Onza" ? 0.0625m : 1),
                    "Ciento(s)" => cantidad * (unidadSeleccionada == "Unidad" ? 0.01m : 1),
                    "Quintal(es)" => cantidad * (unidadSeleccionada switch
                    {
                        "Libra" => 0.01m,
                        "Arroba" => 0.25m,
                        "Quintal" => 1,
                        _ => throw new Exception("Unidad no válida para el producto comprado por quintales.")
                    }),
                    _ => throw new Exception("Unidad de compra desconocida.")
                };

                if (cantidadConvertida > stock)
                {
                    await MostrarMensajeAsync("Stock insuficiente", $"La cantidad seleccionada ({cantidad}{unidadSeleccionada}) supera el stock disponible.");
                    return;
                }

                var precio = unidadesVenta.First(u => u.unidad == unidadSeleccionada).precio;
                var producto = new ProductoEscaneado
                {
                    CodigoBarras = codigoBarras,
                    Tipo = tipo,
                    Marca = marca,
                    Descripcion = descripcion,
                    Stock = limiteStock,
                    Precio = precio,
                    PrecioCompra = precioCompra,
                    Cantidad = cantidad,
                    UnidadComprada = unidadComprada,
                    UnidadSeleccionada = unidadSeleccionada, // ASIGNAR UNIDAD SELECCIONADA
                    CantidadConvertida = cantidadConvertida
                };

                AñadirOActualizarProductoEnLista(producto);
            }
        }

        private async void ApplyDiscount_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var producto = button?.DataContext as ProductoEscaneado;

            if (producto != null)
            {
                decimal precioCompra = producto.PrecioCompra;
                decimal precioNormal = producto.Precio;

                // Calcular el mínimo del slider
                decimal minimoDescuento = precioCompra + (precioCompra * 0.15m);

                // Crear el diálogo
                var dialog = new ContentDialog
                {
                    Title = $"Rebaja Precio de {producto.Tipo} {producto.Marca}",
                    CloseButtonText = "Cancelar",
                    PrimaryButtonText = "Aplicar Descuento",
                    XamlRoot = this.Content.XamlRoot
                };

                var slider = new Slider
                {
                    Minimum = (double)minimoDescuento,
                    Maximum = (double)precioNormal,
                    Value = (double)precioNormal,
                    StepFrequency = 0.25,
                    TickFrequency = 0.25,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var precioActualText = new TextBlock
                {
                    Text = $"Precio Ahora: Q{producto.Precio:F2}",
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 10)
                };

                slider.ValueChanged += (_, _) =>
                {
                    precioActualText.Text = $"Precio Ahora: Q{slider.Value:F2}";
                    dialog.IsPrimaryButtonEnabled = slider.Value < (double)precioNormal;
                };

                dialog.Content = new StackPanel
                {
                    Children =
            {
                new TextBlock { Text = $"Precio Normal: Q{precioNormal:F2}" },
                precioActualText,
                slider
            },
                    Spacing = 10
                };

                dialog.IsPrimaryButtonEnabled = false;

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    // Actualizar el precio del producto
                    producto.Precio = (decimal)slider.Value;

                    // Actualizar la vista
                    ProductsListView.ItemsSource = null;
                    ProductsListView.ItemsSource = productosEscaneados;

                    // Recalcular los totales
                    ActualizarTotalAPagar();
                }
            }
        }

        private async Task MostrarMensajeAsync(string titulo, string mensaje)
        {
            var dialog = new ContentDialog
            {
                Title = titulo,
                Content = mensaje,
                CloseButtonText = "Aceptar",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async void AñadirOActualizarProductoEnLista(ProductoEscaneado producto)
        {
            var productoExistente = productosEscaneados.FirstOrDefault(p => p.CodigoBarras == producto.CodigoBarras);

            if (productoExistente != null)
            {
                // Verificar si la cantidad total no supera el stock
                decimal nuevaCantidadConvertida = productoExistente.CantidadConvertida + producto.CantidadConvertida;
                if (nuevaCantidadConvertida > producto.Stock)
                {
                    await MostrarMensajeAsync("Stock insuficiente", $"La cantidad total supera el stock disponible ({producto.Stock} {producto.UnidadComprada}).");
                    return;
                }

                productoExistente.Cantidad += producto.Cantidad;
                productoExistente.CantidadConvertida = nuevaCantidadConvertida;
            }
            else
            {
                // Verificar si la cantidad inicial no supera el stock
                if (producto.CantidadConvertida > producto.Stock)
                {
                    await MostrarMensajeAsync("Stock insuficiente", $"La cantidad seleccionada supera el stock disponible ({producto.Stock} {producto.UnidadComprada}).");
                    return;
                }

                productosEscaneados.Add(producto);
            }

            ProductsListView.ItemsSource = null;
            ProductsListView.ItemsSource = productosEscaneados;
            ActualizarListaProductos();
            ActualizarTotalAPagar();

            MontoClienteTextBox.IsEnabled = productosEscaneados.Count > 0;
        }
        private async void IncreaseQuantity_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var producto = button?.DataContext as ProductoEscaneado;

            if (producto != null)
            {
                await IncreaseQuantity(producto);
            }
        }

        private async Task IncreaseQuantity(ProductoEscaneado producto)
        {
            decimal limiteStock = CalcularStockLimite(producto.Stock, producto.UnidadComprada, producto.UnidadSeleccionada);

            if (producto.Cantidad < limiteStock)
            {
                producto.Cantidad++;
                producto.CantidadConvertida = CalcularCantidadConvertida(producto.UnidadComprada, producto.UnidadSeleccionada, producto.Cantidad);

                ProductsListView.ItemsSource = null;
                ProductsListView.ItemsSource = productosEscaneados;
                ActualizarTotalAPagar();
            }
            else
            {
                await MostrarMensajeAsync("Stock insuficiente", "No puedes agregar más productos de los que hay en stock.");
            }
        }

        private void DecreaseQuantity_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var producto = button?.DataContext as ProductoEscaneado;

            if (producto != null)
            {
                DecreaseQuantity(producto);
            }
        }

        private void DecreaseQuantity(ProductoEscaneado producto)
        {
            if (producto.Cantidad > 1)
            {
                producto.Cantidad--;
                producto.CantidadConvertida = CalcularCantidadConvertida(producto.UnidadComprada, producto.UnidadSeleccionada, producto.Cantidad);
            }
            else
            {
                productosEscaneados.Remove(producto);
            }

            ProductsListView.ItemsSource = null;
            ProductsListView.ItemsSource = productosEscaneados;
            ActualizarListaProductos();
            ActualizarTotalAPagar();
        }

        private void RemoveProduct_Click(object sender, RoutedEventArgs e)
        {
            var producto = (sender as Button).DataContext as ProductoEscaneado;
            productosEscaneados.Remove(producto);
            ProductsListView.ItemsSource = null;
            ProductsListView.ItemsSource = productosEscaneados;
            ActualizarListaProductos();
            ActualizarTotalAPagar();
        }
        private void ActualizarListaProductos()
        {
            // Refrescar la lista de productos en el ListView
            ProductsListView.ItemsSource = null;
            ProductsListView.ItemsSource = productosEscaneados;

            // Actualizar el total a pagar
            ActualizarTotalAPagar();

            // Habilitar o deshabilitar el MontoClienteTextBox
            if (productosEscaneados.Count == 0)
            {
                MontoClienteTextBox.Text = string.Empty;
                MontoClienteTextBox.IsEnabled = false;
            }
            else
            {
                MontoClienteTextBox.IsEnabled = true;
            }
        }
        private void MontoClienteTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            decimal totalVenta = ActualizarTotalAPagar();

            var cultureInfo = new System.Globalization.CultureInfo("es-GT");
            //TotalAPagarTextBlock.Text = $"Total a Pagar: {total.ToString("C", cultureInfo)}";

            if (decimal.TryParse(MontoClienteTextBox.Text, out decimal montoCliente))
            {
                if (montoCliente >= totalVenta)
                {
                    decimal cambio = montoCliente - totalVenta;
                    CambioOFalta.Text = $"Cambio: {cambio.ToString("C", cultureInfo)}";
                }
                else
                {
                    decimal falta = totalVenta - montoCliente;
                    CambioOFalta.Text = $"Faltan: {falta.ToString("C", cultureInfo)}";
                }
            }
            else
            {
                CambioOFalta.Text = "Cambio: Q0.00";
            }
        }
        private decimal ActualizarTotalAPagar()
        {
            decimal total = productosEscaneados.Sum(p => p.Precio * p.Cantidad);

            var cultureInfo = new System.Globalization.CultureInfo("es-GT");
            TotalAPagarTextBlock.Text = $"Total a Pagar: {total.ToString("C", cultureInfo)}";

            return total;
        }

        // AÑADIR O BUSCAR CLIENTES POR NIT /////////////////////////////////////////////////////**************************************************************//////////////////

        private void NitTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string nit = NitTextBox.Text;

            // Si no hay nada o el NIT tiene menos de 9 dígitos, mostrar "C.F."
            if (string.IsNullOrWhiteSpace(nit) || nit.Length < 9)
            {
                ClientNameLabel.Text = "C.F.";
                return; // No hacemos nada más si no tiene 9 dígitos
            }

            // Cuando el NIT tiene exactamente 9 dígitos, buscar el cliente en la base de datos
            if (nit.Length == 9 && long.TryParse(nit, out _))
            {
                BuscarClientePorNit(nit);
            }
        }
        private async void BuscarClientePorNit(string nit)
        {
            using var connection = _dbConnection.GetConnection();
            {
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    connection.Open();
                }

                using var command = new SqliteCommand("SELECT nombre FROM clientes WHERE nit = @nit", connection);
                {
                    command.Parameters.AddWithValue("@nit", nit);

                    using var reader = command.ExecuteReader();
                    {
                        if (reader.Read())
                        {
                            // Cliente encontrado, mostrar el nombre
                            string nombreCliente = reader.GetString(0);
                            ClientNameLabel.Text = nombreCliente;
                        }
                        else
                        {
                            // Si el cliente no existe, abrir el diálogo para agregar uno nuevo
                            await MostrarDialogoNuevoCliente(nit);
                        }
                    }
                }
            }
        }
        private async Task MostrarDialogoNuevoCliente(string nit)
        {
            TextBox nitTextBox = new() { PlaceholderText = "Nuevo NIT", Text = nit, IsReadOnly = true };
            TextBox nombreTextBox = new() { PlaceholderText = "Nombre del Cliente" };

            ContentDialog nuevoClienteDialog = new()
            {
                Title = "Agregar Nuevo Cliente",
                Content = new StackPanel
                {
                    Children =
            {
                new TextBlock { Text = "Nuevo NIT:" },
                nitTextBox,
                new TextBlock { Text = "Nombre del Cliente:" },
                nombreTextBox
            }
                },
                PrimaryButtonText = "Agregar",
                CloseButtonText = "Cancelar",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await nuevoClienteDialog.ShowAsync();

            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(nombreTextBox.Text))
            {
                // Insertar el nuevo cliente en la base de datos
                AgregarCliente(nitTextBox.Text, nombreTextBox.Text);
                // Volver a buscar para mostrar el nuevo cliente
                BuscarClientePorNit(nitTextBox.Text);
            }
        }
        private void AgregarCliente(string nit, string nombre)
        {
            using var connection = _dbConnection.GetConnection();
            {
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    connection.Open();
                }

                using var command = new SqliteCommand("INSERT INTO clientes (nit, nombre) VALUES (@nit, @nombre)", connection);
                {
                    command.Parameters.AddWithValue("@nit", nit);
                    command.Parameters.AddWithValue("@nombre", nombre);

                    command.ExecuteNonQuery();
                }
            }
        }

        // AL FINALIZAR LA VENTA***********************************************************************/////////////////////++++++++++++++++++++++++++++++++++++++++**********///////
        private decimal CalcularGananciaProducto(ProductoEscaneado producto)
        {
            // Recuperar el precio de compra y la unidad de compra
            decimal precioCompra = producto.PrecioCompra; // Suponemos que ProductoEscaneado tiene esta propiedad
            string unidadCompra = producto.UnidadComprada;
            string unidadVenta = producto.UnidadSeleccionada;
            decimal precioVenta = producto.Precio; // Precio de la unidad seleccionada
            decimal gananciaPorUnidad;

            // Calcular la ganancia por unidad vendida
            gananciaPorUnidad = unidadCompra switch
            {
                "Unidad(es)" => precioVenta - precioCompra,
                "Libra(s)" => unidadVenta switch
                {
                    "Onza" => precioVenta - (precioCompra / 16),
                    "Libra" => precioVenta - precioCompra,
                    _ => throw new Exception($"Conversión no válida: {unidadCompra} a {unidadVenta}.")
                },
                "Ciento(s)" => unidadVenta switch
                {
                    "Unidad" => precioVenta - (precioCompra / 100),
                    "Ciento" => precioVenta - precioCompra,
                    _ => throw new Exception($"Conversión no válida: {unidadCompra} a {unidadVenta}.")
                },
                "Quintal(es)" => unidadVenta switch
                {
                    "Libra" => precioVenta - (precioCompra / 100),
                    "Arroba" => precioVenta - (precioCompra / 4),
                    "Quintal" => precioVenta - precioCompra,
                    _ => throw new Exception($"Conversión no válida: {unidadCompra} a {unidadVenta}.")
                },
                _ => throw new Exception($"Unidad de compra desconocida: {unidadCompra}.")
            };

            // Calcular la ganancia total para la cantidad vendida
            return gananciaPorUnidad * producto.Cantidad;
        }

        private async void FinalizarVenta_Click(object sender, RoutedEventArgs e)
        {
            if (productosEscaneados.Count == 0)
            {
                await MostrarMensajeAsync("Sin productos", "No ha agregado productos a la lista.");
                return;
            }

            // Recuperar configuraciones de factura desde LocalSettings
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            string facturaTienda = localSettings.Values["FacturaTienda"] as string ?? "Nombre de la tienda";
            string facturaDireccion = localSettings.Values["FacturaDireccion"] as string ?? "Dirección de la tienda";
            string facturaMensaje = localSettings.Values["FacturaMensaje"] as string ?? "No se aceptan devoluciones";
            string facturaPie = localSettings.Values["FacturaPie"] as string ?? "Gracias por su compra.";

            string nitCliente = NitTextBox.Text.Trim();
            string nombreCliente = ClientNameLabel.Text; // Tomar el nombre del cliente desde el TextBlock

            if (string.IsNullOrWhiteSpace(nitCliente) || nitCliente.Length < 9)
            {
                var nitDialog = new ContentDialog
                {
                    Title = "Cliente no identificado",
                    Content = "El NIT ingresado es inválido o incompleto. ¿Desea continuar sin identificar al cliente?",
                    PrimaryButtonText = "Sí",
                    SecondaryButtonText = "Solo Nombre",
                    CloseButtonText = "Cancelar",
                    XamlRoot = this.Content.XamlRoot
                };

                var nitResult = await nitDialog.ShowAsync();
                if (nitResult == ContentDialogResult.Primary)
                {
                    nitCliente = "C.F.";
                    nombreCliente = "Desconocido";
                }
                else if (nitResult == ContentDialogResult.Secondary)
                {
                    var nameInputDialog = new ContentDialog
                    {
                        Title = "Ingrese el nombre del cliente",
                        Content = new TextBox { PlaceholderText = "Nombre del cliente", Width = 200, MaxLength = 20 },
                        PrimaryButtonText = "Guardar",
                        CloseButtonText = "Cancelar",
                        XamlRoot = this.Content.XamlRoot
                    };
                    var nameInputResult = await nameInputDialog.ShowAsync();

                    if (nameInputResult == ContentDialogResult.Primary)
                    {
                        var textBox = nameInputDialog.Content as TextBox;
                        if (!string.IsNullOrWhiteSpace(textBox?.Text))
                        {
                            nitCliente = "C.F.";
                            nombreCliente = textBox.Text.Trim();
                        }
                        else
                        {
                            await MostrarMensajeAsync("Error", "Debe ingresar un nombre.");
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            string montoClienteStr = MontoClienteTextBox.Text.Trim();
            bool montoIngresado = decimal.TryParse(montoClienteStr, out decimal montoCliente);

            // Obtener el total de la venta actual
            decimal totalVenta = ActualizarTotalAPagar();
            decimal totalGanancias = 0;

            // Verificar si el monto es suficiente para la venta
            string cambio = "Menor";
            string montoGuardar = "Menor";

            if (montoIngresado)
            {
                if (montoCliente < totalVenta)
                {
                    await MostrarMensajeAsync("Monto insuficiente", $"Faltan: Q{totalVenta - montoCliente:F2}. No puede finalizar la venta.");
                    return;
                }
                else
                {
                    decimal cambioCalculado = montoCliente - totalVenta;
                    cambio = $"Q{cambioCalculado:F2}";
                    montoGuardar = $"Q{montoCliente:F2}";
                }
            }
            var dialog = new ContentDialog
            {
                Title = "Confirmación",
                Content = "¿Finalizar la venta?",
                PrimaryButtonText = "Sí",
                CloseButtonText = "Cancelar",
                XamlRoot = this.Content.XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                string fechaVenta = DateTime.Now.ToString("dd-MM-yyyy");
                string horaVenta = DateTime.Now.ToString("HH:mm:ss");

                var connection = _dbConnection.GetConnection(); // conexión con la base de datos
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    connection.Open(); // conexión esté abierta
                }
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        foreach (var producto in productosEscaneados)
                        {
                            if (string.IsNullOrEmpty(producto.UnidadSeleccionada))
                            {
                                throw new Exception($"El producto con código de barras {producto.CodigoBarras} no tiene unidad seleccionada asignada correctamente.");
                            }

                            decimal cantidadDescontar = producto.CantidadConvertida;

                            if (cantidadDescontar <= 0)
                            {
                                throw new Exception($"Cantidad no válida para descontar: {cantidadDescontar}. Producto: {producto.Descripcion}, UnidadComprada: {producto.UnidadComprada}, UnidadSeleccionada: {producto.UnidadSeleccionada}.");
                            }

                            using var updateCommand = new SqliteCommand("UPDATE productos SET stock = stock - @cantidad WHERE codigo_barras = @codigo_barras", connection);
                            {
                                updateCommand.Parameters.AddWithValue("@cantidad", cantidadDescontar);
                                updateCommand.Parameters.AddWithValue("@codigo_barras", producto.CodigoBarras);
                                updateCommand.Transaction = transaction;

                                int rowsAffected = updateCommand.ExecuteNonQuery();
                                if (rowsAffected == 0)
                                {
                                    throw new Exception($"No se pudo actualizar el stock para el producto con código de barras {producto.CodigoBarras}.");
                                }

                                totalGanancias += CalcularGananciaProducto(producto);
                            }
                        }

                        string productosVendidos = string.Join(";", productosEscaneados.Select(p => $"{p.Tipo}~{p.Marca}~{p.Cantidad} {p.UnidadSeleccionada}~{p.Precio}"));
                        string productosVendidosp = string.Join("\n", productosEscaneados.Select(p => $"{p.Tipo} {p.Marca} x {p.Cantidad} {p.UnidadSeleccionada} (PxU: {p.Precio.ToString("C", CultureInfo.GetCultureInfo("es-GT"))})"));

                        using var insertCommand = new SqliteCommand("INSERT INTO ventas (productosvendidos, ventashechas, total, monto, cambio, nitcliente, nombrecliente, fecha, hora, totalganancias) VALUES (@productosvendidos, @ventashechas, @total, @monto, @cambio, @nitcliente, @nombrecliente, @fecha, @hora, @totalganancias)", connection);
                        {
                            insertCommand.Parameters.AddWithValue("@productosvendidos", productosVendidosp);
                            insertCommand.Parameters.AddWithValue("@ventashechas", productosVendidos);
                            insertCommand.Parameters.AddWithValue("@total", totalVenta);
                            insertCommand.Parameters.AddWithValue("@monto", montoGuardar);
                            insertCommand.Parameters.AddWithValue("@cambio", cambio);
                            insertCommand.Parameters.AddWithValue("@nitcliente", nitCliente);
                            insertCommand.Parameters.AddWithValue("@nombrecliente", nombreCliente);
                            insertCommand.Parameters.AddWithValue("@fecha", fechaVenta);
                            insertCommand.Parameters.AddWithValue("@hora", horaVenta);
                            insertCommand.Parameters.AddWithValue("@totalganancias", totalGanancias);
                            insertCommand.Transaction = transaction;
                            insertCommand.ExecuteNonQuery();

                            /*await MostrarMensajeAsync("Datos Insertados",
                                $"Productos Vendidos: {productosVendidosp}\n" +
                                $"Ventas Hechas: {productosVendidos}\n" +
                                $"Total: {totalVenta}\n" +
                                $"Monto: {montoGuardar}\n" +
                                $"Cambio: {cambio}\n" +
                                $"NIT Cliente: {nitCliente}\n" +
                                $"Nombre Cliente: {nombreCliente}\n" +
                                $"Fecha: {fechaVenta}\n" +
                                $"Hora: {horaVenta}\n" +
                                $"Total Ganancias: {totalGanancias}");*/

                        }

                        transaction.Commit(); // Confirmar la transacción

                        long idFactura;
                        using (var command = new SqliteCommand("SELECT last_insert_rowid()", connection))
                        {
                            idFactura = (long)command.ExecuteScalar();
                        }

                        //aqui iva

                        var printDialog = new ContentDialog
                        {
                            Title = "Imprimir Comprobante",
                            Content = "¿Desea imprimir el comprobante de compra?",
                            PrimaryButtonText = "Sí",
                            CloseButtonText = "No",
                            XamlRoot = this.Content.XamlRoot
                        };
                        var printResult = await printDialog.ShowAsync();
                        if (printResult == ContentDialogResult.Primary)
                        {
                            ImprimirFacturaDirectamente(facturaTienda, facturaDireccion, nitCliente, nombreCliente, totalVenta.ToString("C", CultureInfo.GetCultureInfo("es-GT")), montoGuardar, cambio, facturaMensaje, facturaPie, fechaVenta, horaVenta, idFactura);
                        }
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback(); // Revertir la transacción si ocurre un error
                        await new ContentDialog
                        {
                            Title = "Error",
                            Content = $"E: {ex.Message}",
                            CloseButtonText = "Aceptar",
                            XamlRoot = this.Content.XamlRoot
                        }.ShowAsync();
                    }
                    finally
                    {
                        connection.Close(); // Cerrar la conexión después de usarla
                    }
                }
                productosEscaneados.Clear();
                ProductsListView.ItemsSource = null;
                ActualizarTotalAPagar();
                CambioOFalta.Text = "Cambio: Q0.00";
                MontoClienteTextBox.Text = "";
                MontoClienteTextBox.IsEnabled = false;
                NitTextBox.Text = "";
                CheckProductStock();
            }
        }
        private async void CancelarVenta_Click(object sender, RoutedEventArgs e)
        {
            if (productosEscaneados.Count == 0)
            {
                var noProductsDialog = new ContentDialog
                {
                    Title = "No hay venta en curso",
                    Content = "No hay productos agregados para cancelar.",
                    CloseButtonText = "Aceptar",
                    XamlRoot = this.Content.XamlRoot
                };

                await noProductsDialog.ShowAsync();
                return;
            }

            var cancelDialog = new ContentDialog
            {
                Title = "Confirmación de cancelación",
                Content = "¿Está seguro de que desea cancelar la venta? Todos los productos agregados se perderán.",
                PrimaryButtonText = "Sí",
                CloseButtonText = "No",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await cancelDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                productosEscaneados.Clear();
                ProductsListView.ItemsSource = null;

                NitTextBox.Text = string.Empty;

                ActualizarTotalAPagar();

                CambioOFalta.Text = "Cambio: Q0.00";

                MontoClienteTextBox.Text = "";
                MontoClienteTextBox.IsEnabled = false;
            }
        }

        //NOTIFICACIONES//////////////////*****************/*/*/*/*/*///+/+/+//+********************************************/
        private void CheckProductStock()
        {
            int stockThreshold = (int)(ApplicationData.Current.LocalSettings.Values["StockThreshold"] ?? 5); // Valor por defecto

            using var dbConnection = new DatabaseConnection();
            {
                dbConnection.OpenConnection();
                string query = "SELECT codigo_barras, marca, descripcion, stock FROM productos";
                using var command = new SqliteCommand(query, dbConnection.GetConnection());
                {
                    using var reader = command.ExecuteReader();
                    {
                        List<string> productosConStockBajo = new();

                        while (reader.Read())
                        {
                            string codigo = reader.GetString(0);
                            string marca = reader.GetString(1);
                            string descripcion = reader.GetString(2);
                            int stock = reader.GetInt32(3);

                            if (stock <= stockThreshold)
                            {
                                productosConStockBajo.Add(codigo);

                                // Verificar si la notificación ya existe
                                string checkQuery = "SELECT COUNT(*) FROM notificaciones WHERE codigo_barras = @codigo_barras";
                                using var checkCommand = new SqliteCommand(checkQuery, dbConnection.GetConnection());
                                checkCommand.Parameters.AddWithValue("@codigo_barras", codigo);

                                long count = (long)checkCommand.ExecuteScalar();

                                if (count > 0)
                                {
                                    // Actualizar solo el stock
                                    string updateQuery = @"UPDATE notificaciones SET stock = @stock WHERE codigo_barras = @codigo_barras";
                                    using var updateCommand = new SqliteCommand(updateQuery, dbConnection.GetConnection());
                                    updateCommand.Parameters.AddWithValue("@codigo_barras", codigo);
                                    updateCommand.Parameters.AddWithValue("@stock", stock);
                                    updateCommand.ExecuteNonQuery();
                                }
                                else
                                {
                                    // Insertar nueva notificación con fecha y hora
                                    string insertQuery = @"INSERT INTO notificaciones (codigo_barras, marca, descripcion, stock, fecha_hora)
                                                           VALUES (@codigo_barras, @marca, @descripcion, @stock, @fecha_hora)";
                                    using var insertCommand = new SqliteCommand(insertQuery, dbConnection.GetConnection());
                                    insertCommand.Parameters.AddWithValue("@codigo_barras", codigo);
                                    insertCommand.Parameters.AddWithValue("@marca", marca);
                                    insertCommand.Parameters.AddWithValue("@descripcion", descripcion);
                                    insertCommand.Parameters.AddWithValue("@stock", stock);
                                    insertCommand.Parameters.AddWithValue("@fecha_hora", DateTime.Now.ToString("g"));
                                    insertCommand.ExecuteNonQuery();
                                }
                            }
                        }
                        // Eliminar notificaciones de productos con stock suficiente
                        EliminarNotificacionesRelevantes(productosConStockBajo, dbConnection);
                    }
                }
            }
        }

        private void EliminarNotificacionesRelevantes(List<string> productosConStockBajo, DatabaseConnection dbConnection)
        {
            if (productosConStockBajo == null || productosConStockBajo.Count == 0)
                return;

            string placeholders = string.Join(", ", productosConStockBajo.Select((_, index) => $"@p{index}"));
            string deleteQuery = $@"DELETE FROM notificaciones
                                    WHERE codigo_barras NOT IN ({placeholders})";

            using var deleteCommand = new SqliteCommand(deleteQuery, dbConnection.GetConnection());

            for (int i = 0; i < productosConStockBajo.Count; i++)
            {
                deleteCommand.Parameters.AddWithValue($"@p{i}", productosConStockBajo[i]);
            }

            deleteCommand.ExecuteNonQuery();
        }

        //IMPRIMIR FACTURA///////////////////////////////////////////////////////////////////////////********************************************************************
        private void ImprimirFacturaDirectamente(string facturaTienda, string facturaDireccion, string nitCliente, string nombreCliente, string totalVentaStr, string montoGuardar, string cambio, string facturaMensaje, string facturaPie, string fechaVenta, string horaVenta, long idFactura)
        {
            // Crear el documento de impresión
            System.Drawing.Printing.PrintDocument printDocument = new();

            // Asignar el controlador para el evento de impresión
            printDocument.PrintPage += (sender, e) => PrintPageHandler(sender, e, facturaTienda, facturaDireccion, nitCliente, nombreCliente, totalVentaStr, montoGuardar, cambio, facturaMensaje, facturaPie, fechaVenta, horaVenta, idFactura);

            // Establecer la impresora predeterminada o asignar una específica
            // printDocument.PrinterSettings.PrinterName = "NombreDeTuImpresora"; // Descomenta y cambia si es necesario

            // Enviar el documento a la impresora
            printDocument.Print();
        }
        private void PrintPageHandler(object sender, PrintPageEventArgs e, string facturaTienda, string facturaDireccion, string nitCliente, string nombreCliente, string totalVentaStr, string montoGuardar, string cambio, string facturaMensaje, string facturaPie, string fechaVenta, string horaVenta, long idFactura)
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

            // Productos vendidos (alineados por columnas)
            foreach (var producto in productosEscaneados)
            {
                // Limitar la descripción a 20 caracteres y añadir "**" si es más larga
                string tipo = producto.Tipo.Length > 9 ? string.Concat(producto.Tipo.AsSpan(0, 9), "**") : producto.Tipo;
                string marca = producto.Marca.Length > 10 ? string.Concat(producto.Marca.AsSpan(0, 10), "**") : producto.Marca;
                string cantidadConUnidad = $"{producto.Cantidad} {producto.UnidadSeleccionada}";

                // Dibujar cada columna en su posición
                e.Graphics.DrawString(tipo, fontSmall, Brushes.Black, new PointF(columnaMarca, yPos));
                e.Graphics.DrawString(marca, fontSmall, Brushes.Black, new PointF(columnaDescripcion, yPos));
                e.Graphics.DrawString(cantidadConUnidad, fontSmall, Brushes.Black, new PointF(columnaCantidad, yPos));
                e.Graphics.DrawString(producto.Precio.ToString("C", CultureInfo.GetCultureInfo("es-GT")), fontSmall, Brushes.Black, new PointF(columnaPrecio, yPos));
                yPos += fontSmall.GetHeight(e.Graphics);
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
    }
    public class ProductoEscaneado
    {
        public string CodigoBarras { get; set; }
        public string Tipo { get; set; }
        public string Marca { get; set; }
        public string Descripcion { get; set; }
        public string UnidadComprada { get; set; }
        public string UnidadSeleccionada { get; set; }
        public decimal Stock { get; set; }
        public decimal Precio { get; set; }
        public decimal PrecioOriginal { get; set; }
        public decimal PrecioCompra { get; set; }
        public int Cantidad { get; set; }
        public decimal CantidadConvertida { get; set; }

        public string Typee => Tipo;
        public string Brandd => Marca;
        public string Descc => Descripcion;
        public string Pricee => Precio.ToString("C", new System.Globalization.CultureInfo("es-GT"));
        public string Quantityy => Cantidad.ToString();
    }
}
