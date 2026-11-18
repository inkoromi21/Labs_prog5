using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace WpfApp2;

public sealed record ChartCurve(Geometry geometry, bool derivative);

public sealed record ChartFrame(PlotViewport view, double width, double height,
    IReadOnlyList<ChartCurve> curves, long bytes) {
  public string expression {
    get;
    init;
  } = "";
  public bool includesDerivative {
    get;
    init;
  }

  public static ChartFrame parse(byte[] svg, PlotViewport view) {
    try {
      using var stream = new MemoryStream(svg);
      using var reader = XmlReader.Create(stream, new XmlReaderSettings {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 8_000_000
      });
      var document = XDocument.Load(reader);
      var root = document.Root;
      if (root?.Name.LocalName != "svg") {
        throw new FormatException();
      }
      double[] box = numbers(root.Attribute("viewBox")?.Value ?? "");
      if (box.Length != 4 || box[2] <= 0 || box[3] <= 0
          || box.Any(v => !double.IsFinite(v))) {
        throw new FormatException();
      }
      var curves = new List<ChartCurve>();
      foreach (var path in root.Descendants().Where(e => e.Name.LocalName == "path")) {
        var style = (path.Attribute("style")?.Value ?? "").Split(';')
            .Select(s => s.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        if (!style.TryGetValue("stroke", out string? stroke) || stroke == "none") {
          continue;
        }
        double[] rgb = numbers(stroke);
        if (rgb.Length != 3) {
          throw new FormatException();
        }
        if (stroke.Contains('%')) {
          rgb = rgb.Select(v => v * 2.55).ToArray();
        }
        bool derivative = Math.Abs(rgb[0] - 139) < 2 && Math.Abs(rgb[1] - 92) < 2
            && Math.Abs(rgb[2] - 246) < 2;
        bool function = Math.Abs(rgb[0] - 37) < 2 && Math.Abs(rgb[1] - 99) < 2
            && Math.Abs(rgb[2] - 235) < 2;
        if (!function && !derivative) {
          throw new FormatException();
        }
        var geometry = Geometry.Parse(path.Attribute("d")?.Value ?? "").Clone();
        var matrix = Matrix.Identity;
        foreach (var node in path.AncestorsAndSelf()) {
          if (node.Attribute("transform") is not XAttribute transform) {
            continue;
          }
          double[] values = numbers(transform.Value);
          if (!transform.Value.StartsWith("matrix(", StringComparison.Ordinal)
              || values.Length != 6 || values.Any(v => !double.IsFinite(v))) {
            throw new FormatException();
          }
          matrix.Append(new Matrix(
              values[0], values[1], values[2], values[3], values[4], values[5]));
        }
        matrix.Translate(-box[0], -box[1]);
        geometry.Transform = new MatrixTransform(matrix);
        geometry.Freeze();
        curves.Add(new(geometry, derivative));
      }
      return new(view, box[2], box[3], curves, svg.LongLength * 8);
    } catch (Exception error) when (error is XmlException or FormatException
        or ArgumentException or InvalidOperationException) {
      throw new ServiceException("Сервис вернул неподдерживаемый векторный график.", error);
    }
  }

  private static double[] numbers(string text) => Regex.Matches(text,
      @"[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?").Select(m =>
          double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
}
