using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinNetControl.Core;

public class AppIconCache
{
    // Store the in-flight task too: one busy browser can own hundreds of sockets,
    // but its executable icon should be extracted only once.
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<ImageSource?> GetIconAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Task.FromResult<ImageSource?>(null);

        return _cache.GetOrAdd(filePath, LoadIconAsync);
    }

    private static async Task<ImageSource?> LoadIconAsync(string filePath)
    {
        try
        {
            byte[]? pngBytes = await Task.Run(() =>
            {
                using Icon? icon = Icon.ExtractAssociatedIcon(filePath);
                if (icon == null) return null;
                using Bitmap bitmap = icon.ToBitmap();
                using MemoryStream stream = new();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                return stream.ToArray();
            });

            if (pngBytes == null) return null;

            var tcs = new TaskCompletionSource<ImageSource?>();
            if (!App.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var bitmapImage = new BitmapImage();
                    using var stream = new MemoryStream(pngBytes);
                    await bitmapImage.SetSourceAsync(stream.AsRandomAccessStream());
                    tcs.TrySetResult(bitmapImage);
                }
                catch { tcs.TrySetResult(null); }
            }))
                return null;

            return await tcs.Task;
        }
        catch { /* Fallback for protected files or missing files */ }
        return null;
    }
}
