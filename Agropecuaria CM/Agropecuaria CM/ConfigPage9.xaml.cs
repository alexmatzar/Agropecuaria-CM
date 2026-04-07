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
using System.Text.Json;
using Windows.Storage;
using Firebase.Auth;
using System.Threading.Tasks;

namespace Agropecuaria_CM
{
    public sealed partial class ConfigPage9 : Page
    {
        private ShellPage _shellPage;
        public ConfigPage9()
        {
            this.InitializeComponent();
            VerificarEstado();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is ShellPage shellPage)
            {
                _shellPage = shellPage;
            }
        }
        private async Task<bool> VerificarPinAsync()
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

            if (!localSettings.Values.ContainsKey("WindowAccessPin"))
            {
                await MostrarErrorDialog("Error", "No se ha definido un PIN.");
                return false;
            }

            // Pedir el PIN
            var pinInput = new PasswordBox { PlaceholderText = "PIN", MaxLength = 6, FontSize = 20, Width = 130 };

            pinInput.PasswordChanging += (s, e) =>
            {
                var box = s as PasswordBox;
                string input = box.Password;

                // Permitir solo números, eliminando cualquier otro carácter
                if (!int.TryParse(input, out _))
                {
                    box.Password = new string(input.Where(char.IsDigit).ToArray());
                }
            };

            var pinDialog = new ContentDialog
            {
                Title = "Introduzca el PIN",
                Content = new StackPanel
                {
                    Children =
            {
                        new TextBlock { Text = "Introduce tu PIN para continuar", Margin = new Thickness(0,0,0,10) },
                        pinInput
            }
                },
                PrimaryButtonText = "Confirmar",
                CloseButtonText = "Cancelar",
                XamlRoot = this.Content.XamlRoot
            };

            var pinResult = await pinDialog.ShowAsync();

            if (pinResult == ContentDialogResult.Primary && !string.IsNullOrEmpty(pinInput.Password))
            {
                string savedPin = localSettings.Values["WindowAccessPin"]?.ToString();

                // Verificar si el PIN es correcto
                if (pinInput.Password == savedPin)
                {
                    return true;
                }
                else
                {
                    await MostrarErrorDialog("PIN Incorrecto", "El PIN introducido es incorrecto.");
                    return false;
                }
            }

