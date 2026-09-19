using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace IMPWeldPhotos;

/// <summary>One report: every photo of one skid (unit), grouped by isometrija.</summary>
public sealed record ReportRequest(string ProjectFolder, string ProjectName, string UnitName, IReadOnlyList<IsoEntry> Isos);

public sealed record ReportResult(string DocumentPath, string Folder, int Photos, int Isometrije, IReadOnlyList<string> Problems);

/// <summary>
/// Fills a Word template and packs the stamped photos beside it:
/// {project}\Poročila\{number} - {skid}\{number}.docx plus one stamped photo per file.
///
/// The template is an ordinary .docx that anyone can edit in Word; the app only replaces
/// these tokens, so everything else stays exactly as the template has it:
///   {{PorociloSt}}      report number          {{Projekt}}   project folder name
///   {{Sklop}}           skid (unit) name       {{Datum}}     today, dd.MM.yyyy
///   {{SteviloZvarov}}   number of welds
///   {{Isometrija}}      BOM code — its whole paragraph is repeated once per isometrija
///   {{IsometrijaNaziv}} "{BomCode} - {name}", same repetition
///   {{Fotografije}}     replaced by that isometrija's photos, two per row
/// A {{Fotografije}} paragraph right below an {{Isometrija}} paragraph belongs to it; on its
/// own it gets every photo of the skid.
/// </summary>
public static class ReportBuilder
{
    public const string ReportsFolderName = "Poročila";
    public const string TemplatesFolderName = "_Predloge";
    public const string ProjectTemplateName = "Predloga.docx";
    public const string DefaultTemplateName = "Weld Inspection Report.docx";

    /// <summary>Photo box in the annex, as in the sample report: 6.5 x 4.9 cm (EMU).</summary>
    private const long BoxWidth = 2340000;
    private const long BoxHeight = 1764000;

    /// <summary>Text width of an A4 page with the template's margins, in twentieths of a point.</summary>
    private const int TableWidth = 9072;

    private sealed record StampedPhoto(string Name, string Path, string Extension, int Width, int Height, string WeldLabel);

    /// <summary>The project's own template, else the shared one, else null.</summary>
    public static string? FindTemplate(string destinationRoot, string projectFolder)
    {
        var own = Path.Combine(projectFolder, ReportsFolderName, ProjectTemplateName);
        if (File.Exists(own)) return own;
        var shared = Path.Combine(destinationRoot, TemplatesFolderName, DefaultTemplateName);
        return File.Exists(shared) ? shared : null;
    }

    public static string TemplateHint(string destinationRoot, string projectFolder) =>
        $"Predlogo postavite v {Path.Combine(projectFolder, ReportsFolderName, ProjectTemplateName)} " +
        $"(za ta projekt) ali v {Path.Combine(destinationRoot, TemplatesFolderName, DefaultTemplateName)} (za vse projekte).";

    /// <summary>The last number with its final digits raised by one; a new year starts at 1.</summary>
    public static string SuggestNumber(string? last, DateTime today)
    {
        if (!string.IsNullOrWhiteSpace(last))
        {
            var m = Regex.Match(last.Trim(), @"^(?<head>.*?)(?<year>(19|20)\d{2})?(?<sep>\D*)(?<n>\d+)$");
            if (m.Success)
            {
                var digits = m.Groups["n"].Value;
                if (m.Groups["year"].Success && int.Parse(m.Groups["year"].Value) != today.Year)
                    return m.Groups["head"].Value + today.Year + m.Groups["sep"].Value + 1.ToString(new string('0', digits.Length));
                var next = long.Parse(digits) + 1;
                return m.Groups["head"].Value + m.Groups["year"].Value + m.Groups["sep"].Value +
                       next.ToString(new string('0', digits.Length));
            }
        }
        return $"VT {today:yyyy}-001";
    }

    /// <summary>The name in three parts, as the inspectors write it:
    /// "Weld Inspection Report" · the skid · the report number.</summary>
    public const string DocumentTitle = "Weld Inspection Report";

    public static string DocumentName(string skidLabel, string number) =>
        FolderConventions.SanitizeFolderName($"{DocumentTitle} {skidLabel} - {number}") + ".docx";

    /// <summary>A short label for the skid, for the file and folder name: the unit without the
    /// word that only says it is a skid ("HCL skid" → "HCL"). Anything else is kept as it is,
    /// and the dialog lets it be corrected.</summary>
    public static string ShortSkid(string unitName)
    {
        var words = unitName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        string[] generic = { "skid", "station", "postaja", "unit", "sklop", "system", "sistem" };
        while (words.Count > 1 && generic.Contains(words[^1], StringComparer.OrdinalIgnoreCase)) words.RemoveAt(words.Count - 1);
        return words.Count == 0 ? unitName : string.Join(' ', words);
    }

    public static string FolderFor(ReportRequest request, string number, string skidLabel) =>
        Path.Combine(request.ProjectFolder, ReportsFolderName,
                     FolderConventions.SanitizeFolderName($"{number} - {skidLabel}"));

