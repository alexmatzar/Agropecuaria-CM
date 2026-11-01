using System.Net.Http;
using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Agropecuaria.Client;

namespace Agropecuaria.Desktop.Wpf;

public partial class App : System.Windows.Application
{
    private HttpClient? http;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var render = e.Args.Length == 2 && e.Args[0] == "--render-preview";
        if (render || e.Args.SequenceEqual(new[] { "--preview" }))
        {
            var preview = new MainWindow(InventoryViewModel.Preview());
            preview.Show();
            if (render)
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var content = (FrameworkElement)preview.Content;
                        content.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(content);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = new FileStream(Path.GetFullPath(e.Args[1]), FileMode.CreateNew, FileAccess.Write);
                        encoder.Save(output); Shutdown(0);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                    { Shutdown(2); }
                }, DispatcherPriority.ApplicationIdle);
            return;
        }
        var token = Environment.GetEnvironmentVariable("AGROPECUARIA_LOCAL_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show("Configura AGROPECUARIA_LOCAL_TOKEN para conectar esta interfaz de desarrollo con el servicio local.",
                "Configuración pendiente", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1); return;
        }
        http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5187/"), Timeout = TimeSpan.FromSeconds(5) };
        var viewModel = new InventoryViewModel(new LocalApiClient(http, token));
        new MainWindow(viewModel).Show();
    }
    protected override void OnExit(ExitEventArgs e) { http?.Dispose(); base.OnExit(e); }
}
