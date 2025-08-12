namespace Arad.SMS.Core.DbReader.Models;

public class ScheduledJob(string name, TimeSpan interval, Func<CancellationToken, Task> action)
{
    public string Name { get; } = name;

    public Func<CancellationToken, Task> Action { get; } = action;

    public TimeSpan Interval { get; set; } = interval;

    public bool UseAlignment { get; set; } = false;

    public TimeSpan? AlignmentStartTime { get; set; } 
}