using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Occa.Core.Courses;
using Occa.Core.Sheet;

namespace Occa.App.Views;

/// <summary>
/// Course planner side panel (M4): event, courses, control order, IOF 2024 control descriptions, print layout and tools.
/// Edits mutate the project's <see cref="CourseSet"/> in place and raise <see cref="Changed"/>; the map view redraws the
/// overprint and saves the package. Placement of controls happens on the map (<see cref="PlaceKind"/>).
/// </summary>
public sealed partial class CoursePanel : UserControl
{
    /// <summary>Data changed: redraw and save.</summary>
    public event EventHandler? Changed;
    /// <summary>Selected course or control changed: redraw only.</summary>
    public event EventHandler? SelectionChanged;
    public event EventHandler? CloseRequested;
    public event EventHandler? ImportRequested;
    public event EventHandler? ExportIofRequested;

    private CourseSet _set = new();
    private SheetSpec _sheet = new();
    private bool _updating;

    public CourseSet Set => _set;
    public Course? SelectedCourse { get; private set; }
    public Guid? SelectedControlId { get; private set; }
    /// <summary>Kind of control the next map click places; null = select / move.</summary>
    public ControlKind? PlaceKind { get; private set; }

    public CoursePanel()
    {
        InitializeComponent();
        FillPickers(indoor: false);
    }

    /// <summary>Shows a project's course set (the same instance the map view saves).</summary>
    public void Bind(CourseSet set, SheetSpec sheet)
    {
        _set = set;
        _sheet = sheet;
        FillPickers(sheet.IsIndoor);
        SelectedCourse = set.Courses.FirstOrDefault();
        SelectedControlId = null;
        RefreshAll();
    }

    /// <summary>Re-reads the set after the map changed it (control added or moved).</summary>
    public void Refresh()
    {
        RefreshCourseList();
        RefreshControls();
        RefreshControlFields();
    }

    public void SelectControl(Guid? id)
    {
        SelectedControlId = id;
        RefreshControls();
        RefreshControlFields();
    }

    public void ClearPlacement()
    {
        PlaceKind = null;
        _updating = true;
        PlaceStart.IsChecked = PlaceControl.IsChecked = PlaceFinish.IsChecked = false;
        _updating = false;
    }

    // MARK: Fill

    private void FillPickers(bool indoor)
    {
        _updating = true;
        void Fill(ComboBox box, IofColumn column)
        {
            box.Items.Clear();
            box.Items.Add(new ComboBoxItem { Content = "—", Tag = "" });
            foreach (var s in IofSymbols.PickerSymbols(column, indoor)) box.Items.Add(new ComboBoxItem { Content = $"{s.Id}  {s.English}", Tag = s.Id });
            box.SelectedIndex = 0;
        }
        Fill(WhichBox, IofColumn.C);
        Fill(FeatureBox, IofColumn.D);
        Fill(AppearanceBox, IofColumn.E);
        Fill(CombinationBox, IofColumn.F);
        Fill(LocationBox, IofColumn.G);
        Fill(OtherBox, IofColumn.H);
        DirectionBox.Items.Clear();
        foreach (var d in IofSymbols.Directions) DirectionBox.Items.Add(new ComboBoxItem { Content = d, Tag = d });
        DirectionBox.SelectedIndex = 0;
        _updating = false;
    }

    private void RefreshAll()
    {
        _updating = true;
        EventTitleBox.Text = _set.EventTitle;
        OrganizerBox.Text = _set.Organizer ?? "";
        var layout = _set.EffectivePrintLayout;
        DescriptionOnMapCheck.IsChecked = layout.DescriptionOnMap;
        CornerBox.SelectedIndex = (int)layout.DescriptionCorner;
        CellBox.Value = layout.DescriptionCellMM;
        DescriptionStyleBox.SelectedIndex = layout.DescriptionSymbols ? 0 : 1;
        MaxRowsBox.Value = layout.DescriptionMaxRows ?? 0;
        SeparatePageCheck.IsChecked = layout.SeparateDescriptionPage;
        PurpleDescriptionCheck.IsChecked = layout.DescriptionColorValue == CoursePrintLayout.DescriptionColor.Purple;
        WarningsText.Text = "";
        _updating = false;
        RefreshCourseList();
        RefreshControls();
        RefreshControlFields();
    }

