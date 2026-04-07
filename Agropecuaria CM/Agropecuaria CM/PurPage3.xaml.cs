using Agropecuaria_CM.Models;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Agropecuaria_CM
{
    public sealed partial class PurPage3 : Page
    {
        private readonly ObservableCollection<Product> products;
        private MainWindow _mainWindow;
        private byte[] _selectedPhotoBytes = null; // Para guardar la foto en memoria temporalmente

        public PurPage3()
        {
            this.InitializeComponent();
            products = [];
            LoadProducts();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is MainWindow mainWindow)
            {
                _mainWindow = mainWindow;  // Guardar la referencia de MainWindow
            }
        }

        private void TextBEsc_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            // Permitir solo dígitos numéricos
            sender.Text = new string(sender.Text.Where(char.IsDigit).ToArray());

            // Limitar a 13 caracteres
            if (sender.Text.Length > 13)
            {
                sender.Text = sender.Text[..13];
            }
        }
        private void TextVA_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            string newText = sender.Text;

            // Eliminar los caracteres no permitidos
            if (newText.Contains(';') || newText.Contains('~'))
            {
                newText = newText.Replace(";", "").Replace("~", "");
                sender.Text = newText;

                // Colocar el cursor al final del texto
                sender.SelectionStart = newText.Length;
            }
        }
        private void TextVA1_TextChanging(object sender, TextChangedEventArgs e)
        {
            TextBox textBox = sender as TextBox;
            if (textBox == null) return;

            // Verificar si hay caracteres no permitidos
            string newText = textBox.Text;

            // Eliminar los caracteres no permitidos
            if (newText.Contains(";") || newText.Contains("~"))
            {
                newText = newText.Replace(";", "").Replace("~", "");
                textBox.Text = newText;

                // Colocar el cursor al final del texto
                textBox.SelectionStart = newText.Length;
            }
        }
        //LISTA////*//*///////////////////////////////******************************************************//////////*/*/*/*/*/*/*/*/*/*/

        private decimal CalculateTotalCapital()
        {
            return products.Sum(p => Convert.ToDecimal(((Agropecuaria_CM.Models.Product)p).ProductCapital));
        }

        private int CalculateTotalByUnit(string unidad)
        {
            return products.Count(p => p.ProductUnidadComprada.Equals(unidad, StringComparison.OrdinalIgnoreCase));
        }

        private int CalculateTotalProducts()
        {
            return products.Count;
        }

        private void LoadProducts()
        {
            var cultureInfo = new System.Globalization.CultureInfo("es-GT");
            try
            {
                using (var dbConnection = new DatabaseConnection())
                {
                    dbConnection.OpenConnection();

                    // Consulta para recuperar información de productos y unidades de venta
                    string query = @"SELECT p.codigo_barras, p.tipo, p.marca, p.descripcion, p.unidad_comprada, p.stock, p.preciocompra, u.unidad_venta, u.precio_venta
                        FROM productos p
                        LEFT JOIN unidades_venta u ON p.codigo_barras = u.codigo_barras
                        ORDER BY p.codigo_barras";

                    using var command = new SqliteCommand(query, dbConnection.GetConnection());
                    {
                        using var reader = command.ExecuteReader();
                        {
                            products.Clear(); // Limpiar la colección antes de añadir nuevos productos

                            // Estructura auxiliar para agrupar las unidades de venta por producto
                            var productDict = new Dictionary<string, Product>();

                            while (reader.Read())
                            {
                                string barcode = reader.GetString(0);

                                // Si el producto no existe en el diccionario, se crea y se añade
                                if (!productDict.ContainsKey(barcode))
                                {
                                    var product = new Product
                                    {
                                        ProductName = barcode,
                                        ProductType = reader.GetString(1),
                                        ProductBrand = reader.GetString(2),
                                        ProductDescription = reader.GetString(3),
                                        ProductUnidadComprada = reader.GetString(4),
                                        ProductStock = reader.GetDecimal(5).ToString(),
                                        ProductPriceCompra = reader.GetDecimal(6).ToString("F2"),
                                        ProductCapital = (reader.GetDecimal(5) * reader.GetDecimal(6)).ToString("F2"),
                                    };
                                    productDict[barcode] = product;
                                }

                                // Añadir la unidad de venta al producto si está presente
                                if (!reader.IsDBNull(7) && !reader.IsDBNull(8))
                                {
                                    var unitSale = new UnitSale
                                    {
                                        Unit = reader.GetString(7),
                                        Price = reader.GetDecimal(8)
                                    };
                                    productDict[barcode].UnitSales.Add(unitSale);
                                }
                            }

                            foreach (var product in productDict.Values)
                            {
                                products.Add(product);
                            }
                        }
                    }
                }

                // Actualizar la interfaz gráfica
                TotalProductosUnidadTextBlock.Text = $"Productos por Unidad: {CalculateTotalByUnit("Unidad(es)")}";
                TotalProductosLibraTextBlock.Text = $"Por Libra: {CalculateTotalByUnit("Libra(s)")}";
                TotalProductosCientoTextBlock.Text = $"Por Ciento: {CalculateTotalByUnit("Ciento(s)")}";
                TotalProductosQuintalTextBlock.Text = $"Por Quintal: {CalculateTotalByUnit("Quintal(es)")}";
                TotalProductosTotalTextBlock.Text = $"Productos Totales: {CalculateTotalProducts()}";
                TotalCapitalTextBlock.Text = "Capital Total: " + CalculateTotalCapital().ToString("C", cultureInfo);

                // Configurar visibilidad
                if (products.Count == 0)
                {
                    NoProductsTextBlock.Visibility = Visibility.Visible;
                    StackPList.Visibility = Visibility.Collapsed;
                    StackPFilters.Visibility = Visibility.Collapsed;
                }
                else
                {
                    StackPList.Visibility = Visibility.Visible;
                    StackPFilters.Visibility = Visibility.Visible;
                    NoProductsTextBlock.Visibility = Visibility.Collapsed;
                }

                StockList.SelectedItem = null;
                StockList.ItemsSource = products;
            }
            catch (Exception ex)
            {
                var contentDialog = new ContentDialog
                {
                    Title = "Error",
                    Content = $"Error al cargar productos: {ex.Message}",
                    CloseButtonText = "Ok",
                    XamlRoot = this.XamlRoot
                };

                if (this.XamlRoot != null)
                {
                    _ = contentDialog.ShowAsync();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Error al cargar productos sin XamlRoot: {ex.Message}");
                }
            }
        }

        // EL INICIO DE LOS CAMBIOS, LOS METODOS.//////////////////////////////////////////////////////////////////////////////////////////

        // --- LÓGICA DE TARJETAS AUTOMÁTICAS ---

        private void UnidadCompradaComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedItem = UnidadCompradaComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                string unidadComprada = selectedItem.Content.ToString();

                // 1. Habilitar campos de entrada
                if (ProductStockNB != null) ProductStockNB.IsEnabled = true;
                if (ProductPriceCompraNumberBox != null) ProductPriceCompraNumberBox.IsEnabled = true;

                // 2. Generar las tarjetas de venta automáticamente
                GenerarTarjetasDeVenta(unidadComprada);
            }
        }

        private void GenerarTarjetasDeVenta(string unidadComprada)
        {
            // Limpiar contenedor anterior
            if (SellingCardsContainer == null) return;
            SellingCardsContainer.Children.Clear();

            // Definir qué unidades son compatibles según lo comprado
            List<string> unidadesCompatibles = new List<string>();

            switch (unidadComprada)
            {
                case "Quintal(es)":
                    unidadesCompatibles.AddRange(new[] { "Quintal", "Arroba", "Libra", "Onza" });
                    break;
                case "Libra(s)":
                    unidadesCompatibles.AddRange(new[] { "Libra", "Onza" });
                    break;
                case "Ciento(s)":
                    unidadesCompatibles.AddRange(new[] { "Ciento", "Unidad" });
                    break;
                case "Unidad(es)":
                    unidadesCompatibles.AddRange(new[] { "Unidad" });
                    break;
            }

            // Crear una tarjeta visual por cada unidad compatible
            foreach (string unidad in unidadesCompatibles)
            {
                SellingCardsContainer.Children.Add(CrearTarjetaUI(unidad));
            }
        }

        private FrameworkElement CrearTarjetaUI(string unidad)
        {
            // Crear el diseño de la tarjeta (Border > StackPanel > TextBlock + NumberBox)
            StackPanel panel = new StackPanel { Spacing = 5, Width = 120 };

            TextBlock label = new TextBlock
            {
                Text = unidad,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            NumberBox precioBox = new NumberBox
            {
                PlaceholderText = "0.00",
                Header = "Precio (Q)",
                Minimum = 0,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Tag = unidad // IMPORTANTE: Guardamos el nombre de la unidad en el Tag para leerlo luego
            };

            // --- CORRECCIÓN DEL ERROR ---

            // 1. Recuperamos el recurso como Color (porque es un ...FillColorDefault)
            var colorRecurso = (Windows.UI.Color)Application.Current.Resources["LayerOnAccentAcrylicFillColorDefault"];

            // 2. Creamos un pincel (Brush) usando ese color
            var backgroundBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(colorRecurso);

            // 3. Recuperamos el pincel del borde de forma segura (usando 'as' para evitar crash)
            var borderBrush = Application.Current.Resources["SystemControlForegroundBaseLowBrush"] as Microsoft.UI.Xaml.Media.Brush;

            // Estilo visual de la tarjeta
            Border card = new Border
            {
                Child = panel,
                Background = backgroundBrush, // Usamos el Brush creado arriba
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                // Si por alguna razón el recurso del borde falla, usamos Gray por defecto
                BorderBrush = borderBrush ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(4,4,4,4))
            };

            panel.Children.Add(label);
            panel.Children.Add(precioBox);

            return card;
        }

        // --- LÓGICA DE CONVERSIÓN A STOCK ATÓMICO ---

        private int ObtenerFactorConversion(string unidad)
        {
            if (string.IsNullOrEmpty(unidad)) return 1;

            // Usamos StartsWith para ignorar plurales o paréntesis al final
            // Así detectamos la raíz de la palabra
            if (unidad.StartsWith("Quintal")) return 1600;
            if (unidad.StartsWith("Arroba")) return 400;
            if (unidad.StartsWith("Libra")) return 16;
            if (unidad.StartsWith("Onza")) return 1;
            if (unidad.StartsWith("Ciento")) return 100;
            if (unidad.StartsWith("Unidad")) return 1;

            return 1; // Default
        }

        private bool LasValidaciones()
        {
            // Validar el código de barras
            if (string.IsNullOrWhiteSpace(TextBEsc.Text) || (TextBEsc.Text.Length != 12 && TextBEsc.Text.Length != 13))
            {
                MostrarError("El código de barras debe tener 12 o 13 dígitos.");
                return false;
            }

            // Validar tipo, marca y descripción
            if (string.IsNullOrWhiteSpace(ProductTypeTextBox.Text))
            {
                MostrarError("El tipo del producto es obligatorio.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(ProductBrandTextBox.Text))
            {
                MostrarError("La marca del producto es obligatoria.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(ProductDescriptionTextBox.Text))
            {
                MostrarError("La descripción del producto es obligatoria.");
                return false;
            }

            // Validar unidad de compra
            if (UnidadCompradaComboBox.SelectedItem == null)
            {
                MostrarError("Debe seleccionar una unidad de medida en la que se compró el producto.");
                return false;
            }

            string unidadComprada = (UnidadCompradaComboBox.SelectedItem as ComboBoxItem)?.Content.ToString();
            if (string.IsNullOrEmpty(unidadComprada))
            {
                MostrarError("Debe seleccionar una unidad válida.");
                return false;
            }

            // Validar cantidad en stock y precio de compra
            decimal stock = GetValidNumber(ProductStockNB);
            decimal precioCompra = GetValidNumber(ProductPriceCompraNumberBox);

            if (stock <= 0)
            {
                MostrarError("Debe especificar una cantidad en stock mayor a 0.");
                return false;
            }

            if (precioCompra <= 0)
            {
                MostrarError("Debe especificar un precio de compra mayor a 0.");
                return false;
            }

            /* Validar precios según la unidad comprada
            foreach (var (unidadVenta, precioVenta) in unidadesSeleccionadas)
            {
                decimal precioCompraVenta;

                switch (unidadComprada)
                {
                    case "Unidad(es)":
                        precioCompraVenta = precioCompra;
                        break;
                    case "Libra(s)":
                        precioCompraVenta = unidadVenta == "Onza" ? precioCompra / 16 : precioCompra;
                        break;
                    case "Ciento(s)":
                        precioCompraVenta = unidadVenta == "Unidad" ? precioCompra / 100 : precioCompra;
                        break;
                    case "Quintal(es)":
                        precioCompraVenta = unidadVenta == "Libra" ? precioCompra / 100 :
                                            unidadVenta == "Arroba" ? precioCompra / 4 :
                                            precioCompra;
                        break;
                    default:
                        MostrarError($"No se reconoce la unidad comprada: {unidadComprada}");
                        return false;
                }

                if (precioVenta <= precioCompraVenta)
                {
                    MostrarError($"El precio de venta para la unidad '{unidadVenta}' debe ser mayor que el precio de compra ajustado ({precioCompraVenta}).");
                    return false;
                }
            }*/

            return true;
        }
        private void MostrarError(string mensaje)
        {
            ContentDialog errorDialog = new()
            {
                Title = "Falta algo...",
                Content = mensaje,
                CloseButtonText = "Aceptar",
                XamlRoot = this.XamlRoot
            };
            _ = errorDialog.ShowAsync();
        }
        // LAS CLASES PARA AÑADIR UN PRODUCTO NUEVO ****************************************************************************************
        private async void OnAddProductClick_Click(object sender, RoutedEventArgs e)
        {
            await OnAddProductClick(sender, e);  // Llama al método que ahora es async Task
        }
        // --- GUARDAR PRODUCTO ---

        private async Task OnAddProductClick(object sender, RoutedEventArgs e)
        {
            // 1. Validaciones
            if (!LasValidaciones()) return;

            // 2. Obtener datos básicos
            string barcode = TextBEsc.Text.Trim();
            string type = ProductTypeTextBox.Text.Trim();
            string brand = ProductBrandTextBox.Text.Trim();
            string desc = ProductDescriptionTextBox.Text.Trim();
            string unidadCompradaRaw = (UnidadCompradaComboBox.SelectedItem as ComboBoxItem).Content.ToString();

            decimal cantidadIngresada = (decimal)ProductStockNB.Value;
            decimal costoTotal = (decimal)ProductPriceCompraNumberBox.Value;

            // 3. CÁLCULO DEL STOCK ATÓMICO (SIN DECIMALES)
            // Ejemplo: Si puso 2 Quintales -> 2 * 1600 = 3200 onzas a guardar.
            int factor = ObtenerFactorConversion(unidadCompradaRaw);
            decimal stockFinalBase = cantidadIngresada * factor;

            // 4. Recolectar precios de las tarjetas dinámicas
            var preciosAInsertar = new List<(string unidad, decimal precio)>();
            bool hayAlMenosUnPrecio = false;

            foreach (var element in SellingCardsContainer.Children)
            {
                if (element is Border card && card.Child is StackPanel panel)
                {
                    // Buscamos el NumberBox dentro de la tarjeta
                    var nb = panel.Children.OfType<NumberBox>().FirstOrDefault();
                    if (nb != null && nb.Value > 0)
                    {
                        string uNombre = nb.Tag.ToString(); // Recuperamos el nombre del Tag
                        decimal pVenta = (decimal)nb.Value;
                        preciosAInsertar.Add((uNombre, pVenta));
                        hayAlMenosUnPrecio = true;
                    }
                }
            }

            if (!hayAlMenosUnPrecio)
            {
                await MostrarErrorDialog("Debes ponerle precio a al menos una unidad de venta.");
                return;
            }

            // 5. INSERTAR EN BASE DE DATOS
            try
            {
                using (var db = new DatabaseConnection())
                {
                    db.OpenConnection();

                    // A. Verificar duplicado
                    var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM productos WHERE codigo_barras = @cb", db.GetConnection());
                    checkCmd.Parameters.AddWithValue("@cb", barcode);
                    if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
                    {
                        await MostrarErrorDialog("El código de barras ya existe.");
                        return;
                    }

                    // B. Insertar Producto (OJO: Guardamos stockFinalBase)
                    string qProd = @"INSERT INTO productos (codigo_barras, tipo, marca, descripcion, unidad_comprada, stock, preciocompra, foto) 
                             VALUES (@cb, @ti, @ma, @de, @uc, @st, @pc, @fo)";

                    using (var cmd = new SqliteCommand(qProd, db.GetConnection()))
                    {
                        cmd.Parameters.AddWithValue("@cb", barcode);
                        cmd.Parameters.AddWithValue("@ti", type);
                        cmd.Parameters.AddWithValue("@ma", brand);
                        cmd.Parameters.AddWithValue("@de", desc);
                        cmd.Parameters.AddWithValue("@uc", unidadCompradaRaw); // Guardamos "Quintal(es)" para referencia visual futura
                        cmd.Parameters.AddWithValue("@st", stockFinalBase);    // Guardamos 3200 (onzas)
                        cmd.Parameters.AddWithValue("@pc", costoTotal);
                        cmd.Parameters.AddWithValue("@fo", _selectedPhotoBytes ?? (object)DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }

                    // C. Insertar Unidades de Venta
                    string qUnit = "INSERT INTO unidades_venta (codigo_barras, unidad_venta, precio_venta) VALUES (@cb, @uv, @pv)";
                    foreach (var item in preciosAInsertar)
                    {
                        using (var cmd = new SqliteCommand(qUnit, db.GetConnection()))
                        {
                            cmd.Parameters.AddWithValue("@cb", barcode);
                            cmd.Parameters.AddWithValue("@uv", item.unidad);
                            cmd.Parameters.AddWithValue("@pv", item.precio);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                // 6. Finalizar
                // Aquí llamamos a LoadProducts, PERO OJO: LoadProducts lee de la base de datos.
                // Como no hemos tocado la lista ni el READ, mostrará "3200" en stock.
                // Eso lo arreglamos luego. Por ahora, el registro funcionará perfecto.

                LoadProducts();
                LimpiarCampos();
                await MostrarErrorDialog("Producto registrado correctamente (Stock guardado en unidad base).");
            }
            catch (Exception ex)
            {
                await MostrarErrorDialog("Error DB: " + ex.Message);
            }
        }

        // --- LÓGICA DE FOTOGRAFÍA (NUEVO) ---

        private async void PhotoUploadButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            // Obtener el identificador de ventana para WinUI 3
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");

            StorageFile file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await LoadImageFromFile(file);
            }
        }

        private void PhotoUploadButton_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }

        private async void PhotoUploadButton_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile file)
                {
                    var extension = file.FileType.ToLower();
                    if (extension == ".jpg" || extension == ".jpeg" || extension == ".png")
                    {
                        await LoadImageFromFile(file);
                    }
                }
            }
        }

        private async Task LoadImageFromFile(StorageFile file)
        {
            using (var stream = await file.OpenReadAsync())
            {
                // 1. Mostrar en UI (XAML)
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                ProductImagePreview.Source = bitmap;

                // Mostrar panel de vista previa, ocultar icono de carga
                PhotoPreviewPanel.Visibility = Visibility.Visible;
                ((StackPanel)PhotoUploadButton.Content).Visibility = Visibility.Collapsed;

                // 2. Guardar bytes para la BD
                using (var dataReader = new DataReader(stream.GetInputStreamAt(0)))
                {
                    await dataReader.LoadAsync((uint)stream.Size);
                    _selectedPhotoBytes = new byte[stream.Size];
                    dataReader.ReadBytes(_selectedPhotoBytes);
                }
            }
        }

        private void RemovePhoto_Click(object sender, RoutedEventArgs e)
        {
            _selectedPhotoBytes = null;
            ProductImagePreview.Source = null;
            PhotoPreviewPanel.Visibility = Visibility.Collapsed;
            ((StackPanel)PhotoUploadButton.Content).Visibility = Visibility.Visible;
        }

        private decimal GetValidNumber(NumberBox numberBox)
        {
            // Validar si el NumberBox tiene un valor válido
            return numberBox != null && !double.IsNaN(numberBox.Value) && numberBox.Value > 0 ? (decimal)numberBox.Value : 0;
        }
        private async Task MostrarErrorDialog(string mensaje)
        {
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = mensaje,
                CloseButtonText = "Aceptar",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        private void LimpiarCampos()
        {
            TextBEsc.Text = string.Empty;
            ProductTypeTextBox.Text = string.Empty;
            ProductBrandTextBox.Text = string.Empty;
            ProductDescriptionTextBox.Text = string.Empty;

            // Limpiar foto
            _selectedPhotoBytes = null;
            ProductImagePreview.Source = null;
            PhotoPreviewPanel.Visibility = Visibility.Collapsed;
            if (PhotoUploadButton.Content is StackPanel sp) sp.Visibility = Visibility.Visible;

            UnidadCompradaComboBox.SelectedItem = null;
            ProductStockNB.Value = 0;
            ProductPriceCompraNumberBox.Value = 0;
        }
        // LIMITE DE LOS METODOS AGREGADOS.////////////////////////////////////////////////////////////////////////////////////////////////
        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            // Limpiar los campos
            TextBEsc.Text = string.Empty;
            ProductTypeTextBox.Text = string.Empty;
            ProductBrandTextBox.Text = string.Empty;
            ProductDescriptionTextBox.Text = string.Empty;
            ProductStockNB.Value = 0;
            ProductPriceCompraNumberBox.Value = 0;
        }
        // EDICIÓN ******************************************************************************************************
        private bool ValidarFormularioDeEdicion(ScrollViewer formularioEdicion)
        {
            if (formularioEdicion.Content is not StackPanel panel)
            {
                MostrarError("Error interno: no se encontró el contenido del formulario de edición.");
                return false;
            }

            var unidadComprada = panel.Children.OfType<TextBlock>()
                .FirstOrDefault(tb => tb.Text.StartsWith("Unidad comprada:"))?.Text.Split(":")[1].Trim();

            if (string.IsNullOrEmpty(unidadComprada))
            {
                MostrarError("No se pudo determinar la unidad comprada.");
                return false;
            }

            var precioCompra = GetValidNumber(panel.Children.OfType<NumberBox>()
                .FirstOrDefault(nb => nb.Header?.ToString().Contains("Precio de compra") == true));

            if (precioCompra <= 0)
            {
                MostrarError("El precio de compra debe ser mayor a 0.");
                return false;
            }

            var stock = GetValidNumber(panel.Children.OfType<NumberBox>()
                .FirstOrDefault(nb => nb.Header?.ToString().Contains("Stock") == true));

            if (stock <= 0)
            {
                MostrarError("La cantidad en stock debe ser mayor a 0.");
                return false;
            }

            var unidadesPanel = panel.Children.OfType<StackPanel>().LastOrDefault();
            if (unidadesPanel != null)
            {
                var checkBoxNumberBoxPairs = unidadesPanel.Children
                    .OfType<StackPanel>()
                    .Select(sp => (checkBox: sp.Children.OfType<CheckBox>().FirstOrDefault(), numberBox: sp.Children.OfType<NumberBox>().FirstOrDefault()))
                    .Where(pair => pair.checkBox != null && pair.numberBox != null);

                foreach (var (checkBox, numberBox) in checkBoxNumberBoxPairs.Where(pair => pair.checkBox.IsChecked == true))
                {
                    var unidadVenta = checkBox.Content?.ToString();
                    var precioVenta = GetValidNumber(numberBox);

                    decimal precioCompraVenta;

                    switch (unidadComprada)
                    {
                        case "Unidad(es)":
                            precioCompraVenta = precioCompra;
                            break;
                        case "Libra(s)":
                            precioCompraVenta = unidadVenta == "Onza" ? precioCompra / 16 : precioCompra;
                            break;
                        case "Ciento(s)":
                            precioCompraVenta = unidadVenta == "Unidad" ? precioCompra / 100 : precioCompra;
                            break;
                        case "Quintal(es)":
                            precioCompraVenta = unidadVenta == "Libra" ? precioCompra / 100 :
                                                unidadVenta == "Arroba" ? precioCompra / 4 :
                                                precioCompra;
                            break;
                        default:
                            MostrarError($"No se reconoce la unidad comprada: {unidadComprada}");
                            return false;
                    }

                    if (precioVenta <= precioCompraVenta)
                    {
                        MostrarError($"El precio de venta para '{unidadVenta}' debe ser mayor a {precioCompraVenta}.");
                        return false;
                    }
                }
            }

            return true;
        }

        // Helper para convertir bytes a BitmapImage (usado en Edición)
        private async Task<BitmapImage> BytesToImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;

            // QUITE EL 'using' AQUÍ PARA QUE EL STREAM VIVA LO SUFICIENTE
            var stream = new InMemoryRandomAccessStream();

            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);

            return image;
        }

        private async void OnEditButtonClick(object sender, RoutedEventArgs e)
        {
            var menuFlyoutItem = sender as MenuFlyoutItem;
            if (menuFlyoutItem != null && menuFlyoutItem.DataContext is Product selectedProduct)
            {
                // Recuperar las unidades de venta asociadas al producto
                var unidadesVenta = GetUnidadesDeVenta(selectedProduct.ProductName);
                byte[] fotoExistente = null;
                try
                {
                    using (var conn = new DatabaseConnection())
                    {
                        conn.OpenConnection();
                        var cmd = new SqliteCommand("SELECT foto FROM productos WHERE codigo_barras = @cb", conn.GetConnection());
                        cmd.Parameters.AddWithValue("@cb", selectedProduct.ProductName);

                        var dbresult = cmd.ExecuteScalar();
                        if (dbresult != null && dbresult != DBNull.Value)
                        {
                            fotoExistente = (byte[])dbresult;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Error al cargar foto: " + ex.Message);
                }

                // Crear el formulario de edición
                ContentDialog editDialog = new()
                {
                    Title = $"Editar Producto - {selectedProduct.ProductName}",
                    PrimaryButtonText = "Guardar",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };

                ScrollViewer scrollablePanel = await CrearFormularioDeEdicionAsync(selectedProduct, unidadesVenta, fotoExistente);
                editDialog.Content = scrollablePanel;

                // Mostrar el formulario
                var result = await editDialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    if (!ValidarFormularioDeEdicion(scrollablePanel)) return;
                    byte[] fotoFinal = null;

                    if (scrollablePanel.Content is StackPanel panel)
                    {
                        selectedProduct.ProductType = panel.Children.OfType<TextBox>()
                            .FirstOrDefault(tb => tb.Header?.ToString() == "Tipo del producto")?.Text;

                        selectedProduct.ProductBrand = panel.Children.OfType<TextBox>()
                            .FirstOrDefault(tb => tb.Header?.ToString() == "Marca del producto")?.Text;

                        selectedProduct.ProductDescription = panel.Children.OfType<TextBox>()
                            .FirstOrDefault(tb => tb.Header?.ToString() == "Descripción del producto")?.Text;

                        selectedProduct.ProductStock = panel.Children.OfType<NumberBox>()
                            .FirstOrDefault(nb => nb.Header?.ToString() == "Stock")?.Value.ToString();

                        selectedProduct.ProductPriceCompra = panel.Children.OfType<NumberBox>()
                            .FirstOrDefault(nb => nb.Header?.ToString() == "Precio de compra")?.Value.ToString();

                        // Buscamos el Grid de la foto (es el primer Grid hijo del panel)
                        var gridFoto = panel.Children.OfType<Grid>().FirstOrDefault();
                        if (gridFoto != null)
                        {
                            if (gridFoto.Children.Count > 1 && gridFoto.Children[1] is Button btnFoto)
                            {
                                fotoFinal = (byte[])btnFoto.Tag;
                            }
                        }
                    }

                    var unidadesPanel = ((StackPanel)((ScrollViewer)editDialog.Content).Content).Children.OfType<StackPanel>().Last();
                    var unidadesVentaEditadas = ObtenerUnidadesVentaDesdeFormulario(unidadesPanel);

                    // Guardar cambios
                    UpdateProductInDatabase(selectedProduct, selectedProduct.ProductUnidadComprada, unidadesVentaEditadas, fotoFinal);

                    LoadProducts();
                }
            }
        }

        private List<string> ObtenerUnidadesDisponiblesParaVenta(string unidadComprada)
        {
            return unidadComprada switch
            {
                "Unidad(es)" => new List<string> { "Unidad" },
                "Libra(s)" => new List<string> { "Onza", "Libra" },
                "Ciento(s)" => new List<string> { "Unidad", "Ciento" },
                "Quintal(es)" => new List<string> { "Libra", "Arroba", "Quintal" },
                _ => [] // Manejo de casos no previstos
            };
        }

        private List<(string unidad, decimal precio)> GetUnidadesDeVenta(string codigoBarras)
        {
            var unidadesVenta = new List<(string unidad, decimal precio)>();
            try
            {
                using var dbConnection = new DatabaseConnection();
                dbConnection.OpenConnection();

                string query = "SELECT unidad_venta, precio_venta FROM unidades_venta WHERE codigo_barras = @codigo_barras";
                using var command = new SqliteCommand(query, dbConnection.GetConnection());
                command.Parameters.AddWithValue("@codigo_barras", codigoBarras);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    unidadesVenta.Add((reader.GetString(0), reader.GetDecimal(1)));
                }
            }
            catch (Exception ex)
            {
                ContentDialog errorDialog = new()
                {
                    Title = "Error al recuperar unidades de venta",
                    Content = ex.Message,
                    CloseButtonText = "Aceptar",
                    XamlRoot = this.XamlRoot
                };
                _ = errorDialog.ShowAsync();
            }

            return unidadesVenta;
        }

        private List<(string unidad, decimal precio)> ObtenerUnidadesVentaDesdeFormulario(StackPanel stackUnidades)
        {
            var unidadesVenta = new List<(string unidad, decimal precio)>();

            // Recorrer los controles dinámicos en el panel
            var checkBoxNumberBoxPairs = stackUnidades.Children
                .OfType<StackPanel>()
                .Select(sp => (checkBox: sp.Children.OfType<CheckBox>().FirstOrDefault(), numberBox: sp.Children.OfType<NumberBox>().FirstOrDefault()))
                .Where(pair => pair.checkBox != null && pair.numberBox != null);

            foreach (var (checkBox, numberBox) in checkBoxNumberBoxPairs)
            {
                if (checkBox.IsChecked == true && !double.IsNaN(numberBox.Value))
                {
                    unidadesVenta.Add((checkBox.Content.ToString(), Convert.ToDecimal(numberBox.Value)));
                }
            }

            System.Diagnostics.Debug.WriteLine($"Unidades editadas: {string.Join(", ", unidadesVenta)}");
            return unidadesVenta;
        }

        private async Task<ScrollViewer> CrearFormularioDeEdicionAsync(Product selectedProduct, List<(string unidad, decimal precio)> unidadesVenta, byte[] fotoBytes)
        {
            var panel = new StackPanel { Spacing = 10, Padding = new Thickness(10) };
            // --- BLOQUE 1: FOTOGRAFÍA ---
            var gridFoto = new Grid { Height = 120, Margin = new Thickness(0, 0, 0, 10) };
            gridFoto.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            gridFoto.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var imgPreview = new Image { Width = 120, Height = 120, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };

            // AQUÍ ESTÁ LA SOLUCIÓN: Asignamos la imagen directamente esperando la conversión
            if (fotoBytes != null && fotoBytes.Length > 0)
            {
                imgPreview.Source = await BytesToImage(fotoBytes);
            }

            var btnCambiarFoto = new Button
            {
                Content = "Cambiar Foto",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 0),
                Tag = fotoBytes // Guardamos los bytes originales en el Tag por si no cambian la foto
            };
            Grid.SetColumn(btnCambiarFoto, 1);

            // Evento cambiar foto
            btnCambiarFoto.Click += async (s, e) =>
            {
                var picker = new FileOpenPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".png");

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    using (var stream = await file.OpenReadAsync())
                    {
                        // Actualizar visualmente
                        var bmp = new BitmapImage(); await bmp.SetSourceAsync(stream);
                        imgPreview.Source = bmp;

                        // Actualizar datos en memoria (Tag)
                        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                        {
                            await reader.LoadAsync((uint)stream.Size);
                            var bytes = new byte[stream.Size];
                            reader.ReadBytes(bytes);
                            btnCambiarFoto.Tag = bytes;
                        }
                    }
                }
            };

            gridFoto.Children.Add(imgPreview);
            gridFoto.Children.Add(btnCambiarFoto);
            panel.Children.Add(new TextBlock { Text = "Fotografía:", FontWeight = FontWeights.Bold });
            panel.Children.Add(gridFoto);

            // Campos del producto principal
            var tipoTextBox = new TextBox
            {
                Text = selectedProduct.ProductType,
                Header = "Tipo del producto",
                MaxLength = 15
            };
            tipoTextBox.TextChanged += TextVA1_TextChanging; // Vincular el evento TextChanged dinámicamente
            panel.Children.Add(tipoTextBox);
            var marcaTextBox = new TextBox
            {
                Text = selectedProduct.ProductBrand,
                Header = "Marca del producto",
                MaxLength = 15
            };
            marcaTextBox.TextChanged += TextVA1_TextChanging; // Vincular el evento TextChanged dinámicamente
            panel.Children.Add(marcaTextBox);
            var descripcionTextBox = new TextBox
            {
                Text = selectedProduct.ProductDescription,
                Header = "Descripción del producto",
                MaxLength = 150
            };
            descripcionTextBox.TextChanged += TextVA1_TextChanging; // Vincular el evento TextChanged dinámicamente
            panel.Children.Add(descripcionTextBox);

            // Mostrar la unidad comprada como texto
            panel.Children.Add(new TextBlock
            {
                Text = $"Unidad comprada: {selectedProduct.ProductUnidadComprada}",
                FontWeight = FontWeights.Bold
            });

            // Stock y precio de compra
            var stockNumberBox = new NumberBox
            {
                Header = "Stock",
                Value = Convert.ToDouble(decimal.Round(decimal.Parse(selectedProduct.ProductStock), 3)),
                Minimum = 0.001,
                Maximum = 9999
            };
            stockNumberBox.ValueChanged += (s, e) =>
            {
                // Limitar a 3 decimales
                stockNumberBox.Value = Math.Round(stockNumberBox.Value, 3);
            };
            panel.Children.Add(stockNumberBox);

            panel.Children.Add(new NumberBox
            {
                Header = "Precio de compra",
                Value = Convert.ToDouble(selectedProduct.ProductPriceCompra),
                Minimum = 0.01,
                Maximum = 9999
            });

            // Sección de unidades de venta
            panel.Children.Add(new TextBlock { Text = "Unidades de venta y precios:", FontWeight = FontWeights.Bold });
            var stackUnidades = new StackPanel { Orientation = Orientation.Vertical };

            // Generar los CheckBoxes y NumberBoxes según la unidad comprada
            var unidadesDisponibles = ObtenerUnidadesDisponiblesParaVenta(selectedProduct.ProductUnidadComprada);
            foreach (var unidad in unidadesDisponibles)
            {
                var checkBox = new CheckBox
                {
                    Content = unidad,
                    IsChecked = unidadesVenta.Any(u => u.unidad == unidad),
                    IsEnabled = true // Activado por defecto
                };

                var numberBox = new NumberBox
                {
                    Header = $"Precio para {unidad}",
                    Value = Convert.ToDouble(unidadesVenta.FirstOrDefault(u => u.unidad == unidad).precio),
                    IsEnabled = checkBox.IsChecked ?? false
                };

                checkBox.Checked += (_, _) => numberBox.IsEnabled = true;
                checkBox.Unchecked += (_, _) =>
                {
                    var seleccionados = stackUnidades.Children.OfType<StackPanel>()
                    .Select(sp => sp.Children.OfType<CheckBox>().FirstOrDefault())
                    .Count(cb => cb.IsChecked == true);

                    if (seleccionados == 0) // Si no hay ninguno seleccionado
                    {
                        checkBox.IsChecked = true; // Revertir el cambio
                    }
                    else
                    {
                        numberBox.IsEnabled = false; // Deshabilitar el NumberBox si se permite la deselección
                    }
                };

                var unidadPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                unidadPanel.Children.Add(checkBox);
                unidadPanel.Children.Add(numberBox);

                stackUnidades.Children.Add(unidadPanel);
            }

            panel.Children.Add(stackUnidades);

            // Envolver el StackPanel en un ScrollViewer
            return new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private void UpdateProductInDatabase(Product product, string unidadComprada, List<(string unidad, decimal precio)> unidadesVentaEditadas, byte[]? foto)
        {
            try
            {
                using var dbConnection = new DatabaseConnection();
                dbConnection.OpenConnection();

                // Actualizar la tabla productos
                string updateProductQuery = @"UPDATE productos SET tipo = @tipo, marca = @marca, descripcion = @descripcion, unidad_comprada = @unidad_comprada, 
                stock = @stock, preciocompra = @preciocompra, foto = @foto WHERE codigo_barras = @codigo_barras";

                using var productCommand = new SqliteCommand(updateProductQuery, dbConnection.GetConnection());
                {
                    productCommand.Parameters.AddWithValue("@codigo_barras", product.ProductName);
                    productCommand.Parameters.AddWithValue("@tipo", product.ProductType);
                    productCommand.Parameters.AddWithValue("@marca", product.ProductBrand);
                    productCommand.Parameters.AddWithValue("@descripcion", product.ProductDescription);
                    productCommand.Parameters.AddWithValue("@unidad_comprada", unidadComprada);
                    productCommand.Parameters.AddWithValue("@stock", Convert.ToDecimal(product.ProductStock));
                    productCommand.Parameters.AddWithValue("@preciocompra", Convert.ToDecimal(product.ProductPriceCompra));
                    productCommand.Parameters.AddWithValue("@foto", foto ?? (object)DBNull.Value);

                    productCommand.ExecuteNonQuery();
                }

                // Borrar unidades de venta existentes
                string deleteUnitsQuery = "DELETE FROM unidades_venta WHERE codigo_barras = @codigo_barras";
                using (var deleteCommand = new SqliteCommand(deleteUnitsQuery, dbConnection.GetConnection()))
                {
                    deleteCommand.Parameters.AddWithValue("@codigo_barras", product.ProductName);
                    deleteCommand.ExecuteNonQuery();
                }

                // Insertar unidades de venta actualizadas
                string insertUnitQuery = @"INSERT INTO unidades_venta (codigo_barras, unidad_venta, precio_venta) 
                                           VALUES (@codigo_barras, @unidad_venta, @precio_venta)";
                foreach (var (unidad, precio) in unidadesVentaEditadas)
                {
                    if (string.IsNullOrWhiteSpace(unidad) || precio <= 0)
                        throw new ArgumentException("Las unidades de venta deben tener una unidad válida y un precio mayor a 0.");

                    using var unitCommand = new SqliteCommand(insertUnitQuery, dbConnection.GetConnection());
                    {
                        System.Diagnostics.Debug.WriteLine($"Insertando unidad: {unidad}, Precio: {precio}");
                        unitCommand.Parameters.AddWithValue("@codigo_barras", product.ProductName);
                        unitCommand.Parameters.AddWithValue("@unidad_venta", unidad);
                        unitCommand.Parameters.AddWithValue("@precio_venta", precio);
                        unitCommand.ExecuteNonQuery();
                    }
                }

                CheckProductStock();
            }
            catch (Exception ex)
            {
                ContentDialog errorDialog = new()
                {
                    Title = "Error al actualizar",
                    Content = ex.Message,
                    CloseButtonText = "Aceptar",
                    XamlRoot = this.XamlRoot
                };
                _ = errorDialog.ShowAsync();
            }
        }

        //BORRAR PRODUCTO ******************
        private async void OnDeleteButtonClick(object sender, RoutedEventArgs e)
        {
            var menuFlyoutItem = sender as MenuFlyoutItem;
            if (menuFlyoutItem != null && (menuFlyoutItem.DataContext is Product selectedProduct))
            {
                ContentDialog deleteDialog = new()
                {
                    Title = $"Eliminar Producto - {selectedProduct.ProductName}",
                    Content = "¿Está seguro de que desea eliminar este producto?",
                    PrimaryButtonText = "Eliminar",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                var result = await deleteDialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    // Eliminar el producto de la base de datos
                    DeleteProductFromDatabase(selectedProduct);

                    // Recargar la lista de productos
                    LoadProducts();
                }
            }
        }
        private void DeleteProductFromDatabase(Product product)
        {
            try
            {
                using var dbConnection = new DatabaseConnection();
                {
                    dbConnection.OpenConnection();
                    string query = "DELETE FROM productos WHERE codigo_barras = @codigo_barras";

                    using var command = new SqliteCommand(query, dbConnection.GetConnection());
                    {
                        command.Parameters.AddWithValue("@codigo_barras", product.ProductName);
                        command.ExecuteNonQuery();
                    }
                }

                CheckProductStock();
            }
            catch (Exception ex)
            {
                ContentDialog dialog = new()
                {
                    Title = "Error",
                    Content = $"Error al eliminar el producto: {ex.Message}",
                    CloseButtonText = "Ok",
                    XamlRoot = this.XamlRoot
                };
                _ = dialog.ShowAsync(); // Mostrar el diálogo de error
            }
        }
        private void OnFilterChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }
        private void ApplyFilters()
        {
            // Obtener los valores de los filtros
            string barcodeFilter = FilterByBarcode.Text.ToLower();
            string typeFilter = FilterByType.Text.ToLower();
            string brandFilter = FilterByBrand.Text.ToLower();
            string ucFilter = FilterByUC.Text.ToLower();

            // Filtrar los productos en base a los valores de los filtros
            var filteredProducts = products.Where(product =>
                (string.IsNullOrEmpty(barcodeFilter) || product.ProductName.ToLower().Contains(barcodeFilter)) &&
                (string.IsNullOrEmpty(typeFilter) || product.ProductType.ToLower().Contains(typeFilter)) &&
                (string.IsNullOrEmpty(brandFilter) || product.ProductBrand.ToLower().Contains(brandFilter)) &&
                (string.IsNullOrEmpty(ucFilter) || product.ProductUnidadComprada.ToLower().Contains(ucFilter))
            ).ToList();

            if (filteredProducts.Count != 0)
            {
                StockList.ItemsSource = filteredProducts;
                StockList.Visibility = Visibility.Visible;
                NoMatchesTextBlock.Visibility = Visibility.Collapsed;
            }
            else
            {
                StockList.ItemsSource = null;
                StockList.Visibility = Visibility.Collapsed;
                NoMatchesTextBlock.Visibility = Visibility.Visible;
            }
            var cultureInfo = new System.Globalization.CultureInfo("es-GT");
            TotalCapitalTextBlock.Text = "Capital Total: Q." + CalculateTotalCapital().ToString("F2", cultureInfo);
        }
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
            if (productosConStockBajo == null)
                productosConStockBajo = new List<string>();

            // Eliminar notificaciones para productos que no están en la lista de stock bajo
            string deleteQuery = $@"
                DELETE FROM notificaciones 
                WHERE codigo_barras NOT IN ({string.Join(", ", productosConStockBajo.Select((_, index) => $"@p{index}"))})";

            using var deleteCommand = new SqliteCommand(deleteQuery, dbConnection.GetConnection());

            for (int i = 0; i < productosConStockBajo.Count; i++)
            {
                deleteCommand.Parameters.AddWithValue($"@p{i}", productosConStockBajo[i]);
            }

            deleteCommand.ExecuteNonQuery();
        }
    }
}
