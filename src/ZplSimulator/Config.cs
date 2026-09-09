using System.Text.Json;

namespace ZplSimulator;

public sealed class Config
{
    public string PrinterName { get; set; } = "ZPL Simulator";
    public string TargetPrinter { get; set; } = "Microsoft Print to PDF";
    public int Port { get; set; } = 19100;
    public string OutputDirectory { get; set; } = @"C:\ProgramData\ZplSimulator\Output";
    public bool PrintToFile { get; set; } = true;
    public string PaperMode { get; set; } = "PrinterDefault";
    public int DotsPerMillimeter { get; set; } = 8;
    public double LabelWidthMm { get; set; } = 101.6;
    public double LabelHeightMm { get; set; } = 152.4;
    public int MaxJobBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxPages { get; set; } = 100;
    public bool RejectUnknownCommands { get; set; } = false;
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZplSimulator");
    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public static Config Load(string? path = null)
    {
        var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(path ?? ConfigPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        }) ?? throw new InvalidDataException("Empty configuration.");
        if (string.IsNullOrWhiteSpace(c.PrinterName) || string.IsNullOrWhiteSpace(c.TargetPrinter)
            || c.PrinterName.Equals(c.TargetPrinter, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Input and target printer names must be nonempty and different.");
        if (c.PaperMode is not ("PrinterDefault" or "Label") || c.Port is < 1024 or > 65535 || c.DotsPerMillimeter is not (6 or 8 or 12 or 24)
            || !double.IsFinite(c.LabelWidthMm) || !double.IsFinite(c.LabelHeightMm)
            || c.LabelWidthMm is < 1 or > 1000 || c.LabelHeightMm is < 1 or > 1000
            || c.MaxJobBytes is < 1024 or > 67108864 || c.MaxPages is < 1 or > 1000
            || !Path.IsPathFullyQualified(c.OutputDirectory))
            throw new InvalidDataException("Invalid port, dimensions, density, limits, or output directory.");
        return c;
    }
}