            return false;
        }
        private async void SelectBackgroundImage_Click(object sender, RoutedEventArgs e)
        {
            if (!await VerificarPinAsync()) return;

            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary
            };

            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow);

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);

            Windows.Storage.StorageFile selectedImage = await picker.PickSingleFileAsync();

            if (selectedImage != null)
            {
                var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                string newFileName = $"Background_{DateTime.Now.Ticks}{selectedImage.FileType}";
                Windows.Storage.StorageFile copiedImage = await selectedImage.CopyAsync(localFolder, newFileName, Windows.Storage.NameCollisionOption.ReplaceExisting);
                var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
                localSettings.Values["BackgroundImage"] = copiedImage.Path;

                _ = _shellPage?.ActualizarFondo();
            }
        }

        private void VerificarEstado()
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            bool pinExistente = localSettings.Values.ContainsKey("WindowAccessPin");

            PinAccessButton.Content = pinExistente ? "Cambiar la clave de acceso a ventanas" : "Definir una clave para el acceso a ventanas";
        }

        private async void PinAccessButton_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = ApplicationData.Current.LocalSettings;

            // Solicitar la contraseña del usuario logueado para verificar
            var passwordInput = new PasswordBox { PlaceholderText = "Contraseña de Comercial Mendoza", Width = 250 };
            var passwordDialog = new ContentDialog
            {
                Title = "Verificación de contraseña",
                Content = new StackPanel
                {
                    Children =
            {
                        new TextBlock { Text = "Introduce la contraseña para continuar:", Margin = new Thickness(0,0,0,10) },
                        passwordInput
            }
                },
                PrimaryButtonText = "Confirmar",
                CloseButtonText = "Cancelar",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await passwordDialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrEmpty(passwordInput.Password))
            {
                string storedEmail = localSettings.Values["UserEmail"]?.ToString();
                try
                {
                    // Verificar la contraseña con Firebase
                    var credential = await App.AuthClient.SignInWithEmailAndPasswordAsync(storedEmail, passwordInput.Password);

                    // Solicitar el PIN si la autenticación es correcta
                    await SolicitarPinYVentanas();
                }
                catch (FirebaseAuthException)
                {
                    await MostrarErrorDialog("Error de autenticación", "Contraseña incorrecta.");
                }
            }
        }

        private async Task SolicitarPinYVentanas()
        {
            // ContentDialog para definir o cambiar el PIN
            var pinInput1 = new PasswordBox { PlaceholderText = "Introduce el PIN (4-6 dígitos)", MaxLength = 6 };
            var pinInput2 = new PasswordBox { PlaceholderText = "Confirma el PIN", MaxLength = 6 };

            var pinDialog = new ContentDialog
            {
                Title = "Definir PIN",
                Content = new StackPanel
                {
                    Children =
            {
                        new TextBlock { Text = "Introduce un PIN para el acceso a las ventanas protegidas:" },
                        pinInput1,
                        pinInput2
            }
                },
                PrimaryButtonText = "Guardar",
                CloseButtonText = "Cancelar",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await pinDialog.ShowAsync();
            if (result == ContentDialogResult.Primary && pinInput1.Password == pinInput2.Password && pinInput1.Password.Length >= 4)
            {
                // Guardar el PIN en LocalSettings
                var localSettings = ApplicationData.Current.LocalSettings;
                localSettings.Values["WindowAccessPin"] = pinInput1.Password;

                // Ahora mostrar las opciones de ventanas a bloquear
                await SeleccionarVentanasABloquear();
            }
            else if (pinInput1.Password != pinInput2.Password)
            {
                await MostrarErrorDialog("Error", "Los PIN no coinciden.");
            }
            else
            {
                await MostrarErrorDialog("Error", "El PIN debe tener entre 4 y 6 dígitos.");
            }
        }

        private async Task SeleccionarVentanasABloquear()
        {
            // Checkboxes para seleccionar las ventanas a bloquear
            var checkbox1 = new CheckBox { Content = "Inventario Compras" };
            var checkbox2 = new CheckBox { Content = "Inventario NIT" };
            var checkbox3 = new CheckBox { Content = "Historial de ventas" };
            var checkbox4 = new CheckBox { Content = "Plantilla de facturas" };
            var checkbox5 = new CheckBox { Content = "Estadísticas de ventas" };
            var selectAll = new CheckBox { Content = "Seleccionar todos" };

            selectAll.Checked += (s, e) =>
            {
                checkbox1.IsChecked = true;
                checkbox2.IsChecked = true;
                checkbox3.IsChecked = true;
                checkbox4.IsChecked = true;
                checkbox5.IsChecked = true;
            };

            var ventanaDialog = new ContentDialog
            {
                Title = "Seleccionar ventanas a bloquear",
                Content = new StackPanel
                {
                    Children = { selectAll, checkbox1, checkbox2, checkbox3, checkbox4, checkbox5 }
                },
                PrimaryButtonText = "Guardar",
                CloseButtonText = "Cancelar",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await ventanaDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                // Guardar las ventanas seleccionadas
                var localSettings = ApplicationData.Current.LocalSettings;
                localSettings.Values["BloquearPurPage3"] = checkbox1.IsChecked ?? false;
                localSettings.Values["BloquearNitPage5"] = checkbox2.IsChecked ?? false;
                localSettings.Values["BloquearVentasPage5"] = checkbox3.IsChecked ?? false;
                localSettings.Values["BloquearFactPage6"] = checkbox4.IsChecked ?? false;
                localSettings.Values["BloquearDashPage7"] = checkbox5.IsChecked ?? false;
            }
        }

        private async void ChangeWindowsSecret_Click(object sender, RoutedEventArgs e)
        {
            if (!await VerificarPinAsync()) return;

            await EditarSeguridadDeVentanas();
        }

        private async Task EditarSeguridadDeVentanas()
        {
            // Mismo ContentDialog con los checkboxes para seleccionar las ventanas a bloquear
            var checkbox1 = new CheckBox { Content = "Ventana de Gestión de Compras" };
            var checkbox2 = new CheckBox { Content = "Ventana de Gestión de números NIT" };
            var checkbox3 = new CheckBox { Content = "Ventana de Historial de Ventas" };
            var checkbox4 = new CheckBox { Content = "Ventana de Plantilla de Comprobantes" };
            var checkbox5 = new CheckBox { Content = "Ventana de Estadísticas de Ventas" };

            // Cargar valores actuales de las ventanas bloqueadas desde LocalSettings
            var localSettings = ApplicationData.Current.LocalSettings;
            checkbox1.IsChecked = (bool)(localSettings.Values["BloquearPurPage3"] ?? false);
            checkbox2.IsChecked = (bool)(localSettings.Values["BloquearNitPage5"] ?? false);
            checkbox3.IsChecked = (bool)(localSettings.Values["BloquearVentasPage5"] ?? false);
            checkbox4.IsChecked = (bool)(localSettings.Values["BloquearFactPage6"] ?? false);
            checkbox5.IsChecked = (bool)(localSettings.Values["BloquearDashPage7"] ?? false);

            var ventanaDialog = new ContentDialog
            {
                Title = "Editar seguridad de ventanas",
                Content = new StackPanel
                {
                    Children = { checkbox1, checkbox2, checkbox3, checkbox4, checkbox5 }
                },
                PrimaryButtonText = "Guardar",
                CloseButtonText = "Cancelar",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await ventanaDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                // Guardar las nuevas selecciones en LocalSettings
                localSettings.Values["BloquearPurPage3"] = checkbox1.IsChecked ?? false;
                localSettings.Values["BloquearNitPage5"] = checkbox2.IsChecked ?? false;
                localSettings.Values["BloquearVentasPage5"] = checkbox3.IsChecked ?? false;
                localSettings.Values["BloquearFactPage6"] = checkbox4.IsChecked ?? false;
                localSettings.Values["BloquearDashPage7"] = checkbox5.IsChecked ?? false;
            }
        }

        private async Task MostrarErrorDialog(string titulo, string mensaje)
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

        private async void RealizarCopiaSeguridad_Click(object sender, RoutedEventArgs e)
        {
            if (!await VerificarPinAsync()) return;

            try
            {
                // Usar FileSavePicker para que el usuario elija dónde guardar la copia
                var savePicker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
                };

                savePicker.FileTypeChoices.Add("Base de datos SQLite", new List<string>() { ".db" });
                savePicker.SuggestedFileName = $"CopiaSeguridad_{DateTime.Now:yyyyMMddHHmmss}.db";

                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);

                Windows.Storage.StorageFile backupFile = await savePicker.PickSaveFileAsync();

                if (backupFile != null)
                {
                    string dbName = "AgroComercialMendoza.db";
                    string sourcePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, dbName);

                    // Verificar si la base de datos existe antes de intentar copiarla
                    if (!File.Exists(sourcePath))
                    {
                        await MostrarErrorDialog("Error", "No se encuentra la base de datos original.");
                        return;
                    }

                    // 4. Ejecutar el backup seguro (Online Backup API de SQLite)
                    // Esto funciona incluso si la app está usando la base de datos en ese momento.
                    using (var source = new SqliteConnection($"Data Source={sourcePath}"))
                    using (var destination = new SqliteConnection($"Data Source={backupFile.Path}"))
                    {
                        await source.OpenAsync();
                        await destination.OpenAsync();

                        // Este comando mágico consolida el archivo .db, el .wal y el .shm en un solo archivo de destino
                        source.BackupDatabase(destination);
                    }

                    //await MostrarResultadoDialog("Éxito", "La copia de seguridad se guardó correctamente. \n\nPuedes abrir este archivo con SQLite Studio.");
                }
                else
                {
                    await MostrarErrorDialog("Cancelado", "No se seleccionó ninguna ubicación para la copia de seguridad.");
                }
            }
            catch (Exception ex)
            {
                await MostrarErrorDialog("Error al realizar la copia", $"Ocurrió un error: {ex.Message}");
            }
        }
        private async void RestaurarCopiaSeguridad_Click(object sender, RoutedEventArgs e)
        {
            if (!await VerificarPinAsync()) return;

            // Mostrar un cuadro de diálogo para seleccionar el archivo de copia de seguridad
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary // Cambiar si prefieres otra carpeta
            };
            picker.FileTypeFilter.Add(".db");

            // Inicializar el picker con la ventana principal
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);

            // Obtener el archivo seleccionado
            Windows.Storage.StorageFile selectedFile = await picker.PickSingleFileAsync();

            if (selectedFile != null)
            {
                // Cerrar la conexión a la base de datos actual antes de fusionar o sobrescribir
                var dbConnection = new DatabaseConnection();
                dbConnection.CloseConnection();

                ContentDialog decisionDialog = new()
                {
                    Title = "Restaurar Datos",
                    Content = "Si tienes datos existentes en la base de datos, se fusionarán con esta copia de seguridad.",
                    PrimaryButtonText = "Fusionar",
                    CloseButtonText = "Cancelar",
                    XamlRoot = this.Content.XamlRoot
                };

                var decisionResult = await decisionDialog.ShowAsync();

                if (decisionResult == ContentDialogResult.Primary)
                {
                    await FusionarBasesDeDatosAsync(selectedFile.Path);
                }
            }
            else
            {
                await MostrarErrorDialog("Error", "No se seleccionó ningún archivo.");
            }
        }
        private async Task FusionarBasesDeDatosAsync(string backupPath)
        {
            try
            {
                using var backupConnection = new SqliteConnection($"Data Source={backupPath}");
                using var currentConnection = new DatabaseConnection();
                {
                    backupConnection.Open();
                    currentConnection.OpenConnection();

                    // Fusionar tabla productos
                    string selectProductosQuery = "SELECT codigo_barras, tipo, marca, descripcion, unidad_comprada, stock, preciocompra FROM productos";
                    SqliteCommand selectProductosCommand = new(selectProductosQuery, backupConnection);
                    SqliteDataReader productosReader = selectProductosCommand.ExecuteReader();

                    while (productosReader.Read())
                    {
                        string insertOrReplaceProductosQuery = @"
                    INSERT OR REPLACE INTO productos (codigo_barras, tipo, marca, descripcion, unidad_comprada, stock, preciocompra)
                    VALUES (@codigo_barras, @tipo, @marca, @descripcion, @unidad_comprada, @stock, @preciocompra)";

                        SqliteCommand insertOrReplaceProductosCommand = new(insertOrReplaceProductosQuery, currentConnection.GetConnection());
                        insertOrReplaceProductosCommand.Parameters.AddWithValue("@codigo_barras", productosReader["codigo_barras"]);
                        insertOrReplaceProductosCommand.Parameters.AddWithValue("@tipo", productosReader["tipo"]);
                        insertOrReplaceProductosCommand.Parameters.AddWithValue("@marca", productosReader["marca"]);
                        insertOrReplaceProductosCommand.Parameters.AddWithValue("@descripcion", productosReader["descripcion"]);
                        insertOrReplaceProductosCommand.Parameters.AddWithValue("@unidad_comprada", productosReader["unidad_comprada"]);
                        insertOrReplaceProductosCommand.Parameters.AddWithValue("@stock", productosReader["stock"]);
                        insertOrReplaceProductosCommand.Parameters.AddWithValue("@preciocompra", productosReader["preciocompra"]);

                        insertOrReplaceProductosCommand.ExecuteNonQuery();
                    }

                    productosReader.Close();

                    // Fusionar tabla unidades_venta
                    string selectUnidadesVentaQuery = "SELECT codigo_barras, unidad_venta, precio_venta FROM unidades_venta";
                    SqliteCommand selectUnidadesVentaCommand = new(selectUnidadesVentaQuery, backupConnection);
                    SqliteDataReader unidadesVentaReader = selectUnidadesVentaCommand.ExecuteReader();

                    while (unidadesVentaReader.Read())
                    {
                        string insertOrReplaceUnidadesVentaQuery = @"
                    INSERT OR REPLACE INTO unidades_venta (codigo_barras, unidad_venta, precio_venta)
                    VALUES (@codigo_barras, @unidad_venta, @precio_venta)";

                        SqliteCommand insertOrReplaceUnidadesVentaCommand = new(insertOrReplaceUnidadesVentaQuery, currentConnection.GetConnection());
                        insertOrReplaceUnidadesVentaCommand.Parameters.AddWithValue("@codigo_barras", unidadesVentaReader["codigo_barras"]);
                        insertOrReplaceUnidadesVentaCommand.Parameters.AddWithValue("@unidad_venta", unidadesVentaReader["unidad_venta"]);
                        insertOrReplaceUnidadesVentaCommand.Parameters.AddWithValue("@precio_venta", unidadesVentaReader["precio_venta"]);

                        insertOrReplaceUnidadesVentaCommand.ExecuteNonQuery();
                    }

                    unidadesVentaReader.Close();

                    // Fusionar tabla clientes
                    string selectClientesQuery = "SELECT nit, nombre FROM clientes";
                    SqliteCommand selectClientesCommand = new(selectClientesQuery, backupConnection);
                    SqliteDataReader clientesReader = selectClientesCommand.ExecuteReader();

                    while (clientesReader.Read())
                    {
                        string insertOrReplaceClientesQuery = @"
                    INSERT OR REPLACE INTO clientes (nit, nombre)
                    VALUES (@nit, @nombre)";

                        SqliteCommand insertOrReplaceClientesCommand = new(insertOrReplaceClientesQuery, currentConnection.GetConnection());
                        insertOrReplaceClientesCommand.Parameters.AddWithValue("@nit", clientesReader["nit"]);
                        insertOrReplaceClientesCommand.Parameters.AddWithValue("@nombre", clientesReader["nombre"]);

                        insertOrReplaceClientesCommand.ExecuteNonQuery();
                    }

                    clientesReader.Close();

                    // Fusionar tabla ventas
                    string selectVentasQuery = "SELECT codigo_id, ventashechas, productosvendidos, total, totalganancias, monto, cambio, nitcliente, nombrecliente, fecha, hora FROM ventas";
                    SqliteCommand selectVentasCommand = new(selectVentasQuery, backupConnection);
                    SqliteDataReader ventasReader = selectVentasCommand.ExecuteReader();

                    while (ventasReader.Read())
                    {
                        string insertOrReplaceVentasQuery = @"
                    INSERT OR REPLACE INTO ventas (codigo_id, ventashechas, productosvendidos, total, totalganancias, monto, cambio, nitcliente, nombrecliente, fecha, hora)
                    VALUES (@codigo_id, @ventashechas, @productosvendidos, @total, @totalganancias, @monto, @cambio, @nitcliente, @nombrecliente, @fecha, @hora)";

                        SqliteCommand insertOrReplaceVentasCommand = new(insertOrReplaceVentasQuery, currentConnection.GetConnection());
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@codigo_id", ventasReader["codigo_id"]);
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@ventashechas", ventasReader["ventashechas"]);
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@productosvendidos", ventasReader["productosvendidos"]);
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@total", ventasReader["total"]);
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@totalganancias", ventasReader["totalganancias"]);
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@monto", ventasReader["monto"]);
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@cambio", ventasReader["cambio"]);
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@nitcliente", ventasReader["nitcliente"]);
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@nombrecliente", ventasReader["nombrecliente"]);
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@fecha", ventasReader["fecha"]);
                        insertOrReplaceVentasCommand.Parameters.AddWithValue("@hora", ventasReader["hora"]);

                        insertOrReplaceVentasCommand.ExecuteNonQuery();
                    }

                    ventasReader.Close();

                    await MostrarResultadooDialog("Fusión completa", "Los datos se han fusionado con éxito.");
                }
            }
            catch (Exception ex)
            {
                await MostrarErrorDialog("Error al fusionar", $"Ocurrió un error: {ex.Message}");
            }
        }

        private async Task MostrarResultadooDialog(string titulo, string mensaje)
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
            string deleteQuery = $@"
            DELETE FROM notificaciones
            WHERE codigo_barras NOT IN ({placeholders})";

            using var deleteCommand = new SqliteCommand(deleteQuery, dbConnection.GetConnection());

            for (int i = 0; i < productosConStockBajo.Count; i++)
            {
                deleteCommand.Parameters.AddWithValue($"@p{i}", productosConStockBajo[i]);
            }

            deleteCommand.ExecuteNonQuery();
        }
        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            _shellPage?.CerrarSesion();
        }
    }
}
