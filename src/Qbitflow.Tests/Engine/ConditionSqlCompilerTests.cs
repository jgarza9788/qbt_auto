using System.Text.Json;
using Qbitflow.Core.Domain.Conditions;
using Qbitflow.Engine.Conditions;
using Xunit;

namespace Qbitflow.Tests.Engine;

public class ConditionSqlCompilerTests
{
    private readonly ConditionSqlCompiler _compiler = new();

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static ComparisonNode Cmp(string field, ComparisonOperator op, JsonElement? value = null) =>
        new() { Field = field, Operator = op, Value = value };

    [Fact]
    public void Compile_SimpleEquality_ProducesParameterizedSql()
    {
        var query = _compiler.Compile(Cmp("category", ComparisonOperator.Eq, Json("linux")));

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE (t.category = $p0)",
            query.Sql);
        Assert.Equal("linux", query.Parameters["$p0"]);
    }

    [Fact]
    public void Compile_AndGroup_JoinsChildrenWithAnd()
    {
        var tree = new GroupNode
        {
            Operator = LogicalOperator.And,
            Children =
            [
                Cmp("category", ComparisonOperator.Eq, Json("linux")),
                Cmp("state", ComparisonOperator.Eq, Json("uploading"))
            ]
        };

        var query = _compiler.Compile(tree);

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE ((t.category = $p0) AND (t.state = $p1))",
            query.Sql);
        Assert.Equal("linux", query.Parameters["$p0"]);
        Assert.Equal("uploading", query.Parameters["$p1"]);
    }

    [Fact]
    public void Compile_OrGroup_JoinsChildrenWithOr()
    {
        var tree = new GroupNode
        {
            Operator = LogicalOperator.Or,
            Children =
            [
                Cmp("category", ComparisonOperator.Eq, Json("linux")),
                Cmp("category", ComparisonOperator.Eq, Json("tv"))
            ]
        };

        var query = _compiler.Compile(tree);

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE ((t.category = $p0) OR (t.category = $p1))",
            query.Sql);
    }

    [Fact]
    public void Compile_NestedGroup_ProducesNestedParens()
    {
        var tree = new GroupNode
        {
            Operator = LogicalOperator.And,
            Children =
            [
                Cmp("state", ComparisonOperator.Eq, Json("uploading")),
                new GroupNode
                {
                    Operator = LogicalOperator.Or,
                    Children =
                    [
                        Cmp("category", ComparisonOperator.Eq, Json("linux")),
                        Cmp("category", ComparisonOperator.Eq, Json("tv"))
                    ]
                }
            ]
        };

        var query = _compiler.Compile(tree);

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE ((t.state = $p0) AND ((t.category = $p1) OR (t.category = $p2)))",
            query.Sql);
    }

    [Fact]
    public void Compile_Not_WrapsChildInNegation()
    {
        var query = _compiler.Compile(new NotNode { Child = Cmp("state", ComparisonOperator.Eq, Json("error")) });

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE (NOT (t.state = $p0))",
            query.Sql);
    }

    [Fact]
    public void Compile_In_BindsOneParameterPerElement()
    {
        var query = _compiler.Compile(Cmp("category", ComparisonOperator.In, Json(new[] { "linux", "tv", "movies" })));

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE (t.category IN ($p0,$p1,$p2))",
            query.Sql);
        Assert.Equal("linux", query.Parameters["$p0"]);
        Assert.Equal("tv", query.Parameters["$p1"]);
        Assert.Equal("movies", query.Parameters["$p2"]);
    }

    [Fact]
    public void Compile_EmptyIn_IsAlwaysFalse()
    {
        var query = _compiler.Compile(Cmp("category", ComparisonOperator.In, Json(Array.Empty<string>())));

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE (0=1)",
            query.Sql);
    }

    [Fact]
    public void Compile_Contains_WrapsValueInWildcards()
    {
        var query = _compiler.Compile(Cmp("tags", ComparisonOperator.Contains, Json("verified")));

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE (t.tags LIKE $p0)",
            query.Sql);
        Assert.Equal("%verified%", query.Parameters["$p0"]);
    }

    [Fact]
    public void Compile_IsNull_NeedsNoParameter()
    {
        var query = _compiler.Compile(Cmp("category", ComparisonOperator.IsNull));

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE (t.category IS NULL)",
            query.Sql);
        Assert.Empty(query.Parameters);
    }

    [Fact]
    public void Compile_ComputedField_UsesUdfExpression()
    {
        var query = _compiler.Compile(Cmp("size_gb", ComparisonOperator.Gt, Json(5.0)));

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE (size_gb(t.size_bytes) > $p0)",
            query.Sql);
        Assert.Equal(5.0, query.Parameters["$p0"]);
    }

    [Fact]
    public void Compile_StorageField_CompilesToScalarSubquery()
    {
        var query = _compiler.Compile(Cmp("storage.downloads.used_percent", ComparisonOperator.Gt, Json(85)));

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE ((SELECT used_percent FROM storage_paths WHERE name = $p0) > $p1)",
            query.Sql);
        Assert.Equal("downloads", query.Parameters["$p0"]);
        Assert.Equal(85.0, query.Parameters["$p1"]);
    }

    [Fact]
    public void Compile_NotExists_CorrelatesByPathKeyAndNegates()
    {
        var tree = new ExistsNode
        {
            Relation = "watch_history",
            Negate = true,
            Condition = Cmp("days_since_watched", ComparisonOperator.Lte, Json(90.0))
        };

        var query = _compiler.Compile(tree);

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE " +
            "(NOT EXISTS (SELECT 1 FROM watch_history wh0 WHERE wh0.path_key = t.path_key AND (days_since(wh0.watched_at) <= $p0)))",
            query.Sql);
        Assert.Equal(90.0, query.Parameters["$p0"]);
    }

    [Fact]
    public void Compile_Exists_WithoutNegate_OmitsNot()
    {
        var tree = new ExistsNode
        {
            Relation = "watch_history",
            Negate = false,
            Condition = Cmp("user_name", ComparisonOperator.Eq, Json("alice"))
        };

        var query = _compiler.Compile(tree);

        Assert.Contains("EXISTS (SELECT 1 FROM watch_history wh0", query.Sql);
        Assert.DoesNotContain("NOT EXISTS", query.Sql);
    }

    [Fact]
    public void Compile_TargetInstanceIds_AddsInstanceFilter()
    {
        var query = _compiler.Compile(Cmp("category", ComparisonOperator.Eq, Json("linux")), targetInstanceIds: [1, 2]);

        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE (t.category = $p0) AND t.instance_id IN ($p1,$p2)",
            query.Sql);
        Assert.Equal(1, query.Parameters["$p1"]);
        Assert.Equal(2, query.Parameters["$p2"]);
    }

    [Fact]
    public void Compile_EmptyAndGroup_IsVacuouslyTrue()
    {
        var query = _compiler.Compile(new GroupNode { Operator = LogicalOperator.And, Children = [] });
        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE (1=1)",
            query.Sql);
    }

    [Fact]
    public void Compile_EmptyOrGroup_IsVacuouslyFalse()
    {
        var query = _compiler.Compile(new GroupNode { Operator = LogicalOperator.Or, Children = [] });
        Assert.Equal(
            "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE (0=1)",
            query.Sql);
    }

    [Fact]
    public void Compile_UnknownField_ThrowsConditionCompileException()
    {
        var ex = Assert.Throws<ConditionCompileException>(() =>
            _compiler.Compile(Cmp("not_a_real_field", ComparisonOperator.Eq, Json("x"))));

        Assert.Contains("not_a_real_field", ex.Message);
    }

    [Fact]
    public void Compile_UnknownRelationInExists_Throws()
    {
        var tree = new ExistsNode { Relation = "not_a_relation", Condition = Cmp("x", ComparisonOperator.Eq, Json("y")) };
        Assert.Throws<ConditionCompileException>(() => _compiler.Compile(tree));
    }
}
