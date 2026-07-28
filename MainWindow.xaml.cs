using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace PictureMatch;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };
    private readonly ObservableCollection<FolderItem> folders = [];
    private readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PictureMatch", "settings.json");
    private Bitmap? queryBitmap;
    private CancellationTokenSource? searchCancellation;

    public MainWindow()
    {
        InitializeComponent();
        FoldersList.ItemsSource = folders;
        LoadSettings();
        Closed += (_, _) => { searchCancellation?.Cancel(); queryBitmap?.Dispose(); SaveSettings(); };
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder containing pictures",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK &&
            !folders.Any(f => string.Equals(f.Path, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase)))
        {
            folders.Add(new FolderItem(dialog.SelectedPath));
            SaveSettings();
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (FolderItem item in FoldersList.SelectedItems.Cast<FolderItem>().ToList())
            folders.Remove(item);
        SaveSettings();
    }

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a screenshot",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*"
        };
        if (dialog.ShowDialog() == true) SetQueryFromFile(dialog.FileName);
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        Exception? lastError = null;
        string? clipboardHtml = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var pasted = TryReadClipboardImage();
                if (pasted != null)
                {
                    SetQuery(pasted, "Pasted from clipboard");
                    return;
                }

                if (Clipboard.ContainsFileDropList())
                {
                    var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsImageFile);
                    if (file != null)
                    {
                        SetQueryFromFile(file);
                        return;
                    }
                }
                if (Clipboard.ContainsData(DataFormats.Html))
                    clipboardHtml = Clipboard.GetData(DataFormats.Html) as string;
                break;
            }
            catch (ExternalException ex)
            {
                lastError = ex;
                Thread.Sleep(80);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(clipboardHtml))
        {
            try
            {
                StatusText.Text = "Loading copied web image…";
                var webImage = await TryReadHtmlClipboardImageAsync(clipboardHtml);
                if (webImage != null)
                {
                    SetQuery(webImage, "Pasted from browser");
                    return;
                }
            }
            catch (Exception ex) { lastError = ex; }
        }

        var detail = lastError == null ? "" : $"\n\nWindows reported: {lastError.Message}";
        ShowNotice("No readable picture was found on the clipboard. Try copying the image again, or use “Select screenshot or photo…”." + detail);
    }

    private static Bitmap? TryReadClipboardImage()
    {
        // Windows Forms recognizes common DIB/bitmap formats used by Snipping Tool,
        // browsers, Photos, Paint, and many third-party screenshot utilities.
        if (Forms.Clipboard.ContainsImage())
        {
            using var image = Forms.Clipboard.GetImage();
            if (image != null) return new Bitmap(image);
        }

        var data = Clipboard.GetDataObject();
        if (data == null) return null;

        if (data.GetDataPresent(DataFormats.Bitmap, true))
        {
            var bitmapData = data.GetData(DataFormats.Bitmap, true);
            if (bitmapData is BitmapSource bitmapSource) return BitmapFromSource(bitmapSource);
            if (bitmapData is System.Drawing.Image drawingImage) return new Bitmap(drawingImage);
        }

        // Some browsers and screenshot tools expose PNG without advertising it as Bitmap.
        foreach (var format in new[] { "PNG", "image/png", "System.Drawing.Bitmap" })
        {
            if (!data.GetDataPresent(format, true)) continue;
            var value = data.GetData(format, true);
            if (value is BitmapSource source) return BitmapFromSource(source);
            if (value is System.Drawing.Image image) return new Bitmap(image);
            if (value is MemoryStream stream)
            {
                stream.Position = 0;
                using var temporary = new Bitmap(stream);
                return new Bitmap(temporary);
            }
            if (value is byte[] bytes)
            {
                using var streamFromBytes = new MemoryStream(bytes);
                using var temporary = new Bitmap(streamFromBytes);
                return new Bitmap(temporary);
            }
        }
        return null;
    }

    private static async Task<Bitmap?> TryReadHtmlClipboardImageAsync(string html)
    {
        var match = Regex.Match(html, @"<img\b[^>]*?\bsrc\s*=\s*[""'](?<src>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return null;

        var source = WebUtility.HtmlDecode(match.Groups["src"].Value);
        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0) return null;
            var metadata = source[..comma];
            var payload = source[(comma + 1)..];
            var bytes = metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            using var stream = new MemoryStream(bytes);
            using var image = System.Drawing.Image.FromStream(stream);
            return new Bitmap(image);
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var imageUri))
        {
            var sourceUrl = Regex.Match(html, @"(?im)^SourceURL:(?<url>\S+)\s*$").Groups["url"].Value;
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var pageUri) ||
                !Uri.TryCreate(pageUri, source, out imageUri)) return null;
        }

        if (imageUri.IsFile)
        {
            using var fileImage = new Bitmap(imageUri.LocalPath);
            return new Bitmap(fileImage);
        }
        if (imageUri.Scheme is not ("http" or "https")) return null;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PictureMatch/1.0");
        var bytesFromWeb = await client.GetByteArrayAsync(imageUri);
        using var webStream = new MemoryStream(bytesFromWeb);
        using var webDrawingImage = System.Drawing.Image.FromStream(webStream);
        return new Bitmap(webDrawingImage);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Paste_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        queryBitmap?.Dispose();
        queryBitmap = null;
        QueryPreview.Source = null;
        PreviewHint.Visibility = Visibility.Visible;
        ResultsList.ItemsSource = null;
        ResultSummary.Text = "Your five closest matches will appear here.";
        StatusText.Text = "Screenshot cleared";
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var path = ((string[])e.Data.GetData(DataFormats.FileDrop)).FirstOrDefault();
        if (path == null) return;
        if (Directory.Exists(path))
        {
            if (!folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                folders.Add(new FolderItem(path));
        }
        else if (IsImageFile(path)) SetQueryFromFile(path);
    }

    private void SetQueryFromFile(string path)
    {
        try
        {
            using var original = new Bitmap(path);
            SetQuery(new Bitmap(original), Path.GetFileName(path));
        }
        catch (Exception ex) { ShowNotice($"Could not open that image: {ex.Message}"); }
    }

    private void SetQuery(Bitmap bitmap, string label)
    {
        queryBitmap?.Dispose();
        queryBitmap = bitmap;
        QueryPreview.Source = ToBitmapSource(bitmap, 600);
        PreviewHint.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Screenshot ready: {label} ({bitmap.Width} × {bitmap.Height})";
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (queryBitmap == null) { ShowNotice("Paste or browse for a screenshot first."); return; }
        var validFolders = folders.Select(f => f.Path).Where(Directory.Exists).ToList();
        if (validFolders.Count == 0) { ShowNotice("Add at least one picture folder first."); return; }

        SetSearching(true);
        ResultsList.ItemsSource = null;
        searchCancellation = new CancellationTokenSource();
        var token = searchCancellation.Token;
        var threshold = ThresholdSlider.Value / 100.0;
        var queryCopy = new Bitmap(queryBitmap);
        try
        {
            var files = await Task.Run(() => EnumerateImages(validFolders, token), token);
            SearchProgress.IsIndeterminate = false;
            SearchProgress.Maximum = Math.Max(1, files.Count);
            ResultSummary.Text = $"Checking {files.Count:N0} images…";

            var progress = new Progress<int>(n =>
            {
                SearchProgress.Value = n;
                StatusText.Text = $"Compared {n:N0} of {files.Count:N0} images";
            });
            var matches = await Task.Run(() => FindMatches(queryCopy, files, threshold, progress, token), token);
            ResultsList.ItemsSource = matches;
            ResultSummary.Text = matches.Count == 0
                ? $"No images reached {threshold:P0}. Try lowering the minimum match."
                : $"Showing the top {matches.Count} match{(matches.Count == 1 ? "" : "es")}, best first.";
            StatusText.Text = $"Search complete — {files.Count:N0} images checked";
        }
        catch (OperationCanceledException)
        {
            ResultSummary.Text = "Search cancelled.";
            StatusText.Text = "Cancelled";
        }
        catch (Exception ex) { ShowNotice($"Search failed: {ex.Message}"); }
        finally
        {
            queryCopy.Dispose();
            SetSearching(false);
            searchCancellation?.Dispose();
            searchCancellation = null;
        }
    }

    private static List<string> EnumerateImages(List<string> roots, CancellationToken token)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                        if (IsImageFile(file)) found.Add(file);
                    foreach (var child in Directory.EnumerateDirectories(dir)) pending.Push(child);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        return found.ToList();
    }

    private static List<MatchResult> FindMatches(Bitmap query, List<string> files, double threshold,
        IProgress<int> progress, CancellationToken token)
    {
        var queryFeatures = ImageFeatures.Extract(query);
        var results = new List<(string Path, double Score)>();
        int done = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var candidate = new Bitmap(file);
                var score = queryFeatures.Similarity(ImageFeatures.Extract(candidate));
                if (score >= threshold) results.Add((file, score));
            }
            catch (ArgumentException) { }
            catch (ExternalException) { }
            catch (IOException) { }
            if (++done % 10 == 0 || done == files.Count) progress.Report(done);
        }
        return results.OrderByDescending(r => r.Score).Take(5)
            .Select(r => new MatchResult(r.Path, r.Score)).ToList();
    }

    private void SetSearching(bool searching)
    {
        SearchButton.IsEnabled = !searching;
        TopSearchButton.IsEnabled = !searching;
        CancelButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        SearchProgress.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        SearchProgress.IsIndeterminate = searching;
        if (!searching) SearchProgress.Value = 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => searchCancellation?.Cancel();
    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdLabel != null) ThresholdLabel.Text = $"{e.NewValue:0}%";
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e) => OpenPath((sender as Button)?.Tag as string);
    private void ShowFolder_Click(object sender, RoutedEventArgs e) => ShowInFolder((sender as Button)?.Tag as string);
    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is MatchResult result) ShowInFolder(result.FullPath);
    }

    private static void OpenPath(string? path)
    {
        if (path != null && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void ShowInFolder(string? path)
    {
        if (path != null && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "PictureMatch compares the visual fingerprint, colors, and shape of your screenshot against pictures in the selected folders.\n\n" +
            "It handles resizing and normal image compression. A heavily cropped, rotated, or edited screenshot may need a lower threshold.",
            "About matching", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowNotice(string message) =>
        MessageBox.Show(this, message, "PictureMatch", MessageBoxButton.OK, MessageBoxImage.Information);

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath));
            if (settings == null) return;
            foreach (var path in settings.Folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                folders.Add(new FolderItem(path));
            ThresholdSlider.Value = Math.Clamp(settings.Threshold, 40, 98);
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(
                new AppSettings(folders.Select(f => f.Path).ToList(), ThresholdSlider.Value)));
        }
        catch { }
    }

    private static bool IsImageFile(string path) => Extensions.Contains(Path.GetExtension(path));

    private static Bitmap BitmapFromSource(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var temp = new Bitmap(stream);
        return new Bitmap(temp);
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap, int maxPixel)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Min(maxPixel, bitmap.Width);
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private sealed record AppSettings(List<string> Folders, double Threshold);
}

