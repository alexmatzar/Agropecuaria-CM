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
using Microsoft.UI.Text;
using Microsoft.Data.Sqlite;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Agropecuaria_CM
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class NitPage4 : Page
    {
        private DatabaseConnection _dbConnection;
        public NitPage4()
        {
            this.InitializeComponent();
            _dbConnection = new DatabaseConnection();
            CargarClientes();
        }

        private void TextNit_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            // Permitir solo dígitos numéricos
            string filteredText = new string(sender.Text.Where(char.IsDigit).ToArray());

            // Limitar a 9 caracteres
            if (filteredText.Length > 9)
            {
                filteredText = filteredText[..9];
            }

            // Actualizar el texto solo si ha cambiado
            if (sender.Text != filteredText)
            {
                // Usar Dispatcher para actualizar el texto y mover el cursor
                sender.DispatcherQueue.TryEnqueue(() =>
                {
                    sender.Text = filteredText;
                    sender.SelectionStart = sender.Text.Length; // Mover el cursor al final
                });
            }
        }
        private void TextFName_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            TextBox textBox = sender as TextBox;

            if (textBox == null) return;

            // Filtrar solo letras y espacios
            string filteredText = new(textBox.Text.Where(c => char.IsLetter(c) || char.IsWhiteSpace(c)).ToArray());

            // Limitar a 40 caracteres
            if (filteredText.Length > 40)
            {
                filteredText = filteredText[..40];
            }

            // Actualizar el texto del TextBox y mover el cursor al final
            if (textBox.Text != filteredText)
            {
                // Usar Dispatcher para evitar problemas de actualización del texto
                textBox.DispatcherQueue.TryEnqueue(() =>
                {
                    textBox.Text = filteredText;
                    textBox.SelectionStart = textBox.Text.Length; // Mover el cursor al final
                });
            }
        }

        private void AnadirCliente_Click(object sender, RoutedEventArgs e)
        {
            string nit = Nitcliente.Text;
            string nombre = Fullname.Text;

            if (nit.Length != 9)
            {
                MostrarMensajeError("El NIT debe contener exactamente 9 dígitos.");
                return;
            }

            // Validar el nombre: debe tener entre 3 y 40 caracteres
            if (nombre.Length < 3 || nombre.Length > 40)
            {
                MostrarMensajeError("El nombre debe tener entre 3 y 40 caracteres.");
                return;
            }

            if (!string.IsNullOrEmpty(nit) && !string.IsNullOrEmpty(nombre))
            {
                try
                {
                    using (var connection = _dbConnection.GetConnection())
                    {
                        connection.Open();

                        using (var command = new SqliteCommand("INSERT INTO clientes (nit, nombre) VALUES (@nit, @nombre)", connection))
                        {
                            command.Parameters.AddWithValue("@nit", nit);
                            command.Parameters.AddWithValue("@nombre", nombre);
                            command.ExecuteNonQuery();
                        }
                    }

                    Nitcliente.Text = string.Empty;
                    Fullname.Text = string.Empty;
                    CargarClientes();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Error al añadir el cliente: " + ex.Message);
                }
            }
            else
            {
                // Mostrar mensaje de validación
            }
        }

        private void MostrarMensajeError(string mensaje)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = "Error de Validación",
                Content = mensaje,
                CloseButtonText = "Aceptar",
                XamlRoot = this.Content.XamlRoot
            };
            _ = dialog.ShowAsync();
        }

        private void findxname_TextChanged(object sender, TextChangedEventArgs e)
        {
            FiltrarClientes();
        }

        private void findxnit_TextChanged(object sender, TextChangedEventArgs e)
        {
            FiltrarClientes();
        }
        private void FiltrarClientes()
        {
            string nitFiltro = findxnit.Text;
            string nombreFiltro = findxname.Text;

            List<Cliente> clientes = [];

            try
            {
                using var connection = _dbConnection.GetConnection();
                {
                    connection.Open();

                    using (var command = new SqliteCommand("SELECT nit, nombre FROM clientes WHERE nit LIKE @nit AND nombre LIKE @nombre", connection))
                    {
                        command.Parameters.AddWithValue("@nit", $"%{nitFiltro}%");
                        command.Parameters.AddWithValue("@nombre", $"%{nombreFiltro}%");
                        using var reader = command.ExecuteReader();
                        {
                            while (reader.Read())
                            {
                                clientes.Add(new Cliente
                                {
                                    NIT = reader.GetString(0),
                                    Nombre = reader.GetString(1)
                                });
                            }
                        }
                    }

                    // Actualiza la lista en el ListView
                    ClientesListView.ItemsSource = clientes;

                    if (clientes.Count > 0)
                    {
                        SearchPanel.Visibility = Visibility.Visible;
                        ClientesPanel.Visibility = Visibility.Visible;
                        NoClientsTextBlock.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        // Si no hay resultados, ocultar la lista de clientes y los campos de búsqueda
                        SearchPanel.Visibility = Visibility.Collapsed;
                        ClientesPanel.Visibility = Visibility.Collapsed;
                        NoClientsTextBlock.Visibility = Visibility.Visible; // Mostrar mensaje de no resultados
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error al filtrar los clientes: " + ex.Message);
            }
        }

        private void CargarClientes()
        {
            List<Cliente> clientes = [];

            try
            {
                _dbConnection.OpenConnection();

                using (var command = new SqliteCommand("SELECT nit, nombre FROM clientes", _dbConnection.GetConnection()))
                {
                    using var reader = command.ExecuteReader();
                    {
                        while (reader.Read())
                        {
                            clientes.Add(new Cliente
                            {
                                NIT = reader.GetString(0),
                                Nombre = reader.GetString(1)
                            });
                        }
                    }
                }
                ClientesListView.ItemsSource = clientes;

                if (clientes.Count > 0)
                {
                    ClientesListView.ItemsSource = clientes;
                    SearchPanel.Visibility = Visibility.Visible;
                    ClientesPanel.Visibility = Visibility.Visible;
                    NoClientsTextBlock.Visibility = Visibility.Collapsed; // Ocultar mensaje de no clientes
                }
                else
                {
                    // No hay clientes, ocultar los paneles y mostrar el mensaje de no clientes
                    SearchPanel.Visibility = Visibility.Collapsed;
                    ClientesPanel.Visibility = Visibility.Collapsed;
                    NoClientsTextBlock.Visibility = Visibility.Visible; // Mostrar mensaje de no clientes
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error: " + ex.Message);
            }
            finally
            {
                _dbConnection.CloseConnection();
            }
        }

        private void ClientesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Habilitar el botón de eliminar cuando se selecciona un cliente
            EditButtonsPanel.Visibility = ClientesListView.SelectedItem != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void EliminarCliente_Click(object sender, RoutedEventArgs e)
        {
            if (ClientesListView.SelectedItem is Cliente clienteSeleccionado)
            {
                var deleteDialog = new ContentDialog
                {
                    Title = "Confirmar eliminación",
                    Content = $"¿Está seguro de que desea eliminar al cliente con NIT: {clienteSeleccionado.NIT}?",
                    PrimaryButtonText = "Eliminar",
                    CloseButtonText = "Cancelar",
                    XamlRoot = this.XamlRoot // Asegúrate de establecer el XamlRoot
                };

                var result = await deleteDialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    try
                    {
                        // Verifica si la conexión es nula o está cerrada
                        var connection = _dbConnection.GetConnection();
                        if (connection.State != System.Data.ConnectionState.Open)
                        {
                            _dbConnection.OpenConnection();
                        }

                        using (var command = new SqliteCommand("DELETE FROM clientes WHERE nit = @nit", connection))
                        {
                            command.Parameters.AddWithValue("@nit", clienteSeleccionado.NIT);
                            int rowsAffected = command.ExecuteNonQuery();

                            if (rowsAffected == 0)
                            {
                                System.Diagnostics.Debug.WriteLine($"No se encontró el cliente con NIT: {clienteSeleccionado.NIT}");
                            }
                        }

                        CargarClientes();
                    }
                    catch (Exception ex)
                    {
                        // Muestra un mensaje de error
                        await new ContentDialog
                        {
                            Title = "Error",
                            Content = "Hubo un error al intentar eliminar el cliente: " + ex.Message,
                            CloseButtonText = "Aceptar",
                            XamlRoot = this.XamlRoot // También establece el XamlRoot aquí
                        }.ShowAsync();

                        System.Diagnostics.Debug.WriteLine("Error al eliminar el cliente: " + ex.Message);
                    }
                    finally
                    {
                        _dbConnection.CloseConnection();
                    }
                }
            }
            else
            {
                await new ContentDialog
                {
                    Title = "Error",
                    Content = "No se ha seleccionado ningún cliente para eliminar.",
                    CloseButtonText = "Aceptar",
                    XamlRoot = this.XamlRoot // Establece el XamlRoot aquí también
                }.ShowAsync();
            }
        }
    }
    public class Cliente
    {
        public string NIT { get; set; }
        public string Nombre { get; set; }
    }
}
