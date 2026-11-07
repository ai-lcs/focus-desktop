using System.IO;
using System.Text;
using System.Text.Json;
using focus_desktop.Services;

namespace focus_desktop.Tests;

/// <summary>Pure external-app identity and compatibility checks; does not access the desktop.</summary>
public static class ExternalAppIdentitySelfTest
{
    public static int Run(Action<string, bool> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var failures = 0;
        void Verify(string name, bool condition)
        {
            if (!condition) failures++;
            check(name, condition);
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"focus-desk-entry-identity-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var executablePath = Path.Combine(temporaryDirectory, "Editor.exe");
            var projectA = NewExecutableEntry(executablePath, "--project A", temporaryDirectory, 0);
            var projectB = NewExecutableEntry(executablePath, "--project B", Path.Combine(temporaryDirectory, "ProjectB"), 1);
            Verify("stable entry IDs still include launch arguments and working directory",
                !string.Equals(projectA.Id, projectB.Id, StringComparison.OrdinalIgnoreCase));
            Verify("normalized executable paths share one runtime identity",
                projectA.HasSameWindowMatchIdentity(NewExecutableEntry(
                    Path.Combine(temporaryDirectory, "nested", "..", "editor.exe").ToUpperInvariant(),
                    "--different-project", temporaryDirectory, 2)));

            var packagedA = NewPackagedEntry("Vendor.Editor_123!Main", "--project A", 0);
            var packagedB = NewPackagedEntry("vendor.editor_123!main", "--project B", 1);
            Verify("AUMIDs share one runtime identity without comparing launch arguments",
                packagedA.HasSameWindowMatchIdentity(packagedB));

            var sameNameDifferentExecutable = NewExecutableEntry(
                Path.Combine(temporaryDirectory, "OtherEditor.exe"), "--project A", temporaryDirectory, 2);
            Verify("equal display names do not make different executables conflict",
                !projectA.HasSameWindowMatchIdentity(sameNameDifferentExecutable));

            var scanned = new Dictionary<string, ExternalAppEntry>(StringComparer.OrdinalIgnoreCase);
            Verify("desktop scan keeps the first launch definition",
                ExternalAppCatalog.TryAddScannedEntry(scanned, projectA, out var firstScanConflict)
                && firstScanConflict == null);
            Verify("desktop scan keeps and explains a different launch definition",
                ExternalAppCatalog.TryAddScannedEntry(scanned, projectB, out var scanConflict)
                && scanned.Count == 2
                && scanConflict?.Contains("参数或工作目录不同", StringComparison.Ordinal) == true);
            var browseConflict = ExternalAppEntry.FindWindowMatchIdentityConflict(new[] { projectA }, projectB);
            Verify("browse identity conflict explains why definitions cannot both be selected",
                browseConflict == projectA
                && ExternalAppEntry.DescribeWindowMatchIdentityConflict(browseConflict, projectB)
                    .Contains("参数或工作目录不同", StringComparison.Ordinal));

            var conflictingSettings = new AppSettings
            {
                ExternalAppsConfirmed = true,
                ExternalApps = new List<ExternalAppEntry> { projectA, projectB },
            };

            Verify("new settings reject two launch definitions for one executable identity",
                !conflictingSettings.TryValidateExternalApps(out var validationError)
                && validationError?.Contains("同一应用", StringComparison.Ordinal) == true);

            var separateApps = new AppSettings
            {
                ExternalAppsConfirmed = true,
                ExternalApps = new List<ExternalAppEntry> { projectA, sameNameDifferentExecutable },
            };
            Verify("same-name applications with different executable paths remain valid",
                separateApps.TryValidateExternalApps(out _));

            var configPath = Path.Combine(temporaryDirectory, "legacy-config.json");
            var legacyJson = JsonSerializer.Serialize(conflictingSettings);
            File.WriteAllBytes(configPath, Encoding.UTF8.GetBytes(legacyJson));
            var originalBytes = File.ReadAllBytes(configPath);
            var loaded = AppSettings.LoadFromPath(configPath, out var loadStatus);
            Verify("legacy identity conflicts have a dedicated load state",
                loadStatus.ToString() == "LegacyIdentityConflict");
            Verify("legacy conflict entries remain intact in memory",
                loaded.ExternalApps?.Count == 2
                && loaded.ExternalApps[0].Id == projectA.Id
                && loaded.ExternalApps[1].Id == projectB.Id);
            Verify("loading a legacy conflict does not rewrite its configuration bytes",
                File.ReadAllBytes(configPath).SequenceEqual(originalBytes));

            var saveRejected = false;
            try { conflictingSettings.SaveToPath(configPath); }
            catch (InvalidDataException) { saveRejected = true; }
            Verify("saving a conflicting list is rejected", saveRejected);
            Verify("rejected save preserves the existing configuration bytes",
                File.ReadAllBytes(configPath).SequenceEqual(originalBytes));
        }
        catch (Exception ex)
        {
            Verify($"identity self-test completes ({ex.GetType().Name}: {ex.Message})", false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                Verify($"identity self-test temporary data cleanup ({ex.GetType().Name})", false);
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static ExternalAppEntry NewExecutableEntry(string executablePath, string arguments, string workingDirectory, int order) => new()
    {
        Id = ExternalAppEntry.CreateWin32StableId(executablePath, arguments, workingDirectory),
        DisplayName = "Editor",
        Kind = ExternalAppEntry.ExecutableKind,
        ExecutablePath = executablePath,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        Order = order,
    };

    private static ExternalAppEntry NewPackagedEntry(string appUserModelId, string arguments, int order) => new()
    {
        Id = ExternalAppEntry.CreatePackagedStableId(appUserModelId, arguments, null),
        DisplayName = "Packaged Editor",
        Kind = ExternalAppEntry.PackagedAppKind,
        AppUserModelId = appUserModelId,
        Arguments = arguments,
        Order = order,
    };
}
