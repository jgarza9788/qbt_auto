namespace Qbitflow.Engine.Conditions;

public enum FieldValueType
{
    Text,
    Integer,
    Real,
    Boolean,

    /// <summary>An ISO-8601 timestamp column -- comparison values are parsed and re-emitted as ISO-8601 text so they compare correctly against how the snapshot stores dates.</summary>
    DateTime
}
