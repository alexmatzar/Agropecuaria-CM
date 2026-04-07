using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Firebase.Auth;
using System.Threading.Tasks;
using Firebase.Auth.Providers;
using System.Diagnostics;

namespace Agropecuaria_CM
{
    public partial class App : Application
    {
        public static FirebaseAuthClient? AuthClient { get; private set; } = null;
        public static MainWindow? MainAppWindow { get; private set; } = null;

        public App()
        {
            this.InitializeComponent();

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var exception = (Exception)e.ExceptionObject;
                LogException(exception);
            };

            // Manejador para excepciones no controladas en el hilo de la UI
            Application.Current.UnhandledException += (sender, e) =>
            {
                e.Handled = true; // Prevenir el cierre abrupto de la aplicación
                LogException(e.Exception); // Registrar la excepción
            };
            AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
            {
                Debug.WriteLine($"Excepción capturada: {e.Exception.Message}");
            };


            InitializeFirebase();
        }

        private void LogException(Exception ex)
        {
            // Aquí puedes hacer un log de la excepción o mostrarla en un mensaje de error
            Debug.WriteLine($"Excepción no controlada: {ex.Message}");
        }
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                MainAppWindow = new MainWindow();
                MainAppWindow.Activate();
            }
            catch (Exception ex)
            {
                LogException(ex); // Registrar cualquier excepción en OnLaunched
            }
        }
        private void InitializeFirebase()
        {
            var config = new FirebaseAuthConfig
            {
                ApiKey = "AIzaSyCAYaS9gFdBsYzVomxGZGjS_7Ocim_1u0w", // API Key de Firebase ^^
                AuthDomain = "sistemadeventas2024.firebaseapp.com",  // Dominio :)
                Providers = new FirebaseAuthProvider[]
                {
                    new EmailProvider()  // Proveedor Email
                }
            };

            AuthClient = new FirebaseAuthClient(config);
        }
    }
}
