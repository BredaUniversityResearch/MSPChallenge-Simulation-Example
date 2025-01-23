namespace MSPChallenge_Simulation_Example.Extensions;

public static class TaskExtensions
{
    // for HttPost
    public static Task ContinueWithOnSuccess(this Task task, Action<Task> continuationAction)
    {
        // any unhandled exceptions in the antecedent task will propagate to the continuation task
        return task.ContinueWith(continuationAction, TaskContinuationOptions.OnlyOnRanToCompletion);
    }
    
    // for HttpPost<TTargetType>
    public static Task<TResult> ContinueWithOnSuccess<TResult>(this Task task, Func<Task, TResult> continuationFunction)
    {
        // any unhandled exceptions in the antecedent task will propagate to the continuation task
        return task.ContinueWith(continuationFunction, TaskContinuationOptions.OnlyOnRanToCompletion);
    }
    
    public static Task ContinueWithOnSuccess<TResult>(this Task<TResult> task, Action<Task<TResult>> continuationAction)
    {
        // any unhandled exceptions in the antecedent task will propagate to the continuation task
        return task.ContinueWith(continuationAction, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public static Task<TNewResult> ContinueWithOnSuccess<TResult, TNewResult>(this Task<TResult> task, Func<Task<TResult>, TNewResult> continuationFunction)
    {
        // any unhandled exceptions in the antecedent task will propagate to the continuation task
        return task.ContinueWith(continuationFunction, TaskContinuationOptions.OnlyOnRanToCompletion);
    }
}
