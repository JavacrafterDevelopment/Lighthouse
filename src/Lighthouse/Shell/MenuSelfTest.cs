using Lighthouse.Shell;

namespace Lighthouse;

/// <summary>
/// Diagnostic for the shell context menu reader: run
/// `Lighthouse.exe --menu-selftest "C:\some\file.zip"` to print the menu Explorer
/// would show for that path, including anything third party extensions add.
/// Handy for checking that an installed archiver actually contributes its submenu.
/// </summary>
internal static class MenuSelfTest
{
    public static void Run(string? path, bool extended, string? jsonOut = null)
    {
        path = string.IsNullOrWhiteSpace(path) ? FindSampleArchive() : path;

        if (path is null || !File.Exists(path) && !Directory.Exists(path))
        {
            Console.WriteLine("No file to test. Pass one: --menu-selftest \"C:\\path\\to\\file.zip\"");
            return;
        }

        Console.WriteLine($"path     : {path}");
        Console.WriteLine($"extended : {extended}");
        Console.WriteLine();

        using var menu = new ShellContextMenu();
        // Some shell extensions dereference the owner window, so give them a real one.
        var items = menu.Build(path, Native.NativeMethods.GetDesktopWindow(), extended,
                               s => Console.WriteLine("  trace: " + s));
        Console.WriteLine();

        if (items.Count == 0)
        {
            Console.WriteLine("(no items returned - the shell gave us nothing)");
            return;
        }

        Print(items, 0);
        Console.WriteLine();
        Console.WriteLine($"{Count(items)} entries total");

        if (jsonOut is not null)
        {
            using var stream = File.Create(jsonOut);
            using var w = new System.Text.Json.Utf8JsonWriter(stream,
                new System.Text.Json.JsonWriterOptions { Indented = true });
            w.WriteStartArray();
            ShellMenuJson.Write(w, items);
            w.WriteEndArray();
            w.Flush();
            Console.WriteLine($"json written to {jsonOut}");
        }
    }

    private static void Print(List<ShellMenuItem> items, int depth)
    {
        string pad = new(' ', depth * 4);
        foreach (var item in items)
        {
            if (item.Separator)
            {
                Console.WriteLine($"{pad}  ---");
                continue;
            }

            var marks = new List<string>();
            if (item.IsDefault) marks.Add("default");
            if (!item.Enabled) marks.Add("disabled");
            if (item.Checked) marks.Add("checked");
            if (item.Icon is not null) marks.Add("icon");

            string suffix = marks.Count > 0 ? $"   [{string.Join(", ", marks)}]" : "";
            string id = item.Children is null ? $"#{item.Id,-3}" : "sub ";

            Console.WriteLine($"{pad}  {id} {item.Label}{suffix}");
            if (item.Children is not null) Print(item.Children, depth + 1);
        }
    }

    private static int Count(List<ShellMenuItem> items)
    {
        int n = 0;
        foreach (var i in items)
        {
            if (!i.Separator) n++;
            if (i.Children is not null) n += Count(i.Children);
        }
        return n;
    }

    /// <summary>Looks for any .zip lying around so the test has something realistic to use.</summary>
    private static string? FindSampleArchive()
    {
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                var hit = Directory.EnumerateFiles(folder, "*.zip", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch { }
        }
        return null;
    }
}
