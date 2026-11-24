using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Microsoft.UI.Xaml;

namespace Elevate.App.Views;

/// <summary>
/// The run window: a heading around <see cref="RunProfileContent"/>, which plans and runs one
/// profile from its session in the app model. Port of the macOS run window.
/// </summary>
public sealed partial class RunProfileWindow : Window
{
    private readonly AppModel _model;
    private readonly Guid _profileId;
    private int _seenRunRequest;

    public RunProfileWindow(AppModel model, Guid profileId)
    {
        InitializeComponent();
        _model = model;
        _profileId = profileId;
        DialogWindows.Configure(this, "Run profile", 600, 560, Root, autoHeight: true);
        _seenRunRequest = model.RunRequests.GetValueOrDefault(profileId);
        // The session exists before the control binds; one that already ran starts over.
        model.ShowProfile(profileId);
        model.RunWindowsOpen.Add(profileId);
        RunContent.Bind(model, profileId, compact: false);
        RunContent.Done += (_, _) => Close();
        UpdateHeading();
        _model.Changed += OnModelChanged;
        Closed += (_, _) =>
        {
            _model.Changed -= OnModelChanged;
            RunContent.Unbind();
            _model.RunWindowsOpen.Remove(profileId);
        };
    }

    public Guid ProfileId => _profileId;

    private void UpdateHeading()
    {
        var name = _model.Profile(_profileId)?.Name ?? "profile";
        var finished = _model.RunSessions.TryGetValue(_profileId, out var session) && session.Finished;
        Heading.Text = $"{(finished ? "Ran" : "Run")} \"{name}\"";
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        var running = _model.RunSessions.TryGetValue(_profileId, out var session) && session.Running;
        // Re-plan when the user asks to run this profile again, never merely because the model redrew.
        var request = _model.RunRequests.GetValueOrDefault(_profileId);
        if (request != _seenRunRequest && !running)
        {
            _seenRunRequest = request;
            _model.ResetRunSession(_profileId);
        }

        if (_model.Profile(_profileId) is null && !running)
        {
            Close();
            return;
        }

        UpdateHeading();
    }
}
