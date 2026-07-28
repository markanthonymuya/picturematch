# PictureMatch

PictureMatch is a private Windows desktop app that finds the original photo behind a screenshot or copied image. Choose folders on your computer, provide a reference image, and PictureMatch shows the five closest results with a similarity percentage and direct links to their locations.

## Features

- Select a screenshot or photo with the Windows file picker
- Paste images from the clipboard with the button or `Ctrl+V`
- Supports PNG, bitmap/DIB, copied files, and images copied from Chrome or Edge
- Drag and drop an image or picture folder
- Search multiple folders and all their subfolders
- Adjustable minimum-match threshold
- Displays only the five highest-scoring matches
- Open a matched image or reveal it in File Explorer
- Remembers selected folders and match threshold
- Runs locally—your photo library is not uploaded

## Requirements

- Windows 10 or Windows 11
- [.NET 10 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Getting started

1. Download or build PictureMatch.
2. Open `PictureMatch.exe`.
3. Select **Add folder** and choose one or more folders containing pictures.
4. Select **Select screenshot or photo…**, paste with `Ctrl+V`, or drag an image onto the window.
5. Adjust the minimum-match percentage if needed.
6. Select the purple **SEARCH NOW** button.
7. Use **Open image** or **Show folder** beside a result.

## How matching works

PictureMatch creates compact visual fingerprints for the reference image and every candidate. The displayed score combines four signals:

| Signal | Weight | What it compares |
| --- | ---: | --- |
| Difference hash | 40% | Edges and visual structure |
| Average perceptual hash | 30% | Overall light and dark layout |
| Color histogram | 20% | Distribution of red, green, and blue |
| Aspect ratio | 10% | Similarity of image dimensions |

Images below the selected threshold are excluded. The remaining results are sorted by score, and only the best five are displayed.

The matcher tolerates resizing and normal image compression. Heavily cropped, rotated, or edited screenshots may require a lower threshold.

## Build from source

```powershell
dotnet build PictureMatch.csproj -c Release
dotnet run --project PictureMatch.csproj
```

To create a release build:

```powershell
dotnet publish PictureMatch.csproj -c Release -o publish
```

## Privacy

Folder searches and visual comparisons run on your computer. Pictures are not uploaded. When an image copied from a browser contains only a web-image URL, PictureMatch downloads that individual image so it can be used as the search reference.

## License

Licensed under the [MIT License](LICENSE).
