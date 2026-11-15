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
    public ICommand LiveUpCommand { get; }
    public IReadOnlyCollection<string> Controls => ChiakiController.Keys.Keys.ToArray();
    public string SelectedControl { get; set; } = "Up";
    public string Goal { get; set; } = "Play The Walking Dead Season Two, Episode One.";
    public ICommand PlayCommand { get; }
    public ICommand CaptureCommand { get; }
    private System.Windows.Media.Imaging.BitmapSource? preview;
    public System.Windows.Media.Imaging.BitmapSource? Preview { get => preview; private set => Set(ref preview,value); }
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
        LiveUpCommand = new AsyncCommand(() => RunLiveAsync("manual"), () => !IsRunning);
        PlayCommand = new AsyncCommand(() => RunLiveAsync("play"), () => !IsRunning);
        CaptureCommand = new AsyncCommand(() => RunLiveAsync("capture"), () => !IsRunning);
        StopCommand = new AsyncCommand(StopAsync, () => IsRunning);
        Activity.Add("Connect chiaki-ng to a PS4 menu to test live input.");
        Activity.Add("Run offline step uses simulated input.");
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

    private async Task RunLiveAsync(string mode)
    {
        if (IsRunning) return;
        IsRunning = true;
        using var cts = new CancellationTokenSource();
        operation = cts;
        try
        {
            var live = await ChiakiController.ConnectAsync(cts.Token);
            if(mode == "manual") await live.PressAsync([SelectedControl],150,cts.Token);
            await Task.Delay(200,cts.Token);
            Preview = live.Capture();
            Status = mode == "manual" ? "Sent "+SelectedControl+"." : "Captured the Remote Play window.";
            if(mode == "play")
            {
                string previous = "Starting";
                for(int i=0; i<60; i++)
                {
                    Status = $"Reading game frame {i+1} of 60…";
                    var move = await LiveGameAgent.DecideAsync(LiveGameAgent.Encode(Preview),Goal,previous,cts.Token);
                    live.CheckFocus();
                    cts.Token.ThrowIfCancellationRequested();
                    CurrentDecision = string.Join(" + ",move.Buttons)+" — "+move.Reason;
                    Activity.Add(CurrentDecision);
                    if(move.Stop) { Status = move.Reason; break; }
                    await live.PressAsync(move.Buttons,move.Duration,cts.Token);
                    previous = CurrentDecision;
                    await Task.Delay(350,cts.Token);
                    Preview = live.Capture();
                    Status = "Completed 60 moves.";
                }
            }
        }
        catch (OperationCanceledException) { Status = "Stopped."; }
        catch (Exception ex) { Status = ex.Message; }
        finally
        {
            Activity.Add(Status);
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

