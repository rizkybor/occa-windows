using System.Globalization;

namespace Occa.Core.Courses;

/// <summary>
/// Control description sheet for one course (IOF Control Descriptions 2024; text mode like Purple Pen's):
/// title, organizer, optional classes, course name + length + climb, optional 14.1 distance to the start triangle,
/// start row, control rows (A number, B code, C–H symbols or IOF text) with 15.x special instructions between them,
/// and the 16.x finish row (distance from the last control). Port of <c>DescriptionSheet</c>.
/// </summary>
public sealed class DescriptionSheet
{
    /// <summary>A full-width row that is not a control: 14.1 or a 15.x special instruction.</summary>
    public abstract record Special
    {
        /// <summary>14.1: distance from the timed start to the start triangle (m).</summary>
        public sealed record StartDistance(int Meters) : Special;
        /// <summary>15.1–15.6 special instruction.</summary>
        public sealed record Instruction(SpecialInstructionKind Kind, int? Meters) : Special;

        public string Text => this switch
        {
            StartDistance s => $"Distance to start triangle: {s.Meters} m",
            Instruction i => i.Kind.Text(i.Meters),
            _ => "",
        };

        public bool IsInstruction => this is Instruction;
    }

    public sealed record Row
    {
        /// <summary>Start/finish are drawn with their symbol (triangle / double circle) in column A.</summary>
        public ControlKind Kind { get; set; }
        public string Number { get; set; } = "";
        public string Code { get; set; } = "";
        public string Text { get; set; } = "";
        /// <summary>Columns C–H for symbol descriptions.</summary>
        public ControlDescription Description { get; set; } = new();
        /// <summary>Finish: navigation distance from the last control (m).</summary>
        public int? FinishMeters { get; set; }
        /// <summary>Finish: 16.1 taped, 16.2 funnel, 16.3 navigate.</summary>
        public FinishRoute FinishRoute { get; set; } = FinishRoute.Navigate;
        /// <summary>14.1 / 15.x row (drawn full width); null = start, control, or finish row.</summary>
        public Special? SpecialRow { get; set; }
        /// <summary>Index of the row's control in the course's control list (or the "All controls" order), for editing.</summary>
        public int? ControlIndex { get; set; }
        /// <summary>Thick line below this row: after every 3rd control and on both sides of a special instruction.</summary>
        public bool ThickBelow { get; set; }

        public Row() { }

        public Row(ControlKind kind, string number, string code, string text, ControlDescription? description = null, int? finishMeters = null,
                   FinishRoute finishRoute = FinishRoute.Navigate, Special? special = null, int? controlIndex = null)
        {
            Kind = kind;
            Number = number;
            Code = code;
            Text = text;
            Description = description ?? new ControlDescription();
            FinishMeters = finishMeters;
            FinishRoute = finishRoute;
            SpecialRow = special;
            ControlIndex = controlIndex;
        }

        /// <summary>A 14.1 / 15.x row on its own (e.g. a preview in the editor).</summary>
        public static Row ForSpecial(Special special)
            => new(special.IsInstruction ? ControlKind.Control : ControlKind.Start, "", "", special.Text, special: special);

        /// <summary>Drawn as one full-width row (finish only in symbol mode).</summary>
        public bool IsFullWidth(bool symbols) => SpecialRow is not null || (symbols && Kind == ControlKind.Finish);
    }

    public string Title { get; set; }
    /// <summary>Organizer name (row below the event title); empty = not printed.</summary>
    public string Organizer { get; set; } = "";
    /// <summary>Classes line (optional, below the title/organizer), e.g. "M45 M50 W21"; empty = not printed.</summary>
    public string Classes { get; set; } = "";
    public string CourseName { get; set; }
    public string LengthText { get; set; }
    public string ClimbText { get; set; }
    public List<Row> Rows { get; set; }
    /// <summary>Continuation column (no title or course name row), used when rows exceed the per-column limit.</summary>
    public bool IsContinuation { get; set; }

    /// <summary>Heading rows above the course line (title, organizer, classes); 0 for a continuation column.</summary>
    public int HeadingRowCount => IsContinuation ? 0 : new[] { Title, Organizer, Classes }.Count(s => s.Length > 0);

    /// <summary>Split into columns of at most <paramref name="maxRows"/> rows; the first column carries the title + course name.</summary>
    public List<DescriptionSheet> Columns(int? maxRows)
    {
        if (maxRows is not int max || max <= 0 || Rows.Count <= max) return new List<DescriptionSheet> { this };
        var result = new List<DescriptionSheet>();
        for (int start = 0; start < Rows.Count; start += max)
        {
            var col = (DescriptionSheet)MemberwiseClone();
            col.Rows = Rows.GetRange(start, Math.Min(max, Rows.Count - start));
            col.IsContinuation = start > 0;
            result.Add(col);
        }
        return result;
    }

