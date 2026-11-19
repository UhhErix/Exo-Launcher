using System.Globalization;

namespace ExoLauncher.Helpers;

/// <summary>
/// An SVG path's data (M, L, H, V, C, S, A and Z, absolute or relative, numbers run together as SVG allows: ".4.4",
/// an arc's "01.591") as figures of lines and cubic Béziers, arcs turned into Béziers. One reading for the window chrome's
/// XAML paths (whose own parser takes neither run-together numbers nor flags) and for the icons drawn from the same marks.
/// </summary>
internal static class SvgPath
{
    internal readonly record struct Pt(double X, double Y);

    /// A line when <see cref="Line"/>, else a cubic Bézier through <see cref="C1"/> and <see cref="C2"/>.
    internal readonly record struct Segment(bool Line, Pt C1, Pt C2, Pt To);

    internal sealed class Figure(Pt start)
    {
        internal Pt Start { get; } = start;
        internal List<Segment> Segments { get; } = [];
        internal bool Closed { get; set; }
    }

    internal static List<Figure> Parse(string data)
    {
        var figures = new List<Figure>();
        var i = 0;
        var command = '\0';
        Pt current = default, start = default, lastControl = default;
        var lastWasCubic = false;
        Figure? figure = null;

        double Number()
        {
            Skip(data, ref i);
            var from = i;
            if (i < data.Length && data[i] is '+' or '-') i++;
            var dot = false;
            while (i < data.Length && (char.IsAsciiDigit(data[i]) || (data[i] == '.' && !dot)))
            {
                if (data[i] == '.') dot = true;
                i++;
            }
            if (i < data.Length && data[i] is 'e' or 'E')
            {
                i++;
                if (i < data.Length && data[i] is '+' or '-') i++;
                while (i < data.Length && char.IsAsciiDigit(data[i])) i++;
            }
            if (i == from) throw new FormatException($"A number was expected at {from} in the path.");
            return double.Parse(data.AsSpan(from, i - from), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        bool Flag()
        {
            Skip(data, ref i);
            if (i >= data.Length || data[i] is not ('0' or '1')) throw new FormatException($"An arc flag was expected at {i} in the path.");
            return data[i++] == '1';
        }
        Pt Point(bool relative)
        {
            var x = Number();
            var y = Number();
            return relative ? new Pt(current.X + x, current.Y + y) : new Pt(x, y);
        }
        Figure Open() => figure ??= Add(current);
        Figure Add(Pt at)
        {
            var added = new Figure(at);
            figures.Add(added);
            return added;
        }
        void Line(Pt to)
        {
            Open().Segments.Add(new Segment(true, current, to, to));
            current = to;
        }
        void Curve(Pt c1, Pt c2, Pt to)
        {
            Open().Segments.Add(new Segment(false, c1, c2, to));
            current = to;
            lastControl = c2;
        }

        while (true)
        {
            Skip(data, ref i);
            if (i >= data.Length) break;
            if (char.IsAsciiLetter(data[i])) command = data[i++];
            else if (command is '\0' or 'Z' or 'z') throw new FormatException($"A command was expected at {i} in the path.");
            var relative = char.IsAsciiLetterLower(command);
            var cubic = false;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    current = start = Point(relative);
                    figure = Add(current);
                    // Pairs after a move are lines.
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    Line(Point(relative));
                    break;
                case 'H':
                    Line(new Pt(Number() + (relative ? current.X : 0), current.Y));
                    break;
                case 'V':
                    Line(new Pt(current.X, Number() + (relative ? current.Y : 0)));
                    break;
                case 'C':
                {
                    var c1 = Point(relative);
                    var c2 = Point(relative);
                    Curve(c1, c2, Point(relative));
                    cubic = true;
                    break;
                }
                case 'S':
                {
                    var c1 = lastWasCubic ? new Pt(2 * current.X - lastControl.X, 2 * current.Y - lastControl.Y) : current;
                    var c2 = Point(relative);
                    Curve(c1, c2, Point(relative));
                    cubic = true;
                    break;
                }
                case 'A':
                {
                    var rx = Number();
                    var ry = Number();
                    var rotation = Number();
                    var large = Flag();
                    var sweep = Flag();
                    Arc(current, rx, ry, rotation, large, sweep, Point(relative), Line, Curve);
                    break;
                }
                case 'Z':
                    if (figure is not null) figure.Closed = true;
                    figure = null;
                    current = start;
                    break;
                default:
                    throw new FormatException($"The path command '{command}' isn't read.");
            }
            lastWasCubic = cubic;
        }
        return figures;
    }

    /// Each figure as a closed outline of points, its curves cut into lines no longer than about a pixel at
    /// <paramref name="scale"/> pixels per unit, then moved by (<paramref name="dx"/>, <paramref name="dy"/>).
    internal static List<List<Pt>> Outlines(IEnumerable<Figure> figures, double scale, double dx, double dy)
    {
        var outlines = new List<List<Pt>>();
        Pt Map(Pt p) => new(p.X * scale + dx, p.Y * scale + dy);
        foreach (var figure in figures)
        {
            var points = new List<Pt> { Map(figure.Start) };
            var from = figure.Start;
            foreach (var segment in figure.Segments)
            {
                if (segment.Line) points.Add(Map(segment.To));
                else
                {
                    var hull = Distance(from, segment.C1) + Distance(segment.C1, segment.C2) + Distance(segment.C2, segment.To);
                    var steps = Math.Clamp((int)Math.Ceiling(hull * scale), 1, 64);
                    for (var step = 1; step <= steps; step++)
                    {
                        var t = (double)step / steps;
                        var u = 1 - t;
                        points.Add(Map(new Pt(
                            u * u * u * from.X + 3 * u * u * t * segment.C1.X + 3 * u * t * t * segment.C2.X + t * t * t * segment.To.X,
                            u * u * u * from.Y + 3 * u * u * t * segment.C1.Y + 3 * u * t * t * segment.C2.Y + t * t * t * segment.To.Y)));
                    }
                }
                from = segment.To;
            }
            if (points.Count > 2) outlines.Add(points);
        }
        return outlines;
    }

    private static double Distance(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static void Skip(string data, ref int i)
    {
        while (i < data.Length && (char.IsWhiteSpace(data[i]) || data[i] == ',')) i++;
    }

    // SVG 1.1 F.6.5 and F.6.6: the endpoint arc to its center, then a cubic per quarter turn or less.
    private static void Arc(Pt from, double rx, double ry, double rotation, bool large, bool sweep, Pt to,
        Action<Pt> line, Action<Pt, Pt, Pt> curve)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0 || from == to)
        {
            if (from != to) line(to);
            return;
        }
        var phi = rotation * Math.PI / 180;
        var (cos, sin) = (Math.Cos(phi), Math.Sin(phi));
        var hx = (from.X - to.X) / 2;
        var hy = (from.Y - to.Y) / 2;
        var x1 = cos * hx + sin * hy;
        var y1 = -sin * hx + cos * hy;
        var lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1)
        {
            rx *= Math.Sqrt(lambda);
            ry *= Math.Sqrt(lambda);
        }
        var numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        var denominator = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        var coefficient = Math.Sqrt(Math.Max(0, numerator / denominator)) * (large == sweep ? -1 : 1);
        var cx1 = coefficient * rx * y1 / ry;
        var cy1 = -coefficient * ry * x1 / rx;
        var cx = cos * cx1 - sin * cy1 + (from.X + to.X) / 2;
        var cy = sin * cx1 + cos * cy1 + (from.Y + to.Y) / 2;
        static double Angle(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        var theta = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        var delta = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;
        var parts = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
        var step = delta / parts;
        var k = 4.0 / 3 * Math.Tan(step / 4);
        Pt Map(double ux, double uy) => new(cx + rx * cos * ux - ry * sin * uy, cy + rx * sin * ux + ry * cos * uy);
        for (var part = 0; part < parts; part++)
        {
            var a1 = theta + part * step;
            var a2 = a1 + step;
            var end = part == parts - 1 ? to : Map(Math.Cos(a2), Math.Sin(a2));
            curve(Map(Math.Cos(a1) - k * Math.Sin(a1), Math.Sin(a1) + k * Math.Cos(a1)),
                Map(Math.Cos(a2) + k * Math.Sin(a2), Math.Sin(a2) - k * Math.Cos(a2)), end);
        }
    }
}
