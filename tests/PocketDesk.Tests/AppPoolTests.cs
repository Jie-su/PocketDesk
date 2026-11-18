using System.IO;
using PocketDesk.Core;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestAppPool()
    {
        Test("App pool accepts full executable paths, never URIs, shell commands or network paths", () =>
        {
            foreach (var invalid in new[] { @"program.exe", @"D:program.exe", @"\\server\share\one.exe", @"\\?\C:\one.exe",
                @"https://example.invalid/tool.exe", @"C:\Data\my.exe --open folder", @"C:\Data\my.exe:extra.exe", @"C:\Data\my.lnk" })
                Require(AppPoolStore.LocalExe(invalid) is null, "Rejected path was accepted: " + invalid);
            Require(AppPoolStore.LocalExe(@"C:\Apps\Group\..\工具 1.EXE") == @"C:\Apps\工具 1.EXE", "Local spaced path was not canonicalized.");
        });

        Test("Pool pins survive restart; missing or removed executables cannot launch; empty pool stays empty", () =>
        {
            var folder = Directory.CreateTempSubdirectory("PocketDeskPool-");
            var settings = Path.Combine(folder.FullName, "pins.json");
            var exe = Path.Combine(folder.FullName, "App with spaces.exe");
            try
            {
                File.WriteAllText(exe, "test placeholder — never run");
                var store = new AppPoolStore(settings);
                foreach (var seed in store.Read()) store.Remove(seed.ExecutablePath);
                store.Add(new("Demo", exe));
                store.Add(new("Duplicate", exe.ToUpperInvariant()));
                var reopened = new AppPoolStore(settings);
                Require(reopened.Read() is [var item] && item.Name == "Demo", "Pin did not persist or duplicated.");
                var info = reopened.PrepareLaunch(exe);
                Require(info.FileName == exe && !info.UseShellExecute && info.Arguments == "" && info.WorkingDirectory == folder.FullName,
                    "Launch was not the exact executable without arguments.");
                File.Delete(exe);
                try { reopened.PrepareLaunch(exe); throw new Exception("Missing program allowed."); } catch (FileNotFoundException) { }
                reopened.Remove(exe);
                Require(new AppPoolStore(settings).Read().Count == 0, "Empty pool unexpectedly reseeded.");
                File.WriteAllText(exe, "");
                try { reopened.PrepareLaunch(exe); throw new Exception("Removed pin allowed."); } catch (InvalidOperationException) { }
            }
            finally
            {
                File.Delete(exe);
                File.Delete(settings);
                folder.Delete();
            }
        });
    }
}
