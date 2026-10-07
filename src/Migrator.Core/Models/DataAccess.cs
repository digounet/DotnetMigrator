namespace Migrator.Core.Models;

/// <summary>Kind of database object the code touches.</summary>
public enum DataObjectKind { Table, StoredProcedure, Function }

/// <summary>
/// One table/view/procedure accessed by a project, with the columns and operations the code reveals. Built by
/// <c>Analysis/DataAccessAnalyzer</c> from SQL literals, .sql resources, ADO.NET readers, Dapper, EF6/EF Core models and EDMX files.
/// Never counts against the migration inventory: it is documentation the business needs (which data a system touches).
/// </summary>
public sealed class TableAccess
{
    public required string Project { get; init; }
    /// <summary>Database name (Initial Catalog), the connection-string name when the catalog is unknown, or "não identificado".</summary>
    public string Database { get; set; } = "não identificado";
    /// <summary>Engine: SQL Server, Oracle, MySQL, PostgreSQL, SQLite, OLE DB/ODBC.</summary>
    public string Technology { get; set; } = "SQL Server";
    /// <summary>How the code reaches it: ADO.NET, Dapper, EF6, EF Core, EDMX, arquivo .sql, stored procedure.</summary>
    public SortedSet<string> Access { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Schema { get; set; }
    public required string Name { get; init; }
    public DataObjectKind Kind { get; init; } = DataObjectKind.Table;
    /// <summary>Columns (or procedure parameters) seen in the code; "*" when the code selects every column.</summary>
    public SortedSet<string> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>SELECT, INSERT, UPDATE, DELETE, MERGE, EXEC, EF (leitura), EF (escrita)...</summary>
    public SortedSet<string> Operations { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Locations { get; } = [];
    /// <summary>Connection-string names the accessing code refers to (used to resolve <see cref="Database"/>).</summary>
    public SortedSet<string> ConnectionNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Database name taken from a three-part name in the SQL itself (db.schema.table): never overridden by heuristics.</summary>
    public bool DatabaseFromSql { get; set; }
    public bool DatabaseResolved { get; set; }

    public string QualifiedName => Schema != null && !Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? $"{Schema}.{Name}" : Name;

    public void AddLocation(string? location)
    {
        if (location != null && Locations.Count < 8 && !Locations.Contains(location)) Locations.Add(location);
    }

    public void MergeFrom(TableAccess other)
    {
        foreach (var c in other.Columns) Columns.Add(c);
        foreach (var o in other.Operations) Operations.Add(o);
        foreach (var a in other.Access) Access.Add(a);
        foreach (var n in other.ConnectionNames) ConnectionNames.Add(n);
        foreach (var l in other.Locations) AddLocation(l);
        if (other.DatabaseFromSql && !DatabaseFromSql) { Database = other.Database; DatabaseFromSql = true; }
        Schema ??= other.Schema;
    }
}

public static class DataAccessText
{
    public static string Display(this DataObjectKind kind) => kind switch
    {
        DataObjectKind.StoredProcedure => "procedure",
        DataObjectKind.Function => "função",
        _ => "tabela/view"
    };
}
