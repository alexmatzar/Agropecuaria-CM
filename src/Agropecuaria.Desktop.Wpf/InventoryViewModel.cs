using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using Agropecuaria.Client;

namespace Agropecuaria.Desktop.Wpf;

public sealed record InventoryRow(string Name, string Available);
public sealed class InventoryViewModel(LocalApiClient? api, bool isPreview = false) : INotifyPropertyChanged
{
    public bool IsPreview { get; } = isPreview;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<InventoryRow> Products { get; } = [];
    public string LocalStatus { get; private set; } = "Conectando con el servicio local";
    public string CloudStatus { get; private set; } = "Comprobando sincronización";
    public static InventoryViewModel Preview()
    {
        var model = new InventoryViewModel(null, true)
        {
            LocalStatus = "Vista previa visual · datos ficticios · sin acceso a base de datos",
            CloudStatus = "Servicios reales sin conectar"
        };
        model.Products.Add(new("Concentrado para ganado", "482 lb"));
        model.Products.Add(new("Alimento para aves", "77 lb"));
        model.Products.Add(new("Fertilizante granulado", "125 lb"));
        model.Products.Add(new("Vitaminas veterinarias", "24 unidades"));
        model.Products.Add(new("Herramientas de jardinería", "12 unidades"));
        return model;
    }
    public async Task PollAsync(CancellationToken ct)
    {
        if (IsPreview) return;
        if (api == null) throw new InvalidOperationException("Falta el cliente del servicio local.");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await api.GetSyncStatusAsync(ct);
                var products = await api.GetProductsAsync(ct);
                var rows = products.Select(p => new InventoryRow(p.Name,
                    p.BaseUnit == "micro-pound" ? $"{p.StockBase / 1_000_000m:0.######} lb" : $"{p.StockBase} unidades")).ToList();
                // Retain the collection during cloud outages; update only when local data changes.
                if (!Products.SequenceEqual(rows)) { Products.Clear(); foreach (var row in rows) Products.Add(row); }
                LocalStatus = "Servicio local disponible";
                CloudStatus = ConnectionPresentation.Describe(status);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                LocalStatus = "Servicio local no disponible · las existencias mostradas pueden estar desactualizadas";
                CloudStatus = "Estado de nube desconocido";
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloudStatus)));
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
