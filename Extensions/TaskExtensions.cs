namespace MSPChallenge_Simulation.Extensions;

public static class TaskExtensions
{
    public delegate void ExceptionHandlerDelegate(Exception exception);
    private static Dictionary<string, List<ExceptionHandlerDelegate>> _events = new();

	public static async IAsyncEnumerable<T> AwaitAll<T>(this IEnumerable<Func<Task<T>>> tasks)
	{
		foreach (var task in tasks)
			yield return await task();
	}

	public static void RegisterExceptionHandler<TException>(ExceptionHandlerDelegate handler) where TException : Exception
    {
        var key = typeof(TException).FullName;
        if (key == null) return;
        if (!_events.TryGetValue(key, out var handlers))
        {
            handlers = [];
            _events[key] = handlers;
        }
        handlers.Add(handler);
    }

    private static void TriggerExceptionHandler<TException>(TException exception) where TException : Exception
    {
        var key = typeof(TException).FullName;
        if (key == null || !_events.TryGetValue(key, out var handlers)) return;
        foreach (var handler in handlers)
        {
            handler(exception);
        }
    }   
    
    public static Task ContinueWithOnSuccess(this Task task, Action<Task> continuationAction)
    {
        return task.ContinueWith(t =>
        {
            if (!OnSuccess(t)) return;
            continuationAction(t);
        });
    }

    public static Task<TResult> ContinueWithOnSuccess<TResult>(this Task task, Func<Task, TResult> continuationFunction)
    {
        return task.ContinueWith(t => OnSuccess(t) ? continuationFunction(t) : default!);
    }
    
    public static Task ContinueWithOnSuccess<TResult>(
        this Task<TResult> task,
        Action<Task<TResult>> continuationAction,
        Action<Exception>? errorAction = null)
    {
        return task.ContinueWith(t =>
        {
            if (!OnSuccess(t, errorAction)) return;
            continuationAction(t);
        });
    }

    public static Task<TNewResult> ContinueWithOnSuccess<TResult, TNewResult>(this Task<TResult> task, Func<Task<TResult>, TNewResult> continuationFunction)
    {
        return task.ContinueWith(t => OnSuccess(t) ? continuationFunction(t) : default!);
    }
    
    private static bool OnSuccess(Task task, Action<Exception>? errorAction = null)
    {
        if (null == task.Exception) return true;
        foreach (var exception in task.Exception!.InnerExceptions)
        {
            Console.WriteLine(exception.Message);
            TriggerExceptionHandler(exception);
            errorAction?.Invoke(exception);
        }

        return false;
    }
}