    private void RefreshCourseList()
    {
        _updating = true;
        CourseBox.Items.Clear();
        CourseBox.Items.Add(new ComboBoxItem { Content = "All controls", Tag = null });
        foreach (var c in _set.Courses) CourseBox.Items.Add(new ComboBoxItem { Content = c.Name.Length == 0 ? "(unnamed)" : c.Name, Tag = c.Id });
        int index = SelectedCourse is null ? 0 : _set.Courses.FindIndex(c => c.Id == SelectedCourse.Id) + 1;
        CourseBox.SelectedIndex = Math.Max(index, 0);
        RefreshCourseFields();
        _updating = false;
    }

    private void RefreshCourseFields()
    {
        bool wasUpdating = _updating;
        _updating = true;
        var c = SelectedCourse;
        CourseFields.Visibility = c is null ? Visibility.Collapsed : Visibility.Visible;
        DeleteCourseButton.IsEnabled = c is not null;
        if (c is not null)
        {
            CourseNameBox.Text = c.Name;
            KindBox.SelectedIndex = c.Kind == CourseKind.Score ? 1 : 0;
            CompetitorsBox.Value = c.Competitors;
            ClimbBox.Value = c.ClimbM ?? double.NaN;
            StartDistanceBox.Value = c.StartDistanceM ?? double.NaN;
            ClassesBox.Text = c.Classes ?? "";
            FinishRouteBox.SelectedIndex = c.EffectiveFinishRoute switch { FinishRoute.Taped => 1, FinishRoute.Funnel => 2, _ => 0 };
            var controls = _set.ControlsOf(c).Count(x => x.Kind == ControlKind.Control);
            CourseStatsText.Text = c.Kind == CourseKind.Score ? $"{controls} controls · score" : $"{_set.Length(c) / 1000:0.0} km · {controls} controls";
        }
        _updating = wasUpdating;
    }

    private void RefreshControls()
    {
        _updating = true;
        var list = SelectedCourse is { } course ? _set.ControlsOf(course) : DescriptionSheet.AllControlsOrder(_set);
        var rows = new List<ControlRow>();
        int n = 0;
        foreach (var c in list)
        {
            string label = c.Kind switch
            {
                ControlKind.Control => SelectedCourse is { Kind: CourseKind.Normal } ? $"{++n}" : c.Code?.ToString() ?? "?",
                _ => c.Label(_set),
            };
            var detail = c.Kind == ControlKind.Control ? $"{c.Code?.ToString() ?? "no code"} · {(c.Description.IsEmpty ? "no description" : c.Description.Text)}" : c.Kind.Label();
            rows.Add(new ControlRow(c.Id, label, detail));
        }
        ControlsList.ItemsSource = rows;
        var selectedIndex = SelectedControlId is { } id ? rows.FindIndex(r => r.Id == id) : -1;
        ControlsList.SelectedIndex = selectedIndex;
        bool hasSelection = selectedIndex >= 0;
        MoveUpButton.IsEnabled = hasSelection && SelectedCourse is not null && selectedIndex > 0;
        MoveDownButton.IsEnabled = hasSelection && SelectedCourse is not null && selectedIndex < rows.Count - 1;
        RemoveFromCourseButton.IsEnabled = hasSelection && SelectedCourse is not null;
        DeleteControlButton.IsEnabled = hasSelection;
        _updating = false;
    }

    private void RefreshControlFields()
    {
        _updating = true;
        var c = SelectedControlId is { } id ? _set.Control(id) : null;
        ControlFields.Visibility = c is null ? Visibility.Collapsed : Visibility.Visible;
        if (c is not null)
        {
            CodeBox.IsEnabled = c.Kind == ControlKind.Control;
            CodeBox.Value = c.Code ?? double.NaN;
            SizeBox.Value = Math.Round(c.EffectiveSize * 100);
            var d = c.Description;
            Select(WhichBox, d.Which);
            Select(FeatureBox, d.Feature);
            Select(AppearanceBox, d.Appearance);
            var dim = d.Dimension?.Trim() ?? "";
            bool isCombination = IofSymbols.Symbol(dim)?.Column == IofColumn.F;
            Select(CombinationBox, isCombination ? dim : "");
            DimensionBox.Text = isCombination ? "" : dim;
            var (locBase, dir) = d.Location is { } loc ? IofSymbols.SplitDirection(loc) : ("", null);
            Select(LocationBox, locBase);
            DirectionBox.IsEnabled = IofSymbols.IsDirectional(locBase);
            Select(DirectionBox, dir ?? IofSymbols.DefaultDirection);
            Select(OtherBox, d.Other);
            DescriptionText.Text = d.IsEmpty ? "No description yet." : d.Text;
        }
        _updating = false;
    }

