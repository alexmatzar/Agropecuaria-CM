using Firebase.Auth;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;

namespace Agropecuaria_CM
{
    public sealed partial class MainWindow : Window
    {
        private readonly AppWindow m_appWindow;

        public MainWindow()
        {
            this.InitializeComponent();
            m_appWindow = GetAppWindowForCurrentWindow();
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);
            MaximizeToWorkArea();
            CheckStoredCredentialsAndNavigate();
        }

        private AppWindow GetAppWindowForCurrentWindow()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId myWndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(myWndId);
        }

        private void MaximizeToWorkArea()
        {
            OverlappedPresenter? presenter = m_appWindow.Presenter as OverlappedPresenter;
            presenter?.Maximize();
        }

        private async void CheckStoredCredentialsAndNavigate()
        {
            // Pequeña espera técnica para asegurar carga de UI
            await Task.Delay(50);

            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

            if (localSettings.Values.ContainsKey("UserEmail") && localSettings.Values.ContainsKey("UserToken"))
            {
                try
                {
                    string base64Token = localSettings.Values["UserToken"] as string ?? string.Empty;
                    var protectedBuffer = CryptographicBuffer.DecodeFromBase64String(base64Token);

                    // Desencriptar token local
                    var provider = new DataProtectionProvider();
                    var unprotectedBuffer = await provider.UnprotectAsync(protectedBuffer);

                    // Si no falla, vamos a ShellPage
                    RootFrame.Navigate(typeof(ShellPage));
                }
                catch
                {
                    // Token inválido -> Login
                    RootFrame.Navigate(typeof(LoginPage));
                }
            }
            else
            {
                // No hay credenciales -> Login
                RootFrame.Navigate(typeof(LoginPage));
            }
        }
    }
}