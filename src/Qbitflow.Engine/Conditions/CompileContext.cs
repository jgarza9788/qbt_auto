namespace Qbitflow.Engine.Conditions;

internal class CompileContext
{
    private int _paramCounter;
    private int _aliasCounter;

    public Dictionary<string, object> Parameters { get; } = new();

    public string AddParameter(object value)
    {
        var name = $"$p{_paramCounter++}";
        Parameters[name] = value;
        return name;
    }

    public string NextAlias(string aliasPrefix) => $"{aliasPrefix}{_aliasCounter++}";
}
