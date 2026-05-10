using System;
using System.Linq;
using ClosedXML.Excel;

Console.WriteLine("=== IXLCFColorScaleMax methods ===");
foreach (var m in typeof(IXLCFColorScaleMax).GetMethods())
{ var ps = m.GetParameters(); Console.WriteLine($"  {m.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name))}) => {m.ReturnType.Name}"); }

Console.WriteLine("=== IXLCFColorScaleMid methods (all) ===");
foreach (var m in typeof(IXLCFColorScaleMid).GetMethods())
{ var ps = m.GetParameters(); Console.WriteLine($"  {m.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name))}) => {m.ReturnType.Name}"); }

Console.WriteLine("=== XLCFContentType values ===");
foreach (var v in Enum.GetNames(typeof(XLCFContentType))) Console.WriteLine("  " + v);
