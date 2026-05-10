using Microsoft.Extensions.Logging;

namespace AlphaStack.Application.Common;

public static class RetryHelper
{
    public static async Task ExecuteWithRetryAsync(
        Func<Task> action,
        int maxAttempts = 3,
        int delayMs = 1000,
        ILogger? logger = null,
        string? operationName = null)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger?.LogWarning(ex,
                    "[Retry] {Operation} attempt {Attempt}/{MaxAttempts} failed. Retrying in {Delay}ms",
                    operationName ?? "Operation",
                    attempt,
                    maxAttempts,
                    delayMs * attempt);

                await Task.Delay(delayMs * attempt);
            }
        }

        // Final attempt (let exception propagate if fails)
        await action();
    }
}