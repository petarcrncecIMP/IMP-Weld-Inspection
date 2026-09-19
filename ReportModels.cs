using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace IMPWeldPhotos;

/// <summary>One report folder under {project}\Poročila: the document, its PDF and the
/// stamped photos packed with it.</summary>
public sealed class ReportEntry
{
    private static readonly Regex NameRx = new(@"^(?<number>.+?) - (?<unit>.+)$", RegexOptions.CultureInvariant);

    public required string Folder { get; init; }
    public required string FolderName { get; init; }
    public required string Number { get; init; }
    public required string UnitName { get; init; }
    public required string DocumentPath { get; init; }
    public required string? PdfPath { get; init; }
    public required DateTime Created { get; init; }
    public required int Photos { get; init; }
    public required DateTime DocumentModified { get; init; }
    public required DateTime? PdfModified { get; init; }

    public bool HasPdf => PdfPath != null;

    /// <summary>The document was changed in Word after its PDF was made, so the PDF no longer
    /// shows what the report says. Two seconds of slack for file-system timestamps.</summary>
    public bool IsPdfStale => PdfModified is { } pdf && DocumentModified > pdf.AddSeconds(2);

    /// <summary>No PDF yet, or one that is out of date.</summary>
    public bool NeedsPdf => PdfPath == null || IsPdfStale;

    public string Summary =>
        $"{UnitName} · {MainWindow.Plural(Photos, "fotografija", "fotografiji", "fotografije", "fotografij")} · {Created:dd.MM.yyyy}";

    private static int CountImages(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count(f => MediaFiles.IsImage(f));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static ReportEntry? Create(string folder)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // ~$… is Word's lock file for a document someone has open.
        var document = files.FirstOrDefault(f => f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) &&
                                                 !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal));
        if (document == null) return null;

        var name = Path.GetFileName(folder);
        var m = NameRx.Match(name);
        var pdf = files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        return new ReportEntry
        {
            Folder = folder,
            FolderName = name,
            Number = m.Success ? m.Groups["number"].Value : name,
            UnitName = m.Success ? m.Groups["unit"].Value : "",
            DocumentPath = document,
            PdfPath = pdf,
            // The folder's birth, not the document's last edit, so editing doesn't reorder the list.
            Created = Directory.GetCreationTime(folder),
            DocumentModified = File.GetLastWriteTime(document),
            PdfModified = pdf == null ? null : File.GetLastWriteTime(pdf),
            // The stamped photos sit in a subfolder per isometrija (older reports: at the top).
            Photos = CountImages(folder),
        };
    }
}

public static class ReportIndex
{
    /// <summary>Deletes a whole report folder. Only ever holds a report and its stamped
    /// copies: the photos it was made from stay on the share, so it can be made again.</summary>
    public static async Task DeleteAsync(string folder, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleting will report it properly below.
            }
        }
        // A PDF just closed in the viewer, or a document Word has finished with, can be held
        // for a moment longer.
        await ShareRetry.RunAsync(() => Directory.Delete(folder, true), ct);
    }

    /// <summary>Every report of one project, newest first. Predloga.docx sits in the
    /// Poročila folder itself, not in a report folder, so it is never listed.</summary>
    public static List<ReportEntry> Load(string projectFolder)
    {
        var root = Path.Combine(projectFolder, ReportBuilder.ReportsFolderName);
        var list = new List<ReportEntry>();
        try
        {
            if (!Directory.Exists(root)) return list;
            foreach (var folder in Directory.GetDirectories(root))
            {
                if (ReportEntry.Create(folder) is { } entry) list.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return list;
        }
        return list.OrderByDescending(r => r.Created).ToList();
    }
}

/// <summary>Saves a report as PDF with Word itself, so the PDF looks exactly like the
/// document does in Word, fields and all.</summary>
public static class WordExport
{
    private const int WdExportFormatPdf = 17;

    public sealed record PdfResult(string Document, string? Pdf, string? Error);

    public static async Task<string> ToPdfAsync(string documentPath)
    {
        var result = (await ToPdfManyAsync(new[] { documentPath }, null))[0];
        return result.Pdf ?? throw new IOException(result.Error);
    }

    /// <summary>Saves each document as a PDF beside it, all through one Word, reporting the
    /// index of the one being worked on. One failure doesn't stop the rest.</summary>
    public static Task<List<PdfResult>> ToPdfManyAsync(IReadOnlyList<string> documentPaths, IProgress<int>? progress) =>
        RunWorker(() => ExportAll(documentPaths, progress));

