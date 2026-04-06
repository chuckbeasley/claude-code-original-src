namespace ClaudeCode.Services.Tasks;

/// <summary>
/// Tracks a codebase-analysis dream task through its lifecycle phases.
/// Corresponds to DreamTask in src/tasks/DreamTask/DreamTask.ts.
/// </summary>
public enum DreamTaskPhase { Starting, Updating, Completed, Failed, Killed }

/// <summary>
/// Mutable state container for a dream task. Tracks its current phase and accumulates
/// the files touched, conversation turns, and review sessions during execution.
/// </summary>
public sealed class DreamTaskState
{
    private readonly List<string> _filesTouched = [];
    private readonly List<string> _turns = [];
    private readonly List<string> _sessionsReviewing = [];

    /// <summary>Gets the task identifier supplied at construction time.</summary>
    public string TaskId { get; }

    /// <summary>Gets the current lifecycle phase of this dream task.</summary>
    public DreamTaskPhase Phase { get; private set; } = DreamTaskPhase.Starting;

    /// <summary>Gets an ordered, read-only view of every file path touched so far.</summary>
    public IReadOnlyList<string> FilesTouched => _filesTouched.AsReadOnly();

    /// <summary>Gets an ordered, read-only view of conversation turn summaries recorded so far.</summary>
    public IReadOnlyList<string> Turns => _turns.AsReadOnly();

    /// <summary>Gets an ordered, read-only view of session identifiers currently reviewing this task.</summary>
    public IReadOnlyList<string> SessionsReviewing => _sessionsReviewing.AsReadOnly();

    /// <summary>
    /// Initializes a new <see cref="DreamTaskState"/> in the <see cref="DreamTaskPhase.Starting"/> phase.
    /// </summary>
    /// <param name="taskId">Unique identifier for this task. Must not be <see langword="null"/>.</param>
    public DreamTaskState(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        TaskId = taskId;
    }

    /// <summary>Transitions the task to the specified <paramref name="phase"/>.</summary>
    /// <param name="phase">The new lifecycle phase.</param>
    public void UpdatePhase(DreamTaskPhase phase) => Phase = phase;

    /// <summary>Records a file path as having been touched by this task.</summary>
    /// <param name="path">The file path to record. Must not be <see langword="null"/>.</param>
    public void AddFileTouched(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _filesTouched.Add(path);
    }

    /// <summary>Records a conversation turn summary.</summary>
    /// <param name="turn">The turn text to record. Must not be <see langword="null"/>.</param>
    public void AddTurn(string turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        _turns.Add(turn);
    }

    /// <summary>Records a session as currently reviewing this task.</summary>
    /// <param name="sessionId">The session identifier to record. Must not be <see langword="null"/>.</param>
    public void AddSessionReviewing(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        _sessionsReviewing.Add(sessionId);
    }

    /// <summary>
    /// Terminates this dream task by transitioning to the <see cref="DreamTaskPhase.Killed"/> phase.
    /// </summary>
    public void Kill() => Phase = DreamTaskPhase.Killed;
}
