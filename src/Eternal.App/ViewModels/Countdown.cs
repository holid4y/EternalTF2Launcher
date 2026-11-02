namespace Eternal.App.ViewModels;

/// <summary>Something on screen that stays while the player is looking at it or using it.</summary>
public interface IHoldable
{
    bool Held { set; }
}

/// <summary>Runs out after <c>length</c> unless held, then calls <c>done</c> once, on the thread that created it.</summary>
public sealed class Countdown
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);
    private readonly CancellationTokenSource _stop = new();

    public Countdown(TimeSpan length, Action done) => _ = RunAsync(length, done);

    public bool Held { get; set; }

    public void Stop() => _stop.Cancel();

    private async Task RunAsync(TimeSpan left, Action done)
    {
        try
        {
            while (left > TimeSpan.Zero)
            {
                await Task.Delay(Step, _stop.Token);
                if (!Held)
                    left -= Step;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        done();
    }

    /// <summary>Long enough to read the message: errors stay a little longer than news.</summary>
    public static TimeSpan ToRead(string message, bool isError) =>
        TimeSpan.FromSeconds(Math.Clamp(4 + message.Length / 18.0, 5, 12) + (isError ? 4 : 0));
}
