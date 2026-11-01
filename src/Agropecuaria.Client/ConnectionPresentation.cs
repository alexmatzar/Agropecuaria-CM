using Agropecuaria.Contracts;

namespace Agropecuaria.Client;

public static class ConnectionPresentation
{
    public static string Describe(SyncStatus status) => status.State switch
    {
        "Offline" => $"Sin conexión con la nube · {status.PendingCount} pendientes",
        "NotConfigured" => $"Nube sin configurar · {status.PendingCount} pendientes",
        "Syncing" => $"Sincronizando · {status.PendingCount} pendientes",
        "Restoring" => "Restaurando datos de la sucursal",
        "Pending" => $"Cambios pendientes · {status.PendingCount}",
        "UpToDate" => status.PendingCount == 0 ? "Datos enviados a la nube" : $"Cambios pendientes · {status.PendingCount}",
        "AttentionRequired" => "La sincronización requiere atención",
        _ => "Comprobando sincronización"
    };
}
