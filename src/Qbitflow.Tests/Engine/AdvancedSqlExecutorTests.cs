using Microsoft.Data.Sqlite;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Engine.Conditions.AdvancedSql;
using Qbitflow.Snapshot;
using Xunit;

namespace Qbitflow.Tests.Engine;

public class AdvancedSqlExecutorTests : IDisposable
{
    private readonly SnapshotDatabase _db = new();
    private readonly AdvancedSqlExecutor _executor = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Validate_RejectsEmptySql()
    {
        var result = _executor.Validate(_db, "   ", AdvancedSqlMode.WhereClause);
        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorMessage);
    }

    [Fact]
    public void Validate_RejectsMultipleStatements()
    {
        var result = _executor.Validate(_db, "1=1; DROP TABLE torrents", AdvancedSqlMode.WhereClause);
        Assert.False(result.IsValid);
        Assert.Contains("single statement", result.ErrorMessage);
    }

    [Fact]
    public void Validate_RejectsForbiddenKeyword()
    {
        var result = _executor.Validate(_db, "1=1 OR (SELECT 1 WHERE 1 IN (DROP))", AdvancedSqlMode.WhereClause);
        Assert.False(result.IsValid);
        Assert.Contains("DROP", result.ErrorMessage);
    }

    [Fact]
    public void Validate_CatchesInvalidSql_ViaExplainQueryPlan()
    {
        var result = _executor.Validate(_db, "this_column_does_not_exist = 1", AdvancedSqlMode.WhereClause);
        Assert.False(result.IsValid);
        Assert.Contains("Invalid SQL", result.ErrorMessage);
    }

    [Fact]
    public void Validate_WhereClauseMode_AcceptsSimplePredicate()
    {
        var result = _executor.Validate(_db, "category = 'linux'", AdvancedSqlMode.WhereClause);
        Assert.True(result.IsValid);
        Assert.Contains("FROM torrents t WHERE category = 'linux'", result.CompiledSql);
    }

    [Fact]
    public void Validate_FullQueryMode_RejectsQueryMissingRequiredColumns()
    {
        var result = _executor.Validate(_db, "SELECT hash FROM torrents", AdvancedSqlMode.FullQuery);
        Assert.False(result.IsValid);
        Assert.Contains("instance_id", result.ErrorMessage);
        Assert.Contains("torrent_hash", result.ErrorMessage);
    }

    [Fact]
    public void Validate_FullQueryMode_AcceptsQueryWithRequiredColumns()
    {
        var result = _executor.Validate(_db, "SELECT instance_id, hash AS torrent_hash FROM torrents WHERE category = 'linux'", AdvancedSqlMode.FullQuery);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsMatchingRows()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "h1", Name = "A", Category = "linux", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "h2", Name = "B", Category = "tv", SizeBytes = 1, Progress = 1 }
            ]
        });

        var validation = _executor.Validate(_db, "category = 'linux'", AdvancedSqlMode.WhereClause);
        Assert.True(validation.IsValid);

        var matches = await _executor.ExecuteAsync(_db, validation.CompiledSql!);

        var match = Assert.Single(matches);
        Assert.Equal("h1", match.TorrentHash);
    }

    [Fact]
    public void OpenReadOnly_ActuallyRejectsWrites_AtTheSqliteLevel()
    {
        using var readOnly = AdvancedSqlExecutor.OpenReadOnly(_db);
        using var command = readOnly.CreateCommand();
        command.CommandText = "DELETE FROM torrents";

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void OpenReadOnly_StillAllowsReadsAgainstTheSameSnapshot()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents = [new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "h1", Name = "A", SizeBytes = 1, Progress = 1 }]
        });

        using var readOnly = AdvancedSqlExecutor.OpenReadOnly(_db);
        using var command = readOnly.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM torrents";

        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }
}
