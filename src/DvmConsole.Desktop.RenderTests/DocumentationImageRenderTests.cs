// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using System.Text.Json;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class DocumentationImageRenderTests
{
    [AvaloniaFact]
    public void EveryPackagedDocumentationImageDecodesAndRenders()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Documentation");
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "manifest.json")));
        string[] assets = manifest.RootElement.GetProperty("assets")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();

        Assert.NotEmpty(assets);
        foreach (string relativePath in assets)
        {
            string path = Path.GetFullPath(relativePath, root);
            using var bitmap = new Bitmap(path);
            var image = new Image
            {
                Source = bitmap,
                Width = 320,
                Height = 240
            };
            var host = new Window
            {
                Width = 360,
                Height = 280,
                Content = image
            };

            try
            {
                host.Show();
                host.UpdateLayout();
                Assert.True(bitmap.PixelSize.Width > 0, relativePath);
                Assert.True(bitmap.PixelSize.Height > 0, relativePath);
                Assert.True(image.Bounds.Width > 0, relativePath);
                Assert.True(image.Bounds.Height > 0, relativePath);
            }
            finally
            {
                host.Close();
            }
        }
    }
}
