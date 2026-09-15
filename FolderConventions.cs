using System.Text;

namespace IMPWeldPhotos;

public enum FolderAction
{
    Created,
    Existed,
    Renamed,
    Failed,
}

/// <summary>
/// Folder rules copied from GENERATE_FOLDERS
/// (Autodesk_ext\Plant3D\General\CreateProjectStructure.vb), which builds
/// 130_Izometrije. 140_Zvari mirrors that tree, so the two must stay in step:
/// change a rule there, change it here.
/// </summary>
public static class FolderConventions
{
    public const string DesktopIni = "desktop.ini";

    /// <summary>Maps folder code to folder path for every immediate subdirectory
    /// carrying a desktop.ini code.
    ///
    /// A folder is identified by the code stamped inside it, never by its name, so
    /// a project renamed in the database renames its folder instead of growing a
    /// second one beside it.
    ///
    /// Duplicates are reported rather than silently resolved: two folders claiming
    /// one code is something only a human can untangle.</summary>
    public static Dictionary<string, string> IndexFoldersByCode(string parent, string label, ICollection<string>? warnings)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in Directory.GetDirectories(parent))
            {
                var code = ReadFolderCode(Path.Combine(d, DesktopIni));
                if (code == "") continue;
                if (map.TryGetValue(code, out var seen))
                {
                    warnings?.Add($"Podvojena koda {label} {code}: '{Path.GetFileName(seen)}' in " +
                                  $"'{Path.GetFileName(d)}'. Uporabljena je prva, mapi združite ročno.");
                    continue;
                }
                map[code] = d;
            }
        }
        catch
        {
            // Parent may not exist yet on the very first run.
        }
        return map;
    }

    /// <summary>Creates, renames or leaves alone the folder for one code, so that it
    /// ends up named desiredName. resultPath is the folder to use afterwards (null
    /// only when creating failed); detail carries the message for renames and
    /// failures.</summary>
    public static FolderAction EnsureFolder(string parent, string code, string desiredName,
                                            Dictionary<string, string> index,
                                            out string? resultPath, out string? detail)
    {
        resultPath = null;
        detail = null;
        var desired = Path.Combine(parent, SanitizeFolderName(desiredName));

        if (!index.TryGetValue(code ?? "", out var existing))
        {
            // No coded folder for this one. A folder already sitting at the desired
            // name predates the desktop.ini scheme (or was made by hand): adopt it
            // rather than failing to create over the top of it.
            if (Directory.Exists(desired))
            {
                resultPath = desired;
                index[code ?? ""] = desired;
                return FolderAction.Existed;
            }
            try
            {
                Directory.CreateDirectory(desired);
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return FolderAction.Failed;
            }
            resultPath = desired;
            index[code ?? ""] = desired;
            return FolderAction.Created;
        }

        if (string.Equals(existing, desired, StringComparison.Ordinal))
        {
            resultPath = existing;
            return FolderAction.Existed;
        }

        if (!TryRenameFolder(existing, desired, out var why))
        {
            // Keep working in the folder that actually exists; the photos are there
            // and a failed rename must not send anything to a new one.
            resultPath = existing;
            detail = $"'{Path.GetFileName(existing)}' -> '{Path.GetFileName(desired)}': {why}";
            return FolderAction.Failed;
        }

        detail = $"'{Path.GetFileName(existing)}' -> '{Path.GetFileName(desired)}'";
        resultPath = desired;
        index[code ?? ""] = desired;
        return FolderAction.Renamed;
    }

    /// <summary>Renames a folder, refusing to overwrite anything. Case-only changes
    /// go via a temporary name: Windows treats the two paths as equal, so a direct
    /// move is rejected.</summary>
    public static bool TryRenameFolder(string source, string target, out string? detail)
    {
        detail = null;
        try
        {
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                var temp = target + "~" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(source, temp);
                Directory.Move(temp, target);
                return true;
            }

            if (Directory.Exists(target))
            {
                detail = "druga mapa že ima to ime";
                return false;
            }
            Directory.Move(source, target);
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>Reads the [FolderCode] Code= line from a desktop.ini, "" if missing.</summary>
    public static string ReadFolderCode(string iniPath)
    {
        if (!File.Exists(iniPath)) return "";
        try
        {
            foreach (var line in File.ReadAllLines(iniPath))
            {
                var t = line.Trim();
                if (t.StartsWith("Code=", StringComparison.OrdinalIgnoreCase))
                    return t["Code=".Length..].Trim();
            }
        }
        catch
        {
        }
        return "";
    }

    public static void WriteDesktopIni(string folder, string code)
    {
        var iniPath = Path.Combine(folder, DesktopIni);
        if (File.Exists(iniPath)) File.SetAttributes(iniPath, FileAttributes.Normal);

        // Three sections, each does something different:
        //   [.ShellClassInfo]  -> InfoTip on hover
        //   [FolderCode]       -> the code folders are found by
        //   [{F29F85E0-...}]   -> SummaryInformation Prop2 = Title, so the code shows
        //                         in Explorer's Title column. 31 = VT_LPWSTR.
        var sb = new StringBuilder();
        sb.AppendLine("[.ShellClassInfo]");
        sb.AppendLine($"InfoTip=Code: {code}");
        sb.AppendLine();
        sb.AppendLine("[FolderCode]");
        sb.AppendLine($"Code={code}");
        sb.AppendLine();
        sb.AppendLine("[{F29F85E0-4FF9-1068-AB91-08002B27B3D9}]");
        sb.AppendLine($"Prop2=31,{code}");
        File.WriteAllText(iniPath, sb.ToString(), new UTF8Encoding(false));

        File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
        var di = new DirectoryInfo(folder);
        di.Attributes |= FileAttributes.System;
    }

    // Windows reserved device names: CreateDirectory throws on these.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Formats a 10-digit project code as 1-1-2-2-4 ("3640004501" ->
    /// "3-6-40-00-4501"). Returns the code unchanged for any other length.</summary>
    public static string FormatProjectCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code ?? "";
        var c = code.Trim();
        if (c.Length != 10) return c;
        return $"{c[0]}-{c[1]}-{c.Substring(2, 2)}-{c.Substring(4, 2)}-{c.Substring(6, 4)}";
    }

    /// <summary>The canonical project folder name: "{formatted code} - {name}".</summary>
    public static string BuildProjectFolderName(string? code, string? name)
    {
        var formattedCode = FormatProjectCode(code);
        var hasCode = !string.IsNullOrWhiteSpace(formattedCode);
        var hasName = !string.IsNullOrWhiteSpace(name);
        if (hasCode && hasName) return $"{formattedCode} - {name}";
        if (hasCode) return formattedCode;
        return hasName ? name! : "";
    }

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var cleaned = sb.ToString().TrimEnd(' ', '.');
        if (Reserved.Contains(cleaned)) cleaned = "_" + cleaned;
        return cleaned;
    }
}
