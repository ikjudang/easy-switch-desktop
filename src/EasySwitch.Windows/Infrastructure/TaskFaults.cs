namespace EasySwitch.Windows.Infrastructure;

internal static class TaskFaults
{
    // Fire-and-forget work keeps its timing: the continuation is attached after the work has
    // started and runs only on failure, so a failure reaches the log instead of vanishing.
    internal static void LogFaults(this Task task,AppLog log,string context)
    {
        if(task.IsCompletedSuccessfully)return;
        task.ContinueWith(static (failed,state)=>
        {
            var (log,context)=((AppLog,string))state!;
            log.Post($"{context} 실패: {failed.Exception?.GetBaseException()}");
        },(log,context),CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
}
