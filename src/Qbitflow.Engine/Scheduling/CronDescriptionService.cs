using CronExpressionDescriptor;

namespace Qbitflow.Engine.Scheduling;

/// <summary>Cron -> plain English, for showing next to the raw expression editor so authors aren't just guessing at cron syntax.</summary>
public static class CronDescriptionService
{
    public static string Describe(string cronExpression)
    {
        try
        {
            return ExpressionDescriptor.GetDescription(cronExpression);
        }
        catch (Exception ex)
        {
            return $"(unable to describe: {ex.Message})";
        }
    }
}
