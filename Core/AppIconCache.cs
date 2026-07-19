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
    private readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ImageSource?> GetIconAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        if (_cache.TryGetValue(filePath, out var cachedImg)) return cachedImg;

        try
        {
            return await Task.Run(async () =>
            {
                using Icon? icon = Icon.ExtractAssociatedIcon(filePath);
                if (icon != null)
                {
                    using Bitmap bitmap = icon.ToBitmap();
                    using MemoryStream ms = new MemoryStream();
                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;

                    var bitmapImage = new BitmapImage();
                    // Needs to be executed on the UI thread, so we defer the SetSourceAsync
                    var tcs = new TaskCompletionSource<ImageSource>();
                    App.DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            var clonedStream = new MemoryStream(ms.ToArray());
                            await bitmapImage.SetSourceAsync(clonedStream.AsRandomAccessStream());
                            _cache[filePath] = bitmapImage;
                            tcs.SetResult(bitmapImage);
                        }
                        catch { tcs.SetResult(null!); }
                    });
                    return await tcs.Task;
                }
                return null;
            });
        }
        catch { /* Fallback for protected files or missing files */ }
        return null;
    }
}
