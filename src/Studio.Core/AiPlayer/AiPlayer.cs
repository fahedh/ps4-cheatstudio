namespace Studio.Core.AiPlayer;

public enum GameMode { Unknown, MainMenu, Dialogue, Exploration, QuickTimeEvent, Paused }
public enum GameButton { Up, Down, Left, Right, Cross, Circle, Square, Triangle, L1, R1, Options }

public sealed record GameFrame(byte[] ImageBytes, string ContentType, DateTimeOffset CapturedAt);
public sealed record GameObservation(GameMode Mode, string Description, IReadOnlyList<string> Choices, GameFrame? Frame = null);
public sealed record GameAction(GameButton Button, TimeSpan Duration, string Reason);
public sealed record AgentStep(GameObservation Observation, GameAction Action);

public interface IGameCapture
{
    Task<GameFrame> CaptureAsync(CancellationToken cancellationToken);
}

public interface IGameMemoryReader
{
    Task<IReadOnlyDictionary<string, string>> ReadKnownStateAsync(CancellationToken cancellationToken);
}

public interface IGameController
{
    Task PressAsync(GameButton button, CancellationToken cancellationToken);
    Task ReleaseAllAsync(CancellationToken cancellationToken);
}

public interface IGameObserver
{
    Task<GameObservation> ObserveAsync(GameFrame frame, CancellationToken cancellationToken);
}

public interface IGameAgent
{
    Task<GameAction> DecideAsync(GameObservation observation, CancellationToken cancellationToken);
}

// Performs one bounded observe / decide / act cycle, safe to run without hardware.
public sealed class AiPlayerSession(
    IGameCapture capture,
    IGameObserver observer,
    IGameAgent agent,
    IGameController controller)
{
    public static readonly TimeSpan MaximumButtonHold = TimeSpan.FromSeconds(2);

    public async Task<AgentStep> StepAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = await capture.CaptureAsync(cancellationToken);
        var observation = await observer.ObserveAsync(frame, cancellationToken);
        var action = await agent.DecideAsync(observation, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (action.Duration <= TimeSpan.Zero || action.Duration > MaximumButtonHold)
            throw new InvalidOperationException("Button hold duration must be between 1 ms and 2 seconds.");

        try
        {
            await controller.PressAsync(action.Button, cancellationToken);
            await Task.Delay(action.Duration, cancellationToken);
        }
        finally
        {
            // Never let cancellation keep a simulated (or future real) button held.
            await controller.ReleaseAllAsync(CancellationToken.None);
        }

        return new AgentStep(observation, action);
    }
}

// Synthetic adapters for offline development; do not interact with a PS4.
public sealed class SimulatedGameCapture : IGameCapture
{
    public Task<GameFrame> CaptureAsync(CancellationToken cancellationToken)
        => Task.FromResult(new GameFrame([], "application/x-synthetic-frame", DateTimeOffset.UtcNow));
}

public sealed class SimulatedGameObserver : IGameObserver
{
    public Task<GameObservation> ObserveAsync(GameFrame frame, CancellationToken cancellationToken)
        => Task.FromResult(new GameObservation(
            GameMode.Dialogue,
            "Synthetic Walking Dead-style dialogue; this is NOT a live game image.",
            ["Help the stranger", "Stay with Clementine", "Remain silent"], frame));
}

public sealed class SimulatedGameAgent : IGameAgent
{
    public Task<GameAction> DecideAsync(GameObservation observation, CancellationToken cancellationToken)
        => Task.FromResult(new GameAction(GameButton.Down, TimeSpan.FromMilliseconds(40),
            "Offline demonstration: move the dialogue selection once."));
}

public sealed class RecordingGameController : IGameController
{
    public List<string> Events { get; } = [];

    public Task PressAsync(GameButton button, CancellationToken cancellationToken)
    {
        Events.Add("Press " + button);
        return Task.CompletedTask;
    }

    public Task ReleaseAllAsync(CancellationToken cancellationToken)
    {
        Events.Add("Release all");
        return Task.CompletedTask;
    }
}
