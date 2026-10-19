using focus_desktop.Services;
using System.Text.Json;

if (args.Length < 2 || args.Length > 3 || (args[0] != "--resolve" && args[0] != "--launch")
    || (args.Length == 3 && !args[2].StartsWith("--out=", StringComparison.Ordinal)))
{
    Console.Error.WriteLine("Usage: EntryProbe --resolve <shortcut-or-exe> [--out=<entry.json>] | --launch <shortcut-or-exe> [--out=<entry.json>]");
    return 2;
}

try
{
    var entry = ExternalAppCatalog.Resolve(args[1]);
    Console.WriteLine($"Kind: {entry.Kind}");
    Console.WriteLine($"Id: {entry.Id}");
    Console.WriteLine($"AppUserModelId: {entry.AppUserModelId ?? "(none)"}");
    Console.WriteLine($"ExecutablePath: {entry.ExecutablePath ?? "(packaged app)"}");

    if (args.Length == 3)
    {
        var outputPath = Path.GetFullPath(args[2]["--out=".Length..]);
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, entry, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Entry JSON: {outputPath}");
    }

    if (args[0] == "--launch")
    {
        using var process = ExternalAppCatalog.Launch(entry);
        Console.WriteLine(process == null
            ? "Launch: Shell activation handed off"
            : $"Launch: requested (PID {process.Id})");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{args[0][2..]} failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
