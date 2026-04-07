using Agropecuaria_CM.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
using Windows.Storage.Streams;

namespace Agropecuaria_CM
{
    public sealed partial class ProductDetailPage : Page
    {
        public ProductDetailPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is Product p)
            {
                this.DataContext = p;
                // Asignar textos...

                // MOSTRAR IMAGEN GRANDE
                if (p.HasPhoto)
                {
                    // Aquí NO usamos DecodePixelWidth pequeño, queremos calidad completa
                    DestinationImage.Source = await BytesToImage(p.FotoBytes);
                    DestinationImage.Visibility = Visibility.Visible;
                    FallbackBorder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    DestinationImage.Visibility = Visibility.Collapsed;
                    FallbackBorder.Visibility = Visibility.Visible;
                }
            }

            // ARRANCAR ANIMACIÓN
            var animation = ConnectedAnimationService.GetForCurrentView().GetAnimation("forwardAnimation");
            if (animation != null)
            {
                if (DestinationImage.Visibility == Visibility.Visible)
                    animation.TryStart(DestinationImage);
                else
                    animation.TryStart(FallbackBorder);
            }
        }

        // Necesitas el helper BytesToImage aquí también (sin decodeWidth o con uno grande)
        private async Task<BitmapImage> BytesToImage(byte[] bytes)
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
            await image.SetSourceAsync(stream);
            return image;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack(new SuppressNavigationTransitionInfo());
        }
    }
}