    private static void Select(ComboBox box, string? tag)
    {
        var t = tag ?? "";
        for (int i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is ComboBoxItem item && (item.Tag as string ?? "") == t) { box.SelectedIndex = i; return; }
        box.SelectedIndex = box.Items.Count > 0 ? 0 : -1;
    }

    private static string? TagOf(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: string s } && s.Length > 0 ? s : null;

    private void Notify() { if (!_updating) Changed?.Invoke(this, EventArgs.Empty); }

    // MARK: Event + course

    private void EventFields_Changed(object sender, object e)
    {
        if (_updating) return;
        _set.EventTitle = EventTitleBox.Text.Trim();
        _set.Organizer = OrganizerBox.Text.Trim().Length == 0 ? null : OrganizerBox.Text.Trim();
        Notify();
    }

    private void CourseBox_SelectionChanged(object sender, object e)
    {
        if (_updating) return;
        var id = CourseBox.SelectedItem is ComboBoxItem { Tag: Guid g } ? g : (Guid?)null;
        SelectedCourse = id is { } gid ? _set.CourseById(gid) : null;
        SelectedControlId = null;
        _updating = true; RefreshCourseFields(); _updating = false;
        RefreshControls();
        RefreshControlFields();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddCourse_Click(object sender, RoutedEventArgs e)
    {
        var course = new Course($"Course {_set.Courses.Count + 1}");
        // A new course starts with the existing start and finish, if there are any.
        var start = _set.Controls.FirstOrDefault(c => c.Kind == ControlKind.Start);
        var finish = _set.Controls.FirstOrDefault(c => c.Kind == ControlKind.Finish);
        if (start is not null) course.ControlIds.Add(start.Id);
        if (finish is not null) course.ControlIds.Add(finish.Id);
        _set.Courses.Add(course);
        SelectedCourse = course;
        SelectedControlId = null;
        RefreshCourseList();
        RefreshControls();
        RefreshControlFields();
        Notify();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteCourse_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCourse is not { } c) return;
        _set.Courses.RemoveAll(x => x.Id == c.Id);
        SelectedCourse = _set.Courses.FirstOrDefault();
        SelectedControlId = null;
        RefreshCourseList();
        RefreshControls();
        RefreshControlFields();
        Notify();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CourseFields_Changed(object sender, object e)
    {
        if (_updating || SelectedCourse is not { } c) return;
        c.Name = CourseNameBox.Text.Trim();
        c.Kind = KindBox.SelectedIndex == 1 ? CourseKind.Score : CourseKind.Normal;
        c.Competitors = double.IsNaN(CompetitorsBox.Value) ? 0 : (int)CompetitorsBox.Value;
        c.ClimbM = double.IsNaN(ClimbBox.Value) || ClimbBox.Value <= 0 ? null : (int)ClimbBox.Value;
        c.StartDistanceM = double.IsNaN(StartDistanceBox.Value) || StartDistanceBox.Value <= 0 ? null : (int)StartDistanceBox.Value;
        c.Classes = ClassesBox.Text.Trim().Length == 0 ? null : ClassesBox.Text.Trim();
        c.FinishRoute = FinishRouteBox.SelectedIndex switch { 1 => FinishRoute.Taped, 2 => FinishRoute.Funnel, _ => null };
        RefreshCourseList();
        RefreshControls();
        Notify();
    }

    // MARK: Placement + controls

    private void Place_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var button = (ToggleButton)sender;
        _updating = true;
        foreach (var other in new[] { PlaceStart, PlaceControl, PlaceFinish }) if (!ReferenceEquals(other, button)) other.IsChecked = false;
        _updating = false;
        PlaceKind = button.IsChecked == true ? (ReferenceEquals(button, PlaceStart) ? ControlKind.Start : ReferenceEquals(button, PlaceFinish) ? ControlKind.Finish : ControlKind.Control) : null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ControlsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        SelectedControlId = (ControlsList.SelectedItem as ControlRow)?.Id;
        RefreshControls();
        RefreshControlFields();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Move(int delta)
    {
        if (SelectedCourse is not { } c || SelectedControlId is not { } id) return;
        int i = c.ControlIds.IndexOf(id);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= c.ControlIds.Count) return;
        (c.ControlIds[i], c.ControlIds[j]) = (c.ControlIds[j], c.ControlIds[i]);
        RefreshControls();
        RefreshCourseFields();
        Notify();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void RemoveFromCourse_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCourse is not { } c || SelectedControlId is not { } id) return;
        c.ControlIds.RemoveAll(x => x == id);
        SelectedControlId = null;
        RefreshControls();
        RefreshControlFields();
        RefreshCourseFields();
        Notify();
    }

    private void DeleteControl_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedControlId is not { } id) return;
        _set.Controls.RemoveAll(c => c.Id == id);
        foreach (var c in _set.Courses) c.ControlIds.RemoveAll(x => x == id);
        SelectedControlId = null;
        RefreshControls();
        RefreshControlFields();
        RefreshCourseFields();
        Notify();
    }

    private void Renumber_Click(object sender, RoutedEventArgs e)
    {
        var renumbered = _set.Renumbered();
        foreach (var c in _set.Controls) c.Code = renumbered.Control(c.Id)?.Code ?? c.Code;
        RefreshControls();
        RefreshControlFields();
        Notify();
    }

    private void ControlFields_Changed(object sender, object e)
    {
        if (_updating || SelectedControlId is not { } id || _set.Control(id) is not { } c) return;
        if (c.Kind == ControlKind.Control) c.Code = double.IsNaN(CodeBox.Value) ? null : (int)Math.Clamp(CodeBox.Value, CourseSet.CodeRange.Min, CourseSet.CodeRange.Max);
        double size = double.IsNaN(SizeBox.Value) ? 100 : SizeBox.Value;
        c.SizeFactor = Math.Abs(size - 100) < 0.5 ? null : Math.Clamp(size / 100, ControlPoint.SizeRange.Min, ControlPoint.SizeRange.Max);
        var d = c.Description;
        d.Which = TagOf(WhichBox);
        d.Feature = TagOf(FeatureBox);
        d.Appearance = TagOf(AppearanceBox);
        var combination = TagOf(CombinationBox);
        if (ReferenceEquals(sender, CombinationBox) && combination is not null) { d.Dimension = combination; _updating = true; DimensionBox.Text = ""; _updating = false; }
        else if (ReferenceEquals(sender, DimensionBox) || combination is null) d.Dimension = DimensionBox.Text.Trim().Length == 0 ? null : DimensionBox.Text.Trim();
        var locBase = TagOf(LocationBox);
        if (ReferenceEquals(sender, LocationBox)) { _updating = true; DirectionBox.IsEnabled = locBase is not null && IofSymbols.IsDirectional(locBase); _updating = false; }
        d.Location = locBase is null ? null : IofSymbols.IsDirectional(locBase) ? locBase + (TagOf(DirectionBox) ?? IofSymbols.DefaultDirection) : locBase;
        d.Other = TagOf(OtherBox);
        _updating = true;
        DescriptionText.Text = d.IsEmpty ? "No description yet." : d.Text;
        _updating = false;
        RefreshControls();
        Notify();
    }

    // MARK: Layout + tools

    private void Layout_Changed(object sender, object e)
    {
        if (_updating) return;
        var layout = _set.PrintLayout ??= new CoursePrintLayout();
        layout.DescriptionOnMap = DescriptionOnMapCheck.IsChecked == true;
        layout.DescriptionCorner = (CoursePrintLayout.Corner)Math.Max(CornerBox.SelectedIndex, 0);
        if (!double.IsNaN(CellBox.Value)) layout.DescriptionCellMM = Math.Clamp(CellBox.Value, CoursePrintLayout.CellRangeMM.Min, CoursePrintLayout.CellRangeMM.Max);
        layout.DescriptionSymbols = DescriptionStyleBox.SelectedIndex != 1;
        layout.DescriptionMaxRows = double.IsNaN(MaxRowsBox.Value) || MaxRowsBox.Value < CoursePrintLayout.MaxRowsRange.Min ? null : (int)MaxRowsBox.Value;
        layout.SeparateDescriptionPage = SeparatePageCheck.IsChecked == true;
        layout.DescriptionColorValue = PurpleDescriptionCheck.IsChecked == true ? CoursePrintLayout.DescriptionColor.Purple : CoursePrintLayout.DescriptionColor.Black;
        Notify();
    }

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        var w = _set.Warnings();
        WarningsText.Text = w.Count == 0 ? "No problems found." : string.Join("\n", w);
    }

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);
    private void ExportIof_Click(object sender, RoutedEventArgs e) => ExportIofRequested?.Invoke(this, EventArgs.Empty);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>A row of the controls list (public so {Binding} can read it).</summary>
public sealed record ControlRow(Guid Id, string Label, string Detail);
