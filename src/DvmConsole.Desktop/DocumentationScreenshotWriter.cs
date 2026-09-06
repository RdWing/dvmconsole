// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media.Imaging;
using SkiaSharp;

namespace DvmConsole.Desktop;

internal static class DocumentationScreenshotWriter
{
    internal const int ShadowPadding = 24;
    internal const float CornerRadius = 10;
    private const float ShadowBlurRadius = 10;
    private const float ShadowVerticalOffset = 3;
    private const byte ShadowOpacity = 72;

    public static void Save(RenderTargetBitmap source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var encodedSource = new MemoryStream();
        source.Save(encodedSource);
        encodedSource.Position = 0;
        using SKBitmap sourceBitmap = SKBitmap.Decode(encodedSource)
            ?? throw new InvalidOperationException("Unable to decode the rendered screenshot.");

        int outputWidth = checked(sourceBitmap.Width + (ShadowPadding * 2));
        int outputHeight = checked(sourceBitmap.Height + (ShadowPadding * 2));
        var outputInfo = new SKImageInfo(
            outputWidth,
            outputHeight,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var outputBitmap = new SKBitmap(outputInfo);
        using (var canvas = new SKCanvas(outputBitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var shadowPaint = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, ShadowOpacity),
                ImageFilter = SKImageFilter.CreateBlur(
                    ShadowBlurRadius,
                    ShadowBlurRadius)
            };
            var shadowBounds = new SKRect(
                ShadowPadding,
                ShadowPadding + ShadowVerticalOffset,
                ShadowPadding + sourceBitmap.Width,
                ShadowPadding + ShadowVerticalOffset + sourceBitmap.Height);
            canvas.DrawRoundRect(shadowBounds, CornerRadius, CornerRadius, shadowPaint);

            var contentBounds = new SKRect(
                ShadowPadding,
                ShadowPadding,
                ShadowPadding + sourceBitmap.Width,
                ShadowPadding + sourceBitmap.Height);
            using var contentClip = new SKPath();
            contentClip.AddRoundRect(contentBounds, CornerRadius, CornerRadius);
            canvas.Save();
            canvas.ClipPath(contentClip, SKClipOperation.Intersect, antialias: true);
            canvas.DrawBitmap(sourceBitmap, ShadowPadding, ShadowPadding);
            canvas.Restore();
        }

        using SKImage outputImage = SKImage.FromBitmap(outputBitmap);
        using SKData outputData = outputImage.Encode(SKEncodedImageFormat.Png, quality: 100);
        using FileStream output = File.Create(path);
        outputData.SaveTo(output);
    }
}
