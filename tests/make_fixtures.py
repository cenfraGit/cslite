"""Generate and build the C# projects the test suites run against.

    python tests/make_fixtures.py <output-directory>

Creates three projects, restores and builds each so that obj/ holds the
artifacts cslite reads instead of running MSBuild itself:

    sample/   two projects joined by a ProjectReference
    sighelp/  overloads, params arrays and a documented constructor
    genlock/  a source generator plus a consumer that uses its output
    sandbox/  a single console project, used by the Emacs suites and by try.cmd
    web/      an ASP.NET project, which needs a second shared framework
    outline/  one file holding every kind of declaration, for the outline
"""
import subprocess
import sys
from pathlib import Path

ROOT = Path(sys.argv[1] if len(sys.argv) > 1 else "fixtures").resolve()


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def build(project, label, expect_success=True):
    """Build a fixture so obj/ holds the artifacts cslite reads.

    The sandbox contains deliberate errors, so it is built with
    expect_success=False: the compile fails, but the targets that write
    project.assets.json and the implicit-usings file still run.
    """
    print(f"  building {label} ...", flush=True)
    result = subprocess.run(
        ["dotnet", "build", str(project), "--nologo", "-v", "q"],
        capture_output=True, text=True)

    if expect_success and result.returncode != 0:
        print((result.stdout + result.stderr).strip()[-800:])
        raise SystemExit(f"failed to build {label}")


# --- sample: two projects, one referencing the other ------------------------

write(ROOT / "sample" / "Lib" / "Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""")

write(ROOT / "sample" / "Lib" / "Greeter.cs", """namespace Lib;

/// <summary>Produces greetings for people.</summary>
public sealed class Greeter
{
    /// <summary>Greets someone by name.</summary>
    public string Greet(string name) => $"Hello, {name}!";

    public int Count { get; set; }
}
""")

write(ROOT / "sample" / "Lib" / "MessageFormatter.cs", """namespace Lib;

/// <summary>Formats messages for display.</summary>
public sealed class MessageFormatter
{
    /// <summary>Formats every message.</summary>
    public string FormatAll(IEnumerable<string> messages) => string.Join("; ", messages);
}
""")

write(ROOT / "sample" / "App" / "App.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Lib/Lib.csproj" />
  </ItemGroup>
</Project>
""")

write(ROOT / "sample" / "App" / "Program.cs", """using Lib;

var greeter = new Greeter();
var list = new List<string> { "world" };
Console.WriteLine(greeter.Greet(list[0]));
""")

# --- sighelp: overloads and parameter shapes --------------------------------

write(ROOT / "sighelp" / "Sig.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""")

write(ROOT / "sighelp" / "Api.cs", """namespace Sig;

public sealed class Api
{
    /// <summary>Adds two numbers.</summary>
    public int Add(int left, int right) => left + right;

    /// <summary>Joins parts with a separator.</summary>
    public string Join(string separator, params string[] parts) => string.Join(separator, parts);

    public void Overloaded(int a) { }
    public void Overloaded(int a, string b) { }
    public void Overloaded(int a, string b, bool c = true) { }
}
""")

write(ROOT / "sighelp" / "Program.cs", """using Sig;

var api = new Api();
var sum = api.Add(1, 2);
var joined = api.Join(", ", "a", "b");
api.Overloaded(1, "x");
var other = new Api();
""")

# --- genlock: a source generator and something that consumes it -------------

write(ROOT / "genlock" / "Gen" / "Gen.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <IsRoslynComponent>true</IsRoslynComponent>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
""")

write(ROOT / "genlock" / "Gen" / "HelloGenerator.cs", '''using Microsoft.CodeAnalysis;

namespace Gen;

[Generator]
public sealed class HelloGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx => ctx.AddSource(
            "GeneratedGreeting.g.cs",
            "namespace Generated;\\npublic static class GeneratedGreeting\\n{\\n    public static string Text => \\"hello from the generator\\";\\n    public static int Version => 1;\\n}\\n"));
    }
}
''')

write(ROOT / "genlock" / "Consumer" / "Consumer.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Gen/Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
""")

write(ROOT / "genlock" / "Consumer" / "Program.cs", """using Generated;

Console.WriteLine(GeneratedGreeting.Text);
Console.WriteLine(GeneratedGreeting.Version);
""")

# --- sandbox: what the Emacs suites and try.cmd open -----------------------
#
# The Emacs tests assert on exact positions here: Add must be declared on line
# 7 of Calculator.cs, and Program.cs must call it as `calculator.Add`.