public sealed record FolderItem(string Path);

public sealed class MatchResult
{
    public string FullPath { get; }
    public string FileName => Path.GetFileName(FullPath);
    public string Directory => Path.GetDirectoryName(FullPath) ?? "";
    public string ScoreText { get; }
    public BitmapSource? Thumbnail { get; }

    public MatchResult(string path, double score)
    {
        FullPath = path;
        ScoreText = $"{score:P0}";
        try
        {
            using var bitmap = new Bitmap(path);
            Thumbnail = CreateThumbnail(bitmap);
        }
        catch { }
    }

    private static BitmapSource CreateThumbnail(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 150;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

internal sealed class ImageFeatures
{
    private readonly ulong averageHash;
    private readonly ulong differenceHash;
    private readonly double[] histogram;
    private readonly double aspect;

    private ImageFeatures(ulong averageHash, ulong differenceHash, double[] histogram, double aspect)
    {
        this.averageHash = averageHash;
        this.differenceHash = differenceHash;
        this.histogram = histogram;
        this.aspect = aspect;
    }

    public static ImageFeatures Extract(Bitmap source)
    {
        using var small = Resize(source, 32, 32);
        using var hashImage = Resize(source, 9, 8);
        var gray = new double[64];
        var hist = new double[48];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            var c = hashImage.GetPixel(x, y);
            gray[y * 8 + x] = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        }
        var avg = gray.Average();
        ulong ah = 0, dh = 0;
        for (int i = 0; i < 64; i++) if (gray[i] >= avg) ah |= 1UL << i;
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            var left = hashImage.GetPixel(x, y);
            var right = hashImage.GetPixel(x + 1, y);
            var l = left.R + left.G + left.B;
            var r = right.R + right.G + right.B;
            if (l > r) dh |= 1UL << (y * 8 + x);
        }
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            var c = small.GetPixel(x, y);
            hist[c.R / 16]++;
            hist[16 + c.G / 16]++;
            hist[32 + c.B / 16]++;
        }
        for (int i = 0; i < hist.Length; i++) hist[i] /= 3072.0;
        return new ImageFeatures(ah, dh, hist, source.Width / (double)source.Height);
    }

    public double Similarity(ImageFeatures other)
    {
        var a = 1.0 - System.Numerics.BitOperations.PopCount(averageHash ^ other.averageHash) / 64.0;
        var d = 1.0 - System.Numerics.BitOperations.PopCount(differenceHash ^ other.differenceHash) / 64.0;
        var intersection = histogram.Zip(other.histogram, Math.Min).Sum();
        var aspectScore = Math.Min(aspect, other.aspect) / Math.Max(aspect, other.aspect);
        return Math.Clamp(0.30 * a + 0.40 * d + 0.20 * intersection + 0.10 * aspectScore, 0, 1);
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(result);
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(source, 0, 0, width, height);
        return result;
    }
}
