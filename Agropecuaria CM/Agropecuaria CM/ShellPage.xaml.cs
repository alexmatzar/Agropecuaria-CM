using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;

namespace Agropecuaria_CM
{
    public sealed partial class ShellPage : Page
    {
        public ShellPage()
        {
            InitializeComponent();
            _ = ActualizarFondo();
            nvSample.SelectedItem = SamplePage1Item;
            contentFrame.Navigate(typeof(BarcodePage1));
        }

        private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            // 1. Verificamos que sea un item válido y extraemos el Tag
            if (args.SelectedItem is NavigationViewItem selectedItem && selectedItem.Tag is string selectedTag)
            {
                // 2. Leemos la configuración de bloqueos (Igual que antes)
                var localSettings = ApplicationData.Current.LocalSettings;
                bool bloquearPurPage3 = (bool)(localSettings.Values["BloquearPurPage3"] ?? false);
                bool bloquearNitPage5 = (bool)(localSettings.Values["BloquearNitPage5"] ?? false);
                bool bloquearVentasPage5 = (bool)(localSettings.Values["BloquearVentasPage5"] ?? false);
                bool bloquearFactPage6 = (bool)(localSettings.Values["BloquearFactPage6"] ?? false);
                bool bloquearDashPage7 = (bool)(localSettings.Values["BloquearDashPage7"] ?? false);

                // 3. El Switch debe estar DENTRO del IF para poder usar 'selectedTag'
                switch (selectedTag)
                {
                    case "Subpagina1":
                        // Pasamos 'this' (que ahora es ShellPage) por si BarcodePage necesita acceder a métodos de aquí
                        contentFrame.Navigate(typeof(BarcodePage1), this);
                        break;

                    case "Subpagina2":
                        contentFrame.Navigate(typeof(InvPage2));
                        break;

                    case "Subpagina3":
                        // Lógica ternaria para verificar bloqueo
                        contentFrame.Navigate(bloquearPurPage3 ? typeof(SinPermisosPage10) : typeof(PurPage3), this);
                        break;

                    case "Subpagina4":
                        contentFrame.Navigate(bloquearNitPage5 ? typeof(SinPermisosPage10) : typeof(NitPage4));
                        break;

                    case "Subpagina5":
                        contentFrame.Navigate(bloquearVentasPage5 ? typeof(SinPermisosPage10) : typeof(VentasPage5));
                        break;

                    case "Subpagina6":
                        contentFrame.Navigate(bloquearFactPage6 ? typeof(SinPermisosPage10) : typeof(FactPage6));
                        break;

                    case "Subpagina7":
                        contentFrame.Navigate(bloquearDashPage7 ? typeof(SinPermisosPage10) : typeof(DashPage7));
                        break;

                    case "Subpagina8":
                        contentFrame.Navigate(typeof(NotifiPage8));
                        break;

                    case "Subpagina9":
                        // ConfigPage necesita 'this' para llamar a CerrarSesion()
                        contentFrame.Navigate(typeof(ConfigPage9), this);
                        break;

                    default:
                        contentFrame.Navigate(typeof(SinPermisosPage10), this);
                        break;
                }
            }
        }

        public async Task ActualizarFondo()
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var mainGrid = this.Content as Grid; // Obtenemos el Grid principal de ShellPage

            if (localSettings.Values.ContainsKey("BackgroundImage"))
            {
                string? imagePath = localSettings.Values["BackgroundImage"] as string;
                if (!string.IsNullOrEmpty(imagePath))
                {
                    try
                    {
                        var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                        var imageFile = await localFolder.GetFileAsync(System.IO.Path.GetFileName(imagePath));

                        if (mainGrid != null)
                        {
                            mainGrid.Background = new ImageBrush
                            {
                                ImageSource = new BitmapImage(new Uri(imageFile.Path)),
                                Stretch = Stretch.UniformToFill,
                                Opacity = 0.20
                            };
                        }
                    }
                    catch
                    {
                        // Si falla, limpiar y poner default
                        localSettings.Values.Remove("BackgroundImage");
                        if (mainGrid != null) mainGrid.Background = new ImageBrush { Opacity = 0.20 };
                    }
                }
            }
            else
            {
                // Fondo por defecto
                if (mainGrid != null) mainGrid.Background = new ImageBrush { Opacity = 0.20 };
            }
        }

        // --- MÉTODO PÚBLICO PARA CERRAR SESIÓN ---
        public void CerrarSesion()
        {
            // 1. Limpiar credenciales
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            localSettings.Values.Remove("UserEmail");
            localSettings.Values.Remove("UserToken");

            // 2. Navegar HACIA ATRÁS (Al RootFrame que contiene ShellPage)
            // 'this.Frame' aquí se refiere al RootFrame de MainWindow
            if (this.Frame != null)
            {
                this.Frame.Navigate(typeof(LoginPage));
            }
        }
    }
}
