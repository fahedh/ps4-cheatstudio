using System.Collections.ObjectModel;
using System.Windows.Input;
using Studio.Core.AiPlayer;

namespace Studio.Desktop;

public sealed class AiPlayerPanelViewModel : Observable
{
    private CancellationTokenSource? operation;
    private bool isRunning;
    private string status = "Offline preview ready. No PS4 connection required.";
    private string detectedMode = "Not observed";
    private string currentDecision = "No action selected";
    private string observedChoices = "Run an offline step to populate synthetic dialogue choices.";

    public ObservableCollection<string> Activity { get; } = [];
    public ICommand RunOfflineStepCommand { get; }
    public ICommand StopCommand { get; }

    public bool IsRunning
    {
        get => isRunning;
        private set => Set(ref isRunning, value);
    }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public string DetectedMode
    {
        get => detectedMode;
        private set => Set(ref detectedMode, value);
    }

    public string CurrentDecision
    {
        get => currentDecision;
        private set => Set(ref currentDecision, value);
    }

    public string ObservedChoices
    {
        get => observedChoices;
        private set => Set(ref observedChoices, value);
    }

    public AiPlayerPanelViewModel()
    {
        RunOfflineStepCommand = new AsyncCommand(RunOfflineStepAsync, () => !IsRunning);
        StopCommand = new AsyncCommand(StopAsync, () => IsRunning);
        Activity.Add("Live PS4 input, screen capture and OpenAI integration are not configured.");
        Activity.Add("Synthetic mode only: no console commands will be sent.");
    }

    private async Task RunOfflineStepAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        using var cts = new CancellationTokenSource();
        operation = cts;
        var controller = new RecordingGameController();
        try
        {
            Status = "Running synthetic observation and controller step...";
            var session = new AiPlayerSession(
                new SimulatedGameCapture(), new SimulatedGameObserver(),
                new SimulatedGameAgent(), controller);
            var step = await session.StepAsync(cts.Token);
            DetectedMode = step.Observation.Mode.ToString() + " (synthetic)";
            ObservedChoices = string.Join(" | ", step.Observation.Choices);
            CurrentDecision = step.Action.Button + " for " +
                step.Action.Duration.TotalMilliseconds.ToString("0") + " ms";
            Activity.Add("[Observe] " + step.Observation.Description);
            Activity.Add("[Decide] " + step.Action.Reason);
            foreach (var eventName in controller.Events) Activity.Add("[Controller] " + eventName);
            Status = "Offline step successful. Nothing was sent to a PS4.";
        }
        catch (OperationCanceledException)
        {
            Activity.Add("[Stop] Offline action cancelled; all buttons released.");
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Activity.Add("[Error] " + ex.Message);
            Status = "Offline test failed.";
        }
        finally
        {
            operation = null;
            IsRunning = false;
        }
    }

    private Task StopAsync()
    {
        operation?.Cancel();
        return Task.CompletedTask;
    }
}
