using System.Text.Json;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.Conditions;
using Qbitflow.Engine.Conditions;
using Xunit;

namespace Qbitflow.Tests.Engine;

public class ConditionSqlCompilerTests
{
    private const string Select = "SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM qbittorrent t WHERE ";

    private readonly ConditionSqlCompiler _compiler = new();

    /// <summary>An install with two qBittorrent instances, two history sources of different types, and one storage path.</summary>
    private static readonly FieldResolutionContext Configured = FieldResolutionContext.For(
        [
            new InstanceRef(1, "qbt1", SourceType.Qbittorrent),
            new InstanceRef(2, "qbt2", SourceType.Qbittorrent),
            new InstanceRef(3, "taut1", SourceType.Tautulli),
            new InstanceRef(4, "jf1", SourceType.Jellyfin)
        ],
        ["downloads"]);

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static ComparisonNode Cmp(string field, ComparisonOperator op, JsonElement? value = null) =>
        new() { Field = field, Operator = op, Value = value };

    [Fact]
    public void Compile_SimpleEquality_ProducesParameterizedSql()
    {
        var query = _compiler.Compile(Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("linux")));

        Assert.Equal(Select + "(t.category = $p0)", query.Sql);
        Assert.Equal("linux", query.Parameters["$p0"]);
    }

    [Fact]
    public void Compile_NamedTorrentInstance_RestrictsToThatInstance()
    {
        var query = _compiler.Compile(Cmp("qbittorrent.qbt2.category", ComparisonOperator.Eq, Json("linux")), Configured);

        Assert.Equal(Select + "(t.instance = $p0 AND (t.category = $p1))", query.Sql);
        Assert.Equal("qbt2", query.Parameters["$p0"]);
        Assert.Equal("linux", query.Parameters["$p1"]);
    }

    [Fact]
    public void Compile_AndGroup_JoinsChildrenWithAnd()
    {
        var tree = new GroupNode
        {
            Operator = LogicalOperator.And,
            Children =
            [
                Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("linux")),
                Cmp("qbittorrent.*.state", ComparisonOperator.Eq, Json("uploading"))
            ]
        };

        var query = _compiler.Compile(tree);

        Assert.Equal(Select + "((t.category = $p0) AND (t.state = $p1))", query.Sql);
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
                Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("linux")),
                Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("tv"))
            ]
        };

        var query = _compiler.Compile(tree);

        Assert.Equal(Select + "((t.category = $p0) OR (t.category = $p1))", query.Sql);
    }

    [Fact]
    public void Compile_NestedGroup_ProducesNestedParens()
    {
        var tree = new GroupNode
        {
            Operator = LogicalOperator.And,
            Children =
            [
                Cmp("qbittorrent.*.state", ComparisonOperator.Eq, Json("uploading")),
                new GroupNode
                {
                    Operator = LogicalOperator.Or,
                    Children =
                    [
                        Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("linux")),
                        Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("tv"))
                    ]
                }
            ]
        };

        var query = _compiler.Compile(tree);

        Assert.Equal(Select + "((t.state = $p0) AND ((t.category = $p1) OR (t.category = $p2)))", query.Sql);
    }

    [Fact]
    public void Compile_Not_WrapsChildInNegation()
    {
        var query = _compiler.Compile(new NotNode { Child = Cmp("qbittorrent.*.state", ComparisonOperator.Eq, Json("error")) });

        Assert.Equal(Select + "(NOT (t.state = $p0))", query.Sql);
    }

    [Fact]
    public void Compile_In_BindsOneParameterPerElement()
    {
        var query = _compiler.Compile(Cmp("qbittorrent.*.category", ComparisonOperator.In, Json(new[] { "linux", "tv", "movies" })));

        Assert.Equal(Select + "(t.category IN ($p0,$p1,$p2))", query.Sql);
        Assert.Equal("linux", query.Parameters["$p0"]);
        Assert.Equal("tv", query.Parameters["$p1"]);
        Assert.Equal("movies", query.Parameters["$p2"]);
    }

    [Fact]
    public void Compile_EmptyIn_IsAlwaysFalse()
    {
        var query = _compiler.Compile(Cmp("qbittorrent.*.category", ComparisonOperator.In, Json(Array.Empty<string>())));

        Assert.Equal(Select + "(0=1)", query.Sql);
    }

    [Fact]
    public void Compile_Contains_WrapsValueInWildcards()
    {
        var query = _compiler.Compile(Cmp("qbittorrent.*.tags", ComparisonOperator.Contains, Json("verified")));

        Assert.Equal(Select + "(t.tags LIKE $p0)", query.Sql);
        Assert.Equal("%verified%", query.Parameters["$p0"]);
    }

    [Fact]
    public void Compile_IsNull_NeedsNoParameter()
    {
        var query = _compiler.Compile(Cmp("qbittorrent.*.category", ComparisonOperator.IsNull));

        Assert.Equal(Select + "(t.category IS NULL)", query.Sql);
        Assert.Empty(query.Parameters);
    }

    [Fact]
    public void Compile_ComputedField_UsesUdfExpression()
    {
        var query = _compiler.Compile(Cmp("qbittorrent.*.size_gb", ComparisonOperator.Gt, Json(5.0)));

        Assert.Equal(Select + "(size_gb(t.size_bytes) > $p0)", query.Sql);
        Assert.Equal(5.0, query.Parameters["$p0"]);
    }

    [Fact]
    public void Compile_StorageField_CompilesToExistsOverNamedPath()
    {
        var query = _compiler.Compile(Cmp("storage.downloads.used_percent", ComparisonOperator.Gt, Json(85)), Configured);

        Assert.Equal(
            Select + "(EXISTS (SELECT 1 FROM storage storage0 WHERE storage0.instance = $p0 AND (storage0.used_percent > $p1)))",
            query.Sql);
        Assert.Equal("downloads", query.Parameters["$p0"]);
        Assert.Equal(85.0, query.Parameters["$p1"]);
    }

    [Fact]
    public void Compile_RelatedSourceRowField_AutoCorrelatesByPathKey()
    {
        var query = _compiler.Compile(Cmp("jellyfin.jf1.title", ComparisonOperator.Contains, Json("Foo")), Configured);

        Assert.Equal(
            Select + "(EXISTS (SELECT 1 FROM jellyfin jellyfin0 WHERE jellyfin0.path_key = t.path_key AND jellyfin0.instance = $p0 AND (jellyfin0.title LIKE $p1)))",
            query.Sql);
        Assert.Equal("jf1", query.Parameters["$p0"]);
        Assert.Equal("%Foo%", query.Parameters["$p1"]);
    }

    [Fact]
    public void Compile_RelatedSourceRowField_WithWildcard_OmitsInstanceFilter()
    {
        var query = _compiler.Compile(Cmp("tautulli.*.days_since_watched", ComparisonOperator.Lte, Json(90.0)), Configured);

        Assert.Equal(
            Select + "(EXISTS (SELECT 1 FROM tautulli tautulli0 WHERE tautulli0.path_key = t.path_key AND tautulli0.kind = $p0 AND (days_since(tautulli0.watched_at) <= $p1)))",
            query.Sql);
        Assert.Equal("history", query.Parameters["$p0"]);
        Assert.Equal(90.0, query.Parameters["$p1"]);
    }

    [Fact]
    public void Compile_AggregateField_CompilesToScalarSubquery()
    {
        var query = _compiler.Compile(Cmp("tautulli.taut1.play_count", ComparisonOperator.Eq, Json(0)), Configured);

        Assert.Equal(
            Select + "((SELECT COUNT(*) FROM tautulli tautulli0 WHERE tautulli0.path_key = t.path_key AND tautulli0.instance = $p0 AND tautulli0.kind = $p1) = $p2)",
            query.Sql);
        Assert.Equal("taut1", query.Parameters["$p0"]);
        Assert.Equal("history", query.Parameters["$p1"]);
        Assert.Equal(0L, query.Parameters["$p2"]);
    }

    [Fact]
    public void Compile_WrappedAggregateField_AppliesTheWrapper()
    {
        var query = _compiler.Compile(Cmp("tautulli.*.days_since_last_watched", ComparisonOperator.Gt, Json(180.0)), Configured);

        Assert.Contains("days_since((SELECT MAX(tautulli0.watched_at) FROM tautulli tautulli0", query.Sql);
    }

    [Fact]
    public void Compile_NotExists_CorrelatesByPathKeyAndNegates()
    {
        var tree = new ExistsNode
        {
            Source = "tautulli.taut1",
            Negate = true,
            Condition = Cmp("tautulli.taut1.days_since_watched", ComparisonOperator.Lte, Json(90.0))
        };

        var query = _compiler.Compile(tree, Configured);

        Assert.Equal(
            Select + "(NOT EXISTS (SELECT 1 FROM tautulli tautulli0 WHERE tautulli0.path_key = t.path_key AND tautulli0.instance = $p0 "
            + "AND (tautulli0.kind = $p1 AND (days_since(tautulli0.watched_at) <= $p2))))",
            query.Sql);
        Assert.Equal("taut1", query.Parameters["$p0"]);
        Assert.Equal(90.0, query.Parameters["$p2"]);
    }

    [Fact]
    public void Compile_Exists_WithoutNegate_OmitsNot()
    {
        var tree = new ExistsNode
        {
            Source = "tautulli.*",
            Negate = false,
            Condition = Cmp("tautulli.*.user_name", ComparisonOperator.Eq, Json("alice"))
        };

        var query = _compiler.Compile(tree, Configured);

        Assert.Contains("EXISTS (SELECT 1 FROM tautulli tautulli0", query.Sql);
        Assert.DoesNotContain("NOT EXISTS", query.Sql);
    }

    [Fact]
    public void Compile_Exists_RejectsFieldFromAnotherSource()
    {
        var tree = new ExistsNode
        {
            Source = "tautulli.taut1",
            Condition = Cmp("jellyfin.jf1.title", ComparisonOperator.Eq, Json("Foo"))
        };

        var ex = Assert.Throws<ConditionCompileException>(() => _compiler.Compile(tree, Configured));
        Assert.Contains("tautulli.taut1", ex.Message);
    }

    [Fact]
    public void Compile_Exists_RejectsAggregateInside()
    {
        var tree = new ExistsNode
        {
            Source = "tautulli.taut1",
            Condition = Cmp("tautulli.taut1.play_count", ComparisonOperator.Gt, Json(0))
        };

        var ex = Assert.Throws<ConditionCompileException>(() => _compiler.Compile(tree, Configured));
        Assert.Contains("aggregate", ex.Message);
    }

    [Fact]
    public void Compile_Exists_RejectsTheAnchorAsASource()
    {
        var tree = new ExistsNode
        {
            Source = "qbittorrent.*",
            Condition = Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("linux"))
        };

        Assert.Throws<ConditionCompileException>(() => _compiler.Compile(tree, Configured));
    }

    [Fact]
    public void Compile_TargetInstanceIds_AddsInstanceFilter()
    {
        var query = _compiler.Compile(Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("linux")), targetInstanceIds: [1, 2]);

        Assert.Equal(Select + "(t.category = $p0) AND t.instance_id IN ($p1,$p2)", query.Sql);
        Assert.Equal(1, query.Parameters["$p1"]);
        Assert.Equal(2, query.Parameters["$p2"]);
    }

    [Fact]
    public void Compile_EmptyAndGroup_IsVacuouslyTrue()
    {
        var query = _compiler.Compile(new GroupNode { Operator = LogicalOperator.And, Children = [] });
        Assert.Equal(Select + "(1=1)", query.Sql);
    }

    [Fact]
    public void Compile_EmptyOrGroup_IsVacuouslyFalse()
    {
        var query = _compiler.Compile(new GroupNode { Operator = LogicalOperator.Or, Children = [] });
        Assert.Equal(Select + "(0=1)", query.Sql);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("qbittorrent.category")]
    [InlineData("qbittorrent.*.category.extra")]
    public void Compile_MalformedFieldKey_Throws(string field)
    {
        var ex = Assert.Throws<ConditionCompileException>(() =>
            _compiler.Compile(Cmp(field, ComparisonOperator.Eq, Json("x"))));

        Assert.Contains(FieldKey.Shape, ex.Message);
    }

    [Fact]
    public void Compile_UnknownType_ListsTheValidOnes()
    {
        var ex = Assert.Throws<ConditionCompileException>(() =>
            _compiler.Compile(Cmp("plexx.*.title", ComparisonOperator.Eq, Json("x"))));

        Assert.Contains("plexx", ex.Message);
        Assert.Contains("jellyfin", ex.Message);
    }

    [Fact]
    public void Compile_UnknownField_ThrowsConditionCompileException()
    {
        var ex = Assert.Throws<ConditionCompileException>(() =>
            _compiler.Compile(Cmp("qbittorrent.*.not_a_real_field", ComparisonOperator.Eq, Json("x"))));

        Assert.Contains("not_a_real_field", ex.Message);
    }

    [Fact]
    public void Compile_UnknownInstance_NamesTheConfiguredOnes()
    {
        var ex = Assert.Throws<ConditionCompileException>(() =>
            _compiler.Compile(Cmp("tautulli.nope.user_name", ComparisonOperator.Eq, Json("x")), Configured));

        Assert.Contains("nope", ex.Message);
        Assert.Contains("taut1", ex.Message);
    }

    [Fact]
    public void Compile_InstanceOfTheWrongType_SaysWhatItActuallyIs()
    {
        var ex = Assert.Throws<ConditionCompileException>(() =>
            _compiler.Compile(Cmp("tautulli.jf1.user_name", ComparisonOperator.Eq, Json("x")), Configured));

        Assert.Contains("jellyfin instance", ex.Message);
    }

    [Fact]
    public void Compile_WithoutConfiguredInstances_SkipsOnlyTheInstanceCheck()
    {
        // The bundled example rules ship to installs whose instance names can't be known, so a
        // lenient compile must still accept a named instance -- while catching a bad field.
        _compiler.Compile(Cmp("tautulli.whatever.user_name", ComparisonOperator.Eq, Json("x")));

        Assert.Throws<ConditionCompileException>(() =>
            _compiler.Compile(Cmp("tautulli.whatever.nope", ComparisonOperator.Eq, Json("x"))));
    }
}
