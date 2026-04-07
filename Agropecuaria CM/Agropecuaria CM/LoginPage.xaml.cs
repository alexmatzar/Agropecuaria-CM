using Firebase.Auth;
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
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace Agropecuaria_CM
{
    public sealed partial class LoginPage : Page
    {
        public LoginPage()
        {
            InitializeComponent();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string email = EmailTextBox.Text.Trim();
            string password = PasswordBox.Password.Trim();

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                await MostrarResultadoDialog("Campos vacíos", "Por favor ingresa tu correo y contraseña.");
                return;
            }

            try
            {
                LoginProgressBar.Visibility = Visibility.Visible;
                LoginProgressBar.ShowError = false;

                if (App.AuthClient == null)
                {
                    await MostrarResultadoDialog("Error interno", "Firebase no inicializado.");
                    return;
                }

                var userCredential = await App.AuthClient.SignInWithEmailAndPasswordAsync(email, password);
                var token = userCredential.User.Credential.IdToken;

                // Guardar credenciales
                await SaveUserCredentialsAsync(email, token);

                // --- CAMBIO CLAVE: NAVEGAR A SHELL ---
                this.Frame.Navigate(typeof(ShellPage));
            }
            catch (FirebaseAuthException ex)
            {
                LoginProgressBar.ShowError = true;
                await MostrarResultadoDialog("Error de Login", ex.Reason.ToString());
            }
            catch (Exception)
            {
                await MostrarResultadoDialog("Error", "Error inesperado.");
            }
            finally
            {
                if (!LoginProgressBar.ShowError) LoginProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private static async Task SaveUserCredentialsAsync(string email, string token)
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            localSettings.Values["UserEmail"] = email;
            byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
            var provider = new DataProtectionProvider("LOCAL=user");
            var buffer = CryptographicBuffer.CreateFromByteArray(tokenBytes);
            var protectedBuffer = await provider.ProtectAsync(buffer);
            localSettings.Values["UserToken"] = CryptographicBuffer.EncodeToBase64String(protectedBuffer);
        }

        private async Task MostrarResultadoDialog(string title, string message)
        {
            ContentDialog resultDialog = new()
            {
                Title = title,
                Content = message,
                CloseButtonText = "Aceptar",
                XamlRoot = this.XamlRoot
            };
            await resultDialog.ShowAsync();
        }

        private async void ForgotPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            TextBox emailInput = new() { PlaceholderText = "Ingresa tu correo", Width = 300 };

            ContentDialog resetDialog = new()
            {
                Title = "Restablecer contraseña",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children = { new TextBlock { Text = "¿A qué correo deseas enviar el enlace de restablecimiento?" }, emailInput }
                },
                PrimaryButtonText = "Enviar",
                CloseButtonText = "Cancelar",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await resetDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                string email = emailInput.Text;

                if (!string.IsNullOrWhiteSpace(email))
                {
                    try
                    {
                        if (App.AuthClient == null)
                        {
                            await MostrarResultadoDialog("Error interno", "No se pudo inicializar Firebase. Reinicia la aplicación.");
                            return;
                        }

                        await App.AuthClient.ResetEmailPasswordAsync(email);

                        await MostrarResultadoDialog("Éxito", "Se ha enviado el correo de restablecimiento a tu cuenta.");
                    }
                    catch (FirebaseAuthException)
                    {
                        await MostrarResultadoDialog("Error", "Ocurrió un error. Inténtalo de nuevo o revisa tu conexión.");
                    }
                    catch (Exception)
                    {
                        await MostrarResultadoDialog("Error inesperado", "No se pudo enviar el correo. Revisa tu conexión e inténtalo de nuevo.");
                    }
                }
                else
                {
                    await MostrarResultadoDialog("Campo vacío", "Por favor, ingresa un correo válido.");
                }
            }
        }
    }
}
