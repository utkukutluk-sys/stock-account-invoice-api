namespace StockAccount.Domain.Enums;

public enum StockUnit
{
    Piece = 1,
    Kg = 2,
    Gram = 3,
    Liter = 4,
    Meter = 5,
    Box = 6,
}

public enum CurrentAccountType
{
    Customer = 1,
    Supplier = 2,
    Both = 3,
}

public enum InvoiceType
{
    Sales = 1,
    Purchase = 2,
}

public enum InvoiceStatus
{
    Draft = 1,
    Approved = 2,
    Cancelled = 3,
}

public enum StockTransDirection
{
    In = 1,
    Out = 2,
}

public enum ActTransDirection
{
    Debit = 1,
    Credit = 2,
}
