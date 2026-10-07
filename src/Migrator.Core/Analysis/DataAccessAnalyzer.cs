using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Core.Analysis;

/// <summary>Entity class seen in code: its public scalar properties and the mapping attributes it carries.</summary>
public sealed record EntityInfo(string Name, List<(string Property, string Type, string? Column, bool Ignored)> Properties, string? Table, string? Schema);

/// <summary>A <c>DbSet&lt;T&gt;</c> declaration waiting for the entity catalog (the entity may live in another project).</summary>
public sealed record EntityRef(string EntityType, string PropertyName, string? Location, bool EfCore, SortedSet<string> ConnectionNames);

/// <summary>Per-project raw findings; resolved solution-wide by <see cref="DataAccessAnalyzer.Resolve"/>.</summary>
public sealed class DataAccessScan
{
    public List<TableAccess> Tables { get; } = [];
    public Dictionary<string, EntityInfo> Entities { get; } = new(StringComparer.Ordinal);
    public List<EntityRef> EntityRefs { get; } = [];
    /// <summary>Fluent mappings (<c>Entity&lt;T&gt;().ToTable(...)</c>, <c>HasColumnName</c>, <c>Ignore</c>) keyed by entity type.</summary>
    public Dictionary<string, (string? Table, string? Schema, Dictionary<string, string> Columns, HashSet<string> Ignored)> Mappings { get; } = new(StringComparer.Ordinal);
    public bool EfWrites { get; set; }
    /// <summary>Engine implied by the data-access API used in the project (SqlConnection → SQL Server...).</summary>
    public string? TechnologyHint { get; set; }
    /// <summary>Character offset of the statement that created each entry, so reader columns/parameters attach to the nearest one.</summary>
    internal Dictionary<TableAccess, int> Offsets { get; } = [];
}

