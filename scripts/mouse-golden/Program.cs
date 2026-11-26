using System.Text;
using GHelper.Linux.Tests;
using GHelper.Peripherals.Mouse;

string output = args[0];
var sb = new StringBuilder();
sb.Append("upstream ").Append(args.Length > 1 ? args[1] : "unknown").Append('\n');
foreach (var model in MouseScenario.Models(typeof(AsusMouse).Assembly))
{
    AppConfig.Reset();
    sb.Append(MouseScenario.Run(model));
}
File.WriteAllText(output, sb.ToString());
Console.WriteLine($"wrote {output}");
