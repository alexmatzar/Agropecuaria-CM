using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Agropecuaria_CM.Models;

namespace Agropecuaria_CM
{
    public sealed partial class ProductListPage : Page
    {
        public ObservableCollection<Product> Products { get; } = new ObservableCollection<Product>();
        public ProductListPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is List<Product> list)
            {
                Products.Clear();
                foreach (var p in list) Products.Add(p);
            }
        }

        public void UpdateProducts(List<Product> list)
        {
            Products.Clear();
            foreach (var p in list) Products.Add(p);
        }

        private void ProductsGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Product product)
            {
                var animation = ProductsGridView.PrepareConnectedAnimation("forwardAnimation", product, "ItemThumbnail");
                this.Frame.Navigate(typeof(ProductDetailPage), product);
            }
        }
    }
}
