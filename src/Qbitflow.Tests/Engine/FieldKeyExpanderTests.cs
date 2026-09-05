using Qbitflow.Engine.Conditions.AdvancedSql;
using Xunit;

namespace Qbitflow.Tests.Engine;

public class FieldKeyExpanderTests
{
    [Fact]
    public void Expand_RewritesComputedKey_IntoItsRegistryExpression()
    {
        Assert.Equal("(t.active_time_seconds / 86400.0) >= 14", FieldKeyExpander.Expand("active_days >= 14"));
    }

    [Fact]
    public void Expand_WrapsExpansionInParens_SoSurroundingOperatorsBind()
    {
        Assert.Equal("(t.eta_seconds / 3600.0) * 2 < 10", FieldKeyExpander.Expand("eta_hours * 2 < 10"));
    }

    [Fact]
    public void Expand_RewritesRenamedColumnKey()
    {
        Assert.Equal("(t.dl_speed_bps) > 0", FieldKeyExpander.Expand("download_speed_bps > 0"));
    }

    [Fact]
    public void Expand_RewritesBareUdfKey_ButLeavesTheUdfCallAlone()
    {
        Assert.Equal("(size_gb(t.size_bytes)) > 5", FieldKeyExpander.Expand("size_gb > 5"));
        Assert.Equal("size_gb(size_bytes) > 5", FieldKeyExpander.Expand("size_gb(size_bytes) > 5"));
    }

    [Fact]
    public void Expand_LeavesPlainColumnKeysUntouched()
    {
        const string sql = "category = 'linux' AND ratio > 1 AND seeding_time_seconds > 0";
        Assert.Equal(sql, FieldKeyExpander.Expand(sql));
    }

    [Fact]
    public void Expand_IgnoresKeyInsideStringLiteral()
    {
        Assert.Equal("name = 'active_days'", FieldKeyExpander.Expand("name = 'active_days'"));
    }

    [Fact]
    public void Expand_IgnoresQualifiedIdentifier()
    {
        Assert.Equal("x.active_days > 1", FieldKeyExpander.Expand("x.active_days > 1"));
    }

    [Fact]
    public void Expand_IgnoresKeyInsideComment()
    {
        Assert.Equal("ratio > 1 -- active_days here", FieldKeyExpander.Expand("ratio > 1 -- active_days here"));
        Assert.Equal("/* active_days */ ratio > 1", FieldKeyExpander.Expand("/* active_days */ ratio > 1"));
    }

    [Fact]
    public void Expand_DoesNotMatchKeyAsAnIdentifierSubstring()
    {
        Assert.Equal("my_active_days_col > 1", FieldKeyExpander.Expand("my_active_days_col > 1"));
    }

    [Fact]
    public void Expand_LeavesStorageFieldKeyForTheStorageExpander()
    {
        const string sql = "storage.downloads.free_gb < 100";
        Assert.Equal(sql, FieldKeyExpander.Expand(sql));
    }
}
