using SkiaSharp;
using VisionInspection.Core.Models;
using VisionInspection.Modules.SOP.Services;
using YoloDotNet.Models;

namespace VisionInspection.Modules.SOP.Models;

/// <summary>
/// SOP 执行状态机
/// </summary>
public class SOPStateMachine
{
    private SOPWorkflow? _workflow;
    private readonly Dictionary<string, TrackedObject> _trackedObjects = new();
    private readonly List<StepExecutionRecord> _stepHistory = new();
    private readonly List<ViolationRecord> _violations = new();
    private readonly StepConditionEvaluator _conditionEvaluator;
    private readonly ViolationDetector _violationDetector;

    public SOPExecutionState CurrentState { get; private set; } = SOPExecutionState.Idle;
    public int CurrentStepId { get; private set; } = 0;
    public DateTime StepStartTime { get; private set; }
    public SOPWorkflow? Workflow => _workflow;
    public SOPWorkflow? CurrentWorkflow => _workflow;
    public IReadOnlyList<StepExecutionRecord> StepHistory => _stepHistory.AsReadOnly();
    public IReadOnlyList<ViolationRecord> Violations => _violations.AsReadOnly();
    public IReadOnlyDictionary<string, TrackedObject> TrackedObjects => _trackedObjects;

    public event EventHandler<StepChangedEventArgs>? StepChanged;
    public event EventHandler<ViolationEventArgs>? ViolationDetected;
    public event EventHandler<SOPCompletedEventArgs>? WorkflowCompleted;
    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public SOPStateMachine()
    {
        _conditionEvaluator = new StepConditionEvaluator(this);
        _violationDetector = new ViolationDetector(this);
    }

    public void Start(SOPWorkflow workflow)
    {
        _workflow = workflow;
        _stepHistory.Clear();
        _violations.Clear();
        _trackedObjects.Clear();
        CurrentStepId = 1;
        StepStartTime = DateTime.Now;
        ChangeState(SOPExecutionState.Running);
        StepChanged?.Invoke(this, new StepChangedEventArgs(0, CurrentStepId, workflow.Steps.FirstOrDefault(s => s.StepId == CurrentStepId)?.StepName ?? ""));
    }

    public void ProcessFrame(List<ObjectDetection> detections, DateTime timestamp)
    {
        if (_workflow == null || CurrentState != SOPExecutionState.Running)
            return;

        var currentStep = _workflow.Steps.FirstOrDefault(s => s.StepId == CurrentStepId);
        if (currentStep == null)
            return;

        // 更新对象跟踪
        UpdateTrackedObjects(detections, timestamp);

        // 检测违规
        var violations = _violationDetector.DetectViolations(currentStep, detections, timestamp);
        foreach (var violation in violations)
        {
            RecordViolation(violation);
            ViolationDetected?.Invoke(this, new ViolationEventArgs(violation));
        }

        // 评估步骤条件
        var evaluation = _conditionEvaluator.EvaluateConditions(currentStep, detections);
        if (evaluation.IsPass)
        {
            CompleteCurrentStep(timestamp);
        }
    }

    public void Pause()
    {
        if (CurrentState == SOPExecutionState.Running)
        {
            ChangeState(SOPExecutionState.Paused);
        }
    }

    public void Resume()
    {
        if (CurrentState == SOPExecutionState.Paused)
        {
            ChangeState(SOPExecutionState.Running);
        }
    }

    public void Reset()
    {
        _workflow = null;
        _stepHistory.Clear();
        _violations.Clear();
        _trackedObjects.Clear();
        CurrentStepId = 0;
        ChangeState(SOPExecutionState.Idle);
    }

    /// <summary>
    /// 停止工作流
    /// </summary>
    public void Stop()
    {
        ChangeState(SOPExecutionState.Idle);
        _workflow = null;
        CurrentStepId = 0;
    }

    public void ForceCompleteStep()
    {
        if (_workflow != null && CurrentState == SOPExecutionState.Running)
        {
            CompleteCurrentStep(DateTime.Now);
        }
    }

    private void UpdateTrackedObjects(List<ObjectDetection> detections, DateTime timestamp)
    {
        foreach (var detection in detections)
        {
            var labelName = detection.Label?.Name ?? "unknown";
            var key = $"{labelName}_{detection.BoundingBox.GetHashCode()}";
            if (_trackedObjects.TryGetValue(key, out var tracked))
            {
                tracked.Update(detection.BoundingBox, timestamp);
            }
            else
            {
                _trackedObjects[key] = new TrackedObject(labelName, detection.BoundingBox, timestamp);
            }
        }

        // 清理过期跟踪
        var expired = _trackedObjects.Where(kv => (timestamp - kv.Value.LastUpdate).TotalSeconds > 2).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
        {
            _trackedObjects.Remove(key);
        }
    }