write(ROOT / "sandbox" / "Sandbox.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Sandbox</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
""")

write(ROOT / "sandbox" / "Calculator.cs", """namespace Sandbox;

/// <summary>Does arithmetic, badly.</summary>
public sealed class Calculator
{
    /// <summary>Adds two numbers together.</summary>
    public int Add(int left, int right) => left + right;

    /// <summary>Divides one number by another.</summary>
    public double Divide(double numerator, double denominator) => numerator / denominator;

    /// <summary>How many operations have been performed.</summary>
    public int OperationCount { get; private set; }
}
""")

write(ROOT / "sandbox" / "Program.cs", """using Newtonsoft.Json;
using Sandbox;

var calculator = new Calculator();

// Hover over Add, or jump to its definition with M-.
var sum = calculator.Add(2, 3);

// Type a dot after `calculator` to see completion.
Console.WriteLine(JsonConvert.SerializeObject(new { sum }));
""")

write(ROOT / "sandbox" / "Broken.cs", """namespace Sandbox;

// This file exists so the diagnostics tests have something to find.
public sealed class Broken
{
    public void Wrong()
    {
        int number = "this is not an int";
        UndefinedMethod();
    }
}
""")

# --- web: the Web SDK, which pulls in a second shared framework -------------
#
# Deliberately net8.0 while the newest installed pack is likely newer, so the
# reference packs have to be matched to the target framework rather than the
# newest one winning.

write(ROOT / "web" / "Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""")

write(ROOT / "web" / "Controllers" / "ThingsController.cs", """using Microsoft.AspNetCore.Mvc;

namespace Web.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class ThingsController : ControllerBase
{
    /// <summary>Returns every thing.</summary>
    [HttpGet]
    public IActionResult GetAll() => Ok(new[] { "one", "two" });

    [HttpGet("{id}")]
    public ActionResult<string> GetOne(int id) => Ok(id.ToString());
}
""")

write(ROOT / "web" / "Program.cs", """var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();
app.Run();
""")

# --- outline: every declaration shape, for document symbols ----------------

write(ROOT / "outline" / "Outline.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""")

write(ROOT / "outline" / "Shapes.cs", """namespace Outline;

public interface IShape
{
    double Area { get; }
}

public enum Colour
{
    Red,
    Green,
}

public readonly struct Point
{
    public Point(int x, int y) => (X, Y) = (x, y);

    public int X { get; }
    public int Y { get; }
}

public delegate void Painted(Colour colour);

public sealed class Canvas : IShape
{
    public const int MaxLayers = 8;

    private readonly List<string> _layers = new();
    private int _width, _height;

    public event Painted? OnPainted;

    public Canvas(int width, int height)
    {
        _width = width;
        _height = height;
    }

    ~Canvas() { }

    public double Area => _width * _height;

    public string this[int index] => _layers[index];

    public void Paint(Colour colour, int layer = 0) { }

    public static Canvas operator +(Canvas left, Canvas right) => left;

    public sealed class Layer
    {
        public string Name { get; set; } = "";

        public void Clear() { }
    }
}
""")

print(f"writing fixtures to {ROOT}")
build(ROOT / "sample" / "App" / "App.csproj", "sample")
build(ROOT / "sighelp" / "Sig.csproj", "sighelp")
build(ROOT / "genlock" / "Consumer" / "Consumer.csproj", "genlock")
build(ROOT / "web" / "Web.csproj", "web")
build(ROOT / "outline" / "Outline.csproj", "outline")
build(ROOT / "sandbox" / "Sandbox.csproj", "sandbox", expect_success=False)

# The sandbox does not compile, so check directly that the pieces cslite needs
# were still written.
for required in ("obj/project.assets.json",):
    if not (ROOT / "sandbox" / required).exists():
        raise SystemExit(f"sandbox is missing {required}; run dotnet restore there")
if not list((ROOT / "sandbox" / "obj").rglob("*GlobalUsings.g.cs")):
    raise SystemExit("sandbox has no implicit-usings file; the build did not get far enough")

generated = list((ROOT / "genlock" / "Consumer" / "obj").rglob("GeneratedGreeting.g.cs"))
if not generated:
    raise SystemExit("the generator did not emit to disk; EmitCompilerGeneratedFiles may be off")

print(f"""
ready. run the suites with:

  python tests/protocol_test.py          {ROOT / 'sample'} dist/cslite.exe
  python tests/references_rename_test.py {ROOT / 'sample'} dist/cslite.exe
  python tests/signature_help_test.py    {ROOT / 'sighelp'} dist/cslite.exe
  python tests/generator_lock_test.py    {ROOT / 'genlock'} dist/cslite.exe
  python tests/framework_reference_test.py {ROOT / 'web'} dist/cslite.exe
  python tests/document_symbol_test.py   {ROOT / 'outline'} dist/cslite.exe

and the Emacs suites with:

  set CSLITE_SANDBOX={ROOT / 'sandbox'}
  emacs -Q --batch -l tests/emacs-test.el
  emacs -Q --batch -l tests/emacs-refactor-test.el
""")
