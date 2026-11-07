using System.Security.Principal;
using focus_desktop.Services;

var owner = new SecurityIdentifier("S-1-5-21-1001");
var other = new SecurityIdentifier("S-1-5-21-2002");
var localAppData = @"C:\Users\TestUser\AppData\Local";
var dataDir = Path.Combine(localAppData, "focus-desktop-dock");

var cases = new[]
{
    new PolicyCase("same interactive account and directory owner", owner, owner, owner, true, false, false, true),
    new PolicyCase("legacy data directory is never authorized", owner, owner, owner, true, false, false, false, DataDir: Path.Combine(localAppData, "focus-desktop")),
    new PolicyCase("unknown session identity", owner, null, owner, true, false, false, false),
    new PolicyCase("alternate administrator credentials", other, owner, owner, true, false, false, false),
    new PolicyCase("different profile owner", owner, owner, other, true, false, false, false),
    new PolicyCase("different profile path", owner, owner, owner, true, false, false, false,
        DataDir: @"C:\Users\Other\AppData\Local\focus-desktop-dock"),
    new PolicyCase("profile root is a reparse point", owner, owner, owner, true, true, false, false),
    new PolicyCase("portable marker exists", owner, owner, owner, true, false, true, false),
    new PolicyCase("missing profile still requires matching identity", owner, other, null, false, false, false, false),
    new PolicyCase("missing profile with proven identity has no data to remove", owner, owner, null, false, false, false, true)
};

foreach (var test in cases)
{
    var path = test.DataDir ?? Path.Combine(test.LocalAppData ?? localAppData, "focus-desktop-dock");
    var actual = UninstallDataPolicy.IsIdentityAuthorized(
        path,
        test.LocalAppData ?? localAppData,
        test.ProcessSid,
        test.SessionSid,
        test.OwnerSid,
        test.DirectoryExists,
        test.IsReparsePoint,
        test.HasPortableFlag,
        out var reason);

    if (actual != test.Expected)
        throw new InvalidOperationException($"{test.Name}: expected {test.Expected}, got {actual}; {reason}");

    Console.WriteLine($"PASS {test.Name}: {actual}");
}

Console.WriteLine($"Uninstall data policy: {cases.Length} cases passed; no product or filesystem profile was opened.");

internal sealed record PolicyCase(
    string Name,
    SecurityIdentifier? ProcessSid,
    SecurityIdentifier? SessionSid,
    SecurityIdentifier? OwnerSid,
    bool DirectoryExists,
    bool IsReparsePoint,
    bool HasPortableFlag,
    bool Expected,
    string? LocalAppData = null,
    string? DataDir = null);
