// See https://aka.ms/new-console-template for more information

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace MapRender;

[SuppressMessage("Interoperability", "CA1416:Проверка совместимости платформы")]
public static class Program
{
    private static int _downloadCounter;
    private static readonly DirectoryInfo CacheDir = new("Cache");
    private static readonly Dictionary<string, byte[]> CachedImages = new();

    public static void Main()
    {
        CacheDir.Create();
        foreach (var fileName in Directory.GetFiles(CacheDir.FullName))
        {
            CachedImages.Add(Path.GetFileName(fileName), File.ReadAllBytes(fileName));
        }

        var s = Stopwatch.StartNew();
        RenderMap(900, 100, new MapPoint(37.617635, 55.755821), 17).Wait();
        s.Stop();
        Console.WriteLine(s.ElapsedMilliseconds / 1000.0);

        Console.WriteLine(_downloadCounter);
        Console.ReadLine();
    }

    public static async Task RenderMap(int width, int height, MapPoint center, int zoom)
    {
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        object graphicsLock = new();

        int zoomNumTiles = MapHelper.GetNumberOfTilesAtZoom(zoom);
        var centerTilePoint = MapHelper.WorldToTilePos(center, zoom);

        // xStart = floor(center.X - width / 512)
        // yStart = floor(center.Y - height / 512)
        // xNum = celling((width + offset) / 256)
        // xNum = celling((height + offset) / 256)
        // xOffset = mod(center.X - width / 512, 1) * 256
        // yOffset = mod(center.Y - height / 512, 1) * 256

        double xs = centerTilePoint.X - width / 512.0;
        double ys = centerTilePoint.Y - height / 512.0;

        var (xStart, xOffset) = ((int)Math.Floor(xs), (int)(xs % 1 * 256.0));
        var (yStart, yOffset) = ((int)Math.Floor(ys), (int)(ys % 1 * 256.0));
        int xNum = (int)Math.Ceiling((width + xOffset) / 256.0);
        int yNum = (int)Math.Ceiling((height + yOffset) / 256.0);

        List<Task> getImageTask = new();
        for (int x = 0; x < xNum; x++)
        {
            for (int y = 0; y < yNum; y++)
            {
                int xTile = x + xStart;
                int yTile = y + yStart;
                int y1 = y;
                int x1 = x;
                getImageTask.Add(Task.Run(async () =>
                {
                    var image = await GetTileImage(
                        Wrap(xTile, zoomNumTiles),
                        Wrap(yTile, zoomNumTiles), zoom);

                    lock (graphicsLock)
                    {
                        graphics.DrawImage(image, new Point(
                            x1 * 256 - xOffset,
                            y1 * 256 - yOffset));
                    }
                }));
            }
        }

        await Task.WhenAll(getImageTask);

        // Export
        await using var fs = File.Create(@"C:\Users\User\Desktop\Map.png");
        bitmap.Save(fs, ImageFormat.Png);
    }

    private static int Wrap(int value, int by)
    {
        value %= by;
        if (value < 0)
            value += by;
        return value;
    }

    private static readonly HttpClient HttpClient = new();

    private static async Task<Image> GetTileImage(int x, int y, int zoom)
    {
        string name = $"{x}_{y}_{zoom}.png";
        string fileName = $"{CacheDir.FullName}/{name}";

        Stream stream;
        if (CachedImages.ContainsKey(name))
        {
            stream = new MemoryStream(CachedImages[name]);
        }
        else
        {
            _downloadCounter++;
            // We have to set user agent because otherwise we will get 403 http error
            // https://stackoverflow.com/questions/46604840/403-response-with-httpclient-but-not-with-browser
            var url = $"https://tile.openstreetmap.org/{zoom}/{x}/{y}.png";
            var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Headers =
                {
                    {
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.11 (KHTML, like Gecko) " +
                        "Chrome/23.0.1271.95 Safari/537.11"
                    }
                }
            };
            var result = await HttpClient.SendAsync(request);
            byte[] imgByte = await result.Content.ReadAsByteArrayAsync();

            stream = new MemoryStream(imgByte);

            // Save to disk
            await using var fs = File.Create(fileName);
            await stream.CopyToAsync(fs);
            CachedImages.Add(name, imgByte);
        }

        return Image.FromStream(stream);
    }
}