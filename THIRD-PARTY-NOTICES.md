# Third-party components

This application distributes the following components and their transitive dependencies through NuGet. Their licenses remain with their respective authors.

- [BinaryKits.Zpl](https://github.com/BinaryKits/BinaryKits.Zpl): MIT.
- [SkiaSharp](https://github.com/mono/SkiaSharp): MIT; native Skia includes additional third-party notices.
- [ZXing.Net](https://github.com/micjahn/ZXing.Net): Apache-2.0.
- [HarfBuzzSharp / HarfBuzz](https://github.com/mono/SkiaSharp): see the bundled MIT and native third-party notices.
- [ImageSharp](https://github.com/SixLabors/ImageSharp): included transitively by BinaryKits; see its bundled Six Labors Split License, including the transitive-dependency terms.
- [.NET runtime and libraries](https://github.com/dotnet/runtime): MIT, with third-party notices included in self-contained builds.

The build packaging copies license/notice files supplied by the restored NuGet packages into `licenses`. Consult those files and the projects above for full terms.
