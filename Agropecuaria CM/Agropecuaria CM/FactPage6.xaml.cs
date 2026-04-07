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
using System.Globalization;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Agropecuaria_CM
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class FactPage6 : Page
    {
        public FactPage6()
        {
            this.InitializeComponent();
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("es-ES");
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("es-ES");
            CargarConfiguracion();
        }
        private void Validacion1_40char_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            if (sender.Text.Length > 40)
            {
                sender.Text = sender.Text[..40];
            }
            sender.SelectionStart = sender.Text.Length;
        }
        private void EditarButton_Click(object sender, RoutedEventArgs e)
        {
            HabilitarTextBoxes(true);
            GuardarButton.IsEnabled = true;
            EditarButton.IsEnabled = false;
        }
        private async void GuardarButton_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog dialog = new()
            {
                Title = "Información Precisa",
                Content = "Los datos de la factura son leítimamente de la tienda. Por favor, asegúrese que sean correctos.",
                PrimaryButtonText = "Guardar cambios",
                CloseButtonText = "Cancelar cambios",
                XamlRoot = this.XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                GuardarConfiguracion();
                HabilitarTextBoxes(false);
                GuardarButton.IsEnabled = false;
                EditarButton.IsEnabled = true;
            }
        }

        private void HabilitarTextBoxes(bool isEnabled)
        {
            TiendaTextBox.IsEnabled = isEnabled;
            DireccionTextBox.IsEnabled = isEnabled;
            MensajeTextBox.IsEnabled = isEnabled;
            PieTextBox.IsEnabled = isEnabled;
        }

        private void CargarConfiguracion()
        {
            var localSettings = ApplicationData.Current.LocalSettings;

            if (string.IsNullOrEmpty(localSettings.Values["FacturaTienda"] as string))
            {
                localSettings.Values["FacturaTienda"] = "INSERTE NOMBRE";
            }
            TiendaTextBox.Text = localSettings.Values["FacturaTienda"] as string;

            if (string.IsNullOrEmpty(localSettings.Values["FacturaDireccion"] as string))
            {
                localSettings.Values["FacturaDireccion"] = "INSERTE DIRECCIÓN";
            }
            DireccionTextBox.Text = localSettings.Values["FacturaDireccion"] as string;

            if (string.IsNullOrEmpty(localSettings.Values["FacturaMensaje"] as string))
            {
                localSettings.Values["FacturaMensaje"] = "NO SE ACEPTAN DEVOLUCIONES";
            }
            MensajeTextBox.Text = localSettings.Values["FacturaMensaje"] as string;

            if (string.IsNullOrEmpty(localSettings.Values["FacturaPie"] as string))
            {
                localSettings.Values["FacturaPie"] = "Gracias por su compra. ¡Vuelva pronto!";
            }
            PieTextBox.Text = localSettings.Values["FacturaPie"] as string;
        }

        // Guardar configuración en ApplicationData
        private void GuardarConfiguracion()
        {
            var localSettings = ApplicationData.Current.LocalSettings;

            localSettings.Values["FacturaTienda"] = TiendaTextBox.Text;
            localSettings.Values["FacturaDireccion"] = DireccionTextBox.Text;
            localSettings.Values["FacturaMensaje"] = MensajeTextBox.Text;
            localSettings.Values["FacturaPie"] = PieTextBox.Text;
        }
    }
}
