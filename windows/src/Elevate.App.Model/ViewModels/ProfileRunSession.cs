using CommunityToolkit.Mvvm.ComponentModel;
using Elevate.Core.Coordination;
using Elevate.Core.Models;

namespace Elevate.App.ViewModels;

/// <summary>
/// One profile run in progress, kept in the app model rather than a window: the flyout hides
/// whenever it loses focus (an MFA step opens the browser) and the run must survive that. Never
/// persisted. Port of <c>ProfileRunSession.swift</c>.
/// </summary>
public sealed class ProfileRunSession : ObservableObject
{
    private readonly Dictionary<RoleKey, ActivationResult> _rowResults = [];
    private IReadOnlyList<ProfilePlanItem> _items;
    private string _justification;
    private string _ticketNumber = string.Empty;
    private string _ticketSystem = string.Empty;
    private bool _scheduleStart;
    private DateTimeOffset _startAt = DateTimeOffset.Now.AddHours(1);
    private bool _running;
    private bool _finished;
    private bool _resultsSeen;
    private string? _failure;

    public ProfileRunSession(Guid profileId, IReadOnlyList<ProfilePlanItem> items, string justification)
    {
        ProfileId = profileId;
        _items = items;
        _justification = justification;
    }

    public Guid ProfileId { get; }

    public IReadOnlyList<ProfilePlanItem> Items
    {
        get => _items;
        internal set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(ToActivate));
            }
        }
    }

    /// <summary>Roles the user unchecked for this run only; nothing is remembered onto the profile.</summary>
    public ISet<RoleKey> Excluded { get; } = new HashSet<RoleKey>();

    public string Justification { get => _justification; set => SetProperty(ref _justification, value ?? string.Empty); }

    public string TicketNumber { get => _ticketNumber; set => SetProperty(ref _ticketNumber, value ?? string.Empty); }

    public string TicketSystem { get => _ticketSystem; set => SetProperty(ref _ticketSystem, value ?? string.Empty); }

    public bool ScheduleStart { get => _scheduleStart; set => SetProperty(ref _scheduleStart, value); }

    public DateTimeOffset StartAt { get => _startAt; set => SetProperty(ref _startAt, value); }

    public bool Running { get => _running; internal set => SetProperty(ref _running, value); }

    public bool Finished { get => _finished; internal set => SetProperty(ref _finished, value); }

    public bool ResultsSeen { get => _resultsSeen; internal set => SetProperty(ref _resultsSeen, value); }

    /// <summary>The message of an exception that stopped the run before it produced outcomes.</summary>
    public string? Failure { get => _failure; internal set => SetProperty(ref _failure, value); }

    public IReadOnlyDictionary<RoleKey, ActivationResult> RowResults => _rowResults;

    internal Dictionary<RoleKey, ActivationResult> MutableRowResults => _rowResults;

    public void SetDuration(RoleKey key, TimeSpan duration) =>
        Items = [.. _items.Select(i => i.RoleKey == key ? i with { Duration = duration } : i)];

    public IReadOnlyList<ProfilePlanItem> ToActivate =>
        [.. _items.Where(i => i.Disposition == ProfilePlanDisposition.Activate && !Excluded.Contains(i.RoleKey))];

    public bool NeedsTicket => ToActivate.Any(i => i.Role?.Policy.RequiresTicket == true);

    public bool JustificationRequired => ToActivate.Any(i => i.Role?.Policy.RequiresJustification == true);

    public bool CanSubmit(DateTimeOffset now) =>
        !Running && !Finished && ToActivate.Count > 0
        && (!JustificationRequired || !string.IsNullOrWhiteSpace(Justification))
        && (!NeedsTicket || !string.IsNullOrWhiteSpace(TicketNumber))
        // A start that has slipped into the past while the flyout sat open is not submittable.
        && !(ScheduleStart && StartAt <= now);
}