    private void CompleteCurrentStep(DateTime timestamp)
    {
        if (_workflow == null) return;

        var currentStep = _workflow.Steps.FirstOrDefault(s => s.StepId == CurrentStepId);
        if (currentStep == null) return;

        var record = new StepExecutionRecord
        {
            StepId = CurrentStepId,
            StepName = currentStep.StepName,
            StartTime = StepStartTime,
            EndTime = timestamp,
            Duration = timestamp - StepStartTime,
            IsPass = true
        };
        _stepHistory.Add(record);

        // 检查是否完成所有步骤
        if (CurrentStepId >= _workflow.Steps.Max(s => s.StepId))
        {
            ChangeState(SOPExecutionState.Completed);
            WorkflowCompleted?.Invoke(this, new SOPCompletedEventArgs(_stepHistory, _violations));

            if (_workflow.Settings.AutoResetOnComplete)
            {
                Task.Delay(TimeSpan.FromSeconds(_workflow.Settings.ResetDelaySec)).ContinueWith(_ => Reset());
            }
        }
        else
        {
            var previousStepId = CurrentStepId;
            CurrentStepId = _workflow.Steps.Where(s => s.StepId > CurrentStepId).Min(s => s.StepId);
            StepStartTime = timestamp;
            StepChanged?.Invoke(this, new StepChangedEventArgs(previousStepId, CurrentStepId, _workflow.Steps.FirstOrDefault(s => s.StepId == CurrentStepId)?.StepName ?? ""));
        }
    }

    private void RecordViolation(ViolationRecord violation)
    {
        _violations.Add(violation);
    }

    /// <summary>
    /// 尝试完成当前步骤（公开方法供外部调用）
    /// </summary>
    public void TryCompleteCurrentStep(DateTime timestamp)
    {
        if (CurrentState != SOPExecutionState.Running) return;
        CompleteCurrentStep(timestamp);
    }

    /// <summary>
    /// 报告违规（公开方法供外部调用）
    /// </summary>
    public void ReportViolation(ViolationRecord violation)
    {
        RecordViolation(violation);
        ViolationDetected?.Invoke(this, new ViolationEventArgs(violation));
    }

    private void ChangeState(SOPExecutionState newState)
    {
        var oldState = CurrentState;
        CurrentState = newState;
        StateChanged?.Invoke(this, new StateChangedEventArgs(oldState, newState));
    }
}

/// <summary>
/// SOP 执行状态
/// </summary>
public enum SOPExecutionState
{
    Idle,
    Running,
    Paused,
    Completed,
    Error
}

/// <summary>
/// 跟踪对象
/// </summary>
public class TrackedObject
{
    public string ClassName { get; }
    public SKRect BoundingBox { get; private set; }
    public DateTime FirstSeen { get; }
    public DateTime LastUpdate { get; private set; }
    public int FrameCount { get; private set; }
    public List<SKRect> PositionHistory { get; } = new();

    public TrackedObject(string className, SKRect boundingBox, DateTime timestamp)
    {
        ClassName = className;
        BoundingBox = boundingBox;
        FirstSeen = timestamp;
        LastUpdate = timestamp;
        FrameCount = 1;
        PositionHistory.Add(boundingBox);
    }

    public void Update(SKRect newBox, DateTime timestamp)
    {
        BoundingBox = newBox;
        LastUpdate = timestamp;
        FrameCount++;
        PositionHistory.Add(newBox);
        if (PositionHistory.Count > 30)
            PositionHistory.RemoveAt(0);
    }

    public bool IsStable(float tolerance)
    {
        if (PositionHistory.Count < 5) return false;
        var recent = PositionHistory.TakeLast(5).ToList();
        var centerX = recent.Average(r => r.MidX);
        var centerY = recent.Average(r => r.MidY);
        return recent.All(r => Math.Abs(r.MidX - centerX) < tolerance && Math.Abs(r.MidY - centerY) < tolerance);
    }
}

/// <summary>
/// 步骤执行记录
/// </summary>
public class StepExecutionRecord
{
    public int StepId { get; set; }
    public string StepName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public bool IsPass { get; set; }
    public List<ViolationRecord> Violations { get; set; } = new();
}

/// <summary>
/// 违规记录
/// </summary>
public class ViolationRecord
{
    public DateTime Timestamp { get; set; }
    public ViolationType Type { get; set; }
    public string Description { get; set; } = "";
    public int StepId { get; set; }
    public string Evidence { get; set; } = "";
}

// 事件参数类
public class StepChangedEventArgs : EventArgs
{
    public int PreviousStepId { get; }
    public int CurrentStepId { get; }
    public string StepName { get; }

    public StepChangedEventArgs(int previousStepId, int currentStepId, string stepName)
    {
        PreviousStepId = previousStepId;
        CurrentStepId = currentStepId;
        StepName = stepName;
    }
}

public class ViolationEventArgs : EventArgs
{
    public ViolationRecord Violation { get; }

    public ViolationEventArgs(ViolationRecord violation)
    {
        Violation = violation;
    }
}

public class SOPCompletedEventArgs : EventArgs
{
    public IReadOnlyList<StepExecutionRecord> StepHistory { get; }
    public IReadOnlyList<ViolationRecord> Violations { get; }
    public TimeSpan TotalDuration => StepHistory.Last().EndTime - StepHistory.First().StartTime;
    public bool HasViolations => Violations.Count > 0;

    public SOPCompletedEventArgs(IReadOnlyList<StepExecutionRecord> stepHistory, IReadOnlyList<ViolationRecord> violations)
    {
        StepHistory = stepHistory;
        Violations = violations;
    }
}

public class StateChangedEventArgs : EventArgs
{
    public SOPExecutionState OldState { get; }
    public SOPExecutionState NewState { get; }

    public StateChangedEventArgs(SOPExecutionState oldState, SOPExecutionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}
