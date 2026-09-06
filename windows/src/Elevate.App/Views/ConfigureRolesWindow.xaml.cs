using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Catalogue;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>One catalogue role in the Entra checklist.</summary>
public sealed class CatalogueItem(CatalogueRole role, bool selected) : ObservableObject
{
    private bool _selected = selected;

    public CatalogueRole Role { get; } = role;

    public string DisplayName => Role.DisplayName;

    public string Description => Role.Description;

    public Visibility PrivilegedVisibility => Role.IsPrivileged ? Visibility.Visible : Visibility.Collapsed;

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
}

/// <summary>
/// When a tenant refuses discovery, the user tells Elevate which roles exist: Entra roles from the
/// built-in catalogue, Azure resource roles by scope and role name, and groups. Port of the macOS
/// <c>ConfigureRolesView</c>.
/// </summary>
public sealed partial class ConfigureRolesWindow : Window
{
    private static readonly string[] AzureRoleNames =
    [
        "Owner", "Contributor", "Reader", "User Access Administrator", "Key Vault Administrator", "Storage Blob Data Contributor",
    ];

    private readonly AppModel _model;
    private readonly TenantKey _tenantKey;
    private readonly List<CatalogueItem> _catalogue = [];
    private readonly ObservableCollection<CatalogueItem> _filtered = [];
    private readonly Dictionary<string, string> _existingEntraNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(TextBox Scope, ComboBox Role, Grid Row)> _azure = [];
    private readonly List<(TextBox GroupId, TextBox Name, ComboBox Access, Grid Row)> _groups = [];

    public ConfigureRolesWindow(AppModel model, TenantKey tenantKey)
    {
        InitializeComponent();
        _model = model;
        _tenantKey = tenantKey;
        var tenantName = model.Tenant(tenantKey)?.DisplayName ?? tenantKey.TenantId;
        DialogWindows.Configure(this, $"Configure known roles · {tenantName}", 600, 600, Root);
        Heading.Text = tenantName;
        Catalogue.ItemsSource = _filtered;
        Load();
        Sections.SelectedItem = EntraSection;
    }

    public TenantKey TenantKey => _tenantKey;

