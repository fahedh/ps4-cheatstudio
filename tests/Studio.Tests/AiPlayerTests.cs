using Studio.Core.AiPlayer;
using Xunit;

namespace Studio.Tests;

public sealed class AiPlayerTests
{
    [Fact]
    public async Task OfflineStepObservesActsAndReleasesInput()
    {
        var controller = new RecordingGameController();
        var session = new AiPlayerSession(
            new SimulatedGameCapture(), new SimulatedGameObserver(),
            new SimulatedGameAgent(), controller);

        var result = await session.StepAsync();

        Assert.Equal(GameMode.Dialogue, result.Observation.Mode);
        Assert.Equal(GameButton.Down, result.Action.Button);
        Assert.Equal(new[] { "Press Down", "Release all" }, controller.Events);
    }

    [Fact]
    public async Task InvalidDurationIsRejectedBeforePress()
    {
        var controller = new RecordingGameController();
        var session = new AiPlayerSession(
            new SimulatedGameCapture(), new SimulatedGameObserver(),
            new InvalidAgent(), controller);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StepAsync());
        Assert.Empty(controller.Events);
    }

    [Fact]
    public async Task CancellationReleasesPressedButton()
    {
        using var cts = new CancellationTokenSource();
        var controller = new RecordingGameController();
        var session = new AiPlayerSession(
            new SimulatedGameCapture(), new SimulatedGameObserver(),
            new LongHoldAgent(), controller);

        cts.CancelAfter(40);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StepAsync(cts.Token));
        Assert.Equal(new[] { "Press Cross", "Release all" }, controller.Events);
    }

    private sealed class InvalidAgent : IGameAgent
    {
        public Task<GameAction> DecideAsync(GameObservation observation, CancellationToken cancellationToken)
            => Task.FromResult(new GameAction(GameButton.Cross, TimeSpan.FromSeconds(10), "Invalid"));
    }

    private sealed class LongHoldAgent : IGameAgent
    {
        public Task<GameAction> DecideAsync(GameObservation observation, CancellationToken cancellationToken)
            => Task.FromResult(new GameAction(GameButton.Cross, TimeSpan.FromSeconds(1), "Cancellation test"));
    }
}