    /// <summary>Row order for "All controls": start, controls by code, then finish.</summary>
    public static List<ControlPoint> AllControlsOrder(CourseSet set)
        => set.Controls.Where(c => c.Kind == ControlKind.Start)
            .Concat(set.Controls.Where(c => c.Kind == ControlKind.Control).OrderBy(c => c.Code ?? int.MaxValue))
            .Concat(set.Controls.Where(c => c.Kind == ControlKind.Finish)).ToList();

    /// <summary>Climb as printed: rounded to the nearest 5 m (IOF 2024 heading).</summary>
    public static string ClimbTextFor(int? climbM)
    {
        if (climbM is not int m || m <= 0) return "";
        return $"{(int)Math.Round(m / 5.0, MidpointRounding.AwayFromZero) * 5} m";
    }

    /// <summary>IOF rule: a thick line after every 3rd control and on both sides of every special instruction.</summary>
    internal static void MarkThickLines(List<Row> rows)
    {
        int n = 0;
        foreach (var r in rows) r.ThickBelow = false;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].SpecialRow is { } sp)
            {
                if (sp.IsInstruction)
                {
                    rows[i].ThickBelow = true;
                    if (i > 0) rows[i - 1].ThickBelow = true;
                }
            }
            else if (rows[i].Kind == ControlKind.Control)
            {
                n++;
                if (n % 3 == 0) rows[i].ThickBelow = true;
            }
        }
    }

    /// <summary>
    /// Description sheet for <paramref name="course"/>; null = "All controls" (master): column A empty, code in column B,
    /// sorted by code, without course length and finish distance.
    /// </summary>
    public DescriptionSheet(CourseSet set, Course? course)
    {
        Title = set.EventTitle;
        Organizer = set.Organizer?.Trim() ?? "";
        if (course is null)
        {
            CourseName = "All controls";
            LengthText = "";
            ClimbText = "";
            var all = AllControlsOrder(set);
            var rowsAll = new List<Row>();
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                var d = c.Description.Text;
                rowsAll.Add(c.Kind switch
                {
                    ControlKind.Start => new Row(ControlKind.Start, "", "", d.Length == 0 ? "Start" : $"Start: {d}", c.Description, controlIndex: i),
                    ControlKind.Control => new Row(ControlKind.Control, "", c.Code?.ToString() ?? "", d, c.Description, controlIndex: i),
                    _ => new Row(ControlKind.Finish, "", "", "Finish", controlIndex: i),
                });
            }
            MarkThickLines(rowsAll);
            Rows = rowsAll;
            return;
        }
        Classes = course.Classes?.Trim() ?? "";
        CourseName = course.Name;
        double km = set.Length(course) / 1000;
        LengthText = course.Kind == CourseKind.Score ? "Score" : km.ToString("0.0", CultureInfo.InvariantCulture) + " km";
        ClimbText = ClimbTextFor(course.ClimbM);
        var list = set.ControlsOf(course);
        var rows = new List<Row>();
        if (course.StartDistanceM is int sd && sd > 0) rows.Add(Row.ForSpecial(new Special.StartDistance(sd)));
        // Special instructions by position: after the n-th control (0 = after the start).
        var specials = course.AllSpecialInstructions;
        int controlCount = list.Count(c => c.Kind == ControlKind.Control);
        IEnumerable<Row> SpecialRows(int n) => specials.Where(s => Math.Min(Math.Max(s.AfterControl, 0), controlCount) == n)
            .Select(s => Row.ForSpecial(new Special.Instruction(s.Kind, s.Kind.HasLength() ? s.LengthM : null)));
        bool placedFirst = !list.Any(c => c.Kind == ControlKind.Start);
        if (placedFirst) rows.AddRange(SpecialRows(0));
        int num = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            switch (c.Kind)
            {
                case ControlKind.Start:
                {
                    var d = c.Description.Text;
                    rows.Add(new Row(ControlKind.Start, "", "", d.Length == 0 ? "Start" : $"Start: {d}", c.Description, controlIndex: i));
                    if (!placedFirst) { rows.AddRange(SpecialRows(0)); placedFirst = true; }
                    break;
                }
                case ControlKind.Control:
                {
                    num++;
                    var d = c.Description.Text;
                    rows.Add(new Row(ControlKind.Control, course.Kind == CourseKind.Score ? "" : num.ToString(), c.Code?.ToString() ?? "",
                                     d.Length == 0 ? "(no description yet)" : d, c.Description, controlIndex: i));
                    rows.AddRange(SpecialRows(num));
                    break;
                }
                default:
                {
                    var prev = i > 0 ? list[i - 1] : null;
                    int? dist = prev is null ? null : (int)(Math.Round(prev.Position.DistanceTo(c.Position) / 10, MidpointRounding.AwayFromZero) * 10);
                    var route = course.EffectiveFinishRoute;
                    rows.Add(new Row(ControlKind.Finish, "", "", route.Text(dist), finishMeters: dist, finishRoute: route, controlIndex: i));
                    break;
                }
            }
        }
        MarkThickLines(rows);
        Rows = rows;
    }
}