/// <summary>
/// Finds which tables, columns and stored procedures a project touches. Sources: SQL literals in C#/VB (including
/// concatenations), embedded .sql files, ADO.NET readers and parameters, Dapper calls, EF6/EF Core DbSets with their
/// fluent/attribute mappings, and EDMX storage models. Databases are resolved from connection strings solution-wide.
/// The analysis is heuristic by design: it documents what the code reveals, it does not execute anything.
/// </summary>
public static partial class DataAccessAnalyzer
{
    private const string Ident = @"(?:\[[^\]\r\n]+\]|""[^""\r\n]+""|`[^`\r\n]+`|[A-Za-z_][\w$]*)";
    private const string QualifiedIdent = Ident + @"(?:\s*\.\s*" + Ident + @"){0,2}";

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "BETWEEN", "EXISTS", "AS", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "APPLY",
        "ORDER", "GROUP", "BY", "HAVING", "TOP", "DISTINCT", "ASC", "DESC", "CASE", "WHEN", "THEN", "ELSE", "END", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "MERGE", "USING",
        "UNION", "ALL", "WITH", "NOLOCK", "ROWLOCK", "UPDLOCK", "HOLDLOCK", "READPAST", "OUTPUT", "INSERTED", "DELETED", "DEFAULT", "DUAL", "TRUE", "FALSE", "OVER", "PARTITION", "LIMIT", "OFFSET", "FETCH", "NEXT", "ROWS", "ONLY", "FIRST",
        "GETDATE", "GETUTCDATE", "SYSDATE", "SYSDATETIME", "NOW", "COUNT", "SUM", "AVG", "MIN", "MAX", "ISNULL", "COALESCE", "NVL", "CAST", "CONVERT", "LEN", "LENGTH", "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "SUBSTRING", "REPLACE", "ROUND", "ABS", "DATEADD", "DATEDIFF", "DATEPART", "YEAR", "MONTH", "DAY", "ROW_NUMBER", "RANK", "DENSE_RANK", "NEWID", "SCOPE_IDENTITY", "IDENTITY", "IIF", "CHOOSE", "FORMAT", "CONCAT", "STRING_AGG", "EXEC", "EXECUTE", "DECLARE", "BEGIN", "COMMIT", "ROLLBACK", "TRAN", "TRANSACTION", "IF", "PRINT", "RETURN", "GO", "TABLE", "TRUNCATE", "CALL", "INTERSECT", "EXCEPT", "ANY", "SOME", "ESCAPE", "COLLATE", "PIVOT", "UNPIVOT", "FOR", "XML", "JSON", "PATH", "AUTO", "RAW", "OPTION", "RECOMPILE", "OPENQUERY", "OPENROWSET", "SP_EXECUTESQL", "VALUE", "KEY", "ROWCOUNT", "NOCOUNT", "VARCHAR", "NVARCHAR", "INT", "BIGINT", "DECIMAL", "DATETIME", "DATE", "BIT", "CHAR", "NCHAR", "FLOAT", "MONEY", "TEXT", "UNIQUEIDENTIFIER", "SYSTEM_USER", "CURRENT_TIMESTAMP"
    };

    private static readonly HashSet<string> SkippedSchemas = new(StringComparer.OrdinalIgnoreCase) { "sys", "INFORMATION_SCHEMA", "pg_catalog" };

    private static readonly HashSet<string> CollectionTypes = ["ICollection", "List", "IList", "IEnumerable", "HashSet", "ISet", "ObservableCollection", "Collection", "IReadOnlyCollection", "IReadOnlyList", "DbSet"];

    private sealed record SqlFinding(string RawName, DataObjectKind Kind, string Operation, int Offset);

    // ---------------------------------------------------------------- scan (per project)

    /// <summary>
    /// Scans the project's sources. Entries ending in .sql are parsed as SQL files, .edmx as EDMX storage models,
    /// everything else as C#/VB code. Locations are "path:line".
    /// </summary>
    public static DataAccessScan Scan(ProjectInfo project, IEnumerable<(string Path, string Text)> code)
    {
        var scan = new DataAccessScan();
        var visualBasic = project.IsVisualBasic;
        foreach (var (path, text) in code)
        {
            if (string.IsNullOrEmpty(text)) continue;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".sql") { ScanSql(scan, project, path, text, "arquivo .sql", null, []); continue; }
            if (ext == ".edmx") { ScanEdmx(scan, project, path, text); continue; }
            ScanCodeFile(scan, project, path, text, visualBasic || ext == ".vb");
        }
        scan.TechnologyHint ??= project.Packages.Select(p => p.Id).Select(TechnologyOfPackage).FirstOrDefault(t => t != null);
        return scan;
    }

    private static void ScanCodeFile(DataAccessScan scan, ProjectInfo project, string path, string text, bool visualBasic)
    {
        scan.TechnologyHint ??= TechnologyOfCode(text);
        if (text.Contains("SaveChanges", StringComparison.Ordinal)) scan.EfWrites = true;
        var connectionNames = ConnectionNames(text, visualBasic);
        var access = AccessOf(text);
        var lines = new LineIndex(text);
        var before = scan.Tables.Count;

        // 1. SQL inside string literals (concatenated pieces of the same expression are joined).
        var isProcedureFile = text.Contains("CommandType.StoredProcedure", StringComparison.Ordinal) || text.Contains("CommandType.StoredProcedure", StringComparison.OrdinalIgnoreCase);
        foreach (var literal in LogicalStrings(text, visualBasic))
        {
            if (LooksLikeSql(literal.Value))
                ScanSql(scan, project, path, literal.Value, access, lines.LineOf(literal.Offset), connectionNames, literal.Offset);
            else if (isProcedureFile && ProcedureName().IsMatch(literal.Value) && IsCommandText(text, literal.Offset))
                Add(scan, project, literal.Value, DataObjectKind.StoredProcedure, "EXEC", "stored procedure", $"{path}:{lines.LineOf(literal.Offset)}", connectionNames, literal.Offset);
        }
        // Dapper/EF raw calls with commandType: StoredProcedure where the literal has no whitespace.
        foreach (Match m in DapperProcedure().Matches(text))
            Add(scan, project, m.Groups["text"].Value, DataObjectKind.StoredProcedure, "EXEC", access == "ADO.NET" ? "stored procedure" : access, $"{path}:{lines.LineOf(m.Index)}", connectionNames, m.Index);

        // 2. Reader columns and command parameters attach to the nearest statement in the same file.
        var fileTables = scan.Tables.Skip(before).Where(t => t.Locations.Count > 0).ToList();
        if (fileTables.Count > 0)
        {
            var offsets = scan.Offsets;
            foreach (Match m in (visualBasic ? ReaderColumnVb() : ReaderColumnCs()).Matches(text))
                Nearest(fileTables, offsets, m.Index, t => t.Operations.Contains("SELECT") || t.Kind == DataObjectKind.StoredProcedure)?.Columns.Add(m.Groups["col"].Value);
            foreach (Match m in CommandParameter().Matches(text))
            {
                var target = Nearest(fileTables, offsets, m.Index, t => t.Kind == DataObjectKind.StoredProcedure);
                target?.Columns.Add("@" + m.Groups["name"].Value.TrimStart('@', ':'));
            }
        }

        // 3. Entity Framework: DbSets, fluent mappings, attributes and entity classes.
        var efCore = text.Contains("Microsoft.EntityFrameworkCore", StringComparison.Ordinal);
        var contextConnection = new SortedSet<string>(connectionNames, StringComparer.OrdinalIgnoreCase);
        foreach (Match m in DbContextName().Matches(text)) contextConnection.Add(m.Groups["name"].Value);
        foreach (Match m in DbSetDeclaration().Matches(text))
            scan.EntityRefs.Add(new EntityRef(m.Groups["entity"].Value, m.Groups["prop"].Value, $"{path}:{lines.LineOf(m.Index)}", efCore, contextConnection));
        foreach (Match m in FluentToTable().Matches(text))
        {
            var entry = Mapping(scan, m.Groups["entity"].Value);
            scan.Mappings[m.Groups["entity"].Value] = (m.Groups["table"].Value, m.Groups["schema"].Success ? m.Groups["schema"].Value : entry.Schema, entry.Columns, entry.Ignored);
        }
        foreach (Match m in FluentColumn().Matches(text)) Mapping(scan, m.Groups["entity"].Value).Columns[m.Groups["prop"].Value] = m.Groups["column"].Value;
        foreach (Match m in FluentIgnore().Matches(text)) Mapping(scan, m.Groups["entity"].Value).Ignored.Add(m.Groups["prop"].Value);
        foreach (var entity in EntityClasses(text, visualBasic))
            if (!scan.Entities.TryGetValue(entity.Name, out var existing) || existing.Properties.Count < entity.Properties.Count) scan.Entities[entity.Name] = entity;
    }

    private static (string? Table, string? Schema, Dictionary<string, string> Columns, HashSet<string> Ignored) Mapping(DataAccessScan scan, string entity)
    {
        if (!scan.Mappings.TryGetValue(entity, out var entry))
            scan.Mappings[entity] = entry = (null, null, new Dictionary<string, string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        return entry;
    }

    private static void ScanEdmx(DataAccessScan scan, ProjectInfo project, string path, string text)
    {
        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (System.Xml.XmlException) { return; }
        var storage = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "StorageModels");
        if (storage == null) return;
        var types = storage.Descendants().Where(e => e.Name.LocalName == "EntityType")
            .ToDictionary(e => e.Attribute("Name")?.Value ?? "", e => e.Elements().Where(p => p.Name.LocalName == "Property").Select(p => p.Attribute("Name")?.Value).OfType<string>().ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var set in storage.Descendants().Where(e => e.Name.LocalName == "EntitySet"))
        {
            var typeName = (set.Attribute("EntityType")?.Value ?? "").Split('.')[^1];
            var table = set.Attribute("Table")?.Value ?? set.Attribute("Name")?.Value ?? typeName;
            var schema = set.Attribute("Schema")?.Value;
            var isView = set.Attribute("Type")?.Value?.Equals("Views", StringComparison.OrdinalIgnoreCase) == true;
            var access = isView ? "EDMX (view)" : "EDMX (EF6 Database First)";
            var entry = Add(scan, project, (schema != null ? schema + "." : "") + table, DataObjectKind.Table, "EF (leitura)", access, path, [], 0);
            if (!isView) entry.Operations.Add("EF (escrita)");
            if (types.TryGetValue(typeName, out var columns)) foreach (var c in columns) entry.Columns.Add(c);
        }
        foreach (var function in storage.Descendants().Where(e => e.Name.LocalName == "Function" && e.Attribute("IsComposable")?.Value != "true"))
        {
            var name = function.Attribute("StoreFunctionName")?.Value ?? function.Attribute("Name")?.Value ?? "";
            if (name.Length == 0) continue;
            var schema = function.Attribute("Schema")?.Value;
            var entry = Add(scan, project, (schema != null ? schema + "." : "") + name, DataObjectKind.StoredProcedure, "EXEC", "EDMX (function import)", path, [], 0);
            foreach (var p in function.Elements().Where(e => e.Name.LocalName == "Parameter")) entry.Columns.Add("@" + (p.Attribute("Name")?.Value ?? ""));
        }
    }

    // ---------------------------------------------------------------- SQL parsing

    private static void ScanSql(DataAccessScan scan, ProjectInfo project, string path, string sql, string access, int? line, SortedSet<string> connectionNames, int offset = 0)
    {
        var clean = StripComments(sql);
        var ctes = Cte().Matches(clean).Select(m => Name(m.Groups["name"].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var regions = Regions(clean);
        foreach (var (start, end, keyword) in regions)
        {
            var region = clean[start..end];
            var tables = new List<(string Raw, string? Alias, string Operation, DataObjectKind Kind)>();
            void Table(string raw, string? alias, string op, DataObjectKind kind = DataObjectKind.Table)
            {
                var last = Name(raw.Split('.')[^1].Trim());
                if (last.Length == 0 || last.StartsWith('#') || last.StartsWith('@') || Keywords.Contains(last) || ctes.Contains(last)) return;
                var parts = raw.Split('.').Select(p => Name(p.Trim())).ToList();
                if (parts.Count >= 2 && SkippedSchemas.Contains(parts[^2])) return;
                if (tables.Any(t => t.Raw.Equals(raw, StringComparison.OrdinalIgnoreCase) && (t.Operation == op || keyword is "DELETE" or "UPDATE"))) return;
                tables.Add((raw, alias, op, kind));
            }

            switch (keyword)
            {
                case "INSERT":
                    foreach (Match m in InsertInto().Matches(region)) Table(m.Groups["table"].Value, null, "INSERT");
                    break;
                case "UPDATE":
                    foreach (Match m in UpdateTarget().Matches(region)) Table(m.Groups["table"].Value, null, "UPDATE");
                    break;
                case "DELETE":
                    foreach (Match m in DeleteTarget().Matches(region)) Table(m.Groups["table"].Value, null, "DELETE");
                    break;
                case "MERGE":
                    foreach (Match m in MergeTarget().Matches(region)) Table(m.Groups["table"].Value, m.Groups["alias"].Success ? m.Groups["alias"].Value : null, "MERGE");
                    break;
                case "TRUNCATE":
                    foreach (Match m in TruncateTarget().Matches(region)) Table(m.Groups["table"].Value, null, "TRUNCATE");
                    break;
                case "EXEC":
                    foreach (Match m in ExecProcedure().Matches(region)) Table(m.Groups["proc"].Value, null, "EXEC", DataObjectKind.StoredProcedure);
                    break;
            }
            foreach (Match m in FromTables().Matches(region))
            {
                var list = m.Groups["list"].Value;
                foreach (Match t in FromListItem().Matches(list))
                    Table(t.Groups["table"].Value, t.Groups["alias"].Success ? t.Groups["alias"].Value : null, keyword is "SELECT" or "INSERT" or "MERGE" or "WITH" ? "SELECT" : keyword == "DELETE" && tables.Count == 0 ? "DELETE" : "SELECT");
            }
            foreach (Match m in JoinTable().Matches(region)) Table(m.Groups["table"].Value, m.Groups["alias"].Success ? m.Groups["alias"].Value : null, "SELECT");

            // "UPDATE a SET ... FROM Tabela a": the target is an alias of a real table.
            var aliases = tables.Where(t => t.Alias != null).ToDictionary(t => t.Alias!, t => t.Raw, StringComparer.OrdinalIgnoreCase);
            var resolved = new List<(string Raw, string Operation, DataObjectKind Kind)>();
            foreach (var t in tables)
            {
                var raw = aliases.TryGetValue(Name(t.Raw), out var real) && !real.Equals(t.Raw, StringComparison.OrdinalIgnoreCase) ? real : t.Raw;
                if (resolved.Any(r => r.Raw.Equals(raw, StringComparison.OrdinalIgnoreCase) && r.Operation == t.Operation)) continue;
                resolved.Add((raw, t.Operation, t.Kind));
            }
            if (resolved.Count == 0) continue;

            var location = line is { } l ? $"{path}:{l}" : path;
            var entries = resolved.Select(r => (Entry: Add(scan, project, r.Raw, r.Kind, r.Operation, access, location, connectionNames, offset), r.Raw, r.Operation)).ToList();
            var primary = entries.FirstOrDefault(e => e.Operation != "SELECT").Entry ?? entries[0].Entry;
            TableAccess ByAlias(string? alias)
            {
                if (alias == null) return primary;
                if (aliases.TryGetValue(alias, out var raw)) return entries.FirstOrDefault(e => e.Raw.Equals(raw, StringComparison.OrdinalIgnoreCase)).Entry ?? primary;
                return entries.FirstOrDefault(e => Name(e.Raw.Split('.')[^1]).Equals(alias, StringComparison.OrdinalIgnoreCase)).Entry ?? primary;
            }
            if (keyword == "EXEC")
            {
                var named = ExecNamedParameter().Matches(region).Select(p => p.Groups["name"].Value).ToList();
                foreach (var name in named.Count > 0 ? named : ExecParameter().Matches(region).Select(p => p.Groups["name"].Value).ToList())
                    primary.Columns.Add("@" + name);
                continue;
            }
            foreach (var (alias, column) in Columns(region, keyword))
            {
                if (Keywords.Contains(column)) continue;
                ByAlias(alias).Columns.Add(column);
            }
        }
    }

    /// <summary>Statement regions: from each statement keyword to the next one (sub-selects count as regions of their own).</summary>
    private static List<(int Start, int End, string Keyword)> Regions(string sql)
    {
        var matches = StatementKeyword().Matches(sql);
        var regions = new List<(int, int, string)>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : sql.Length;
            var keyword = matches[i].Value.ToUpperInvariant();
            if (keyword.StartsWith("EXEC", StringComparison.Ordinal)) keyword = "EXEC";
            regions.Add((start, end, keyword));
        }
        return regions;
    }

    private static IEnumerable<(string? Alias, string Column)> Columns(string region, string keyword)
    {
        var results = new List<(string?, string)>();
        void Item(string item)
        {
            item = item.Trim();
            if (item.Length == 0 || item == "*" || item.EndsWith(".*", StringComparison.Ordinal)) { if (item.Length > 0) results.Add((item.EndsWith(".*") ? item[..^2] : null, "*")); return; }
            // "Alias = Coluna" (T-SQL) → the right side is the column; "Coluna AS Alias" / "Coluna Alias" → the left side.
            var eq = Regex.Match(item, @"^([A-Za-z_]\w*)\s*=\s*(.+)$");
            if (eq.Success) item = eq.Groups[2].Value;
            if (item.Contains('('))
            {
                var inner = string.Join(" ", InsideParentheses().Matches(item).Select(m => m.Groups["inner"].Value));
                foreach (Match c in ColumnRef().Matches(inner))
                    if (!Keywords.Contains(c.Groups["col"].Value) && !IsFunctionName(inner, c) && !c.Groups["col"].Value.StartsWith('@')) results.Add((c.Groups["alias"].Success ? c.Groups["alias"].Value : null, c.Groups["col"].Value));
                return;
            }
            var first = ColumnRef().Match(item);
            if (first.Success && first.Index == 0 && !Keywords.Contains(first.Groups["col"].Value) && !item.StartsWith('\'') && !char.IsDigit(item[0]) && !item.StartsWith('@'))
                results.Add((first.Groups["alias"].Success ? first.Groups["alias"].Value : null, first.Groups["col"].Value));
        }

        if (keyword == "SELECT")
        {
            var list = SelectList().Match(region);
            if (list.Success) foreach (var item in SplitTopLevel(list.Groups["list"].Value)) Item(item);
        }
        if (keyword == "INSERT")
        {
            var cols = InsertColumns().Match(region);
            if (cols.Success) foreach (var item in SplitTopLevel(cols.Groups["cols"].Value)) Item(item);
        }
        if (keyword == "UPDATE")
        {
            var set = UpdateSet().Match(region);
            if (set.Success)
                foreach (var assignment in SplitTopLevel(set.Groups["set"].Value))
                {
                    var left = assignment.Split('=', 2)[0].Trim();
                    var c = ColumnRef().Match(left);
                    if (c.Success && !Keywords.Contains(c.Groups["col"].Value)) results.Add((c.Groups["alias"].Success ? c.Groups["alias"].Value : null, c.Groups["col"].Value));
                }
        }
        // WHERE / ON / HAVING: identifiers next to a comparison; ORDER BY / GROUP BY lists.
        foreach (Match m in ConditionColumn().Matches(region))
            foreach (var g in new[] { "left", "right" })
                if (m.Groups[g].Success)
                {
                    var refMatch = ColumnRef().Match(m.Groups[g].Value);
                    if (refMatch.Success && !Keywords.Contains(refMatch.Groups["col"].Value) && !IsFunctionName(region, refMatch, m.Groups[g].Index))
                        results.Add((refMatch.Groups["alias"].Success ? refMatch.Groups["alias"].Value : null, refMatch.Groups["col"].Value));
                }
        foreach (Match m in OrderGroupBy().Matches(region))
            foreach (var item in SplitTopLevel(m.Groups["list"].Value))
            {
                var c = ColumnRef().Match(item.Trim());
                if (c.Success && c.Index == 0 && !Keywords.Contains(c.Groups["col"].Value) && !char.IsDigit(item.Trim()[0])) results.Add((c.Groups["alias"].Success ? c.Groups["alias"].Value : null, c.Groups["col"].Value));
            }
        return results.Select(r => (r.Item1, Name(r.Item2))).Where(r => r.Item2.Length > 0).Distinct();
    }

    private static bool IsFunctionName(string text, Match column, int baseOffset = 0)
    {
        var after = baseOffset + column.Index + column.Length;
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
        return after < text.Length && text[after] == '(';
    }

    private static IEnumerable<string> SplitTopLevel(string list)
    {
        var depth = 0; var start = 0;
        for (var i = 0; i < list.Length; i++)
        {
            if (list[i] == '(') depth++;
            else if (list[i] == ')') depth--;
            else if (list[i] == ',' && depth == 0) { yield return list[start..i]; start = i + 1; }
        }
        yield return list[start..];
    }

    private static string StripComments(string sql) => BlockComment().Replace(LineComment().Replace(sql, " "), " ");

    private static string Name(string identifier) => identifier.Trim().Trim('[', ']', '"', '`').Trim();

    private static bool LooksLikeSql(string s) => SqlShape().IsMatch(s) && (UppercaseVerb().IsMatch(s) || SqlStructure().IsMatch(s));

    private static string TechnologyOfCode(string text)
    {
        if (text.Contains("OracleConnection", StringComparison.Ordinal) || text.Contains("Oracle.ManagedDataAccess", StringComparison.Ordinal) || text.Contains("OracleCommand", StringComparison.Ordinal)) return "Oracle";
        if (text.Contains("MySqlConnection", StringComparison.Ordinal) || text.Contains("MySqlCommand", StringComparison.Ordinal)) return "MySQL";
        if (text.Contains("NpgsqlConnection", StringComparison.Ordinal) || text.Contains("NpgsqlCommand", StringComparison.Ordinal)) return "PostgreSQL";
        if (text.Contains("SQLiteConnection", StringComparison.Ordinal) || text.Contains("SqliteConnection", StringComparison.Ordinal)) return "SQLite";
        if (text.Contains("OleDbConnection", StringComparison.Ordinal) || text.Contains("OdbcConnection", StringComparison.Ordinal)) return "OLE DB/ODBC";
        if (text.Contains("SqlConnection", StringComparison.Ordinal) || text.Contains("SqlCommand", StringComparison.Ordinal) || text.Contains("System.Data.SqlClient", StringComparison.Ordinal) || text.Contains("Microsoft.Data.SqlClient", StringComparison.Ordinal)) return "SQL Server";
        return null!;
    }

    private static string? TechnologyOfPackage(string id) => id switch
    {
        _ when id.StartsWith("Oracle.", StringComparison.OrdinalIgnoreCase) => "Oracle",
        _ when id.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) => "MySQL",
        _ when id.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase) => "PostgreSQL",
        _ when id.Contains("SQLite", StringComparison.OrdinalIgnoreCase) => "SQLite",
        _ when id.Equals("System.Data.SqlClient", StringComparison.OrdinalIgnoreCase) || id.Equals("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase) || id.Equals("EntityFramework", StringComparison.OrdinalIgnoreCase) => "SQL Server",
        _ => null
    };

    private static string AccessOf(string text)
    {
        if (text.Contains("using Dapper", StringComparison.Ordinal) || text.Contains("Imports Dapper", StringComparison.OrdinalIgnoreCase) || DapperCall().IsMatch(text)) return "Dapper";
        if (EfRawCall().IsMatch(text)) return "EF (SQL)";
        if (text.Contains("Command(", StringComparison.Ordinal) || text.Contains("CommandText", StringComparison.Ordinal) || text.Contains("DataAdapter", StringComparison.Ordinal)) return "ADO.NET";
        return "ADO.NET";
    }

    private static SortedSet<string> ConnectionNames(string text, bool visualBasic)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ConnectionStringName().Matches(text)) names.Add(m.Groups["name"].Value);
        return names;
    }

    private static bool IsCommandText(string text, int literalOffset)
    {
        var start = Math.Max(0, literalOffset - 80);
        var before = text[start..literalOffset];
        return CommandTextContext().IsMatch(before);
    }

    private static TableAccess? Nearest(List<TableAccess> tables, Dictionary<TableAccess, int> offsets, int position, Func<TableAccess, bool> filter)
    {
        var candidates = tables.Where(filter).Where(offsets.ContainsKey).ToList();
        if (candidates.Count == 0) return null;
        var before = candidates.Where(t => offsets[t] <= position).OrderByDescending(t => offsets[t]).FirstOrDefault();
        return before ?? candidates.OrderBy(t => offsets[t]).First();
    }

    private static TableAccess Add(DataAccessScan scan, ProjectInfo project, string rawName, DataObjectKind kind, string operation, string access, string location, SortedSet<string> connectionNames, int offset)
    {
        var parts = rawName.Split('.').Select(p => Name(p)).Where(p => p.Length > 0).ToList();
        string? database = null, schema = null;
        var name = parts[^1];
        if (parts.Count == 3) { database = parts[0]; schema = parts[1]; }
        else if (parts.Count == 2) schema = parts[0];

        var existing = scan.Tables.FirstOrDefault(t => t.Kind == kind && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                                        && string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase)
                                                        && (database == null || !t.DatabaseFromSql || t.Database.Equals(database, StringComparison.OrdinalIgnoreCase)));
        if (existing == null)
        {
            existing = new TableAccess { Project = project.Name, Name = name, Kind = kind, Schema = schema };
            scan.Tables.Add(existing);
            scan.Offsets[existing] = offset;
        }
        if (database != null) { existing.Database = database; existing.DatabaseFromSql = true; existing.DatabaseResolved = true; }
        existing.Operations.Add(operation);
        existing.Access.Add(access);
        existing.AddLocation(location);
        foreach (var n in connectionNames) existing.ConnectionNames.Add(n);
        return existing;
    }

    // ---------------------------------------------------------------- string literals

    internal sealed record LogicalString(string Value, int Offset);

    /// <summary>String literals of a C#/VB file; pieces joined by + / &amp; (with optional line continuation) become one string.</summary>
    internal static List<LogicalString> LogicalStrings(string text, bool visualBasic)
    {
        var result = new List<LogicalString>();
        var sb = new StringBuilder();
        var current = -1;
        var lastEnd = -1;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (!visualBasic && c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (!visualBasic && c == '/' && i + 1 < text.Length && text[i + 1] == '*') { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? text.Length : end + 2; continue; }
            if (visualBasic && c == '\'') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (!visualBasic && c == '\'' ) { i++; while (i < text.Length && text[i] != '\'' ) { if (text[i] == '\\') i++; i++; } i++; continue; }

            int start = i; string? literal = null;
            if (!visualBasic && c == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"')
            {
                var end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                literal = end < 0 ? text[(i + 3)..] : text[(i + 3)..end];
                i = end < 0 ? text.Length : end + 3;
            }
            else if (!visualBasic && (c == '@' || c == '$') && i + 1 < text.Length && (text[i + 1] == '"' || (text[i + 1] is '@' or '$' && i + 2 < text.Length && text[i + 2] == '"')))
            {
                var verbatim = c == '@' || text[i + 1] == '@';
                var quote = text.IndexOf('"', i);
                (literal, i) = ReadQuoted(text, quote + 1, doubledEscape: verbatim, backslashEscape: !verbatim);
                literal = Interpolation().Replace(literal, "@p");
            }
            else if (c == '"')
            {
                (literal, i) = ReadQuoted(text, i + 1, doubledEscape: visualBasic, backslashEscape: !visualBasic);
            }
            if (literal == null) { i++; continue; }

            var joined = lastEnd >= 0 && Concatenation().IsMatch(text[lastEnd..start]);
            if (!joined)
            {
                if (current >= 0) result.Add(new LogicalString(sb.ToString(), current));
                sb.Clear();
                current = start;
            }
            sb.Append(literal);
            lastEnd = i;
        }
        if (current >= 0) result.Add(new LogicalString(sb.ToString(), current));
        return result;
    }

    private static (string Value, int Next) ReadQuoted(string text, int start, bool doubledEscape, bool backslashEscape)
    {
        var sb = new StringBuilder();
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                if (doubledEscape && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                return (sb.ToString(), i + 1);
            }
            if (backslashEscape && c == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1] == 'n' || text[i + 1] == 'r' || text[i + 1] == 't' ? ' ' : text[i + 1]); i += 2; continue; }
            if (!doubledEscape && c == '\n') return (sb.ToString(), i); // unterminated regular literal
            sb.Append(c);
            i++;
        }
        return (sb.ToString(), i);
    }

    // ---------------------------------------------------------------- entities

    private static IEnumerable<EntityInfo> EntityClasses(string text, bool visualBasic)
    {
        if (visualBasic) yield break;
        foreach (Match m in ClassDeclaration().Matches(text))
        {
            var open = text.IndexOf('{', m.Index + m.Length);
            if (open < 0) continue;
            var depth = 0; var close = -1;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) { close = i; break; }
            }
            if (close < 0) continue;
            var body = text[(open + 1)..close];
            var props = new List<(string, string, string?, bool)>();
            foreach (Match p in AutoProperty().Matches(body))
            {
                var attributes = p.Groups["attrs"].Value;
                var column = Regex.Match(attributes, @"\[Column\(\s*""(?<c>[^""]+)""");
                var ignored = attributes.Contains("NotMapped", StringComparison.Ordinal);
                props.Add((p.Groups["name"].Value, p.Groups["type"].Value.Trim(), column.Success ? column.Groups["c"].Value : null, ignored));
            }
            if (props.Count == 0) continue;
            var attrs = m.Groups["attrs"].Value;
            var table = Regex.Match(attrs, @"\[Table\(\s*""(?<t>[^""]+)""(?:\s*,\s*Schema\s*=\s*""(?<s>[^""]+)"")?");
            yield return new EntityInfo(m.Groups["name"].Value, props, table.Success ? table.Groups["t"].Value : null, table.Success && table.Groups["s"].Success ? table.Groups["s"].Value : null);
        }
    }

    /// <summary>EF6's default convention pluralizes the entity name (Produto → Produtos, Category → Categories, Status → Statuses).</summary>
    internal static string Pluralize(string name)
    {
        if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase) || name.EndsWith("x", StringComparison.OrdinalIgnoreCase) || name.EndsWith("z", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ch", StringComparison.OrdinalIgnoreCase) || name.EndsWith("sh", StringComparison.OrdinalIgnoreCase)) return name + "es";
        if (name.EndsWith("y", StringComparison.OrdinalIgnoreCase) && name.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(name[^2]))) return name[..^1] + "ies";
        return name + "s";
    }

    // ---------------------------------------------------------------- resolve (solution-wide)

    /// <summary>
    /// Turns DbSet references into tables (using entity classes from any project) and resolves the database of every entry:
    /// three-part names win, then the connection-string names the code mentions, then the single database known to the project,
    /// its hosts (projects that reference it) or its dependencies. Fills <see cref="ProjectResult.DataAccess"/>.
    /// </summary>
    public static void Resolve(IReadOnlyList<MigratedProject> projects)
    {
        var scans = projects.Where(p => p.Result.DataScan != null).ToDictionary(p => p, p => p.Result.DataScan!);
        if (scans.Count == 0) return;

        var entities = new Dictionary<string, EntityInfo>(StringComparer.Ordinal);
        var mappings = new Dictionary<string, (string? Table, string? Schema, Dictionary<string, string> Columns, HashSet<string> Ignored)>(StringComparer.Ordinal);
        foreach (var scan in scans.Values)
        {
            foreach (var (name, info) in scan.Entities) if (!entities.TryGetValue(name, out var e) || e.Properties.Count < info.Properties.Count) entities[name] = info;
            foreach (var (name, map) in scan.Mappings)
            {
                if (!mappings.TryGetValue(name, out var existing)) { mappings[name] = map; continue; }
                foreach (var (k, v) in map.Columns) existing.Columns[k] = v;
                foreach (var ig in map.Ignored) existing.Ignored.Add(ig);
                mappings[name] = (map.Table ?? existing.Table, map.Schema ?? existing.Schema, existing.Columns, existing.Ignored);
            }
        }
        var byConnectionName = projects.SelectMany(p => p.Profile.Databases).GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var dependents = projects.ToDictionary(p => p, p => projects.Where(o => o != p && o.Result.Project.ProjectReferences.Contains(p.Result.Project.ProjectPath, StringComparer.OrdinalIgnoreCase)).ToList());

        foreach (var (project, scan) in scans)
        {
            var efWrites = scan.EfWrites || PackageAligner.Closure(project, projects).Any(c => c.Result.DataScan?.EfWrites == true) || dependents[project].Any(d => d.Result.DataScan?.EfWrites == true);
            foreach (var reference in scan.EntityRefs)
            {
                entities.TryGetValue(reference.EntityType, out var entity);
                mappings.TryGetValue(reference.EntityType, out var map);
                var table = map.Table ?? entity?.Table ?? (reference.EfCore ? reference.PropertyName : Pluralize(reference.EntityType));
                var schema = map.Schema ?? entity?.Schema;
                var byConvention = map.Table == null && entity?.Table == null;
                var entry = Add(scan, project.Result.Project, (schema != null ? schema + "." : "") + table, DataObjectKind.Table, "EF (leitura)",
                    (reference.EfCore ? "EF Core" : "EF6") + (byConvention ? " (nome por convenção)" : ""), reference.Location ?? "", reference.ConnectionNames, 0);
                if (efWrites) entry.Operations.Add("EF (escrita)");
                if (entity != null)
                    foreach (var (property, type, column, ignored) in entity.Properties)
                    {
                        if (ignored || map.Ignored?.Contains(property) == true) continue;
                        var bare = type.TrimEnd('?');
                        var generic = bare.IndexOf('<');
                        if (generic > 0 && CollectionTypes.Contains(bare[..generic])) continue;
                        if (entities.ContainsKey(bare)) continue; // navigation property
                        entry.Columns.Add(map.Columns != null && map.Columns.TryGetValue(property, out var renamed) ? renamed : column ?? property);
                    }
                else entry.Columns.Add("(entidade não encontrada no código analisado)");
            }

            var own = project.Profile.Databases.ToList();
            var hosts = dependents[project].SelectMany(d => d.Profile.Databases).ToList();
            var deps = PackageAligner.Closure(project, projects).SelectMany(c => c.Profile.Databases).ToList();
            foreach (var table in scan.Tables)
            {
                if (table.DatabaseFromSql) { table.Technology = scan.TechnologyHint ?? table.Technology; continue; }
                var named = table.ConnectionNames.SelectMany(n => byConnectionName.GetValueOrDefault(n) ?? []).ToList();
                var candidates = named.Count > 0 ? named : [];
                foreach (var pool in new[] { own, hosts, deps })
                {
                    if (candidates.Count > 0) break;
                    candidates = pool.ToList();
                }
                if (scan.TechnologyHint != null && candidates.Select(c => c.Provider).Distinct().Count() > 1)
                    candidates = candidates.Where(c => c.Provider == scan.TechnologyHint).ToList();
                var distinct = candidates.Select(c => (Database: c.Database ?? c.Name, c.Provider)).Distinct().ToList();
                if (distinct.Count == 1 || (distinct.Count > 1 && named.Count > 0))
                {
                    table.Database = string.Join(" / ", distinct.Select(d => d.Database));
                    table.Technology = distinct[0].Provider;
                    table.DatabaseResolved = true;
                }
                else
                {
                    table.Technology = scan.TechnologyHint ?? distinct.Select(d => d.Provider).FirstOrDefault() ?? "SQL Server";
                    table.Database = distinct.Count == 0 ? "não identificado" : "não identificado (candidatos: " + string.Join(", ", distinct.Select(d => d.Database)) + ")";
                }
            }
            project.Result.DataAccess.Clear();
            project.Result.DataAccess.AddRange(scan.Tables.OrderBy(t => t.Database, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Kind).ThenBy(t => t.QualifiedName, StringComparer.OrdinalIgnoreCase));
        }
    }

    // ---------------------------------------------------------------- regexes

    [GeneratedRegex(@"\b(?:SELECT|INSERT|UPDATE|DELETE|MERGE|TRUNCATE|EXEC|EXECUTE|WITH(?!\s*\())\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatementKeyword();

    [GeneratedRegex(@"(?:\bSELECT\b[\s\S]*?\bFROM\s+" + QualifiedIdent + @")|(?:\bINSERT\s+INTO\s+" + QualifiedIdent + @")|(?:\bUPDATE\s+" + QualifiedIdent + @"\s+SET\b)|(?:\bDELETE\s+FROM\s+" + QualifiedIdent + @")|(?:\bDELETE\s+" + QualifiedIdent + @"\s+(?:FROM|WHERE)\b)|(?:\bMERGE\s+(?:INTO\s+)?" + QualifiedIdent + @")|(?:\bTRUNCATE\s+TABLE\s+" + QualifiedIdent + @")|(?:^\s*EXEC(?:UTE)?\s+" + QualifiedIdent + @")", RegexOptions.IgnoreCase)]
    private static partial Regex SqlShape();

    /// <summary>A SELECT written in prose ("Select the file from the list") has no second structural keyword and no uppercase verb.</summary>
    [GeneratedRegex(@"\b(?:WHERE|JOIN|VALUES|SET|ORDER\s+BY|GROUP\s+BY|TOP|DISTINCT|INTO|EXEC|EXECUTE|NOLOCK|UNION)\b|\*|@\w|\[\w", RegexOptions.IgnoreCase)]
    private static partial Regex SqlStructure();

    [GeneratedRegex(@"\b(?:SELECT|INSERT|UPDATE|DELETE|MERGE|TRUNCATE|EXEC|EXECUTE)\b")]
    private static partial Regex UppercaseVerb();

    [GeneratedRegex(@"\bFROM\s+(?<list>" + QualifiedIdent + @"(?:\s+(?!(?:WHERE|JOIN|INNER|LEFT|RIGHT|FULL|CROSS|ON|GROUP|ORDER|HAVING|UNION|SET|WITH|AS|OUTPUT|OPTION|FOR|LIMIT)\b)(?:AS\s+)?[A-Za-z_]\w*)?(?:\s*\(\s*NOLOCK\s*\))?(?:\s*,\s*" + QualifiedIdent + @"(?:\s+(?!(?:WHERE|JOIN|INNER|LEFT|RIGHT|FULL|CROSS|ON|GROUP|ORDER|HAVING|UNION|SET)\b)(?:AS\s+)?[A-Za-z_]\w*)?)*)", RegexOptions.IgnoreCase)]
    private static partial Regex FromTables();

    [GeneratedRegex(@"(?<table>" + QualifiedIdent + @")(?:\s+(?!(?:WHERE|JOIN|INNER|LEFT|RIGHT|FULL|CROSS|ON|GROUP|ORDER|HAVING|UNION|SET|WITH|AS|OUTPUT|OPTION|FOR|LIMIT|NOLOCK)\b)(?:AS\s+)?(?<alias>[A-Za-z_]\w*))?", RegexOptions.IgnoreCase)]
    private static partial Regex FromListItem();

    [GeneratedRegex(@"\bJOIN\s+(?<table>" + QualifiedIdent + @")(?:\s+(?!(?:ON|WITH|AS)\b)(?:AS\s+)?(?<alias>[A-Za-z_]\w*))?", RegexOptions.IgnoreCase)]
    private static partial Regex JoinTable();

    [GeneratedRegex(@"\bINSERT\s+(?:INTO\s+)?(?<table>" + QualifiedIdent + @")", RegexOptions.IgnoreCase)]
    private static partial Regex InsertInto();

    [GeneratedRegex(@"\bINSERT\s+(?:INTO\s+)?" + QualifiedIdent + @"\s*\((?<cols>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex InsertColumns();

    [GeneratedRegex(@"\bUPDATE\s+(?:TOP\s*\(\s*\d+\s*\)\s+)?(?<table>" + QualifiedIdent + @")\s+SET\b", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateTarget();

    [GeneratedRegex(@"\bSET\s+(?<set>[\s\S]*?)(?:\bWHERE\b|\bFROM\b|\bOUTPUT\b|;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateSet();

    [GeneratedRegex(@"\bDELETE\s+(?:TOP\s*\(\s*\d+\s*\)\s+)?(?:FROM\s+)?(?<table>" + QualifiedIdent + @")", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteTarget();

    [GeneratedRegex(@"\bMERGE\s+(?:INTO\s+)?(?<table>" + QualifiedIdent + @")(?:\s+(?:AS\s+)?(?<alias>[A-Za-z_]\w*))?", RegexOptions.IgnoreCase)]
    private static partial Regex MergeTarget();

    [GeneratedRegex(@"\bTRUNCATE\s+TABLE\s+(?<table>" + QualifiedIdent + @")", RegexOptions.IgnoreCase)]
    private static partial Regex TruncateTarget();

    [GeneratedRegex(@"\bEXEC(?:UTE)?\s+(?!sp_executesql\b)(?!@)(?<proc>" + QualifiedIdent + @")", RegexOptions.IgnoreCase)]
    private static partial Regex ExecProcedure();

    [GeneratedRegex(@"@(?<name>[A-Za-z_]\w*)\b")]
    private static partial Regex ExecParameter();

    [GeneratedRegex(@"@(?<name>[A-Za-z_]\w*)\s*=")]
    private static partial Regex ExecNamedParameter();

    [GeneratedRegex(@"\bWITH\s+(?<name>[A-Za-z_]\w*)\s*(?:\([^)]*\))?\s+AS\s*\(|,\s*(?<name>[A-Za-z_]\w*)\s*(?:\([^)]*\))?\s+AS\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex Cte();

    [GeneratedRegex(@"\bSELECT\s+(?:DISTINCT\s+)?(?:TOP\s*\(?\s*[\d@]\w*\s*\)?\s+(?:PERCENT\s+)?)?(?<list>[\s\S]*?)\s+(?:FROM\b|INTO\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SelectList();

    [GeneratedRegex(@"(?:(?<alias>[A-Za-z_]\w*)\s*\.\s*)?(?:\[(?<col>[^\]]+)\]|""(?<col>[^""]+)""|(?<col>[A-Za-z_]\w*))")]
    private static partial Regex ColumnRef();

    [GeneratedRegex(@"(?<![@:\w.\]])(?<left>(?:[A-Za-z_]\w*\s*\.\s*)?(?:\[[^\]]+\]|[A-Za-z_]\w*))\s*(?:=|<>|!=|>=|<=|>|<|\bLIKE\b|\bIN\b|\bIS\b|\bBETWEEN\b)\s*(?<right>(?:[A-Za-z_]\w*\s*\.\s*)?(?:\[[^\]]+\]|[A-Za-z_]\w*))?", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionColumn();

    [GeneratedRegex(@"\b(?:ORDER|GROUP)\s+BY\s+(?<list>[\s\S]*?)(?:\bHAVING\b|\bLIMIT\b|\bOFFSET\b|\bFOR\b|\bOPTION\b|\)|;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex OrderGroupBy();

    [GeneratedRegex(@"\((?<inner>[^()]*)\)")]
    private static partial Regex InsideParentheses();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*[\s\S]*?\*/")]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Interpolation();

    [GeneratedRegex(@"^\s*(?:[+&]\s*(?:_\s*)?)\s*$|^\s*_?\s*[\r\n]+\s*[+&]\s*$|^\s*[+&]\s*[\r\n]+\s*$", RegexOptions.Singleline)]
    private static partial Regex Concatenation();

    [GeneratedRegex(@"(?:reader|rdr|dr|row|dataRow|leitor|linha|registro|rs|record|rec|r|dataReader|sqlReader|item)\s*\[\s*""(?<col>[A-Za-z_]\w*)""\s*\]|\.GetOrdinal\(\s*""(?<col>[A-Za-z_]\w*)""\s*\)|\.Rows\[[^\]]+\]\s*\[\s*""(?<col>[A-Za-z_]\w*)""\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex ReaderColumnCs();

    [GeneratedRegex(@"(?:reader|rdr|dr|row|dataRow|leitor|linha|registro|rs|record|rec|dataReader|sqlReader|item)\s*\(\s*""(?<col>[A-Za-z_]\w*)""\s*\)|\.GetOrdinal\(\s*""(?<col>[A-Za-z_]\w*)""\s*\)|\.Item\(\s*""(?<col>[A-Za-z_]\w*)""\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ReaderColumnVb();

    [GeneratedRegex(@"Parameters\.(?:AddWithValue|Add)\s*\(\s*""[@:]?(?<name>[A-Za-z_]\w*)""|new\s+\w*Parameter\s*\(\s*""[@:]?(?<name>[A-Za-z_]\w*)""", RegexOptions.IgnoreCase)]
    private static partial Regex CommandParameter();

    [GeneratedRegex(@"^\s*(?:\[?[A-Za-z_][\w$]*\]?\.){0,2}\[?[A-Za-z_][\w$]*\]?\s*$")]
    private static partial Regex ProcedureName();

    [GeneratedRegex(@"(?:new\s+\w*Command\s*\(\s*|CommandText\s*=\s*|\.(?:Query|QueryFirst|QueryFirstOrDefault|QuerySingle|QuerySingleOrDefault|QueryMultiple|Execute|ExecuteScalar|ExecuteReader|SqlQuery|ExecuteSqlCommand|ExecuteSqlRaw|FromSqlRaw)(?:Async)?(?:<[^>]+>)?\s*\(\s*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CommandTextContext();

    [GeneratedRegex(@"\.(?:Query|QueryFirst|QueryFirstOrDefault|QuerySingle|QuerySingleOrDefault|QueryMultiple|Execute|ExecuteScalar|ExecuteReader)(?:Async)?(?:<[^>]+>)?\s*\(\s*\$?@?""(?<text>[\w\.\[\]]+)""[^;]*?commandType\s*:\s*CommandType\.StoredProcedure", RegexOptions.IgnoreCase)]
    private static partial Regex DapperProcedure();

    [GeneratedRegex(@"\.(?:Query|QueryFirst|QueryFirstOrDefault|QuerySingle|QuerySingleOrDefault|QueryMultiple|Execute|ExecuteScalar)(?:Async)?(?:<[^>]+>)?\s*\(")]
    private static partial Regex DapperCall();

    [GeneratedRegex(@"\.(?:SqlQuery|ExecuteSqlCommand|ExecuteSqlRaw|FromSqlRaw|FromSqlInterpolated|ExecuteSqlInterpolated)(?:Async)?\b")]
    private static partial Regex EfRawCall();

    [GeneratedRegex(@"ConnectionStrings\s*[\[\(]\s*""(?<name>[^""]+)""\s*[\]\)]|GetConnectionString\(\s*""(?<name>[^""]+)""\s*\)|""name=(?<name>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringName();

    [GeneratedRegex(@":\s*base\(\s*""name=(?<name>[^""]+)""\s*\)|:\s*base\(\s*""(?<name>[A-Za-z_]\w*)""\s*\)|MyBase\.New\(\s*""(?:name=)?(?<name>[^""]+)""\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex DbContextName();

    [GeneratedRegex(@"\bDbSet\s*\(?\s*(?:Of\s+)?<?\s*(?<entity>[A-Za-z_][\w\.]*)\s*>?\s*\)?\s+(?<prop>[A-Za-z_]\w*)\s*(?:\{|;|$|=)", RegexOptions.Multiline)]
    private static partial Regex DbSetDeclaration();

    [GeneratedRegex(@"Entity\s*<\s*(?<entity>[A-Za-z_][\w\.]*)\s*>\s*\(\s*\)[^;]*?\.ToTable\(\s*""(?<table>[^""]+)""(?:\s*,\s*""(?<schema>[^""]+)"")?", RegexOptions.Singleline)]
    private static partial Regex FluentToTable();

    [GeneratedRegex(@"Entity\s*<\s*(?<entity>[A-Za-z_][\w\.]*)\s*>\s*\(\s*\)[^;]*?\.Property\(\s*\w+\s*=>\s*\w+\.(?<prop>\w+)\s*\)[^;]*?\.HasColumnName\(\s*""(?<column>[^""]+)""", RegexOptions.Singleline)]
    private static partial Regex FluentColumn();

    [GeneratedRegex(@"Entity\s*<\s*(?<entity>[A-Za-z_][\w\.]*)\s*>\s*\(\s*\)[^;]*?\.Ignore\(\s*\w+\s*=>\s*\w+\.(?<prop>\w+)\s*\)", RegexOptions.Singleline)]
    private static partial Regex FluentIgnore();

    [GeneratedRegex(@"(?<attrs>(?:\[[^\]]*\]\s*)*)(?:public|internal)\s+(?:partial\s+)?(?:sealed\s+)?class\s+(?<name>[A-Za-z_]\w*)\b(?![\s\S]{0,80}?:\s*(?:Controller|ApiController|DbContext|ServiceBase|Form|Page|Attribute|Exception)\b)")]
    private static partial Regex ClassDeclaration();

    [GeneratedRegex(@"(?<attrs>(?:\[[^\]]*\]\s*)*)public\s+(?:virtual\s+)?(?!static\b|class\b|void\b|event\b)(?<type>[A-Za-z_][\w\.]*(?:\s*<[^;{}()]+?>)?(?:\[\])?\??)\s+(?<name>[A-Za-z_]\w*)\s*\{\s*(?:get|set)\b")]
    private static partial Regex AutoProperty();

    private sealed class LineIndex
    {
        private readonly List<int> _starts = [0];
        public LineIndex(string text) { for (var i = 0; i < text.Length; i++) if (text[i] == '\n') _starts.Add(i + 1); }
        public int LineOf(int offset)
        {
            var index = _starts.BinarySearch(offset);
            return (index < 0 ? ~index - 1 : index) + 1;
        }
    }
}
