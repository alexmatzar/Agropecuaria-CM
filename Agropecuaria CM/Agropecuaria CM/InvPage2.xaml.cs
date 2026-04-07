using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Windows.ApplicationModel.DataTransfer;
using Agropecuaria_CM.Models;
using Windows.Storage.Streams;

namespace Agropecuaria_CM
{
    public sealed partial class InvPage2 : Page
    {
        public InvPage2()
        {
            this.InitializeComponent();
            this.Loaded += InvPage2_Loaded;
        }

        private async void InvPage2_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadProductsAsync();
        }

        private async Task LoadProductsAsync()
        {
            try
            {
                UnitsStackPanel.Children.Clear(); // Limpiar la pantalla

                using (var dbConnection = new DatabaseConnection())
                {
                    dbConnection.OpenConnection();

                    // Consultamos la foto también (p.foto)
                    string query = @"
                        SELECT p.codigo_barras, p.tipo, p.marca, p.descripcion, p.unidad_comprada, p.stock,
                               u.unidad_venta, u.precio_venta, p.foto 
                        FROM productos p
                        INNER JOIN unidades_venta u ON p.codigo_barras = u.codigo_barras
                        ORDER BY u.unidad_venta;";

                    using var command = new SqliteCommand(query, dbConnection.GetConnection());
                    using var reader = await command.ExecuteReaderAsync();

                    var grouped = new Dictionary<string, List<Product>>();

                    while (await reader.ReadAsync())
                    {
                        var product = new Product
                        {
                            ProductName = reader.GetString(0),
                            ProductType = reader.GetString(1),
                            ProductBrand = reader.GetString(2),
                            ProductDescription = reader.GetString(3),
                            ProductUnidadComprada = reader.GetString(4),
                            ProductStock = reader.GetDecimal(5).ToString(),
                            UnidadVenta = reader.GetString(6),
                            PrecioVenta = reader.GetDecimal(7)
                        };

                        // LEER LA FOTO (BLOB)
                        if (!reader.IsDBNull(8))
                        {
                            using (var stream = reader.GetStream(8))
                            using (var memoryStream = new MemoryStream())
                            {
                                await stream.CopyToAsync(memoryStream);
                                product.FotoBytes = memoryStream.ToArray();
                            }
                        }

                        if (!grouped.ContainsKey(product.UnidadVenta))
                            grouped[product.UnidadVenta] = new List<Product>();

                        grouped[product.UnidadVenta].Add(product);
                    }

                    // GENERAR LA INTERFAZ ESTILO MICROSOFT STORE
                    foreach (var grupo in grouped)
                    {
                        // 1. Título de la categoría (Unidad)
                        var title = new TextBlock
                        {
                            Text = grupo.Key.ToUpperInvariant(),
                            FontSize = 22,
                            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                            Margin = new Thickness(8, 15, 0, 10)
                        };
                        UnitsStackPanel.Children.Add(title);

                        // 2. Panel Horizontal (Scroll de productos)
                        var scrollViewerHorizontal = new ScrollViewer
                        {
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                        };

                        var productsPanel = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Spacing = 15,
                            Padding = new Thickness(0, 0, 0, 10) // Espacio para la sombra
                        };

                        foreach (var product in grupo.Value)
                        {
                            var card = await CreateProductCard(product);
                            productsPanel.Children.Add(card);
                        }

                        scrollViewerHorizontal.Content = productsPanel;
                        UnitsStackPanel.Children.Add(scrollViewerHorizontal);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cargando inventario: {ex.Message}");
            }
        }

        private async Task<StackPanel> CreateProductCard(Product product)
        {
            // Tarjeta contenedora
            var productPanel = new StackPanel
            {
                Width = 160,
                Margin = new Thickness(0, 0, 0, 10),
                Tag = product, // Guardamos el objeto completo aquí
                ContextFlyout = CreateContextMenu(product) // Click derecho para copiar
            };

            // Contenedor de Imagen (Border Redondeado)
            var imageBorder = new Border
            {
                Width = 160,
                Height = 160,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue), // Fondo por defecto
                // Sombra suave (opcional, requiere WinUI avanzado, lo dejamos simple por ahora)
            };

            if (product.HasPhoto)
            {
                var image = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    // OPTIMIZACIÓN DE RAM CRÍTICA:
                    // Le decimos que decodifique la imagen solo a 160px.
                    // Si la foto es 4K, WinUI la encogerá en RAM ahorrando muchísima memoria.
                    Source = await BytesToImage(product.FotoBytes, decodeWidth: 160)
                };
                imageBorder.Child = image;
            }
            else
            {
                // Fallback si no hay foto: Mostrar iniciales o nombre
                imageBorder.Child = new TextBlock
                {
                    Text = product.ProductName,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(5),
                    FontSize = 12
                };
            }

            // Datos Texto
            var brandText = new TextBlock
            {
                Text = product.ProductBrand,
                Margin = new Thickness(2, 8, 2, 0),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 14
            };

            var typeText = new TextBlock
            {
                Text = product.ProductType,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"], // Color grisáceo del tema
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var priceText = new TextBlock
            {
                Text = $"Q{product.PrecioVenta:F2}",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"], // Color de acento
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };

            productPanel.Children.Add(imageBorder);
            productPanel.Children.Add(brandText);
            productPanel.Children.Add(typeText);
            productPanel.Children.Add(priceText);

            // Evento Click para la animación
            productPanel.Tapped += ProductPanel_Tapped;

            return productPanel;
        }

        // --- ANIMACIÓN CONECTADA Y NAVEGACIÓN ---
        private void ProductPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is StackPanel panel && panel.Tag is Product product)
            {
                // El elemento que se "moverá" es el Border con la imagen (el primer hijo)
                UIElement connectedElement = panel.Children[0];

                // Preparamos la animación con la clave "forwardAnimation"
                ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("forwardAnimation", connectedElement);

                // Navegamos al detalle (ProductDetailPage)
                Frame.Navigate(typeof(ProductDetailPage), product, new SuppressNavigationTransitionInfo());
            }
        }

        // --- BÚSQUEDA ---
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Nota: Con la nueva UI agrupada, el filtrado es más complejo porque
            // tendríamos que reconstruir los grupos. 
            // Si quieres implementarlo después, avísame. Por ahora recarga todo.
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _ = LoadProductsAsync();
            }
        }

        // --- UTILIDADES ---
        private MenuFlyout CreateContextMenu(Product product)
        {
            var menu = new MenuFlyout();
            var copyItem = new MenuFlyoutItem
            {
                Text = "Copiar Código de Barras",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyItem.Click += (s, e) =>
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(product.ProductName);
                Clipboard.SetContent(dataPackage);
            };
            menu.Items.Add(copyItem);
            return menu;
        }

        // Helper optimizado con DecodePixelWidth
        private async Task<BitmapImage> BytesToImage(byte[] bytes, int decodeWidth)
        {
            if (bytes == null || bytes.Length == 0) return null;

            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var image = new BitmapImage();
            // ESTA LÍNEA ES LA CLAVE PARA AHORRAR RAM:
            image.DecodePixelWidth = decodeWidth;
            await image.SetSourceAsync(stream);
            return image;
        }
    }
}
