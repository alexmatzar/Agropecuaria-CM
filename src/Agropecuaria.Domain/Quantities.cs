namespace Agropecuaria.Domain;

public enum ProductDimension { Count, Weight }
public enum SaleUnit { Piece, Hundred, Ounce, Pound, Arroba, Quintal }

public sealed class RuleViolation(string message) : Exception(message);
public sealed class OperationConflict(string message) : Exception(message);

public static class Quantities
{
    // Weight: millionths of a commercial pound. Count: whole pieces.
    // GT commercial conventions: 16 oz/lb, 25 lb/arroba, 100 lb/quintal.
    public static long ToBase(ProductDimension dimension, SaleUnit unit, decimal quantity)
    {
        if (quantity <= 0) throw new RuleViolation("La cantidad debe ser mayor que cero.");
        long factor = (dimension, unit) switch
        {
            (ProductDimension.Count, SaleUnit.Piece) => 1,
            (ProductDimension.Count, SaleUnit.Hundred) => 100,
            (ProductDimension.Weight, SaleUnit.Ounce) => 62_500,
            (ProductDimension.Weight, SaleUnit.Pound) => 1_000_000,
            (ProductDimension.Weight, SaleUnit.Arroba) => 25_000_000,
            (ProductDimension.Weight, SaleUnit.Quintal) => 100_000_000,
            _ => throw new RuleViolation("Unidad incompatible con el producto.")
        };
        var value = checked(quantity * factor);
        if (value != decimal.Truncate(value))
            throw new RuleViolation("La cantidad supera la precisión admitida.");
        return checked((long)value);
    }

    public static long LineTotalMinor(long unitPriceMinor, decimal quantity)
    {
        if (unitPriceMinor < 0 || quantity <= 0)
            throw new RuleViolation("Precio o cantidad inválidos.");
        return checked((long)decimal.Round(checked(unitPriceMinor * quantity), 0, MidpointRounding.AwayFromZero));
    }
}
