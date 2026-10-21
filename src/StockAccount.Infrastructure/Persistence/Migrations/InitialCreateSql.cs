using Microsoft.EntityFrameworkCore.Migrations;

namespace StockAccount.Infrastructure.Persistence.Migrations;

/// <summary>
/// Hand-written DDL for the InitialCreate migration (kept separate so the migration can be regenerated).
/// Static DDL only; there is no user input here. Do not edit after the migration has shipped; add a new migration instead.
/// </summary>
internal static class InitialCreateSql
{
    public static void Up(MigrationBuilder migrationBuilder)
    {
        // Reconciliation views. Difference = StoredBalance - LedgerBalance. Includes soft-deleted rows and all companies.
        migrationBuilder.Sql(@"
CREATE VIEW [dbo].[vw_StockBalanceReconciliation]
AS
SELECT  s.[Id]        AS [StockId],
        s.[CompanyId] AS [CompanyId],
        s.[Code]      AS [Code],
        s.[IsDeleted] AS [IsDeleted],
        s.[Balance]   AS [StoredBalance],
        CAST(ISNULL(l.[LedgerBalance], 0) AS decimal(18,3))               AS [LedgerBalance],
        CAST(s.[Balance] - ISNULL(l.[LedgerBalance], 0) AS decimal(18,3)) AS [Difference]
FROM    [dbo].[Stocks] AS s
LEFT JOIN (
        SELECT t.[StockId],
               SUM(CASE WHEN t.[Direction] = 1 THEN t.[Quantity] ELSE -t.[Quantity] END) AS [LedgerBalance]
        FROM   [dbo].[StockTrans] AS t
        GROUP BY t.[StockId]
) AS l ON l.[StockId] = s.[Id];");

        migrationBuilder.Sql(@"
CREATE VIEW [dbo].[vw_AccountBalanceReconciliation]
AS
SELECT  a.[Id]        AS [CurrentAccountId],
        a.[CompanyId] AS [CompanyId],
        a.[Code]      AS [Code],
        a.[IsDeleted] AS [IsDeleted],
        a.[Balance]   AS [StoredBalance],
        CAST(ISNULL(l.[LedgerBalance], 0) AS decimal(18,2))               AS [LedgerBalance],
        CAST(a.[Balance] - ISNULL(l.[LedgerBalance], 0) AS decimal(18,2)) AS [Difference]
FROM    [dbo].[CurrentAccounts] AS a
LEFT JOIN (
        SELECT t.[CurrentAccountId],
               SUM(CASE WHEN t.[Direction] = 1 THEN t.[Amount] ELSE -t.[Amount] END) AS [LedgerBalance]
        FROM   [dbo].[ActTrans] AS t
        GROUP BY t.[CurrentAccountId]
) AS l ON l.[CurrentAccountId] = a.[Id];");

        // Ledgers are append-only (declared via HasTrigger in the model so EF avoids OUTPUT without INTO).
        migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_StockTrans_Immutable] ON [dbo].[StockTrans]
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50001, 'StockTrans is an immutable ledger: UPDATE/DELETE is not allowed. Write reversal rows instead.', 1;
END;");

        migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_ActTrans_Immutable] ON [dbo].[ActTrans]
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50002, 'ActTrans is an immutable ledger: UPDATE/DELETE is not allowed. Write reversal rows instead.', 1;
END;");
    }

    public static void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_ActTrans_Immutable];");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_StockTrans_Immutable];");
        migrationBuilder.Sql("DROP VIEW IF EXISTS [dbo].[vw_AccountBalanceReconciliation];");
        migrationBuilder.Sql("DROP VIEW IF EXISTS [dbo].[vw_StockBalanceReconciliation];");
    }
}
