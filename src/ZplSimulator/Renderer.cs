using System.Text.RegularExpressions;
using BinaryKits.Zpl.Viewer;
using BinaryKits.Zpl.Viewer.ElementDrawers;

namespace ZplSimulator;

public sealed record Page(byte[] Png, double WidthMm, double HeightMm, int Copies);

public sealed class Renderer(Config config, Action<string> log)
{
    // Device settings without a visual effect; dimensions/quantity are handled here.
    private static readonly HashSet<string> Handled = ["PW", "LL", "PQ", "PR", "MD", "MT", "MM", "MN"];
    private static readonly Regex Labels = new(@"\^XA.*?\^XZ", RegexOptions.Singleline | RegexOptions.Compiled);

    public List<Page> Render(string zpl)
    {
        if (Regex.IsMatch(zpl, @"[\^~](CC|CT|CD)|\^GF[Bb]|~D[BbTtUu]|\^(DF|XF|SN|SF)"))
            throw new InvalidDataException("Changed delimiters, binary graphics, downloaded fonts, stored formats, and serialization are not supported.");
        var matches = Labels.Matches(zpl);
        if (matches.Count == 0 || matches.Count > config.MaxPages
            || Regex.Matches(zpl, @"\^XA").Count != matches.Count
            || Regex.Matches(zpl, @"\^XZ").Count != matches.Count)
            throw new InvalidDataException("Expected complete ^XA ... ^XZ labels within the page limit.");

        var storage = new PrinterStorage();
        var analyzer = new ZplAnalyzer(storage);
        var drawer = new ZplElementDrawer(storage, new DrawerOptions { OpaqueBackground = true });
        var pages = new List<Page>();
        var width = config.LabelWidthMm;
        var height = config.LabelHeightMm;
        int previousEnd = 0, count = 0;
        foreach (Match match in matches)
        {
            // Include preceding downloads, e.g. ~DG, with the next label.
            var input = zpl[previousEnd..(match.Index + match.Length)];
            previousEnd = match.Index + match.Length;
            width = Dimension(input, "PW", width);
            height = Dimension(input, "LL", height);
            if (width < 1 || height < 1 || width > 1000 || height > 1000
                || width * height * config.DotsPerMillimeter * config.DotsPerMillimeter > 25_000_000)
                throw new InvalidDataException("Label dimensions exceed the 25 megapixel / 1000 mm limit.");
            var quantity = Regex.Match(input, @"\^PQ(\d+)([^\^~]*)");
            var copies = quantity.Success ? int.Parse(quantity.Groups[1].Value) : 1;
            if (copies < 1 || copies > config.MaxPages || (count += copies) > config.MaxPages)
                throw new InvalidDataException("Print quantity exceeds MaxPages.");
            if (quantity.Success && quantity.Groups[2].Value.Trim().Length > 0)
                log("Only the quantity parameter of ^PQ is simulated.");
            var result = analyzer.Analyze(input);
            if (result.Errors.Length > 0)
                throw new InvalidDataException("ZPL parser errors: " + string.Join("; ", result.Errors));
            var unknown = result.UnknownCommands
                .Where(x => x.Length >= 3 && !Handled.Contains(x.Substring(1, 2)))
                .Select(x => x[..Math.Min(x.Length, 3)]).Distinct().ToArray();
            if (unknown.Length > 0)
            {
                var warning = "Unsupported commands: " + string.Join(", ", unknown);
                if (config.RejectUnknownCommands) throw new InvalidDataException(warning);
                log(warning);
            }
            foreach (var label in result.LabelInfos)
            {
                if (!string.IsNullOrEmpty(label.DownloadFormatName))
                    throw new InvalidDataException("Stored format definitions are not supported; submit expanded labels.");
                pages.Add(new Page(drawer.Draw(label.ZplElements, width, height, config.DotsPerMillimeter), width, height, copies));
            }
        }
        if (pages.Count == 0) throw new InvalidDataException("No printable labels.");
        if (!string.IsNullOrWhiteSpace(zpl[previousEnd..].Trim('\0', '\u0004', '\u001a', '\u000c', ' ', '\r', '\n', '\t')))
            throw new InvalidDataException("Unexpected data after the final label.");
        return pages;
    }

    private double Dimension(string input, string command, double fallback)
    {
        var values = Regex.Matches(input, @"\^" + command + @"(\d+)");
        return values.Count == 0 ? fallback : double.Parse(values[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) / config.DotsPerMillimeter;
    }
}
