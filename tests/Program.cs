using System.Text;
using SkiaSharp;
using ZplSimulator;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
void Reject(string zpl, string name, Config? config = null)
{
    try { new Renderer(config ?? new Config(), _ => { }).Render(zpl); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
var messages = new List<string>();
var renderer = new Renderer(new Config(), messages.Add);
var pages = renderer.Render("^XA^PW400^LL600^FO20,20^GB100,100,100^FS^PQ2^XZ^XA^PW200^LL300^FO10,10^A0N,20,20^FDSecond^FS^XZ");
Check(pages.Count == 2 && pages[0].Copies == 2 && pages[1].Copies == 1, "multiple labels and ^PQ");
using (var bitmap = SKBitmap.Decode(pages[0].Png))
{
    Check(bitmap.Width == 400 && bitmap.Height == 600, "^PW/^LL dimensions in dots");
    Check(bitmap.GetPixel(50, 50).Red < 32 && bitmap.GetPixel(150, 150).Red > 240, "solid graphic and white background");
}
using (var bitmap = SKBitmap.Decode(pages[1].Png)) Check(bitmap.Width == 200 && bitmap.Height == 300, "mixed label dimensions");
var fallback = renderer.Render("^XA^FO0,0^GB10,10,10^FS^XZ")[0];
using (var bitmap = SKBitmap.Decode(fallback.Png)) Check(bitmap.Width == 813 && bitmap.Height == 1219, "default 4 x 6 inch canvas");
var inherited = renderer.Render("^XA^PW240^LL320^FO0,0^GB10,10,10^FS^XZ^XA^FO0,0^GB10,10,10^FS^XZ");
Check(inherited[1].WidthMm == 30 && inherited[1].HeightMm == 40, "dimensions persist within job");
renderer.Render("^XA^ZZignored^FO0,0^GB10,10,10^FS^XZ");
Check(messages.Any(m => m.Contains("^ZZ")), "unsupported command warning");
Reject("^XA^ZZignored^XZ", "strict unsupported commands", new Config { RejectUnknownCommands = true });
Reject("^XA^FO1,1^FDincomplete", "incomplete label");
Reject("^XA^XA^XZ", "nested label");
Reject("^XA^XZ^XZ", "extra label terminator");
Reject("^XA^XZ^FO1,1", "trailing commands");
Reject("^XA^PW999999^LL999999^XZ", "oversized canvas");
Reject("^XA^PQ101^XZ", "quantity limit");
Reject("^XA^PQ0^XZ", "zero copies");
Reject("^XA^CC!^XZ", "changed delimiter");
Reject("^XA^GFB,1,1,1,0^FS^XZ", "binary graphics");
Reject("^XA^DFR:FORMAT.ZPL^FS^XZ", "stored formats");
if (args.Length > 0)
{
    var sample = renderer.Render(File.ReadAllText(args[0]));
    Check(sample.Count == 1 && sample[0].Png.Length > 5000, "sample text, Code 128, and QR render");
    if (args.Length > 1) { Directory.CreateDirectory(args[1]); File.WriteAllBytes(Path.Combine(args[1], "sample.png"), sample[0].Png); }
}
Console.WriteLine($"{checks} checks passed.");
