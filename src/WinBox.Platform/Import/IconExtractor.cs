using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace WinBox.Platform.Import;

/// <summary>Saves an executable's icon as a PNG, the way Explorer shows it.</summary>
public static class IconExtractor
{
    public static async Task<bool> TrySaveAsync(string executable, string pngPath, uint size = 96)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(executable);
            // For an executable the shell returns its icon (Type == Icon) as a bitmap.
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Size == 0)
                return false;
            var decoder = await BitmapDecoder.CreateAsync(thumbnail);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var temp = pngPath + ".tmp";
            using (var output = await (await StorageFile.GetFileFromPathAsync(CreateEmpty(temp))).OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }
            File.Move(temp, pngPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return false;
        }
    }

    private static string CreateEmpty(string path)
    {
        File.WriteAllBytes(path, []);
        return path;
    }
}
