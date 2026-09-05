namespace Qbitflow.Engine.Conditions.AdvancedSql;

public enum AdvancedSqlMode
{
    /// <summary>A boolean expression embedded into the standard "SELECT ... FROM torrents t WHERE {clause}".</summary>
    WhereClause,

    /// <summary>A complete SELECT the author controls end-to-end; must return instance_id and torrent_hash columns.</summary>
    FullQuery
}