    /// <summary>Stamps the skid's photos into a new report folder and fills the template.</summary>
    public static async Task<ReportResult> CreateAsync(string templatePath, ReportRequest request, string number,
                                                       string skidLabel, DateTime date, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(skidLabel)) skidLabel = ShortSkid(request.UnitName);
        var folder = FolderFor(request, number, skidLabel);
        if (Directory.Exists(folder))
            throw new IOException($"mapa {folder} že obstaja; izberite drugo številko ali mapo najprej izbrišite");
        Directory.CreateDirectory(folder);

        var problems = new List<string>();
        var groups = new List<(IsoEntry Iso, List<StampedPhoto> Photos)>();
        foreach (var iso in request.Isos.OrderBy(i => i.BomCode, StringComparer.Ordinal))
        {
            var photos = new List<StampedPhoto>();
            foreach (var item in PhotoItem.List(iso.Path).Where(p => !p.IsVideo))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    photos.Add(await StampAsync(item, folder, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"{item.Name}: {ex.Message}");
                }
            }
            if (photos.Count > 0) groups.Add((iso, photos));
        }

        var documentPath = Path.Combine(folder, DocumentName(skidLabel, number));
        File.Copy(templatePath, documentPath);
        var welds = groups.Sum(g => g.Photos.Select(p => p.WeldLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Fill(documentPath, request, number, date, groups, welds);

        return new ReportResult(documentPath, folder, groups.Sum(g => g.Photos.Count), groups.Count, problems);
    }

    private static async Task<StampedPhoto> StampAsync(PhotoItem item, string folder, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(item.Path, ct);
        DateTime? taken = null;
        try
        {
            taken = MediaFiles.ReadDateTimeOriginal(item.Path);
        }
        catch
        {
            // Only the date in the output's metadata; not worth failing the photo over.
        }
        var ext = Path.GetExtension(item.Name).ToLowerInvariant();
        var stamped = PhotoStamper.Stamp(bytes, Path.GetFileNameWithoutExtension(item.Name), ext, taken);
        var path = Path.Combine(folder, item.Name);
        await File.WriteAllBytesAsync(path, stamped.Bytes, ct);
        return new StampedPhoto(item.Name, path, ext, stamped.Width, stamped.Height, item.WeldLabel);
    }

    // ─── The document ────────────────────────────────────────────────────────

    private static void Fill(string path, ReportRequest request, string number, DateTime date,
                             List<(IsoEntry Iso, List<StampedPhoto> Photos)> groups, int welds)
    {
        using var doc = WordprocessingDocument.Open(path, true);
        var main = doc.MainDocumentPart ?? throw new IOException("predloga ni veljaven dokument Word");
        var body = main.Document.Body ?? throw new IOException("predloga nima vsebine");

        ExpandIsometrije(main, body, groups);

        var values = new Dictionary<string, string>
        {
            ["{{PorociloSt}}"] = number,
            ["{{Projekt}}"] = request.ProjectName,
            ["{{Sklop}}"] = request.UnitName,
            ["{{SteviloZvarov}}"] = welds.ToString(),
            ["{{Datum}}"] = date.ToString("dd.MM.yyyy"),
        };
        ReplaceTokens(body, values);
        foreach (var header in main.HeaderParts) ReplaceTokens(header.Header, values);
        foreach (var footer in main.FooterParts) ReplaceTokens(footer.Footer, values);
        main.Document.Save();
    }

    /// <summary>Repeats the {{Isometrija}} paragraph (and the photos below it) per isometrija.</summary>
    private static void ExpandIsometrije(MainDocumentPart main, W.Body body,
                                         List<(IsoEntry Iso, List<StampedPhoto> Photos)> groups)
    {
        var id = 5000U;
        foreach (var template in body.Descendants<W.Paragraph>()
                     .Where(p => TextOf(p).Contains("{{Isometrija}}") || TextOf(p).Contains("{{IsometrijaNaziv}}"))
                     .ToList())
        {
            var photoMarker = template.NextSibling() is W.Paragraph next && TextOf(next).Contains("{{Fotografije}}")
                ? next
                : null;

            foreach (var (iso, photos) in groups)
            {
                var line = (W.Paragraph)template.CloneNode(true);
                SetText(line, TextOf(line)
                    .Replace("{{IsometrijaNaziv}}", iso.Title)
                    .Replace("{{Isometrija}}", iso.BomCode));
                if (photoMarker != null) KeepWithPhotos(line);
                template.InsertBeforeSelf(line);
                if (photoMarker != null) template.InsertBeforeSelf(PhotoTable(main, photos, CaptionStyle(photoMarker), ref id));
            }
            template.Remove();
            photoMarker?.Remove();
        }

        // A {{Fotografije}} of its own gets every photo of the skid.
        foreach (var marker in body.Descendants<W.Paragraph>().Where(p => TextOf(p).Contains("{{Fotografije}}")).ToList())
        {
            marker.InsertBeforeSelf(PhotoTable(main, groups.SelectMany(g => g.Photos).ToList(), CaptionStyle(marker), ref id));
            marker.Remove();
        }
    }

    /// <summary>Keeps an isometrija's line on the same page as the photos under it.</summary>
    private static void KeepWithPhotos(W.Paragraph line)
    {
        var props = line.GetFirstChild<W.ParagraphProperties>();
        if (props == null)
        {
            props = new W.ParagraphProperties();
            line.PrependChild(props);
        }
        if (props.KeepNext == null) props.PrependChild(new W.KeepNext());
    }

    /// <summary>Captions are written in the {{Fotografije}} placeholder's own formatting, so
    /// the template decides the font; Arial 10 when it says nothing.</summary>
    private static W.RunProperties CaptionStyle(W.Paragraph marker)
    {
        var fromTemplate = marker.Descendants<W.Run>().Select(r => r.RunProperties).FirstOrDefault(p => p != null);
        if (fromTemplate != null) return (W.RunProperties)fromTemplate.CloneNode(true);
        return new W.RunProperties(
            new W.RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
            new W.FontSize { Val = "20" },
            new W.FontSizeComplexScript { Val = "20" });
    }

    /// <summary>Borderless two-column table: photo, name below it.</summary>
    private static W.Table PhotoTable(MainDocumentPart main, IReadOnlyList<StampedPhoto> photos,
                                      W.RunProperties captionStyle, ref uint id)
    {
        var table = new W.Table(
            new W.TableProperties(
                new W.TableWidth { Width = TableWidth.ToString(), Type = W.TableWidthUnitValues.Dxa },
                new W.TableLayout { Type = W.TableLayoutValues.Fixed },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.None },
                    new W.LeftBorder { Val = W.BorderValues.None },
                    new W.BottomBorder { Val = W.BorderValues.None },
                    new W.RightBorder { Val = W.BorderValues.None },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.None },
                    new W.InsideVerticalBorder { Val = W.BorderValues.None })),
            new W.TableGrid(
                new W.GridColumn { Width = (TableWidth / 2).ToString() },
                new W.GridColumn { Width = (TableWidth / 2).ToString() }));

        for (var i = 0; i < photos.Count; i += 2)
        {
            var row = new W.TableRow();
            for (var c = 0; c < 2; c++)
            {
                var photo = i + c < photos.Count ? photos[i + c] : null;
                var cell = new W.TableCell(new W.TableCellProperties(
                    new W.TableCellWidth { Width = (TableWidth / 2).ToString(), Type = W.TableWidthUnitValues.Dxa }));
                if (photo == null)
                {
                    cell.Append(new W.Paragraph());
                }
                else
                {
                    cell.Append(new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "0" }),
                                                ImageRun(main, photo, id++)));
                    cell.Append(Caption(Path.GetFileNameWithoutExtension(photo.Name), captionStyle));
                }
                row.Append(cell);
            }
            table.Append(row);
        }
        return table;
    }

    private static W.Paragraph Caption(string text, W.RunProperties style) =>
        new(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "240" }),
            new W.Run((W.RunProperties)style.CloneNode(true),
                      new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    /// <summary>The photo, scaled to fit the box while keeping its shape.</summary>
    private static W.Run ImageRun(MainDocumentPart main, StampedPhoto photo, uint id)
    {
        var partType = photo.Extension switch
        {
            ".png" => ImagePartType.Png,
            ".bmp" => ImagePartType.Bmp,
            _ => ImagePartType.Jpeg,
        };
        var part = main.AddImagePart(partType);
        using (var stream = File.OpenRead(photo.Path)) part.FeedData(stream);

        var scale = Math.Min(BoxWidth / (double)photo.Width, BoxHeight / (double)photo.Height);
        var cx = (long)Math.Round(photo.Width * scale);
        var cy = (long)Math.Round(photo.Height * scale);

        return new W.Run(new W.Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = id, Name = photo.Name },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = photo.Name },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(
                        new A.Blip { Embed = main.GetIdOfPart(part) },
                        new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
            ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U,
        }));
    }

    // ─── Tokens ──────────────────────────────────────────────────────────────

    public static string TextOf(W.Paragraph p) => string.Concat(p.Descendants<W.Text>().Select(t => t.Text));

    private static void ReplaceTokens(OpenXmlElement? root, Dictionary<string, string> values)
    {
        if (root == null) return;
        foreach (var p in root.Descendants<W.Paragraph>().ToList())
        {
            var text = TextOf(p);
            if (!text.Contains("{{")) continue;
            var replaced = values.Aggregate(text, (s, kv) => s.Replace(kv.Key, kv.Value));
            if (replaced != text) SetText(p, replaced);
        }
    }

    /// <summary>Word splits a paragraph into many runs, so a token rarely sits in one of them.
    /// The whole text goes into the first run (keeping its formatting) and the rest are emptied.</summary>
    public static void SetText(W.Paragraph p, string text)
    {
        var runs = p.Descendants<W.Text>().ToList();
        if (runs.Count == 0)
        {
            p.Append(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
            return;
        }
        runs[0].Text = text;
        runs[0].Space = SpaceProcessingModeValues.Preserve;
        for (var i = 1; i < runs.Count; i++) runs[i].Text = "";
    }
}
