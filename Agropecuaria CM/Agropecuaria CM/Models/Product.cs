using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agropecuaria_CM.Models
{
    public class Product
    {
        public string ProductName { get; set; }
        public string ProductType { get; set; }
        public string ProductBrand { get; set; }
        public string ProductDescription { get; set; }
        public string ProductUnidadComprada { get; set; }
        public string ProductStock { get; set; }
        public string ProductPriceCompra { get; set; } // Nuevo campo
        public string ProductCapital { get; set; } // Nuevo campo para el capital
        public decimal ConvertedStock { get; set; }
        public string UnidadVenta { get; set; }
        public decimal PrecioVenta { get; set; }
        public List<UnitSale> UnitSales { get; set; } = new List<UnitSale>();
        public byte[] FotoBytes { get; set; }
        public bool HasPhoto => FotoBytes != null && FotoBytes.Length > 0;
    }
}