    private static List<PdfResult> ExportAll(IReadOnlyList<string> documentPaths, IProgress<int>? progress)
    {
        var type = Type.GetTypeFromProgID("Word.Application")
                   ?? throw new IOException("Microsoft Word ni nameščen, zato PDF-ja ni mogoče ustvariti.");
        var results = new List<PdfResult>();
        object? word = null;
        object? documents = null;
        try
        {
            word = Activator.CreateInstance(type) ?? throw new IOException("Worda ni mogoče zagnati.");
            Set(word, "Visible", false);
            Set(word, "DisplayAlerts", 0);
            documents = Get(word, "Documents");

            for (var i = 0; i < documentPaths.Count; i++)
            {
                progress?.Report(i);
                // Word wants a plain Windows path: given one with forward slashes it opens
                // nothing and says nothing.
                var documentPath = Path.GetFullPath(documentPaths[i]);
                var pdfPath = Path.ChangeExtension(documentPath, ".pdf");
                object? document = null;
                try
                {
                    document = OpenDocument(word, documents, documentPath);
                    Call(document, "ExportAsFixedFormat", pdfPath, WdExportFormatPdf);
                    results.Add(new PdfResult(documentPaths[i], pdfPath, null));
                }
                catch (Exception ex)
                {
                    results.Add(new PdfResult(documentPaths[i], null, (ex.InnerException ?? ex).Message));
                }
                finally
                {
                    Try(() => Call(document, "Close", 0));
                    Release(document);
                }
            }
            return results;
        }
        finally
        {
            Try(() => Call(word, "Quit"));
            Release(documents);
            Release(word);
        }
    }

    /// <summary>
    /// Opens the document for the export. Two things get in the way:
    /// a file written moments ago can still be held by antivirus or the indexer, and a file
    /// from a network share (U:) opens in Protected View, where Open hands back nothing at
    /// all. Protected View's window can be turned into a real document with Edit(), and the
    /// transient locks clear within a second or two, so both are simply retried.
    /// </summary>
    private static object OpenDocument(object? word, object? documents, string documentPath)
    {
        int[] delays = { 300, 700, 1500 };
        Exception? last = null;
        for (var attempt = 0; ; attempt++)
        {
            object? document = null;
            try
            {
                // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles.
                document = Call(documents, "Open", documentPath, false, false, false)
                           ?? FromProtectedView(word, documentPath);
            }
            catch (Exception ex) when (attempt < delays.Length)
            {
                last = ex.InnerException ?? ex; // busy or briefly locked: try again below
            }
            if (document != null) return document;
            if (attempt >= delays.Length)
                throw new IOException($"Word ni mogel odpreti {Path.GetFileName(documentPath)}" +
                                      (last == null ? " (brez sporočila)." : $": {last.Message}"), last);
            Thread.Sleep(delays[attempt]);
        }
    }

    /// <summary>The document Word put in Protected View, made editable so it can be exported.</summary>
    private static object? FromProtectedView(object? word, string documentPath)
    {
        var windows = Get(word, "ProtectedViewWindows");
        if (windows == null) return null;
        var count = Convert.ToInt32(Get(windows, "Count") ?? 0);
        for (var i = 1; i <= count; i++)
        {
            var window = Call(windows, "Item", i);
            var document = Get(window, "Document");
            var name = document == null ? null : Get(document, "FullName") as string;
            if (name != null && !string.Equals(name, documentPath, StringComparison.OrdinalIgnoreCase)) continue;
            return Call(window, "Edit");
        }
        return null;
    }

    // Late binding by hand: Word is driven through IDispatch, without an Office interop
    // assembly and without the dynamic binder, which refuses these calls.
    private static object? Get(object? target, string name) =>
        Invoke(target, name, System.Reflection.BindingFlags.GetProperty, Array.Empty<object>());

    private static void Set(object? target, string name, object value) =>
        Invoke(target, name, System.Reflection.BindingFlags.SetProperty, new[] { value });

    private static object? Call(object? target, string name, params object[] args) =>
        Invoke(target, name, System.Reflection.BindingFlags.InvokeMethod, args);

    private static object? Invoke(object? target, string name, System.Reflection.BindingFlags how, object[] args)
    {
        if (target == null) throw new IOException($"Word ni odgovoril na {name}.");
        return target.GetType().InvokeMember(name, how, null, target, args);
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Closing up: Word is going away anyway.
        }
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    /// <summary>Word runs in its own process and is driven from a plain worker thread (MTA),
    /// which is what PowerShell does too. A UI thread would be an STA without a message pump,
    /// and Word's answers get lost there. Office late binding also wants an en-US thread.</summary>
    private static Task<T> RunWorker<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return tcs.Task;
    }
}
