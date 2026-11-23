// Renders the static MSIX splash images (shown by Windows before the app's own animated splash) from the in-app
// logo Assets/AppLogo.png: the logo centred with the Mac/iPad corner radius on a transparent background; the
// manifest's BackgroundColor (#FAF7F2, CP.paper) fills the rest.
//
// Usage: swift build/render-splash.swift src/Occa.App/Assets/AppLogo.png src/Occa.App/Assets
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let logoURL = URL(fileURLWithPath: CommandLine.arguments[1])
let outDir = URL(fileURLWithPath: CommandLine.arguments[2])
let logo = CGImageSourceCreateImageAtIndex(CGImageSourceCreateWithURL(logoURL as CFURL, nil)!, 0, nil)!
let space = CGColorSpace(name: CGColorSpace.sRGB)!
for (name, h) in [("SplashScreen.scale-200.png", 600), ("SplashScreen.png", 300)] {
    let w = h * 1240 / 600
    let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.interpolationQuality = .high
    let s = CGFloat(h) * 0.5
    let box = CGRect(x: (CGFloat(w) - s) / 2, y: (CGFloat(h) - s) / 2, width: s, height: s)
    ctx.addPath(CGPath(roundedRect: box, cornerWidth: s * 0.2237, cornerHeight: s * 0.2237, transform: nil))
    ctx.clip()
    ctx.draw(logo, in: box)
    let dest = CGImageDestinationCreateWithURL(outDir.appendingPathComponent(name) as CFURL, UTType.png.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, ctx.makeImage()!, nil)
    CGImageDestinationFinalize(dest)
}
print("splash written")
