// Renders the Windows app assets (tiles, splash, in-app AppLogo.png, .ico sizes) from the shared OCCA logo (build/logo/occa-base-1024.png, produced by the Mac repo's
// Tools/render-logo.swift with a transparent background) and adds a small Windows mark in the orange corner.
//
// Usage: swift build/render-windows-logo.swift <base.png> <assets dir>
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let base = URL(fileURLWithPath: CommandLine.arguments[1])
let outDir = URL(fileURLWithPath: CommandLine.arguments[2])
guard let src = CGImageSourceCreateWithURL(base as CFURL, nil), let logo = CGImageSourceCreateImageAtIndex(src, 0, nil) else {
    fatalError("cannot read \(base.path)")
}
let space = CGColorSpace(name: CGColorSpace.sRGB)!

/// The logo plus the Windows mark, drawn into a square of `size` px with the flag scaled to `share` of the width.
func render(size: Int, share: CGFloat = 1.0, wide: Bool = false) -> CGImage {
    let w = wide ? Int(Double(size) * 310.0 / 150.0) : size, h = size
    let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.interpolationQuality = .high
    let s = CGFloat(size) * share
    let box = CGRect(x: (CGFloat(w) - s) / 2, y: (CGFloat(h) - s) / 2, width: s, height: s)
    ctx.draw(logo, in: box)

    // Windows mark: four white panes in the orange corner (bottom-right; CoreGraphics y axis points up).
    // The flag inside the base image spans 88% of it, so the corner is measured from the flag's edge.
    let flag = box.insetBy(dx: s * 0.06, dy: s * 0.06)
    let mark = flag.width * 0.17, gap = mark * 0.07, pane = (mark - gap) / 2
    let origin = CGPoint(x: flag.maxX - flag.width * 0.11 - mark, y: flag.minY + flag.height * 0.11)
    ctx.setFillColor(CGColor(srgbRed: 1, green: 1, blue: 1, alpha: 1))
    for (i, j) in [(0, 0), (1, 0), (0, 1), (1, 1)] {
        let r = CGRect(x: origin.x + CGFloat(i) * (pane + gap), y: origin.y + CGFloat(j) * (pane + gap), width: pane, height: pane)
        ctx.addPath(CGPath(roundedRect: r, cornerWidth: pane * 0.12, cornerHeight: pane * 0.12, transform: nil))
    }
    ctx.fillPath()
    return ctx.makeImage()!
}

func write(_ image: CGImage, _ name: String) {
    let url = outDir.appendingPathComponent(name)
    let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, image, nil)
    CGImageDestinationFinalize(dest)
}

try FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
// MSIX tiles (scale-200 and scale-100). Tiles keep a little air around the flag; small icons fill the canvas.
for (name, size) in [("Square150x150Logo.scale-200.png", 300), ("Square150x150Logo.png", 150)] { write(render(size: size, share: 0.86), name) }
for (name, size) in [("Square44x44Logo.scale-200.png", 88), ("Square44x44Logo.png", 44),
                     ("Square44x44Logo.targetsize-24_altform-unplated.png", 24), ("LockScreenLogo.scale-200.png", 48),
                     ("StoreLogo.png", 50)] { write(render(size: size), name) }
for (name, size) in [("Wide310x150Logo.scale-200.png", 300), ("Wide310x150Logo.png", 150)] { write(render(size: size, share: 0.86, wide: true), name) }
// Splash screen 620×300 (scale-100) and 1240×600: the logo centred on a transparent background.
for (name, size) in [("SplashScreen.scale-200.png", 600), ("SplashScreen.png", 300)] {
    let w = size * 1240 / 600
    let ctx = CGContext(data: nil, width: w, height: size, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.interpolationQuality = .high
    let logoImage = render(size: size, share: 0.8)
    ctx.draw(logoImage, in: CGRect(x: (w - size) / 2, y: 0, width: size, height: size))
    write(ctx.makeImage()!, name)
}
// In-app logo (project list, about, animated splash): 512 px with air around the flag like the Mac/iPad asset, same mark.
write(render(size: 512, share: 0.75), "AppLogo.png")
// Sizes for the .ico (assembled by build/make-ico.py).
for size in [16, 24, 32, 48, 64, 128, 256] { write(render(size: size), "ico-\(size).png") }
print("done")