    private void Load()
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manual in _model.ManualRoles(_tenantKey))
        {
            switch (manual.Scope)
            {
                case EntraDirectoryScope entra:
                    selected.Add(entra.RoleDefinitionId);
                    _existingEntraNames[entra.RoleDefinitionId] = manual.DisplayName;
                    break;
                case AzureResourceScope azure:
                    AddAzureRow(azure.Scope, azure.RoleDefinitionId);
                    break;
                case GroupScope group:
                    AddGroupRow(group.GroupId, manual.DisplayName, group.AccessId);
                    break;
                default:
                    break;
            }
        }

        try
        {
            foreach (var role in RoleCatalogue.EntraBuiltInRoles())
            {
                var item = new CatalogueItem(role, selected.Contains(role.TemplateId));
                item.PropertyChanged += (_, _) => UpdateSummary();
                _catalogue.Add(item);
            }
        }
        catch (PimException e)
        {
            Summary.Text = "Could not load the built-in role catalogue: " + e.UserMessage;
        }

        Search.PlaceholderText = $"Search {_catalogue.Count} built-in roles";
        ApplyFilter();
        UpdateSummary();
    }

    private void ApplyFilter()
    {
        var query = Search.Text.Trim();
        _filtered.Clear();
        foreach (var item in _catalogue.Where(i => query.Length == 0 || i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            _filtered.Add(item);
        }
    }

    private void UpdateSummary()
    {
        var entra = _catalogue.Count(i => i.Selected);
        var azure = _azure.Count(r => r.Scope.Text.Trim().Length > 0);
        var groups = _groups.Count(r => r.GroupId.Text.Trim().Length > 0);
        var parts = new List<string>();
        if (entra > 0)
        {
            parts.Add($"{entra} Entra role{(entra == 1 ? "" : "s")}");
        }

        if (azure > 0)
        {
            parts.Add($"{azure} Azure role{(azure == 1 ? "" : "s")}");
        }

        if (groups > 0)
        {
            parts.Add($"{groups} group{(groups == 1 ? "" : "s")}");
        }

        Summary.Text = parts.Count == 0 ? "Nothing selected" : string.Join(" · ", parts);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        EntraPane.Visibility = sender.SelectedItem == EntraSection ? Visibility.Visible : Visibility.Collapsed;
        AzurePane.Visibility = sender.SelectedItem == AzureSection ? Visibility.Visible : Visibility.Collapsed;
        GroupsPane.Visibility = sender.SelectedItem == GroupsSection ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: Azure rows

    private void OnAddAzureRow(object sender, RoutedEventArgs e) => AddAzureRow(string.Empty, "Contributor");

    private void AddAzureRow(string scope, string roleName)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var scopeBox = new TextBox { Text = scope, PlaceholderText = "Scope", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
        scopeBox.TextChanged += (_, _) => UpdateSummary();
        var roleBox = new ComboBox { IsEditable = true, Text = roleName, PlaceholderText = "Role name", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var name in AzureRoleNames)
        {
            roleBox.Items.Add(name);
        }

        var remove = RemoveButton();
        Grid.SetColumn(roleBox, 1);
        Grid.SetColumn(remove, 2);
        row.Children.Add(scopeBox);
        row.Children.Add(roleBox);
        row.Children.Add(remove);
        var entry = (scopeBox, roleBox, row);
        remove.Click += (_, _) =>
        {
            _azure.Remove(entry);
            AzureRows.Children.Remove(row);
            UpdateSummary();
        };
        _azure.Add(entry);
        AzureRows.Children.Add(row);
        UpdateSummary();
    }

    // MARK: Group rows

    private void OnAddGroupRow(object sender, RoutedEventArgs e) => AddGroupRow(string.Empty, string.Empty, GroupAccess.Member);

    private void AddGroupRow(string groupId, string displayName, GroupAccess access)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var idBox = new TextBox { Text = groupId, PlaceholderText = "Group id", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
        idBox.TextChanged += (_, _) => UpdateSummary();
        var nameBox = new TextBox { Text = displayName, PlaceholderText = "Display name" };
        var accessBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        accessBox.Items.Add("Member");
        accessBox.Items.Add("Owner");
        accessBox.SelectedIndex = access == GroupAccess.Owner ? 1 : 0;
        var remove = RemoveButton();
        Grid.SetColumn(nameBox, 1);
        Grid.SetColumn(accessBox, 2);
        Grid.SetColumn(remove, 3);
        row.Children.Add(idBox);
        row.Children.Add(nameBox);
        row.Children.Add(accessBox);
        row.Children.Add(remove);
        var entry = (idBox, nameBox, accessBox, row);
        remove.Click += (_, _) =>
        {
            _groups.Remove(entry);
            GroupRows.Children.Remove(row);
            UpdateSummary();
        };
        _groups.Add(entry);
        GroupRows.Children.Add(row);
        UpdateSummary();
    }

    private static Button RemoveButton()
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, "Remove row");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Remove row");
        return button;
    }

    // MARK: Save

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var manual = new List<ManualRole>();
        foreach (var item in _catalogue.Where(i => i.Selected))
        {
            var id = item.Role.TemplateId;
            var name = item.DisplayName.Length > 0 ? item.DisplayName : _existingEntraNames.GetValueOrDefault(id) ?? id;
            manual.Add(new ManualRole(_tenantKey, new EntraDirectoryScope(id, "/"), name));
        }

        foreach (var (scopeBox, roleBox, _) in _azure)
        {
            var scope = scopeBox.Text.Trim();
            var roleName = (roleBox.Text ?? roleBox.SelectedItem as string ?? string.Empty).Trim();
            if (scope.Length == 0 || roleName.Length == 0)
            {
                continue;
            }

            manual.Add(new ManualRole(_tenantKey, new AzureResourceScope(scope, roleName), roleName));
        }

        foreach (var (idBox, nameBox, accessBox, _) in _groups)
        {
            var groupId = idBox.Text.Trim();
            if (groupId.Length == 0)
            {
                continue;
            }

            var access = accessBox.SelectedIndex == 1 ? GroupAccess.Owner : GroupAccess.Member;
            var name = nameBox.Text.Trim();
            manual.Add(new ManualRole(_tenantKey, new GroupScope(groupId, access), name.Length == 0 ? groupId : name));
        }

        _model.SetManualRoles(manual, _tenantKey);
        Close();
    }
}
