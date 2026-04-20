using Arad.SMS.Core.WorkerForDownstreamGateway.DbReader.Models;
using Serilog;

namespace Arad.SMS.Core.WorkerForDownstreamGateway.DbReader.Services;

public class TimedWorker: BackgroundService
{
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    public List<ScheduledJob> ScheduledJobs { get; set; } = [];
    public List<Task> JobTasks { get; set; } = [];
    private bool _disposed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)  
    {
        try
        {
            Log.Information("Execute service");

            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, CancellationTokenSource.Token);
            CancellationToken cancellationToken = linkedCts.Token;
            
            await RuntimeSettings.LoadSetting(cancellationToken);
            
            ScheduledJobs.Add(new ("LoadSetting", TimeSpan.FromMinutes(1), RuntimeSettings.LoadSetting));
            
            foreach (Task jobTask in ScheduledJobs.Select(job => RunJobLoop(job, cancellationToken)))
            {
                JobTasks.Add(jobTask);
            }

            await Task.WhenAll(JobTasks);
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
        }
    }
    
    private async Task RunJobLoop(ScheduledJob job, CancellationToken token)
    {
        Log.Information($"Started job : {job.Name}");

        while (!token.IsCancellationRequested)
        {
            int failureCount = 0;
            const int maxFailures = 5;

            try
            {
                if (job is { UseAlignment: true, AlignmentStartTime: not null })
                {
                    DateTime now = DateTime.Now;
                    DateTime today = now.Date + job.AlignmentStartTime.Value;

                    DateTime nextRunTime;
                    if (now < today)
                    {
                        nextRunTime = today;
                    }
                    else
                    {
                        int intervalsPassed = (int)((now - today).TotalMilliseconds / job.Interval.TotalMilliseconds) + 1;
                        nextRunTime = today.AddMilliseconds(intervalsPassed * job.Interval.TotalMilliseconds);
                    }

                    TimeSpan delay = nextRunTime - now;
                    Log.Information($"Delaying {job.Name} until aligned time: {nextRunTime}");
                    await Task.Delay(delay, token);
                }
                
                PeriodicTimer timer = new (job.Interval);
                
                while (await timer.WaitForNextTickAsync(token))
                {
                    //Log.Information($"Running {job.Name} at {DateTimeOffset.Now}");
                    await job.Action(token);

                    failureCount = 0;
                }
            }
            catch (OperationCanceledException)
            {
                Log.Error($"{job.Name} canceled.");
                break;
            }
            catch (Exception ex)
            {
                failureCount++;
                Log.Information(ex, $"Error in job {job.Name} (failure #{failureCount})");

                if (failureCount >= maxFailures)
                {
                    failureCount = 0;//TODO
                    Log.Error($"Job {job.Name} failed {failureCount} times. Triggering fallback/alerting and stopping.");
                    //await TriggerJobAlertAsync(job, ex, token);
                    
                    //break;
                }
            }
        }
        
        Log.Information($"Stopped job : {job.Name}");
    }
    
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("Worker starting...");
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationTokenSource.Token);
            await base.StartAsync(linkedCts.Token);
        }
        catch (Exception ex)
        {
            Log.Error($"Error starting Worker {ex.Message}");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("Worker stopping...");
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationTokenSource.Token);
        await base.StopAsync(linkedCts.Token);
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Log.Information("Disposing Worker...");
            CancellationTokenSource.Cancel();
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            CancellationTokenSource.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Error disposing Worker {ex.Message}");
        }
        _disposed = true;
    }
}